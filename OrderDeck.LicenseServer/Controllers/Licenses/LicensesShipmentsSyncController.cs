using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App ↔ LicenseServer Shipment senkronizasyonu (Kümülatif kargo PR-D).
/// Pattern: <see cref="LicensesPaymentsSyncController"/> (Payment sync) ile
/// paralel.
///
/// <list type="bullet">
///   <item>POST .../shipments/sync — outbox push (WPF → server upsert)</item>
///   <item>GET  .../shipments/since?since=... — reverse sync (server → WPF pull)</item>
/// </list>
///
/// Auth: Bearer-Customer + route'taki licenseId yetkili customer'a ait olmalı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/shipments")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesShipmentsSyncController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly LabelRuleApplier _labels;
    private readonly CustomerIdResolver _customerIds;
    private readonly SalesReplicaService _replicas;
    private readonly ShipmentAmountDeriver _shipmentAmounts;
    private readonly ILogger<LicensesShipmentsSyncController> _log;

    public LicensesShipmentsSyncController(
        LicenseDbContext db, LabelRuleApplier labels, CustomerIdResolver customerIds,
        SalesReplicaService replicas, ShipmentAmountDeriver shipmentAmounts,
        ILogger<LicensesShipmentsSyncController> log)
    {
        _db = db;
        _labels = labels;
        _customerIds = customerIds;
        _replicas = replicas;
        _shipmentAmounts = shipmentAmounts;
        _log = log;
    }

    public sealed record SyncShipmentItem(
        Guid Id,
        string CustomerId,
        string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt,
        DateTimeOffset? ShippedAt);

    /// <param name="ReplicaId">v2 zarfı (spec 2026-10-13 §4.0); <see cref="ISalesEnvelopeFields"/>.</param>
    public sealed record SyncRequest(
        List<SyncShipmentItem> Shipments,
        Guid? ReplicaId = null,
        int? Protocol = null,
        int? SeenMode = null) : ISalesEnvelopeFields;

    public sealed record SyncedShipmentDto(
        Guid Id,
        string CustomerId,
        string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt,
        DateTimeOffset? ShippedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// Batch upsert from WPF outbox. WPF authoritative — server field'ları
    /// kabul eder; tek istisna kargoya verilmiş dosya (Shipped tek yönlü, spec
    /// 2026-10-13 §7.1). Mobile Panel sadece okur (Payment.Approve gibi mutation
    /// endpoint'i yok, çünkü Shipment kararını WPF veriyor).
    ///
    /// <para>Satış senkronu (spec 2026-10-13 §3.1, §7.1): sunucunun her dosya
    /// yazımı <see cref="Shipment.Version"/>'ı artırır; sürüm jetondur, eşzamanlı
    /// yazım (başka bir parti, K7 türetmesi) 409 <c>sync-conflict</c> alır. v2
    /// isteği boş <see cref="Shipment.OriginReplicaId"/>'yi çağıranla doldurur
    /// (mod Off'ta dosyanın tek yazıcısı) ve dosya başına sunucunun güncel
    /// satırını alır (<see cref="SalesShipmentItem"/>); v1 yanıtı değişmez.</para>
    ///
    /// <para><b>Mod On</b> (spec 2026-10-13 §7.1, §8.3): dosya ve etiket–dosya
    /// bağı yalnız sunucuda yazılır; gönderim hiçbir şey yazmaz (ne güncelleme
    /// ne ekleme) ve yanıt sunucunun güncel satırlarıdır — v1'e bugünkü DTO,
    /// v2'ye akış öğesi. Protokolsüz isteğin izini kalp atışı zaten yazdı
    /// (<see cref="SalesReplicaService.AcceptAsync"/>); yok sayılan sayı günlüğe
    /// düşer. Tek istisna açılış penceresindeki kopyanın v2 gönderimidir
    /// (§4.0, §8.4): kararları alınır, tutar bağlardan türetilir
    /// (<see cref="SyncOpeningWindowAsync"/>).</para>
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(
        Guid licenseId,
        [FromBody] SyncRequest req,
        CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (!SalesEnvelope.TryRead(req, out var envelope, out var envelopeError))
            return Problem(title: "invalid-envelope", detail: envelopeError, statusCode: 400);
        var context = await _replicas.AcceptAsync(
            licenseId, envelope, Request.Headers.UserAgent.ToString(), "shipments/sync", ct);
        var v2 = envelope.IsV2;

        if (req?.Shipments is null || req.Shipments.Count == 0)
            return v2 ? Ok(Array.Empty<SalesShipmentItem>()) : Ok(Array.Empty<SyncedShipmentDto>());

        if (req.Shipments.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 shipment per batch.", statusCode: 400);

        if (context.Mode == SalesSyncMode.On)
            return context.OpeningWindow
                ? await SyncOpeningWindowAsync(licenseId, req.Shipments, envelope.ReplicaId, ct)
                : await IgnoreInModeOnAsync(licenseId, req.Shipments, envelope, ct);

        var now = DateTimeOffset.UtcNow;

        // Durumu GERÇEKTEN değişen kargoların müşterileri. WPF outbox aynı
        // satırı değişmeden tekrar gönderebiliyor; "her sync = olay" deseydik
        // sohbet her turda yeniden etiketlenmeye çalışılırdı.
        var statusChangedCustomers = new List<string>();
        var ignoredShipped = 0;

        var ids = req.Shipments.Select(s => s.Id).ToList();
        var existing = await _db.Shipments
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        // Eski sürüm birleştirmeden sonra da kopyanın Id'sini gönderir (A5b):
        // ekleme de güncelleme de asıl kaydın hex'ini yazar. Güncelleme
        // CustomerId'yi her gönderimde yeniden yazıyor — çözülmeseydi
        // birleştiricinin asıl kayda taşıdığı kargo bir sonraki durum
        // değişikliğinde kopyaya geri dönerdi. Paket tek çağrıda çözülür.
        // CustomerId gövdede zorunlu (null → 400, model doğrulaması); Guid
        // olmayan değer aynen döner.
        var canonicalHex = await _customerIds.CanonicalHexOfAsync(
            licenseId, req.Shipments.Select(s => s.CustomerId), ct);
        string CanonicalHex(string hex) => canonicalHex.GetValueOrDefault(hex, hex);

        foreach (var item in req.Shipments)
        {
            var status = ParseStatus(item.Status);
            var customerHex = CanonicalHex(item.CustomerId);

            if (existing.TryGetValue(item.Id, out var current))
            {
                // Shipped tek yönlü (spec 2026-10-13 §7.1, M19; her kipte, v1
                // dahil): kargoya verilmiş dosyanın kararı ve tutarı donmuştur.
                // Yerelde de Shipped son durum; buraya gelen farklı bir değer
                // ancak bayat bir yerel aynadır ve dosyayı yeniden açıp yeni
                // etiketi ona bağlatırdı. Yalnız CustomerId kanonikleştirmesi
                // sürer. Durum değişmediği için olay yok; yanıt sunucunun
                // satırını döner.
                if (current.Status == ShipmentStatus.Shipped)
                {
                    if (status != current.Status
                        || item.CumulativeAmount != current.CumulativeAmount
                        || item.HeldAt != current.HeldAt
                        || item.ShippedAt != current.ShippedAt)
                        ignoredShipped++;
                    var wrote = false;
                    if (current.CustomerId != customerHex)
                    {
                        current.CustomerId = customerHex;
                        wrote = true;
                    }
                    if (v2 && current.OriginReplicaId is null)
                    {
                        current.OriginReplicaId = envelope.ReplicaId;
                        wrote = true;
                    }
                    // Değişmeyen satır yazılmaz: sürüm ve akış imleci boşuna
                    // ilerlemesin (Ws Shipped dosyayı yeniden gönderebilir).
                    if (wrote)
                    {
                        current.UpdatedAt = now;
                        current.Version++;
                    }
                    continue;
                }

                if (current.Status != status && !string.IsNullOrWhiteSpace(customerHex))
                    statusChangedCustomers.Add(customerHex);

                // WPF authoritative — tüm mutable alanları update
                current.CustomerId = customerHex;
                current.Status = status;
                current.CumulativeAmount = item.CumulativeAmount;
                current.HeldAt = item.HeldAt;
                current.ShippedAt = item.ShippedAt;
                current.UpdatedAt = now;
                // CreatedAt değişmez (WPF tarafında oluşturulduğu an)
                // Dosya kaynağı yalnız boşsa dolar: dolu değeri disable turu
                // (§4.10) bağlı etiketlerin sahibine göre yazar, gönderen değil.
                if (v2) current.OriginReplicaId ??= envelope.ReplicaId;
                current.Version++;
            }
            else
            {
                // Yeni dosya "Pending" ile açılıyor — bu bir karar değil, yalnız
                // kaydın doğuşu. Yayıncı beklet/alıcı ödemeli/kargolandı dediyse
                // ilk sync'te bile olay sayılır.
                if (status != ShipmentStatus.Pending && !string.IsNullOrWhiteSpace(customerHex))
                    statusChangedCustomers.Add(customerHex);

                _db.Shipments.Add(new Shipment
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    CustomerId = customerHex,
                    Status = status,
                    CumulativeAmount = item.CumulativeAmount,
                    CreatedAt = item.CreatedAt,
                    HeldAt = item.HeldAt,
                    ShippedAt = item.ShippedAt,
                    UpdatedAt = now,
                    // v2: dosyanın mod Off'taki tek yazıcısı (§3.1); v1'de kaynak
                    // bilinmez kalır.
                    OriginReplicaId = v2 ? envelope.ReplicaId : null,
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Sürüm jetonu (spec 2026-10-13 §3.1): dosyayı biz okuduktan sonra
            // başka bir yazım (eşzamanlı parti, K7 türetmesi) yazdı. İşlem
            // tümüyle geri sarıldı; 409 diyoruz, WPF'in outbox'ı partiyi
            // işaretlemediği için bir sonraki turda yeniden gönderir (sipariş
            // partisinin deseni).
            _log.LogInformation(
                "Shipment sync çakışması, istemci yeniden denemeli (license={LicenseId}, count={Count})",
                licenseId, req.Shipments.Count);
            return Problem(
                title: "sync-conflict",
                detail: "Bu kargo dosyaları eşzamanlı başka bir istekle yazıldı; paketi yeniden gönderin.",
                statusCode: 409);
        }

        if (ignoredShipped > 0)
            _log.LogInformation(
                "shipments/sync: kargolanmış {Count} dosyaya gelen değişiklik yok sayıldı (license={LicenseId})",
                ignoredShipped, licenseId);

        await _labels.TryApplyAndSaveByWpfCustomersAsync(
            licenseId, WaLabelEvent.ShipmentStatusChanged, statusChangedCustomers, ct);

        return await CurrentRowsAsync(licenseId, ids, v2, ct);
    }

    /// <summary>Açılış penceresinde alınan bir dosya kararı.</summary>
    /// <param name="Inserted">Dosya bu gönderimle açıldı.</param>
    private sealed record WindowDecision(
        Shipment Shipment, ShipmentStatus Status, DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt, bool Inserted);

    /// <summary>
    /// Açılış penceresindeki kopyanın v2 gönderimi — spec 2026-10-13 §7.1, §4.0, §8.4, M16. Mod açıldıktan sonra
    /// modu henüz görmemiş kopyanın kuyruğunda Off kurallarıyla verilmiş güncel kararlar vardır; kaybolmasınlar diye:
    /// <list type="bullet">
    ///   <item><b>Var olan dosya</b>, Shipped değilse ve <c>CommandedAt</c> boşsa, karar alanlarını (Status, HeldAt,
    ///   ShippedAt) gönderimden alır. Shipped dosya tek yönlüdür (S1); komutun dokunduğu dosya korunur — pencere modu
    ///   görmeden verilmiş kararı kurtarır, sonradan verilmiş sunucu kararını geri almaz. Karar alanları aynıysa yazım
    ///   yok. Kaynak ve müşteri değişmez (mod On'da dosya sunucunundur).</item>
    ///   <item><b>Yeni dosya Id'si</b> eklenir: asıl kayda çözülmüş müşteri, <c>OriginReplicaId</c> = çağıran,
    ///   <c>AmountDerivedAt</c> = şimdi.</item>
    ///   <item><b>Tutar</b> hiçbir zaman gönderimden alınmaz: türetilmiş dosya bağlı siparişlerden yeniden türetilir
    ///   (DM7; yeni dosyada çoğu zaman 0 — kargo gönderimi etiket gönderiminden sık koştuğu için dosya bağlarından önce
    ///   gelir, bağlar sonra <c>orders/sync</c>'in K4 istisnasıyla gelince K7 dosyayı yeniden türetir; burada kargoya
    ///   verilen dosyayı da aynı kopyanın geç bağı bir kez daha türetir,
    ///   <see cref="ShipmentAmountDeriver.DeriveShippedInWindowAsync"/>); kabul edilmiş (türetilmemiş) dosya saklanan
    ///   tutarıyla kalır.</item>
    ///   <item><b>Kargoya geçişte</b> (§7.2) türetilmiş dosyada o anda iptal edilmiş bağlı siparişlerin bağı çözülür
    ///   (<see cref="ShipmentShipping.UnlinkCancelled"/>, <c>shipment-decision</c> ile aynı kural; kabul edilmiş dosyada
    ///   bağ kalır), sonra dosya türetilir ve tutarı donar.</item>
    /// </list>
    /// Sürüm jetonu ve 409 <c>sync-conflict</c> Off yolundaki gibidir. Durum gerçekten değiştiyse etiket kuralı
    /// tetiklenir (yeni dosyada Pending dışında doğduysa). Yanıt güncel satırlardır.
    /// </summary>
    private async Task<IActionResult> SyncOpeningWindowAsync(
        Guid licenseId, IReadOnlyList<SyncShipmentItem> items, Guid caller, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = items.Select(s => s.Id).ToList();

        // Dosyalar izlenerek ve bağlı siparişlerden ÖNCE (ShipmentAmountDeriver'ın sıra ve jeton gerekçesi).
        var shipments = await _shipmentAmounts.LoadAsync(licenseId, ids, ct);

        // Yeni dosyanın müşterisi asıl kayda çözülür (A5b); etiket kuralı var olan dosyanın müşterisinin asıl kaydına
        // bakar (shipment-decision ile eşlik). Tek çağrı.
        var canonicalHex = await _customerIds.CanonicalHexOfAsync(
            licenseId, items.Select(s => s.CustomerId).Concat(shipments.Values.Select(s => s.CustomerId)), ct);
        string CanonicalHex(string hex) => canonicalHex.GetValueOrDefault(hex, hex);

        var decisions = new List<WindowDecision>();
        var statusChangedCustomers = new List<string>();
        var ignoredShipped = 0;
        var ignoredCommanded = 0;

        // Aynı Id iki kez gelirse son giriş kazanır (istemcinin niyet sırası); yeni dosya iki kez eklenmez.
        foreach (var item in items.GroupBy(i => i.Id).Select(g => g.Last()))
        {
            var status = ParseStatus(item.Status);
            if (shipments.TryGetValue(item.Id, out var current))
            {
                if (current.Status == ShipmentStatus.Shipped)
                {
                    if (Differs(item, current)) ignoredShipped++;
                    continue;
                }
                if (current.CommandedAt is not null)
                {
                    if (Differs(item, current)) ignoredCommanded++;
                    continue;
                }
                if (status == current.Status && item.HeldAt == current.HeldAt && item.ShippedAt == current.ShippedAt)
                    continue;

                var customerHex = CanonicalHex(current.CustomerId);
                if (status != current.Status && !string.IsNullOrWhiteSpace(customerHex))
                    statusChangedCustomers.Add(customerHex);
                decisions.Add(new WindowDecision(current, status, item.HeldAt, item.ShippedAt, Inserted: false));
            }
            else
            {
                var customerHex = CanonicalHex(item.CustomerId);
                var shipment = new Shipment
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    CustomerId = customerHex,
                    // Karar türetmeden SONRA yazılır: kargoya verilmiş doğan dosya da önce türetilir, sonra donar.
                    Status = ShipmentStatus.Pending,
                    CumulativeAmount = 0m,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = now,
                    AmountDerivedAt = now,
                    OriginReplicaId = caller,
                };
                _db.Shipments.Add(shipment);
                shipments[item.Id] = shipment;
                if (status != ShipmentStatus.Pending && !string.IsNullOrWhiteSpace(customerHex))
                    statusChangedCustomers.Add(customerHex);
                decisions.Add(new WindowDecision(shipment, status, item.HeldAt, item.ShippedAt, Inserted: true));
            }
        }

        // Kargoya geçiş (§7.2): türetilmiş dosyada o anda iptal edilmiş bağlar çözülür (kabul edilmiş dosyada bağ kalır);
        // bağ değişikliği siparişin bir yazımıdır (jeton).
        IReadOnlyList<Order> unlinked = Array.Empty<Order>();
        var shipping = decisions.Where(d => d.Status == ShipmentStatus.Shipped).Select(d => d.Shipment).ToList();
        if (shipping.Count > 0)
        {
            var shippingIds = shipping.Select(s => s.Id).ToList();
            var linked = await _db.Orders
                .Where(o => o.LicenseId == licenseId && o.ShipmentId != null && shippingIds.Contains(o.ShipmentId.Value))
                .ToListAsync(ct);
            unlinked = ShipmentShipping.UnlinkCancelled(linked, shipping, now);
            foreach (var order in unlinked)
            {
                order.UpdatedAt = now;
                order.SyncVersion++;
            }
        }

        // Tutar bağlardan (DM7), karar yazılmadan ÖNCE: Shipped dosya türetilmez, kargoya verilecek dosyanın tutarı
        // bağı çözülen iptalliler olmadan şimdi donar. Kabul edilmiş dosyayı türetici atlar.
        var derived = (await _shipmentAmounts.DeriveAsync(
            licenseId, shipments, decisions.Select(d => d.Shipment.Id), unlinked, now, ct)).ToHashSet();

        foreach (var decision in decisions)
        {
            var shipment = decision.Shipment;
            shipment.Status = decision.Status;
            shipment.HeldAt = decision.HeldAt;
            shipment.ShippedAt = decision.ShippedAt;
            shipment.UpdatedAt = now;
            // Sürüm yazım başına bir kez: türetme ilerlettiyse yeniden artmaz.
            if (!decision.Inserted && !derived.Contains(shipment.Id)) shipment.Version++;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Off yolundaki gerekçe: dosya ya da bağı çözülen sipariş, biz okuduktan sonra başka bir yazımla değişti;
            // işlem tümüyle geri sarıldı, istemci partiyi yeniden gönderir.
            _log.LogInformation(
                "Shipment sync çakışması (açılış penceresi), istemci yeniden denemeli (license={LicenseId}, "
                + "count={Count})",
                licenseId, items.Count);
            return Problem(
                title: "sync-conflict",
                detail: "Bu kargo dosyaları eşzamanlı başka bir istekle yazıldı; paketi yeniden gönderin.",
                statusCode: 409);
        }

        // Gözlem (§10.5): SeenMode = 0 ile alınan kargo gönderimleri; yalnız sayı ve kimlik.
        _log.LogInformation(
            "shipments/sync: açılış penceresindeki kopyanın gönderimi Off kurallarıyla alındı (SeenMode = 0) — "
            + "{Decided} dosyada karar ({Inserted} yeni dosya, {Derived} türetildi), {Unlinked} iptalli bağ çözüldü; "
            + "{Commanded} komutlu ve {Shipped} kargolanmış dosyaya dokunulmadı (license={LicenseId}, "
            + "replica={ReplicaId})",
            decisions.Count, decisions.Count(d => d.Inserted), derived.Count, unlinked.Count, ignoredCommanded,
            ignoredShipped, licenseId, caller);

        await _labels.TryApplyAndSaveByWpfCustomersAsync(
            licenseId, WaLabelEvent.ShipmentStatusChanged, statusChangedCustomers, ct);

        return await CurrentRowsAsync(licenseId, ids, v2: true, ct);
    }

    /// <summary>
    /// Mod On'da açılış penceresi dışındaki gönderim (spec 2026-10-13 §7.1, §8.3, §4.0): protokolsüz istek (eski
    /// sürüm), modu görmüş ya da mod açıldıktan sonra doğmuş kopya, yedekten geri yüklenmiş kopya (§8.7) ve disk klonu.
    /// Hiçbir şey yazılmaz — Shipped olmayan dosyanın kararı ve tutarı da, kaynağı ve müşterisi de, yeni dosya Id'si de.
    /// Geri yüklenmiş veritabanının kuyruğu yedek anındaki bayat değerlerdir; Off kurallarıyla alınsalar sonradan
    /// verilmiş komut kararlarını geri alırlardı. Durum değişmediği için etiket kuralı da yok. Sayılar günlüğe yazılır
    /// (§10.5; ad ya da metin yok).
    /// </summary>
    private async Task<IActionResult> IgnoreInModeOnAsync(
        Guid licenseId, IReadOnlyList<SyncShipmentItem> items, SalesEnvelope envelope, CancellationToken ct)
    {
        var ids = items.Select(s => s.Id).ToList();
        var existing = await _db.Shipments.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        var changed = items.Count(i => existing.TryGetValue(i.Id, out var s) && Differs(i, s));
        var inserts = ids.Distinct().Count(id => !existing.ContainsKey(id));

        if (envelope.IsV2)
            _log.LogInformation(
                "shipments/sync: mod On'da v2 gönderimi On kurallarıyla yargılandı (açılış penceresi dışında, "
                + "seenMode={SeenMode}) — {Count} dosya satırı yok sayıldı: {Changed} değişiklik ve {Inserts} yeni "
                + "dosya yazılmadı (license={LicenseId}, replica={ReplicaId})",
                (int)envelope.SeenMode, items.Count, changed, inserts, licenseId, envelope.ReplicaId);
        else
            _log.LogInformation(
                "shipments/sync: mod On'da protokolsüz gönderim yok sayıldı — {Count} dosya satırı: {Changed} "
                + "değişiklik ve {Inserts} yeni dosya yazılmadı (license={LicenseId})",
                items.Count, changed, inserts, licenseId);

        return await CurrentRowsAsync(licenseId, ids, envelope.IsV2, ct);
    }

    /// <summary>Gönderilen satır sunucudakinden karar ya da tutarda farklı mı?</summary>
    private static bool Differs(SyncShipmentItem item, Shipment current) =>
        ParseStatus(item.Status) != current.Status
        || item.CumulativeAmount != current.CumulativeAmount
        || item.HeldAt != current.HeldAt
        || item.ShippedAt != current.ShippedAt;

    /// <summary>
    /// Yanıt: dosyaların veritabanındaki güncel hâli. v2'ye istek sırasıyla akış öğesi (Ws onu guard altında yerel
    /// aynaya yazar, §7.1); v1'e bugünkü <see cref="SyncedShipmentDto"/> (eski istemci yanıtı atar). Sunucuda olmayan
    /// Id yanıtta yoktur.
    /// </summary>
    private async Task<IActionResult> CurrentRowsAsync(
        Guid licenseId, IReadOnlyList<Guid> ids, bool v2, CancellationToken ct)
    {
        if (v2)
        {
            var saved = await _db.Shipments.AsNoTracking()
                .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);
            return Ok(ids.Distinct().Where(saved.ContainsKey).Select(id => SalesShipmentItem.From(saved[id])).ToList());
        }

        var echoed = await _db.Shipments
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .Select(s => new SyncedShipmentDto(
                s.Id, s.CustomerId,
                s.Status.ToString().ToLowerInvariant(),
                s.CumulativeAmount,
                s.CreatedAt, s.HeldAt, s.ShippedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return Ok(echoed);
    }

    /// <summary>
    /// Reverse sync: WPF App nadiren bu endpoint'i çağırır (Shipment WPF
    /// authoritative; mobile mutation yapmıyor). Yine de eklendi — gelecekte
    /// multi-instance WPF veya CSR'ın server'da düzeltme yaptığı durumlar için.
    ///
    /// <para>İmleç bileşik, sorgu kararlılık ufkunun altında; gerekçe
    /// <see cref="ReverseSyncCursor"/>'da. İstemci bir sonraki imleci sayfanın
    /// son satırından okur — yanıt sırası <c>(UpdatedAt, Id)</c>.</para>
    /// </summary>
    [HttpGet("since")]
    public async Task<IActionResult> Since(
        Guid licenseId,
        [FromQuery] DateTimeOffset since,
        [FromQuery] Guid sinceId = default,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);
        var horizon = ReverseSyncCursor.Horizon();

        var rows = await _db.Shipments
            .Where(s => s.LicenseId == licenseId
                        && s.UpdatedAt <= horizon
                        && (s.UpdatedAt > since
                            || (s.UpdatedAt == since && s.Id.CompareTo(sinceId) > 0)))
            .OrderBy(s => s.UpdatedAt).ThenBy(s => s.Id)
            .Take(take)
            .Select(s => new SyncedShipmentDto(
                s.Id, s.CustomerId,
                s.Status.ToString().ToLowerInvariant(),
                s.CumulativeAmount,
                s.CreatedAt, s.HeldAt, s.ShippedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    private static ShipmentStatus ParseStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ShipmentStatus.Pending;
        return raw.Trim().ToLowerInvariant() switch
        {
            "pending" => ShipmentStatus.Pending,
            "held" => ShipmentStatus.Held,
            "recipientpays" => ShipmentStatus.RecipientPays,
            "shipped" => ShipmentStatus.Shipped,
            _ => ShipmentStatus.Pending
        };
    }

}
