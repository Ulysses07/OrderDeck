using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Push;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.Shared.Sales;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private readonly ILogger<LicensesSessionsSyncController> _log;

    public LicensesSessionsSyncController(
        LicenseDbContext db,
        INotificationSender push,
        Services.Stock.StockLedgerWriter ledger,
        LabelRuleApplier labels,
        CustomerIdResolver customerIds,
        SalesReplicaService replicas,
        SessionReleaser releaser,
        ILogger<LicensesSessionsSyncController> log)
    {
        _db = db;
        _push = push;
        _ledger = ledger;
        _labels = labels;
        _customerIds = customerIds;
        _replicas = replicas;
        _releaser = releaser;
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
    private const string ReasonOwnedElsewhere = "session-owned-elsewhere";
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
        // Geri yüklenmiş kopyanın devralabileceği önceki kopyalar (§8.7); gerekirse ve istek başına bir kez okunur.
        IReadOnlySet<Guid>? predecessors = null;

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
                // zaten dolu ve tekdüze. Satıra hiç yazılmaz — her yeniden gönderim (W1 devri görene kadar oturumu
                // yeniden yollar) oturumun rowversion'ını ve akışı boşuna ilerletirdi.
                if (current.ReleasedAt is not null)
                {
                    if (current.Title != item.Title || current.Platforms != item.Platforms || current.Notes != item.Notes)
                        ignored[item.Id] = ReasonReleased;
                    continue;
                }

                role = RoleOf(current);
                if (role == SessionRole.NonOwner && current.OwnerReplicaId is { } owner)
                {
                    predecessors ??= await _replicas.SilentPredecessorsAsync(
                        licenseId, caller, context.ReplicaBefore, ct);
                    if (predecessors.Contains(owner))
                    {
                        // §8.7: önceki veritabanı bu kopyanın ilk görüldüğü andan beri susuyor — yayının kuyruğu
                        // artık bu kopyada. Sahiplik geçer; çağıran sahip olarak yargılanır.
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
                // "açıyordu". Sahip olmayan v2 kopyası yayını kapatamaz; tek
                // başka yol force-end (§4.6).
                if (role != SessionRole.NonOwner)
                    current.EndedAt ??= item.EndedAt;
                else if (current.EndedAt is null && item.EndedAt is not null)
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
        Guid? ProductVariantId = null);

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
    public sealed record SyncOrdersRequest(
        List<SyncOrderItem> Orders,
        bool CatalogAware = false,
        Guid? ReplicaId = null,
        int? Protocol = null,
        int? SeenMode = null) : ISalesEnvelopeFields;

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
        await _replicas.AcceptAsync(
            licenseId, envelope, Request.Headers.UserAgent.ToString(), "orders/sync", ct);

        if (req?.Orders is null || req.Orders.Count == 0)
            return Ok(Array.Empty<SyncedOrderDto>());

        if (req.Orders.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 order per batch.", statusCode: 400);

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
        if (sessionIds.Count > 0)
        {
            var ownedSessionCount = await _db.StreamSessions
                .CountAsync(s => sessionIds.Contains(s.Id) && s.LicenseId == licenseId, ct);
            if (ownedSessionCount != sessionIds.Count)
                return Problem(
                    title: "unknown-session",
                    detail: "Pakette bu lisansa ait olmayan SessionId var.",
                    statusCode: 400);
        }

        var ids = orders.Select(o => o.Id).ToList();
        var existing = await _db.Orders
            .Where(o => o.LicenseId == licenseId && ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, ct);

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

        // Paketin satırları, birleştirildikten sonraki hâlleriyle (izlenen
        // varlıklar). Defter paketten değil bunlardan beslenir: yazılmayan bir
        // alan deftere bayat durum taşımasın (spec 2026-10-13 §4.2, M4).
        var rows = new List<Order>(orders.Count);
        var ignoredRows = 0;

        foreach (var item in orders)
        {
            if (existing.TryGetValue(item.Id, out var current))
            {
                // Birim kuralı (spec 2026-10-13 §4.2) ortak koddan, moddan
                // bağımsız:
                // - K1: oluşum alanları (SessionId, MessageText, Code,
                //   IsShippingFee, IsBackupPromoted, ProductId,
                //   ProductVariantId; CustomerId ve görünen ad zaten
                //   yazılmıyordu) yalnız eklemede yazılır. Katalog bilmeyen
                //   istemcinin ProductId'yi NULL'laması böyle kapanır.
                // - K2: komutun damgaladığı fiyat, iptal ve yedeği v1 yazamaz;
                //   damgasız birimde bugünkü son-gelen-kazanır sürer. Yedek
                //   yalnız 1'den 0'a gider.
                // - K3: PrintedAt silinmez ve kaymaz; şu anda komutla iptal
                //   edilmiş siparişe baskı alınmaz.
                // v1 yanıtı değişmez: yok sayılan birim de yanıtta sunucunun
                // satırıyla döner.
                var merge = OrderUnitMerge.Merge(
                    current.ToServerRow(),
                    PushOf(item),
                    new OrderMergeContext { Format = OrderPushFormat.V1 });
                current.ApplyUnits(merge.Merged);
                if (merge.Status == OrderMergeStatus.Ignored) ignoredRows++;
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
                var customerHex = CanonicalHex(item.CustomerId);
                var order = new Order
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    SessionId = item.SessionId,
                    CustomerId = customerHex,
                    Platform = item.Platform,
                    Username = item.Username,
                    DisplayName = item.DisplayName,
                    MessageText = item.MessageText,
                    Code = item.Code,
                    Price = item.Price,
                    AddedAt = item.AddedAt,
                    PrintedAt = item.PrintedAt,
                    CancelledAt = item.CancelledAt,
                    CancelReason = item.CancelReason,
                    IsShippingFee = item.IsShippingFee,
                    IsBackupPromoted = item.IsBackupPromoted,
                    IsTentativeBackup = item.IsTentativeBackup,
                    ProductId = item.ProductId,
                    ProductVariantId = item.ProductVariantId,
                    UpdatedAt = now
                };
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

        if (ignoredRows > 0)
            _log.LogInformation(
                "orders/sync: {Count} siparişte birim yok sayıldı (license={LicenseId})",
                ignoredRows, licenseId);

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

    /// <summary>v1 öğesinin birim kuralına giren hâli. v1'in taşımadığı alanlar
    /// (düzeltme damgası, ödenen fiyat, baskı grubu, kargo bağı, ebeveyn) boş
    /// kalır; <see cref="OrderPushFormat.V1"/> onları "gönderilmedi" sayar.</summary>
    private static OrderPush PushOf(SyncOrderItem item) => new()
    {
        Values = new OrderUnitValues
        {
            Price = item.Price,
            CancelledAt = item.CancelledAt,
            CancelReason = item.CancelReason,
            IsTentativeBackup = item.IsTentativeBackup,
            PrintedAt = item.PrintedAt,
        },
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
