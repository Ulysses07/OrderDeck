using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.Push;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.Shared.Sales;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Siparis sync (2026-05-13): WPF StreamSession + Order replikası için
/// outbox push endpoint. Pattern: <see cref="LicensesPaymentsSyncController"/>.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSessionsSyncController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly INotificationSender _push;
    private readonly Services.Stock.StockLedgerWriter _ledger;
    private readonly LabelRuleApplier _labels;
    private readonly CustomerIdResolver _customerIds;
    private readonly SalesReplicaService _replicas;
    private readonly SessionReleaser _releaser;
    private readonly ShipmentAmountDeriver _shipmentAmounts;
    private readonly SalesSyncOptions _salesSync;
    private readonly ILogger<LicensesSessionsSyncController> _log;

    public LicensesSessionsSyncController(
        LicenseDbContext db,
        INotificationSender push,
        Services.Stock.StockLedgerWriter ledger,
        LabelRuleApplier labels,
        CustomerIdResolver customerIds,
        SalesReplicaService replicas,
        SessionReleaser releaser,
        ShipmentAmountDeriver shipmentAmounts,
        IOptions<SalesSyncOptions> salesSync,
        ILogger<LicensesSessionsSyncController> log)
    {
        _db = db;
        _push = push;
        _ledger = ledger;
        _labels = labels;
        _customerIds = customerIds;
        _replicas = replicas;
        _releaser = releaser;
        _shipmentAmounts = shipmentAmounts;
        _salesSync = salesSync.Value;
        _log = log;
    }

    // ─── Session sync ─────────────────────────────────────────────────

    /// <param name="Release">v2, sahip (spec 2026-10-13 §4.1, M2): yayın bitti ve etiket kuyruğu boş — yayını
    /// sunucuya devret. v1'de okunmaz.</param>
    /// <param name="MachineName">v2: gönderen bilgisayarın adı; eklemede ve devralmada
    /// <c>OwnerMachineName</c> olur.</param>
    /// <param name="UnsentLabelCount">v2, sahip, <c>Release = false</c>: bitmiş yayının gönderilmemiş etiket
    /// sayısı; devrediliyor çipi ve CLI gösterir.</param>
    public sealed record SyncSessionItem(
        Guid Id,
        string? Title,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string Platforms,
        string? Notes,
        bool Release = false,
        string? MachineName = null,
        int? UnsentLabelCount = null);

    /// <param name="ReplicaId">v2 zarfı (spec 2026-10-13 §4.0); <see cref="ISalesEnvelopeFields"/>. Bugünkü
    /// istemci zarf alanlarını göndermez (v1).</param>
    public sealed record SyncSessionsRequest(
        List<SyncSessionItem> Sessions,
        Guid? ReplicaId = null,
        int? Protocol = null,
        int? SeenMode = null) : ISalesEnvelopeFields;

    /// <summary>Oturum yanıtı: sunucudaki satır (bugünkü alanlar) + sahiplik ve devir (spec 2026-10-13 §4.1).
    /// Eski istemci yanıtı atar; yeni alanlar onun için zararsız.</summary>
    /// <param name="Protocol">Sunucunun konuştuğu protokol (2). Yoksa sunucu eski sürümdür (§5.2).</param>
    /// <param name="Status"><c>applied</c> ya da <c>ignored</c> (gönderimin bir kısmı yok sayıldı).</param>
    /// <param name="Reason"><c>ignored</c>'da neden: <c>session-owned-elsewhere</c> (sahip olmayanın EndedAt'i)
    /// ya da <c>session-released</c> (devredilmiş oturumun başlık, platform ya da notu).</param>
    public sealed record SyncedSessionDto(
        Guid Id, string? Title,
        DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
        string Platforms, string? Notes,
        DateTimeOffset UpdatedAt,
        Guid? OwnerReplicaId,
        DateTimeOffset? ReleasedAt,
        int Protocol,
        string Status,
        string? Reason);

    private const string StatusApplied = "applied";
    private const string StatusIgnored = "ignored";
    internal const string ReasonOwnedElsewhere = "session-owned-elsewhere";
    private const string ReasonReleased = "session-released";

    /// <summary>Bir oturum öğesinin gönderenle ilişkisi (spec 2026-10-13 §4.1).</summary>
    private enum SessionRole
    {
        /// <summary>Protokolsüz istek (≤0.9.10): EndedAt dolu gelmesi devre yeter.</summary>
        Legacy,
        /// <summary>v2, ama oturum eski sürümde açılmış (sahibi boş): miras yol, Legacy gibi.</summary>
        Unowned,
        /// <summary>v2, çağıran sahip (ya da önceki kopyasından devraldı, §8.7).</summary>
        Owner,
        /// <summary>v2, oturum başka kopyanın: EndedAt yok sayılır, devir yok.</summary>
        NonOwner,
    }

    [HttpPost("sessions/sync")]
    public async Task<IActionResult> SyncSessions(
        Guid licenseId, [FromBody] SyncSessionsRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (!SalesEnvelope.TryRead(req, out var envelope, out var envelopeError))
            return Problem(title: "invalid-envelope", detail: envelopeError, statusCode: 400);
        var context = await _replicas.AcceptAsync(
            licenseId, envelope, Request.Headers.UserAgent.ToString(), "sessions/sync", ct);

        if (req?.Sessions is null || req.Sessions.Count == 0)
            return Ok(Array.Empty<SyncedSessionDto>());

        if (req.Sessions.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 session per batch.", statusCode: 400);

        // v2 öğe alanları: kolon sınırını aşan ad ya da negatif sayı sessizce kırpılmaz ya da 500'e düşmez.
        if (req.Sessions.FirstOrDefault(s =>
                s.MachineName is { Length: > 128 } || s.UnsentLabelCount is < 0) is { } invalid)
            return Problem(
                title: "invalid-session",
                detail: $"Oturum {invalid.Id}: machineName en çok 128 karakter, unsentLabelCount negatif olamaz.",
                statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var caller = envelope.ReplicaId;
        var ids = req.Sessions.Select(s => s.Id).ToList();
        var existing = await _db.StreamSessions
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var newLiveSessions = new List<(string? Title, string Platforms)>();
        var ignored = new Dictionary<Guid, string>();
        var toRelease = new List<Guid>();
        var legacyRelease = new HashSet<Guid>();

        SessionRole RoleOf(StreamSession s) =>
            !context.IsV2 ? SessionRole.Legacy
            : s.OwnerReplicaId is null ? SessionRole.Unowned
            : s.OwnerReplicaId == caller ? SessionRole.Owner
            : SessionRole.NonOwner;

        foreach (var item in req.Sessions)
        {
            StreamSession session;
            SessionRole role;

            if (existing.TryGetValue(item.Id, out var current))
            {
                session = current;

                // Devredilmiş oturum (§2): birim yazıcısı artık sunucu. Başlık, platform ve not yok sayılır; EndedAt
                // zaten dolu ve tekdüze. Satıra yazılmaz — her yeniden gönderim (W1 devri görene kadar oturumu
                // yeniden yollar) oturumun rowversion'ını ve akışı boşuna ilerletirdi. Tek istisna S1 öncesi imaja
                // dönüş kalıntısı: eski kod devredilmiş oturumun EndedAt'ini bayat gönderimle NULL'layabilir. Dolu
                // değer gelirse boş alan doldurulur (tekdüze: yalnız boşsa); gelmezse onarım işi devir anını yazar
                // (sales-release-repair, d).
                if (current.ReleasedAt is not null)
                {
                    if (current.Title != item.Title || current.Platforms != item.Platforms || current.Notes != item.Notes)
                        ignored[item.Id] = ReasonReleased;
                    if (current.EndedAt is null && item.EndedAt is not null)
                    {
                        current.EndedAt = item.EndedAt;
                        current.UpdatedAt = now;
                    }
                    continue;
                }

                role = RoleOf(current);
                if (role == SessionRole.NonOwner && current.OwnerReplicaId is { } owner)
                {
                    // §8.7, Y8: hâl bu push'tan ÖNCEKİ sunucu satırından — gönderimin kendi EndedAt'i canlı oturumu
                    // "devrediliyor" gösterip başka makinenin devralmasını kendi kendine kapatmasın ya da açmasın.
                    var phase = current.EndedAt is null ? TakeoverPhase.Live : TakeoverPhase.HandingOver;
                    if (await _replicas.CanInheritAsync(
                            licenseId, caller, context.ReplicaBefore, current.Id, owner, phase, now, ct))
                    {
                        // §8.7: önceki veritabanı bu kopyanın ilk görüldüğü andan beri susuyor ve Y8 izin veriyor
                        // (aynı makine ya da sahibi yeterince susmuş canlı yayın) — yayının kuyruğu artık bu kopyada.
                        // Sahiplik geçer; çağıran sahip olarak yargılanır.
                        current.OwnerReplicaId = caller;
                        current.OwnerMachineName = item.MachineName ?? current.OwnerMachineName;
                        role = SessionRole.Owner;
                        _log.LogInformation(
                            "sessions/sync: oturum {SessionId} sahipliği önceki kopya {PreviousReplicaId} → geri "
                            + "yüklenmiş kopya {ReplicaId} (license={LicenseId})",
                            current.Id, owner, caller, licenseId);
                    }
                }

                current.Title = item.Title;
                current.Platforms = item.Platforms;
                current.Notes = item.Notes;
                // EndedAt tekdüze (spec 2026-10-13 §4.1, her biçimde): dolu
                // değer NULL'a ya da başka bir değere dönmez. Bayat gönderim
                // (yayın sürerken kuyruğa girmiş satır) bitmiş yayını yeniden
                // "açıyordu". Sahip olmayan v2 kopyası yayını kapatamaz ve
                // devredemez; tek başka yol force-end (§4.6). Yok sayılan
                // kapanış ya da devir isteği yanıtta görünür (Y8: devralamayan
                // geri yüklenmiş kopya "devredildi" sanmasın).
                if (role != SessionRole.NonOwner)
                    current.EndedAt ??= item.EndedAt;
                else if ((current.EndedAt is null && item.EndedAt is not null) || item.Release)
                    ignored[item.Id] = ReasonOwnedElsewhere;
                current.UpdatedAt = now;
            }
            else
            {
                // v2 eklemesinde çağıran sahip olur; v1'de sahip boş kalır (miras, §2).
                session = new StreamSession
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    Title = item.Title,
                    StartedAt = item.StartedAt,
                    EndedAt = item.EndedAt,
                    Platforms = item.Platforms,
                    Notes = item.Notes,
                    UpdatedAt = now,
                    OwnerReplicaId = context.IsV2 ? caller : null,
                    OwnerMachineName = context.IsV2 ? item.MachineName : null,
                };
                _db.StreamSessions.Add(session);
                role = context.IsV2 ? SessionRole.Owner : SessionRole.Legacy;
                // Sadece henüz bitmemiş (canlı) yeni session için notify.
                if (item.EndedAt is null)
                    newLiveSessions.Add((item.Title, item.Platforms));
            }

            // Devir (§4.1): bitmiş ∧ devredilmemiş ∧ (v2 sahibinin Release'i ∨ v1 ∨ sahipsiz oturum). Sahibin
            // Release = false kapanışı yayını "devrediliyor" bırakır: etiketleri sahipte bekliyor (M2).
            if (session.EndedAt is null) continue;
            switch (role)
            {
                case SessionRole.Legacy or SessionRole.Unowned:
                    toRelease.Add(session.Id);
                    legacyRelease.Add(session.Id);
                    break;
                case SessionRole.Owner when item.Release:
                    toRelease.Add(session.Id);
                    break;
                case SessionRole.Owner:
                    session.UnsentLabelCount = item.UnsentLabelCount;
                    break;
            }
        }

        // Kapanış ile devir tek işlemde: devir düşerse kapanış da yazılmaz, istemcinin yeniden gönderimi ikisini
        // birlikte yapar (oturum satırı devir görülene kadar kuyrukta kalır, §5.2).
        IReadOnlyList<Guid> released = Array.Empty<Guid>();
        await using (var transaction = toRelease.Count > 0 && _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null)
        {
            await _db.SaveChangesAsync(ct);
            if (toRelease.Count > 0)
                released = await _releaser.ReleaseAsync(licenseId, toRelease, now, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }

        // Mod On'da miras yoldan (protokolsüz istek ya da sahipsiz oturum) devir bir izdir (§4.1): eski sürüm
        // bilgisayar dönmüş ya da eski sürümde açılmış yayın kapanıyor. Yalnız kimlik ve sayı.
        var legacyReleased = released.Where(legacyRelease.Contains).ToList();
        if (legacyReleased.Count > 0 && context.Mode == SalesSyncMode.On)
            _log.LogInformation(
                "sessions/sync: mod On'da {Count} oturum miras yoldan devredildi (protokolsüz istek ya da sahipsiz "
                + "oturum; license={LicenseId}): {SessionIds}",
                legacyReleased.Count, licenseId, string.Join(",", legacyReleased));

        if (newLiveSessions.Count > 0)
        {
            try
            {
                foreach (var s in newLiveSessions)
                {
                    var title = "Yayın başladı";
                    var body = string.IsNullOrWhiteSpace(s.Title)
                        ? $"Platform: {s.Platforms}"
                        : s.Title!;
                    await _push.SendToCustomerAsync(
                        customerId, title, body,
                        new Dictionary<string, string>
                        {
                            ["type"] = "session-started",
                            ["licenseId"] = licenseId.ToString()
                        }, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Push send failed for new sessions (license={LicenseId}, count={Count})",
                    licenseId, newLiveSessions.Count);
            }
        }

        // İzdüşüm (izlenmeyen) — devir koşullu UPDATE ile yazıldıysa izlenen varlık onu görmez; değerler
        // veritabanından okunur.
        var rows = await _db.StreamSessions
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .Select(s => new
            {
                s.Id, s.Title, s.StartedAt, s.EndedAt, s.Platforms, s.Notes, s.UpdatedAt,
                s.OwnerReplicaId, s.ReleasedAt,
            })
            .ToListAsync(ct);

        return Ok(rows.Select(s => new SyncedSessionDto(
            s.Id, s.Title, s.StartedAt, s.EndedAt, s.Platforms, s.Notes, s.UpdatedAt,
            s.OwnerReplicaId,
            s.ReleasedAt,
            SalesEnvelope.V2,
            ignored.ContainsKey(s.Id) ? StatusIgnored : StatusApplied,
            ignored.GetValueOrDefault(s.Id))).ToList());
    }

    // ─── Order (Label) sync ───────────────────────────────────────────

    public sealed record SyncOrderItem(
        Guid Id,
        Guid? SessionId,
        string CustomerId,
        string Platform,
        string Username,
        string? DisplayName,
        string MessageText,
        string? Code,
        decimal Price,
        DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt,
        string? CancelReason,
        bool IsShippingFee,
        bool IsBackupPromoted,
        bool IsTentativeBackup,
        // Varsayılanlı: eski WPF sürümleri bu alanları göndermiyor ve
        // göndermemeleri hata değil — ürün bağlanmamış sipariş geçerli bir
        // durumdur (kart "tanımlı değil" der, satış yine olur).
        Guid? ProductId = null,
        Guid? ProductVariantId = null,
        // v2 (format = 2; spec 2026-10-13 §4.2). Yalnız v2 biçiminde okunur;
        // v1'de "gönderilmedi" demektir ve sunucudaki değer korunur.
        int? BaseVersion = null,
        Guid? ParentOrderId = null,
        string? PrintBatchId = null,
        int? PrintSeq = null,
        DateTimeOffset? PriceCorrectedAt = null,
        decimal? PaidPrice = null,
        Guid? ShipmentId = null)
    {
        /// <summary>Öğenin birim kuralına giren değerleri. Bütün alanlar taşınır; v1'in taşımadığı alanları (düzeltme
        /// damgası, ödenen fiyat, baskı grubu, kargo bağı, ebeveyn) birim kuralı <see cref="OrderPushFormat.V1"/>'de
        /// "gönderilmedi" sayar ve sunucudaki değeri korur.</summary>
        public OrderUnitValues ToUnitValues() => new()
        {
            Price = Price,
            PriceCorrectedAt = PriceCorrectedAt,
            PaidPrice = PaidPrice,
            CancelledAt = CancelledAt,
            CancelReason = CancelReason,
            IsTentativeBackup = IsTentativeBackup,
            ShipmentId = ShipmentId,
            PrintedAt = PrintedAt,
            PrintBatchId = PrintBatchId,
            PrintSeq = PrintSeq,
            ParentOrderId = ParentOrderId,
        };

        /// <summary>
        /// Eklenecek satır (spec 2026-10-13 §4.2 ekleme dalı; <c>sales/backfill</c> §4.3 aynı yoldan): oluşum
        /// alanları ve birimler öğeden. v2 alanları (048 alanları, ebeveyn) yalnız <paramref name="v2"/>'de yazılır,
        /// v1 satırında boş kalır. Kaynak kopya, kargo bağı (K4), devir anı (DM3) ve KVKK boşaltması çağıranındır.
        /// </summary>
        /// <param name="customerHex">Asıl kayda çözülmüş müşteri Id'si (A5b).</param>
        public Order ToNewOrder(Guid licenseId, string customerHex, bool v2, DateTimeOffset now) => new()
        {
            Id = Id,
            LicenseId = licenseId,
            SessionId = SessionId,
            CustomerId = customerHex,
            Platform = Platform,
            Username = Username,
            DisplayName = DisplayName,
            MessageText = MessageText,
            Code = Code,
            Price = Price,
            AddedAt = AddedAt,
            PrintedAt = PrintedAt,
            CancelledAt = CancelledAt,
            CancelReason = CancelReason,
            IsShippingFee = IsShippingFee,
            IsBackupPromoted = IsBackupPromoted,
            IsTentativeBackup = IsTentativeBackup,
            ProductId = ProductId,
            ProductVariantId = ProductVariantId,
            UpdatedAt = now,
            ParentOrderId = v2 ? ParentOrderId : null,
            PrintBatchId = v2 ? PrintBatchId : null,
            PrintSeq = v2 ? PrintSeq : null,
            PriceCorrectedAt = v2 ? PriceCorrectedAt : null,
            PaidPrice = v2 ? PaidPrice : null,
        };
    }

    /// <param name="CatalogAware">
    /// İstemcinin katalog kimliklerini (<c>ProductId</c>/<c>ProductVariantId</c>)
    /// doldurabildiğini bildirir. VARSAYILAN false, çünkü güncellenmemiş WPF
    /// sürümleri bu alanı hiç göndermez ve göndermemeleri hata değil.
    ///
    /// Bayrak olmadan <c>ProductId = null</c> iki ayrı durumu birbirine karıştırır:
    /// "operatör ürünü belirleyemedi" (meşru — hareket yazılmaz) ile "bu istemci
    /// katalog bilmiyor". İkincisinde sunucu deftere HİÇ dokunmamalı; aksi hâlde
    /// StockLedgerReconciler'ın "istenmeyen anahtarı sıfırla" dalı daha önce
    /// yazılmış satışı geri sarar ve stok sessizce şişer.
    /// </param>
    /// <param name="ReplicaId">v2 zarfı (spec 2026-10-13 §4.0); <see cref="ISalesEnvelopeFields"/>.</param>
    /// <param name="Format">Öğe biçimi (spec 2026-10-13 §4.2). Yok ya da 1: bugünkü öğe ve yanıt
    /// (<see cref="SyncedOrderDto"/> listesi). 2: v2 alanları (<c>baseVersion</c>, 048 alanları, ebeveyn, kargo
    /// bağı) ve öğe başına yanıt (<see cref="SyncedOrderV2Dto"/>); çağıranı bilinen v2 zarfı ister. Zarf tek başına
    /// biçimi değiştirmez: zarflı ama biçimsiz istek v1 biçimidir.</param>
    public sealed record SyncOrdersRequest(
        List<SyncOrderItem> Orders,
        bool CatalogAware = false,
        Guid? ReplicaId = null,
        int? Protocol = null,
        int? SeenMode = null,
        int? Format = null) : ISalesEnvelopeFields;

    /// <summary>
    /// v2 yanıtının öğesi (spec 2026-10-13 §4.2 Yanıt). Sıra istekteki sıradır (yinelenen Id bir kez).
    /// </summary>
    /// <param name="Version">Satırın yazımdan sonraki sürümü (<see cref="Order.SyncVersion"/>): istemcinin sonraki
    /// <c>baseVersion</c>'ı. Reddedilen eklemede satır yoktur: null.</param>
    /// <param name="Status"><c>applied</c>: push'un her değeri yazıldı ya da zaten sunucudaydı. <c>ignored</c>: en az bir
    /// birim yazılmadı (damga, tek yön, baskı). <c>rejected</c>: kural push'u reddetti (devredilmiş oturum, kargoya
    /// verilmiş dosya, başka kopyanın canlı yayını).</param>
    /// <param name="IgnoredUnits">Yazılmayan birimler: <c>price</c>, <c>cancel</c>, <c>backup</c>, <c>shipment</c>,
    /// <c>print</c>.</param>
    /// <param name="Reason">Satırın nedeni; uygulandıysa null.</param>
    /// <param name="Current">Sunucunun güncel satırı, akış öğesi biçiminde (<see cref="SalesOrderItem"/>):
    /// <c>ignored</c> ve <c>rejected</c>'da hep dolu (istemci onu uygular); reddedilen eklemede sunucuda satır yoktur
    /// ve null kalır; <c>applied</c>'da gönderilmez.</param>
    public sealed record SyncedOrderV2Dto(
        Guid Id,
        int? Version,
        Guid? ShipmentId,
        DateTimeOffset? SessionReleasedAt,
        string Status,
        IReadOnlyList<string> IgnoredUnits,
        string? Reason,
        SalesOrderItem? Current);

    private const string StatusRejected = "rejected";

    /// <summary><c>Orders.PrintBatchId</c> kolonunun uzunluğu (<c>LicenseDbContext</c>).</summary>
    internal const int PrintBatchIdMaxLength = 40;

    public sealed record SyncedOrderDto(
        Guid Id, Guid? SessionId, string CustomerId,
        string Platform, string Username, string? DisplayName,
        string MessageText, string? Code, decimal Price,
        DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, string? CancelReason,
        bool IsShippingFee, bool IsBackupPromoted, bool IsTentativeBackup,
        Guid? ProductId, Guid? ProductVariantId,
        DateTimeOffset UpdatedAt);

    [HttpPost("orders/sync")]
    public async Task<IActionResult> SyncOrders(
        Guid licenseId, [FromBody] SyncOrdersRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (!SalesEnvelope.TryRead(req, out var envelope, out var envelopeError))
            return Problem(title: "invalid-envelope", detail: envelopeError, statusCode: 400);
        if (!TryReadFormat(req?.Format, envelope, out var format, out var formatError))
            return Problem(title: "invalid-format", detail: formatError, statusCode: 400);
        var context = await _replicas.AcceptAsync(
            licenseId, envelope, Request.Headers.UserAgent.ToString(), "orders/sync", ct);
        var v2 = format == OrderPushFormat.V2;
        var caller = envelope.ReplicaId;

        if (req?.Orders is null || req.Orders.Count == 0)
            return v2 ? Ok(Array.Empty<SyncedOrderV2Dto>()) : Ok(Array.Empty<SyncedOrderDto>());

        if (req.Orders.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 order per batch.", statusCode: 400);

        // v2 öğe alanı kolon sınırını aşarsa sessizce kırpılmaz ya da 500'e düşmez.
        if (v2 && req.Orders.FirstOrDefault(o => o.PrintBatchId is { Length: > PrintBatchIdMaxLength }) is { } invalid)
            return Problem(
                title: "invalid-order",
                detail: $"Sipariş {invalid.Id}: printBatchId en çok {PrintBatchIdMaxLength} karakter.",
                statusCode: 400);

        var now = DateTimeOffset.UtcNow;

        // Paketi sipariş id'sine göre TEKİLLEŞTİR. Aynı id iki kez gelirse
        // aşağıdaki döngü aynı yeni siparişi iki kez Add eder → EF izleme
        // istisnası → 500. Son giriş kazanır: payload sırası istemcinin niyet
        // sırasıdır. Defter de bu döngünün ürettiği satırlardan beslenir — iki
        // yol aynı siparişleri görmeli.
        var orders = req.Orders.GroupBy(o => o.Id).Select(g => g.Last()).ToList();

        // SessionId'ler bu lisansa ait mi? FK yalnız oturumun VAR olduğunu
        // garantiler, lisans eşleşmesini DEĞİL — doğrulamazsak bir yayıncı
        // başka yayıncının oturumuna sipariş bağlayabilir (2026-09-09 denetimi
        // F01, gerçek SQL Server'da tekrar üretildi). Paketin tamamını
        // reddediyoruz: WPF oturumları siparişlerden ÖNCE push'lar, bu yüzden
        // meşru istemcide bilinmeyen oturum olmaz; hata alan paket senkronsuz
        // kalır ve sonraki turda yeniden denenir.
        var sessionIds = orders
            .Where(o => o.SessionId != null)
            .Select(o => o.SessionId!.Value)
            .Distinct()
            .ToList();

        var ids = orders.Select(o => o.Id).ToList();
        var existing = await _db.Orders
            .Where(o => o.LicenseId == licenseId && ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, ct);

        // Oturum başına devir anı (ekleme onu kopyalar, DM3), sahiplik (başka kopyanın canlı yayınına v2 eklemesi
        // reddedilir) ve hâl (K5). Güncellenen siparişin oturumu sunucu satırınınkidir — SessionId güncellemede
        // yazılmaz (K1); paketteki değer bayat olabilir.
        var sessionKeys = sessionIds
            .Concat(existing.Values.Where(o => o.SessionId != null).Select(o => o.SessionId!.Value))
            .Distinct()
            .ToList();
        var sessions = sessionKeys.Count == 0
            ? new Dictionary<Guid, SessionFacts>()
            : await _db.StreamSessions
                .Where(s => sessionKeys.Contains(s.Id) && s.LicenseId == licenseId)
                .Select(s => new SessionFacts(s.Id, s.EndedAt, s.ReleasedAt, s.OwnerReplicaId, s.ForceEndedBy))
                .ToDictionaryAsync(s => s.Id, ct);
        if (sessionIds.Any(id => !sessions.ContainsKey(id)))
            return Problem(
                title: "unknown-session",
                detail: "Pakette bu lisansa ait olmayan SessionId var.",
                statusCode: 400);

        var newPrintedOrders = new List<decimal>();
        // (CustomerId-hex, Price) for new printed orders — used after Save to
        // resolve which shopper to notify (one Order belongs to at most one
        // shopper, identified via ShopperBroadcasterLink.WpfCustomerId).
        var newOrdersForShopperPush = new List<(string CustomerIdHex, decimal Price)>();

        // Eski sürüm birleştirmeden sonra da kopyanın Id'sini gönderir (A5b):
        // yeni sipariş asıl kaydın hex'iyle eklenir — asıl kayda bağlı Shopper
        // siparişi görsün, etiket kuralı telefonu olan asıl kayda baksın.
        // Paketin tamamı tek çağrıda çözülür. Güncelleme yolu CustomerId
        // yazmıyor (birleştiricinin taşıması korunur) — öyle kalıyor.
        // CustomerId gövdede zorunlu (null → 400, model doğrulaması); Guid
        // olmayan değer aynen döner.
        var canonicalHex = await _customerIds.CanonicalHexOfAsync(
            licenseId, orders.Select(o => o.CustomerId), ct);
        string CanonicalHex(string hex) => canonicalHex.GetValueOrDefault(hex, hex);

        // KVKK (§9, §4.2 ekleme dalı): ayar açıkken asıl projeksiyonu silinmiş müşteriye silmeden sonra gelen geç
        // ekleme (çevrimdışı bilgisayarın geç gönderimi) metni geri getirmez. Ayar kapalıyken hiç okunmaz.
        var purgeDirectory = SalesCustomerDirectory.Empty;
        if (_salesSync.ScrubOrderTextOnPurge)
        {
            var insertCustomers = orders.Where(o => !existing.ContainsKey(o.Id)).Select(o => o.CustomerId).ToList();
            if (insertCustomers.Count > 0)
                purgeDirectory = await SalesCustomerDirectory.LoadAsync(_db, licenseId, insertCustomers, ct);
        }

        // K6 ve K7 (mod On): siparişlerin bağlı olduğu ve v2 push'unun bağladığı dosyalar. Türetmenin okuduğu bağlı
        // siparişlerden ÖNCE yüklenir (sıra ve jeton gerekçesi ShipmentAmountDeriver'da).
        var modeOn = context.Mode == SalesSyncMode.On;
        var shipments = modeOn
            ? await _shipmentAmounts.LoadAsync(
                licenseId,
                existing.Values.Where(o => o.ShipmentId != null).Select(o => o.ShipmentId!.Value)
                    .Concat(orders.Where(o => v2 && o.ShipmentId != null).Select(o => o.ShipmentId!.Value)),
                ct)
            : new Dictionary<Guid, Shipment>();
        var affectedShipments = new List<Guid>();

        // Paketin satırları, birleştirildikten sonraki hâlleriyle (izlenen
        // varlıklar). Defter paketten değil bunlardan beslenir: yazılmayan bir
        // alan deftere bayat durum taşımasın (spec 2026-10-13 §4.2, M4).
        var rows = new List<Order>(orders.Count);
        var merges = new Dictionary<Guid, OrderMergeResult>();
        var rejectedInserts = new HashSet<Guid>();
        var k5ExceptionAccepted = 0;

        // Geri yüklenmiş kopya önceki kopyasının yayınını devralabilir mi (§8.7, Y8)? sessions/sync ve sales/backfill
        // ile aynı yol; önceki kopyalar istek başına bir kez okunur.
        Task<bool> CanInheritAsync(Guid sessionId, Guid owner, TakeoverPhase phase) =>
            _replicas.CanInheritAsync(licenseId, caller, context.ReplicaBefore, sessionId, owner, phase, now, ct);

        foreach (var item in orders)
        {
            if (existing.TryGetValue(item.Id, out var current))
            {
                // Birim kuralı (spec 2026-10-13 §4.2) ortak koddan. K1–K3
                // moddan bağımsız:
                // - K1: oluşum alanları (SessionId, MessageText, Code,
                //   IsShippingFee, IsBackupPromoted, ProductId,
                //   ProductVariantId; CustomerId ve görünen ad zaten
                //   yazılmıyordu) yalnız eklemede yazılır. Katalog bilmeyen
                //   istemcinin ProductId'yi NULL'laması böyle kapanır.
                // - K2: komutun damgaladığı fiyat, iptal ve yedeği v1 yazamaz;
                //   v2 yalnız satırın güncel sürümünü gördüyse (baseVersion =
                //   Version) yazar, damga kalır. Damgasız birimde bugünkü
                //   son-gelen-kazanır sürer. Yedek yalnız 1'den 0'a gider.
                // - K3: PrintedAt silinmez ve kaymaz; şu anda komutla iptal
                //   edilmiş siparişe baskı alınmaz.
                // K4–K6 kipe bağlı:
                // - K4: kargo bağını v1 taşımaz; mod Off'ta v2 K2'nin kuralıyla
                //   yazar, mod On'da yalnız açılış penceresindeki kopya boş bağı
                //   doldurur.
                // - K5 (mod On): devredilmiş oturumda fiyat, iptal ve yedek
                //   reddedilir; zorla bitirilmiş oturumun sahibi ve açılış
                //   penceresi K2 ile yargılanır.
                // - K6 (mod On): kargoya verilmiş dosyadaki siparişin fiyatı
                //   değişmez.
                // v1 yanıtı değişmez: yok sayılan ya da reddedilen birim de
                // yanıtta sunucunun satırıyla döner.
                var session = current.SessionId is { } sessionId ? sessions.GetValueOrDefault(sessionId) : null;
                var mergeContext = new OrderMergeContext
                {
                    Format = format,
                    ModeOn = modeOn,
                    SessionPhase = PhaseOf(session),
                    // Önceki kopya zinciri yalnız K5 istisnası (i) için okunur. Oturum zorla bitirildiğinde
                    // canlıydı (M18): başka makinedeki geri yüklenmiş kopya ancak sahip ForceEndSilenceMinutes
                    // sustuysa sahip sayılır (Y8, canlı dalı).
                    CallerOwnsSession = v2 && session?.OwnerReplicaId is { } owner
                        && (owner == caller
                            || (modeOn && session.ReleasedAt is not null && session.ForceEndedBy is not null
                                && await CanInheritAsync(session.Id, owner, TakeoverPhase.Live))),
                    SessionForceEnded = session?.ForceEndedBy is not null,
                    OpeningWindow = context.OpeningWindow,
                    LinkedShipmentShipped = current.ShipmentId is { } linkedId
                        && shipments.TryGetValue(linkedId, out var linked)
                        && linked.Status == ShipmentStatus.Shipped,
                };
                var shipmentBefore = current.ShipmentId;
                var merge = OrderUnitMerge.Merge(current.ToServerRow(), PushOf(item), mergeContext);
                current.ApplyUnits(merge.Merged);
                merges[item.Id] = merge;
                if (modeOn)
                {
                    affectedShipments.AddRange(ShipmentAmountDeriver.AffectedBy(
                        shipmentBefore, merge.Merged.ShipmentId, merge.ChangedUnits));
                    if (mergeContext.SessionPhase == OrderSessionPhase.Released
                        && !OrderUnitMerge.LocksReleasedSession(mergeContext)
                        && merge.ChangedUnits.Any(u => u is OrderUnit.Price or OrderUnit.Cancel or OrderUnit.Backup))
                        k5ExceptionAccepted++;
                }
                current.UpdatedAt = now;
                // Eşzamanlılık jetonunu ilerlet (bkz. Order.SyncVersion).
                // Sipariş alanları değişmemiş olsa bile artırıyoruz: paketin
                // deftere girmesi tek başına satırın "işlendiğini" gösterir ve
                // aynı siparişe paralel giren ikinci isteğin görmesi gereken
                // tam da bu.
                current.SyncVersion++;
                rows.Add(current);
            }
            else
            {
                var session = item.SessionId is { } sid ? sessions[sid] : null;

                // §4.2 ekleme dalı: v2'de başka kopyanın CANLI oturumuna ekleme satır başına reddedilir — canlı yayın
                // yalnız sahibinde yazılır (Y1). Önceki kopyası susan geri yüklenmiş kopya, Y8 izin veriyorsa (aynı
                // makine ya da sahibi ForceEndSilenceMinutes susmuş), o yayının kuyruğunu taşır (§8.7). Protokolsüz
                // istekte çağıran bilinmez; devrediliyor ve miras (sahipsiz) oturum reddedilmez.
                if (v2 && session is { EndedAt: null, OwnerReplicaId: { } owner } && owner != caller
                    && !await CanInheritAsync(session.Id, owner, TakeoverPhase.Live))
                {
                    rejectedInserts.Add(item.Id);
                    continue;
                }

                var customerHex = CanonicalHex(item.CustomerId);
                // v2 (§4.2, §3.1): yerelde kalan 048 alanları ve yedek bağı öğeden.
                var order = item.ToNewOrder(licenseId, customerHex, v2, now);
                // DM3 (her biçimde): devredilmiş oturuma sonradan gelen ekleme (force-end sonrası satış, geç
                // gönderim, yedek) devir anını kopyalar ve hemen akışa girer; devir onu bir daha damgalamaz.
                order.SessionReleasedAt = session?.ReleasedAt;
                // Gönderen kopya kargo bağının bilindiğini söyler (M22); v1 satırında boş kalır.
                order.OriginReplicaId = v2 ? caller : null;
                // K4 eklemede: mod Off'ta v2'nin bağı, mod On'da yalnız açılış penceresindeki kopyanınki; aksi hâlde
                // bağ yazılmaz ve satır yok sayılmış döner (istemci sunucunun boş bağını uygular).
                var insert = OrderUnitMerge.MergeInsert(PushOf(item), new OrderMergeContext
                {
                    Format = format,
                    ModeOn = modeOn,
                    OpeningWindow = context.OpeningWindow,
                });
                order.ShipmentId = insert.Merged.ShipmentId;
                merges[item.Id] = insert;
                if (modeOn)
                    affectedShipments.AddRange(ShipmentAmountDeriver.AffectedBy(
                        null, order.ShipmentId, insert.ChangedUnits));
                if (OrderTextScrub.AppliesOnInsert(
                        _salesSync.ScrubOrderTextOnPurge, purgeDirectory.Resolve(item.CustomerId).Canonical?.PurgedAt))
                    OrderTextScrub.Apply(order, now);
                _db.Orders.Add(order);
                rows.Add(order);
                // Sadece basılmış (printed), iptal değil, kargo ücreti değil,
                // backup değil order'ları gerçek satış olarak say. Chat akışı
                // üst limit basma operasyonu — sayım yeterli, push'ı spam'lemez.
                if (item.PrintedAt is not null
                    && item.CancelledAt is null
                    && !item.IsShippingFee
                    && !item.IsTentativeBackup)
                {
                    newPrintedOrders.Add(item.Price);
                    if (!string.IsNullOrWhiteSpace(customerHex))
                        newOrdersForShopperPush.Add((customerHex, item.Price));
                }
            }
        }

        // K7 (mod On): kabul edilen fiyat, iptal, yedek ve bağ değişikliği ilgili türetilmiş dosyaları aynı
        // SaveChanges'te yeniden türetir — izlenen birleşik siparişlerden, bağ değiştiyse eski ve yeni dosya.
        var derivedShipments = modeOn
            ? await _shipmentAmounts.DeriveAsync(licenseId, shipments, affectedShipments, rows, now, ct)
            : Array.Empty<Guid>();

        // Katalog bilmeyen istemcinin paketi deftere HİÇ girmez: siparişler her
        // zamanki gibi kaydediliyor, yalnız mutabakat atlanıyor. Ayrım şart,
        // çünkü mutabakat "gönderilmeyen anahtar = artık geçersiz" varsayıyor
        // ve bu varsayım yalnız katalog bilen istemci için doğru.
        if (req.CatalogAware)
        {
            // Defter, siparişlerle AYNI SaveChanges'te yazılır: "sipariş kaydedildi
            // ama stok düşmedi" ara durumu hiç oluşmasın.
            //
            // Defter satırlarının damgası WPF çekme imlecinin sütunu (CreatedAt), bu
            // yüzden yukarıdaki `now` yerine burada TAZE okunuyor: okuma ile commit
            // arasındaki pencere ne kadar darsa imlecin gerisine düşme riski o kadar
            // küçülür. Asıl güvence yine de çekme ucundaki kararlılık ufku; bu sadece
            // ikinci katman. Sipariş alanları `now`'u kullanmaya devam ediyor —
            // onların damgası imleç sütunu değil.
            var ledgerNow = DateTimeOffset.UtcNow;

            // Birleştirilmiş satırdan (spec §4.2): güncellemede yazılmayan ürün
            // bağı, yok sayılan iptal ya da yedek deftere paketteki hâliyle
            // girseydi defter satırın gerçek durumundan ayrışır, satışı geri
            // sarardı.
            await _ledger.ApplyAsync(
                licenseId,
                rows.Select(o => new Services.Stock.LedgerOrderInput(
                    new Services.Stock.LedgerOrderState(
                        o.Id,
                        o.ProductId,
                        o.ProductVariantId,
                        o.IsShippingFee,
                        o.CancelledAt is not null,
                        o.IsTentativeBackup),
                    o.AddedAt,
                    o.CancelledAt)).ToList(),
                ledgerNow,
                ct);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Bu paketteki bir sipariş, biz okuduktan sonra başka bir istek
            // tarafından yazılmış (istemci zaman aşımı sanıp aynı partiyi
            // yeniden yollamış olabilir). Jeton yazmayı durdurdu ve İŞLEM
            // TÜMÜYLE geri sarıldı — yarım uygulanmış paket yok.
            //
            // Kaybedene "tamam" demek en tehlikeli cevap olurdu: hem stok
            // hareketi yazılmadı hem de WPF satırları senkronlandı diye
            // işaretleyip bir daha hiç göndermez. 409 diyoruz; WPF'in outbox'ı
            // hata gören partiyi işaretlemediği için bir sonraki turda aynen
            // yeniden gönderiyor. O turda defter güncel toplamı okur ve fark
            // üretmez — tekrar zararsız, sonuç yakınsıyor.
            _log.LogInformation(
                "Order sync çakışması, istemci yeniden denemeli (license={LicenseId}, count={Count})",
                licenseId, orders.Count);
            return Problem(
                title: "sync-conflict",
                detail: "Bu siparişler eşzamanlı başka bir istekle yazıldı; paketi yeniden gönderin.",
                statusCode: 409);
        }

        // Gözlem (§10.5): yalnız sayı ve lisans; ad ya da metin yazılmaz.
        var ignoredRows = merges.Values.Count(m => m.Status == OrderMergeStatus.Ignored);
        if (ignoredRows > 0)
            _log.LogInformation(
                "orders/sync: {Count} siparişte birim yok sayıldı (license={LicenseId})",
                ignoredRows, licenseId);
        var k5Rejected = merges.Values.Count(m => m.Ignored.Any(i => i.Reason == OrderUnitMerge.ReasonSessionReleased));
        var k6Rejected = merges.Values.Count(m => m.Ignored.Any(i => i.Reason == OrderUnitMerge.ReasonShipmentShipped));
        if (k5Rejected > 0 || k6Rejected > 0 || k5ExceptionAccepted > 0)
            _log.LogInformation(
                "orders/sync: devredilmiş oturumda {K5Rejected} satır reddedildi (K5), kargolanmış dosyada {K6Rejected} "
                + "fiyat reddedildi (K6), K5 istisnasıyla {K5Exception} satırda birim alındı (license={LicenseId}, "
                + "protocol={Protocol})",
                k5Rejected, k6Rejected, k5ExceptionAccepted, licenseId, envelope.Protocol);
        if (derivedShipments.Count > 0)
            _log.LogInformation(
                "orders/sync: {Count} kargo dosyası yeniden türetildi (K7, license={LicenseId})",
                derivedShipments.Count, licenseId);
        if (rejectedInserts.Count > 0)
            _log.LogInformation(
                "orders/sync: başka kopyanın canlı yayınına {Count} ekleme reddedildi (license={LicenseId})",
                rejectedInserts.Count, licenseId);

        // Etiket olayı, push bildiriminin kullandığı listeyi paylaşıyor:
        // "yeni + basılmış + iptal değil + kargo ücreti değil + geçici yedek
        // değil". İkinci bir gerçek-satış tanımı yazmıyoruz ki ikisi zamanla
        // ayrışmasın.
        await _labels.TryApplyAndSaveByWpfCustomersAsync(
            licenseId,
            WaLabelEvent.OrderReceived,
            newOrdersForShopperPush.Select(o => o.CustomerIdHex).ToList(),
            ct);

        if (newPrintedOrders.Count > 0)
        {
            try
            {
                var (title, body) = BuildOrderNotification(newPrintedOrders);
                await _push.SendToCustomerAsync(
                    customerId, title, body,
                    new Dictionary<string, string>
                    {
                        ["type"] = "orders",
                        ["licenseId"] = licenseId.ToString(),
                        ["count"] = newPrintedOrders.Count.ToString()
                    }, ct);

                // Shopper push (Faz 4c-3): her shopper'a kendi siparişleri için
                // bildirim. Customer.Id (hex N format) ↔ ShopperBroadcasterLink
                // .WpfCustomerId üzerinden eşle. Bildirimi açık + silinmemiş
                // shopper'lara gönder.
                await SendShopperOrderPushesAsync(licenseId, newOrdersForShopperPush, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Push send failed for new orders (license={LicenseId}, count={Count})",
                    licenseId, newPrintedOrders.Count);
            }
        }

        if (v2)
            return Ok(await V2ResponseAsync(licenseId, orders, merges, rejectedInserts, ct));

        var echoed = await _db.Orders
            .Where(o => o.LicenseId == licenseId && ids.Contains(o.Id))
            .Select(o => new SyncedOrderDto(
                o.Id, o.SessionId, o.CustomerId,
                o.Platform, o.Username, o.DisplayName,
                o.MessageText, o.Code, o.Price,
                o.AddedAt, o.PrintedAt, o.CancelledAt, o.CancelReason,
                o.IsShippingFee, o.IsBackupPromoted, o.IsTentativeBackup,
                o.ProductId, o.ProductVariantId,
                o.UpdatedAt))
            .ToListAsync(ct);

        return Ok(echoed);
    }

    /// <summary>Yayının birim kararına giren hâli (sahiplik ve devir).</summary>
    private sealed record SessionFacts(
        Guid Id, DateTimeOffset? EndedAt, DateTimeOffset? ReleasedAt, Guid? OwnerReplicaId, string? ForceEndedBy);

    /// <summary>Oturumun hâli (spec §2 tablosu).</summary>
    private static OrderSessionPhase PhaseOf(SessionFacts? session) => session switch
    {
        null => OrderSessionPhase.None,
        { ReleasedAt: not null } => OrderSessionPhase.Released,
        { EndedAt: not null } => OrderSessionPhase.HandingOver,
        _ => OrderSessionPhase.Live,
    };

    /// <summary>
    /// <c>format</c> alanı (§4.2): yok ya da 1 → v1, 2 → v2. v2 biçimi, kaynak kopyayı ve satır başına kararları
    /// çağırana bağladığı için v2 zarfı ister; zarfsız v2 satırı sessizce v1 sayılmaz (§1 amaç 5).
    /// </summary>
    private static bool TryReadFormat(
        int? raw, SalesEnvelope envelope, out OrderPushFormat format, out string? error)
    {
        format = OrderPushFormat.V1;
        error = null;
        switch (raw)
        {
            case null or 1:
                return true;
            case 2 when envelope.IsV2:
                format = OrderPushFormat.V2;
                return true;
            case 2:
                error = "format 2 v2 zarfı (replicaId, protocol = 2, seenMode) ister.";
                return false;
            default:
                error = $"Desteklenmeyen biçim: {raw}.";
                return false;
        }
    }

    /// <summary>
    /// v2 yanıtı (§4.2): istekteki sırayla öğe başına satırın yazımdan sonraki hâli. <c>current</c> yalnız yok
    /// sayılan ve reddedilen satırda, akış öğesi biçiminde (<see cref="SalesOrderItem.From"/>: asıl kaydın Id'si ve
    /// KVKK okuma maskesi akıştakiyle aynı).
    /// </summary>
    private async Task<List<SyncedOrderV2Dto>> V2ResponseAsync(
        Guid licenseId,
        IReadOnlyList<SyncOrderItem> orders,
        IReadOnlyDictionary<Guid, OrderMergeResult> merges,
        IReadOnlySet<Guid> rejectedInserts,
        CancellationToken ct)
    {
        var ids = orders.Select(o => o.Id).ToList();
        var saved = await _db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId && ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, ct);
        var withCurrent = merges.Where(m => m.Value.Status != OrderMergeStatus.Applied).Select(m => m.Key).ToList();
        var customers = withCurrent.Count == 0
            ? SalesCustomerDirectory.Empty
            : await SalesCustomerDirectory.LoadAsync(
                _db, licenseId, withCurrent.Select(id => saved[id].CustomerId), ct);

        return orders.Select(item =>
        {
            if (rejectedInserts.Contains(item.Id))
                return new SyncedOrderV2Dto(
                    item.Id, null, null, null, StatusRejected, Array.Empty<string>(), ReasonOwnedElsewhere, null);

            var row = saved[item.Id];
            var merge = merges.GetValueOrDefault(item.Id);
            if (merge is null || merge.Status == OrderMergeStatus.Applied)
                return new SyncedOrderV2Dto(
                    row.Id, row.SyncVersion, row.ShipmentId, row.SessionReleasedAt, StatusApplied,
                    Array.Empty<string>(), null, null);

            return new SyncedOrderV2Dto(
                row.Id, row.SyncVersion, row.ShipmentId, row.SessionReleasedAt,
                merge.Status == OrderMergeStatus.Rejected ? StatusRejected : StatusIgnored,
                merge.Ignored.Select(i => i.Unit.WireName()).Distinct().ToList(),
                merge.Reason,
                SalesOrderItem.From(row, customers.Resolve(row.CustomerId)));
        }).ToList();
    }

    /// <summary>Öğenin birim kuralına giren hâli (<see cref="SyncOrderItem.ToUnitValues"/>) ve
    /// <c>baseVersion</c>.</summary>
    private static OrderPush PushOf(SyncOrderItem item) => new()
    {
        Values = item.ToUnitValues(),
        BaseVersion = item.BaseVersion,
    };

    /// <summary>
    /// Order batch notification metni. Spam'i azaltmak için her zaman
    /// "N yeni sipariş — X TL" özet formatı kullanılır (tek order olsa bile).
    /// </summary>
    public static (string Title, string Body) BuildOrderNotification(
        IReadOnlyList<decimal> prices)
    {
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var total = prices.Sum();
        return ("Yeni sipariş",
            $"{prices.Count} yeni sipariş, toplam {total.ToString("N2", tr)} ₺");
    }

    /// <summary>
    /// Her shopper için kendi yeni siparişlerinin özetini push'lar.
    /// Order.CustomerId (hex N GUID) ↔ ShopperBroadcasterLink.WpfCustomerId
    /// üzerinden eşleşir. Yayıncı adı title'da görünür (shopper birden çok
    /// yayıncı takip ediyor olabilir, hangisinden geldiğini anlasın).
    /// </summary>
    private async Task SendShopperOrderPushesAsync(
        Guid licenseId,
        IReadOnlyList<(string CustomerIdHex, decimal Price)> newOrders,
        CancellationToken ct)
    {
        if (newOrders.Count == 0) return;

        // Hex-N → Guid; geçersizleri at.
        var customerGuids = newOrders
            .Select(o => Guid.TryParseExact(o.CustomerIdHex, "N", out var g)
                ? (Guid?)g : null)
            .Where(g => g.HasValue).Select(g => g!.Value)
            .Distinct().ToList();
        if (customerGuids.Count == 0) return;

        // (WpfCustomerId → ShopperId) eşleşmeleri — sadece aktif link,
        // bildirimi açık, silinmemiş shopper'lar.
        var matches = await _db.ShopperBroadcasterLinks
            .Where(l => l.LicenseId == licenseId
                && l.LeftAt == null
                && l.WpfCustomerId != null
                && customerGuids.Contains(l.WpfCustomerId!.Value))
            .Join(_db.Shoppers,
                l => l.ShopperId,
                s => s.Id,
                (l, s) => new
                {
                    WpfCustomerId = l.WpfCustomerId!.Value,
                    ShopperId = s.Id,
                    s.DeletedAt,
                    s.NotificationsEnabledOrders
                })
            .Where(x => x.DeletedAt == null && x.NotificationsEnabledOrders)
            .Select(x => new { x.WpfCustomerId, x.ShopperId })
            .ToListAsync(ct);
        if (matches.Count == 0) return;

        var broadcasterName = await _db.Licenses
            .Where(l => l.Id == licenseId)
            .Select(l => l.Customer.Name)
            .FirstOrDefaultAsync(ct) ?? "Yayıncı";

        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

        // Her shopper için kendi order'larını topla.
        // Bir WpfCustomerId birden fazla shopper'a bağlı olabilir (aynı
        // hesap birden fazla cihazda) — her birine ayrı push düşer.
        foreach (var group in matches.GroupBy(m => m.WpfCustomerId))
        {
            var hexId = group.Key.ToString("N");
            var ordersForThis = newOrders
                .Where(o => string.Equals(o.CustomerIdHex, hexId, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Price)
                .ToList();
            if (ordersForThis.Count == 0) continue;

            var total = ordersForThis.Sum();
            var body = ordersForThis.Count == 1
                ? $"{total.ToString("N2", tr)} ₺ tutarında yeni siparişin var"
                : $"{ordersForThis.Count} yeni siparişin var, toplam {total.ToString("N2", tr)} ₺";

            var shopperIds = group.Select(g => g.ShopperId).ToList();
            await _push.SendToShoppersAsync(
                shopperIds,
                title: broadcasterName,
                body: body,
                data: new Dictionary<string, string>
                {
                    ["type"] = "order",
                    ["licenseId"] = licenseId.ToString(),
                    ["count"] = ordersForThis.Count.ToString()
                },
                ct: ct);
        }
    }

}
