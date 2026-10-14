using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.Stock;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.Shared.Sales;
using SyncOrderItem = OrderDeck.LicenseServer.Controllers.Licenses.LicensesSessionsSyncController.SyncOrderItem;
using SyncSessionItem = OrderDeck.LicenseServer.Controllers.Licenses.LicensesSessionsSyncController.SyncSessionItem;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Yayın ve etiket senkronunun kopya uçları — spec 2026-10-13 §4.5. Desen satış gönderim uçlarıyla aynı
/// (<see cref="LicensesSessionsSyncController"/>): <c>Bearer-Customer</c> + rotadaki lisans çağırana ait olmalı,
/// değilse 404.
/// <list type="bullet">
///   <item><c>POST sales/hello</c> — kopyanın kimliği, yetenek düzeyi, bildirdiği kargo ayarı ve geçiş bayrakları.</item>
///   <item><c>GET sales/state</c> — her turda (30 sn) okunan lisans durumu; <c>replicaId</c> ve <c>seenMode</c>
///   verilirse kalp atışı sayılır.</item>
///   <item><c>GET sales/changes</c> — değişiklik akışı (§4.4, <see cref="SalesChangesFeed"/>); kalp atışı state ile
///   aynı.</item>
///   <item><c>POST sales/backfill</c> — Ws'nin ilk açılışındaki geri doldurma (§4.3): yalnız boşu doldurur,
///   bildirimsiz, birim ayrışmasını döner.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/sales")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSalesController : ControllerBase
{
    /// <summary>Mod kapısının "son 14 günde görülen" penceresi (§4.10-1, -2); eski sürüm listesi de bununla.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    private const int MaxNameLength = 128;

    private readonly LicenseDbContext _db;
    private readonly SalesReplicaService _replicas;
    private readonly SalesChangesFeed _feed;
    private readonly SessionReleaser _releaser;
    private readonly ShipmentAmountDeriver _shipmentAmounts;
    private readonly StockLedgerWriter _ledger;
    private readonly CustomerIdResolver _customerIds;
    private readonly SalesSyncOptions _options;
    private readonly ILogger<LicensesSalesController> _log;

    public LicensesSalesController(
        LicenseDbContext db,
        SalesReplicaService replicas,
        SalesChangesFeed feed,
        SessionReleaser releaser,
        ShipmentAmountDeriver shipmentAmounts,
        StockLedgerWriter ledger,
        CustomerIdResolver customerIds,
        IOptions<SalesSyncOptions> options,
        ILogger<LicensesSalesController> log)
    {
        _db = db;
        _replicas = replicas;
        _feed = feed;
        _releaser = releaser;
        _shipmentAmounts = shipmentAmounts;
        _ledger = ledger;
        _customerIds = customerIds;
        _options = options.Value;
        _log = log;
    }

    // ─── hello ────────────────────────────────────────────────────────────

    public sealed record ReportedShippingDto(decimal? Threshold, decimal? Fee);

    /// <param name="PreviousReplicaId">Geri yüklemeden önceki kimlik (§8.7); bir kez yazılır.</param>
    /// <param name="FeatureLevel">Derlemenin yetenek düzeyi; mod kapısı 3 ister (§4.10-2).</param>
    /// <param name="ReportedShipping">Kopyanın yerel kargo ayarı; lisans geneli ayar yazılmadan önce yan yana
    /// gösterilir (§6.2). Verilmezse saklanan değer değişmez.</param>
    /// <param name="BackfillCompleted">Geri doldurma ve birim ayrışması onarımı bitti (§8.2-5). Yalnız
    /// <c>true</c> etkilidir; damga hiç silinmez.</param>
    /// <param name="PaymentHistoryCompleted">Ödeme geçmişi geri doldurması bitti (§8.2-6).</param>
    public sealed record HelloRequest(
        Guid? ReplicaId,
        Guid? PreviousReplicaId,
        string? MachineName,
        string? HardwareFingerprint,
        int? Protocol,
        int? FeatureLevel,
        int? SeenMode,
        ReportedShippingDto? ReportedShipping,
        bool? BackfillCompleted,
        bool? PaymentHistoryCompleted);

    [HttpPost("hello")]
    public async Task<IActionResult> Hello(Guid licenseId, [FromBody] HelloRequest req, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        // hello yalnız v2 konuşur: protokolsüz istek burada zarf hatasıdır.
        if (!SalesEnvelope.TryRead(req.ReplicaId, req.Protocol, req.SeenMode, out var envelope, out var error)
            || !envelope.IsV2)
            return Problem(title: "invalid-envelope", detail: error ?? "hello v2 isteğidir.", statusCode: 400);

        if (req.PreviousReplicaId == Guid.Empty || req.PreviousReplicaId == envelope.ReplicaId)
            return Problem(title: "invalid-previous-replica",
                detail: "previousReplicaId boş GUID ya da kopyanın kendisi olamaz.", statusCode: 400);
        if (req.MachineName is { Length: > MaxNameLength } || req.HardwareFingerprint is { Length: > MaxNameLength })
            return Problem(title: "field-too-long",
                detail: $"machineName ve hardwareFingerprint en çok {MaxNameLength} karakter.", statusCode: 400);
        if (req.FeatureLevel is not { } featureLevel || featureLevel is < 0 or > byte.MaxValue)
            return Problem(title: "invalid-feature-level", detail: "featureLevel 0–255 olmalı.", statusCode: 400);
        if (req.ReportedShipping is { } shipping && (shipping.Threshold < 0 || shipping.Fee < 0))
            return Problem(title: "invalid-shipping", detail: "Kargo eşiği ve ücreti negatif olamaz.", statusCode: 400);

        await _replicas.HelloAsync(
            licenseId,
            new ReplicaHello(
                envelope,
                req.PreviousReplicaId,
                req.MachineName,
                req.HardwareFingerprint,
                (byte)featureLevel,
                ReportsShipping: req.ReportedShipping is not null,
                req.ReportedShipping?.Threshold,
                req.ReportedShipping?.Fee,
                BackfillCompleted: req.BackfillCompleted == true,
                PaymentHistoryCompleted: req.PaymentHistoryCompleted == true),
            Request.Headers.UserAgent.ToString(),
            ct);

        return NoContent();
    }

    // ─── state ────────────────────────────────────────────────────────────

    /// <param name="State"><c>live</c> (<c>EndedAt</c> boş) ya da <c>handing-over</c> (bitti, devredilmedi).</param>
    /// <param name="OwnerLastSeenAt">Sahip kopyanın son kalp atışı (<c>WpfReplicas.LastSeenAt</c>); oturum
    /// satırına yazılmaz.</param>
    public sealed record LiveSessionDto(
        Guid Id,
        string? Title,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string? OwnerMachineName,
        DateTimeOffset? OwnerLastSeenAt,
        string State,
        int? UnsentLabelCount);

    /// <summary>Lisans geneli kargo ayarı (§6.2). Tablo S2'de gelir; S1'de alan hep null.</summary>
    public sealed record ShippingSettingsDto(int Version, decimal? FreeShippingThreshold, decimal? Fee);

    public sealed record LegacyMachineDto(string? MachineName, string? AppVersion, DateTimeOffset LastSeenAt);

    /// <param name="Mode">0 = Off, 1 = On (<see cref="SalesSyncMode"/>).</param>
    /// <param name="MinClientVersion">Tanımsız ya da ayrıştırılamıyorsa null (kapı kapalı).</param>
    public sealed record SalesStateDto(
        int Mode,
        DateTimeOffset? ModeChangedAt,
        string? MinClientVersion,
        IReadOnlyList<LiveSessionDto> LiveSessions,
        ShippingSettingsDto? Shipping,
        bool ScrubOrderTextOnPurge,
        IReadOnlyList<LegacyMachineDto> LegacyMachines);

    /// <summary>
    /// Lisansın satış senkronu durumu (§4.5). Kalp atışı oturum satırına değil kopya satırına yazılır: canlı
    /// yayının <c>ownerLastSeenAt</c>'i sahip kopyanın <c>LastSeenAt</c>'inden okunur, böylece her 30 sn'lik tur
    /// oturumun rowversion'ını ve değişiklik akışını ilerletmez.
    /// </summary>
    [HttpGet("state")]
    public async Task<IActionResult> State(
        Guid licenseId, [FromQuery] Guid? replicaId, [FromQuery] int? seenMode, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();
        if (await HeartbeatAsync(licenseId, replicaId, seenMode, "sales/state", ct) is { } invalid) return invalid;

        var license = await _db.Licenses.AsNoTracking()
            .Where(l => l.Id == licenseId)
            .Select(l => new { l.SalesSyncMode, l.SalesSyncModeChangedAt })
            .SingleAsync(ct);

        // Devredilmemiş oturumlar: filtreli IX_StreamSessions_LicenseId_Unreleased.
        var liveSessions = await _db.StreamSessions.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && s.ReleasedAt == null)
            .OrderBy(s => s.StartedAt).ThenBy(s => s.Id)
            .Select(s => new LiveSessionDto(
                s.Id,
                s.Title,
                s.StartedAt,
                s.EndedAt,
                s.OwnerMachineName,
                _db.WpfReplicas
                    .Where(r => r.LicenseId == s.LicenseId && r.ReplicaId == s.OwnerReplicaId)
                    .Select(r => (DateTimeOffset?)r.LastSeenAt)
                    .FirstOrDefault(),
                s.EndedAt == null ? "live" : "handing-over",
                s.UnsentLabelCount))
            .ToListAsync(ct);

        var minClientVersion = _options.ParseMinClientVersion() is null ? null : _options.MinClientVersion!.Trim();

        return Ok(new SalesStateDto(
            (int)license.SalesSyncMode,
            license.SalesSyncModeChangedAt,
            minClientVersion,
            liveSessions,
            Shipping: null,
            _options.ScrubOrderTextOnPurge,
            await LegacyMachinesAsync(licenseId, ct)));
    }

    // ─── changes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Değişiklik akışı (§4.4): devredilmiş yayınlar, siparişleri ve kargo dosyaları ChangeSeq sırasıyla; yan listede
    /// sayfadaki siparişlerin oturumları ve müşteri taslakları. Mod Off'ta da çalışır. Ayrıntı
    /// <see cref="SalesChangesFeed"/>'te.
    /// </summary>
    /// <param name="take">Sayfa boyu; 1–500 aralığına kırpılır.</param>
    /// <param name="replicaId">Verilirse (<paramref name="seenMode"/> ile) kalp atışı sayılır, state ile aynı.</param>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes(
        Guid licenseId,
        [FromQuery] long afterSeq = 0,
        [FromQuery] int take = SalesChangesFeed.MaxTake,
        [FromQuery] Guid? replicaId = null,
        [FromQuery] int? seenMode = null,
        CancellationToken ct = default)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();
        if (await HeartbeatAsync(licenseId, replicaId, seenMode, "sales/changes", ct) is { } invalid) return invalid;

        return Ok(await _feed.ReadAsync(licenseId, afterSeq, take, ct));
    }

    // ─── backfill ─────────────────────────────────────────────────────────

    /// <summary>Geri doldurma partisinde her listenin üst sınırı (§4.3).</summary>
    public const int MaxBackfillBatch = 200;

    private const string BackfillInserted = "inserted";
    private const string BackfillFilled = "filled";
    private const string BackfillUnchanged = "unchanged";
    private const string BackfillRejected = "rejected";

    /// <summary>Geri doldurma öğesinin Id'si (ya da siparişin oturumu) sunucuda başka bir lisansa ait (§4.3): lisans
    /// değiştiren PC'nin eski lisans geçmişi. Satır yazılmaz; son durumdur, istemci yeniden göndermez.</summary>
    public const string ReasonForeignLicense = "foreign-license";

    /// <summary>Bir kargo dosyası ve bu kopyada ona bağlı etiketler (§4.3 dosya bağları).</summary>
    public sealed record BackfillShipmentDto(Guid Id, List<Guid>? LabelIds);

    /// <summary>
    /// Geri doldurma partisi (§4.3, §8.2-3). Oturum ve sipariş öğeleri <c>sessions/sync</c> ve <c>orders/sync</c> v2
    /// öğeleriyle aynı biçimdedir (siparişte <c>baseVersion</c> okunmaz).
    /// </summary>
    /// <param name="CatalogAware">orders/sync ile aynı: istemci katalog kimliklerini doldurabiliyor; yalnız eklemede
    /// stok defterine yazılır.</param>
    public sealed record BackfillRequest(
        List<SyncSessionItem>? Sessions,
        List<SyncOrderItem>? Orders,
        List<BackfillShipmentDto>? Shipments,
        bool CatalogAware = false,
        Guid? ReplicaId = null,
        int? Protocol = null,
        int? SeenMode = null) : ISalesEnvelopeFields;

    /// <summary>Oturumun yazımdan sonraki sahiplik ve devir hâli.</summary>
    /// <param name="ReleasedAt">Devir anı (bu çağrıda ya da önceden). Mod Off'ta istemci yalnız bunu saklar (§5.2).</param>
    /// <param name="Reason">Doluysa öğe reddedildi ve hiçbir şey yazılmadı (<c>foreign-license</c>: Id başka bir lisansın
    /// oturumu); öbür alanlar boştur.</param>
    public sealed record BackfillSessionResultDto(
        Guid Id, Guid? OwnerReplicaId, DateTimeOffset? EndedAt, DateTimeOffset? ReleasedAt, string? Reason = null);

    /// <param name="Version">Satırın sürümü (<see cref="Order.SyncVersion"/>, v2 push'unun <c>baseVersion</c>'ı);
    /// reddedilen eklemede satır yoktur: null.</param>
    /// <param name="Status"><c>inserted</c>, <c>filled</c> (boş alanlar doldu), <c>unchanged</c> ya da <c>rejected</c>
    /// (<c>session-owned-elsewhere</c>: başka kopyanın canlı yayını; <c>foreign-license</c>: siparişin ya da oturumunun
    /// Id'si başka bir lisansın).</param>
    public sealed record BackfillOrderResultDto(Guid Id, int? Version, string Status, string? Reason);

    /// <summary>Mevcut siparişte yerel değeri sunucudakinden farklı birimler (§4.3); sunucu yazmadı.</summary>
    /// <param name="Units"><c>price</c>, <c>cancel</c>, <c>backup</c>, <c>print</c>.</param>
    /// <param name="LocalWins">Yerel değer doğru sayılır: istemci satırı olağan kuyruğa yeniden alır ve v2 push'uyla
    /// gönderir (§8.2-4). Yalnız mod Off'ta, geri yüklenmemiş ve kopyalanmamış kopyada ve ayrışan hiçbir birim
    /// damgalı değilse; aksi hâlde sunucu kazanır, istemci yalnız kaydeder.</param>
    public sealed record BackfillDivergedDto(Guid Id, IReadOnlyList<string> Units, bool LocalWins);

    public sealed record BackfillResponse(
        IReadOnlyList<BackfillSessionResultDto> Sessions,
        IReadOnlyList<BackfillOrderResultDto> Orders,
        IReadOnlyList<BackfillDivergedDto> Diverged);

    /// <summary>
    /// Ws'nin ilk açılışındaki geri doldurma (spec 2026-10-13 §4.3, §8.2-3): yerelde duran geçmişi sunucuda
    /// tamamlar. <b>Yalnız boşu doldurur</b> — sunucuda dolu bir alan ya da damgalı bir birim yazılmaz; moddan
    /// bağımsız ve idempotenttir (aynı parti ikinci kez hiçbir satırı ve sürümü değiştirmez).
    /// <list type="bullet">
    ///   <item><b>Oturum:</b> yoksa eklenir (sahip çağıran). Sahip boşsa çağıran yazılır; çağıran sahipse boş
    ///   <c>EndedAt</c> ve makine adı dolar (kaçmış kapanış). Devir yalnız öğe <c>Release = true</c> taşıyorsa ve
    ///   çağıran sahipse, <see cref="SessionReleaser"/> ile. Başka kopyanın oturumuna dokunulmaz (Y1, §4.1).</item>
    ///   <item><b>Sipariş:</b> yoksa eklenir — oturum denetimi, asıl kayıt, v2 alanları, kaynak kopya, devir anı ve
    ///   KVKK boşaltması orders/sync'in ekleme dalıyla aynı; başka kopyanın canlı yayınına ekleme reddedilir. Varsa
    ///   <see cref="OrderBackfillRule"/>: yalnız boş yeni alanlar ve kaynak kopya dolar; birimler karşılaştırılır,
    ///   yazılmaz, fark <c>diverged</c> döner ve kopya satırına sayılır. Sürüm yalnız satır değiştiyse artar.</item>
    ///   <item><b>Dosya bağları:</b> sipariş yalnız <c>ShipmentId</c> ve <c>ShipmentSetAt</c> boşsa bağlanır; dosyanın
    ///   boş kaynağı çağıranla dolar. Mod On'da yazılan bağ türetilmiş dosyayı aynı yazımda türetir (K7).</item>
    ///   <item><b>Başka lisansın Id'si:</b> oturum ya da sipariş Id'si sunucuda başka bir lisansa aitse (lisans
    ///   değiştiren PC'nin eski geçmişi; birincil anahtar yalnız Id) satır yazılmaz, satır başına
    ///   <c>foreign-license</c> döner; o oturuma bağlı siparişler de. Parti düşmez. Hiçbir lisansta olmayan oturuma
    ///   bağlı sipariş ise orders/sync gibi partinin tamamını 400 <c>unknown-session</c> ile düşürür.</item>
    /// </list>
    /// <b>Bildirim yok:</b> yayıncıya push, WhatsApp <c>OrderReceived</c> etiket kuralı, shopper push ve "Yayın
    /// başladı" üretilmez — geçmiş yeniden oynatılmaz (D1'in Replay deseni, §10.2). Stok defteri yalnız eklemede ve
    /// katalog bilen istekte, siparişlerle aynı yazımda.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill(Guid licenseId, [FromBody] BackfillRequest req, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        // Geri doldurma çağıranı bilmek zorunda (sahip, kaynak kopya, yerel kazanır): yalnız v2.
        if (!SalesEnvelope.TryRead(req, out var envelope, out var envelopeError) || !envelope.IsV2)
            return Problem(title: "invalid-envelope", detail: envelopeError ?? "backfill v2 isteğidir.", statusCode: 400);
        var context = await _replicas.AcceptAsync(
            licenseId, envelope, Request.Headers.UserAgent.ToString(), "sales/backfill", ct);

        var sessionItems = req.Sessions ?? [];
        var orderItems = req.Orders ?? [];
        var links = req.Shipments ?? [];
        if (sessionItems.Count > MaxBackfillBatch || orderItems.Count > MaxBackfillBatch || links.Count > MaxBackfillBatch)
            return Problem(
                title: "batch-too-large", detail: $"Her listede en çok {MaxBackfillBatch} öğe.", statusCode: 400);
        if (sessionItems.FirstOrDefault(s =>
                s.MachineName is { Length: > MaxNameLength } || s.UnsentLabelCount is < 0) is { } invalidSession)
            return Problem(
                title: "invalid-session",
                detail: $"Oturum {invalidSession.Id}: machineName en çok {MaxNameLength} karakter, unsentLabelCount "
                    + "negatif olamaz.",
                statusCode: 400);
        if (orderItems.FirstOrDefault(o => o.PrintBatchId is
                { Length: > LicensesSessionsSyncController.PrintBatchIdMaxLength }) is { } invalidOrder)
            return Problem(
                title: "invalid-order",
                detail: $"Sipariş {invalidOrder.Id}: printBatchId en çok "
                    + $"{LicensesSessionsSyncController.PrintBatchIdMaxLength} karakter.",
                statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var caller = envelope.ReplicaId;
        var modeOn = context.Mode == SalesSyncMode.On;
        // Aynı Id iki kez gelirse son giriş kazanır (orders/sync ile aynı; aksi hâlde aynı satır iki kez eklenirdi).
        var sessions = sessionItems.GroupBy(s => s.Id).Select(g => g.Last()).ToList();
        var orders = orderItems.GroupBy(o => o.Id).Select(g => g.Last()).ToList();

        // K7 sırası (ShipmentAmountDeriver): dosyalar türetmenin okuduğu siparişlerden ÖNCE yüklenir. Etkilenebilecek
        // dosyalar istekten bellidir — geri doldurma yalnız boş bağı doldurur, eski bağ yoktur.
        var shipments = await _shipmentAmounts.LoadAsync(
            licenseId,
            links.Select(l => l.Id)
                .Concat(modeOn ? orders.Where(o => o.ShipmentId != null).Select(o => o.ShipmentId!.Value) : []),
            ct);

        // Oturum denetimi orders/sync ile aynı (F01): siparişin oturumu bu lisansın olmalı — sunucuda ya da bu
        // partide (eksik oturum aynı partide eklenir). Hiçbir lisansta olmayan oturum paketin tamamını reddeder,
        // hiçbir şey yazılmaz. Başka lisansta var olan oturum ise satır başına reddedilir (aşağıda): StreamSessions'ın
        // birincil anahtarı yalnız Id; onu "eksik" sanıp eklemek 500 olurdu ve parti her denemede aynı yerde düşerdi.
        var requestSessionIds = sessions.Select(s => s.Id).ToHashSet();
        var sessionKeys = requestSessionIds
            .Concat(orders.Where(o => o.SessionId != null).Select(o => o.SessionId!.Value))
            .Distinct()
            .ToList();
        var serverSessions = sessionKeys.Count == 0
            ? new Dictionary<Guid, StreamSession>()
            : await _db.StreamSessions
                .Where(s => s.LicenseId == licenseId && sessionKeys.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);
        var foreignSessions = await ForeignIdsAsync(
            _db.StreamSessions.Where(s => s.LicenseId != licenseId).Select(s => s.Id), sessionKeys, ct);
        if (orders.Any(o => o.SessionId is { } sid && !serverSessions.ContainsKey(sid)
                && !requestSessionIds.Contains(sid) && !foreignSessions.Contains(sid)))
            return Problem(
                title: "unknown-session",
                detail: "Pakette bu lisansa ait olmayan SessionId var.",
                statusCode: 400);

        // ── Oturumlar ──
        var toRelease = new List<Guid>();
        var sessionsOwnedElsewhere = 0;
        foreach (var item in sessions)
        {
            // Başka lisansın oturumu: yazılmaz, yanıtta reddedilir (ve siparişleri de).
            if (foreignSessions.Contains(item.Id)) continue;

            if (!serverSessions.TryGetValue(item.Id, out var session))
            {
                // Bildirimsiz ekleme: "Yayın başladı" yalnız sessions/sync'in canlı eklemesinde.
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
                    OwnerReplicaId = caller,
                    OwnerMachineName = item.MachineName,
                };
                _db.StreamSessions.Add(session);
                serverSessions[item.Id] = session;
            }
            else
            {
                var changed = false;
                if (session.OwnerReplicaId is null)
                {
                    session.OwnerReplicaId = caller;
                    changed = true;
                }
                if (session.OwnerReplicaId != caller)
                {
                    // Başka kopyanın yayını (Y1, §4.1): kapanışı yalnız sahibi ya da force-end yazar, devri yalnız
                    // sahibi ister. Geri doldurma sahipliği de devralmaz (yalnız boşu doldurur; geri yüklenmiş
                    // kopyanın devralması sessions/sync'te, §8.7).
                    if ((session.EndedAt is null && item.EndedAt is not null) || (item.Release && session.ReleasedAt is null))
                        sessionsOwnedElsewhere++;
                    continue;
                }
                if (session.OwnerMachineName is null && item.MachineName is not null)
                {
                    session.OwnerMachineName = item.MachineName;
                    changed = true;
                }
                // Kaçmış kapanış: sunucuda açık kalmış, yerelde bitmiş yayın. Dolu EndedAt değişmez (tekdüze).
                if (session.EndedAt is null && item.EndedAt is not null)
                {
                    session.EndedAt = item.EndedAt;
                    changed = true;
                }
                if (changed) session.UpdatedAt = now;
            }

            // Devir yalnız bayrakla (M2: istemci onu yayının etiket kuyruğu boşken gönderir).
            if (item.Release && session.EndedAt is not null && session.ReleasedAt is null)
                toRelease.Add(session.Id);
        }

        // ── Siparişler ──
        var lookupIds = orders.Select(o => o.Id)
            .Concat(links.SelectMany(l => l.LabelIds ?? []))
            .Distinct()
            .ToList();
        var serverOrders = lookupIds.Count == 0
            ? new Dictionary<Guid, Order>()
            : await _db.Orders
                .Where(o => o.LicenseId == licenseId && lookupIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, ct);
        // Orders'ın da birincil anahtarı yalnız Id: başka lisansta var olan sipariş "yeni" sanılıp eklenmez.
        var foreignOrders = await ForeignIdsAsync(
            _db.Orders.Where(o => o.LicenseId != licenseId).Select(o => o.Id), orders.Select(o => o.Id).ToList(), ct);
        // Satır başına ret nedeni; yalnız ekleme adaylarında (mevcut satırın oturumu bu lisansındır).
        var rejected = new Dictionary<Guid, string>();
        foreach (var item in orders.Where(o => !serverOrders.ContainsKey(o.Id)))
        {
            if (foreignOrders.Contains(item.Id) || (item.SessionId is { } sid && foreignSessions.Contains(sid)))
                rejected[item.Id] = ReasonForeignLicense;
        }

        var insertItems = orders
            .Where(o => !serverOrders.ContainsKey(o.Id) && !rejected.ContainsKey(o.Id))
            .ToList();
        // A5b: eski sürüm birleştirmeden sonra da kopyanın Id'sini taşır; ekleme asıl kaydın hex'iyle yazılır.
        var canonicalHex = insertItems.Count == 0
            ? new Dictionary<string, string>()
            : await _customerIds.CanonicalHexOfAsync(licenseId, insertItems.Select(o => o.CustomerId), ct);
        // KVKK (§9, §4.3 = §4.2 ekleme dalı): ayar açıkken silinmiş müşteriye geç gelen ekleme metni geri getirmez.
        var purgeDirectory = _options.ScrubOrderTextOnPurge && insertItems.Count > 0
            ? await SalesCustomerDirectory.LoadAsync(_db, licenseId, insertItems.Select(o => o.CustomerId), ct)
            : SalesCustomerDirectory.Empty;

        // Yerel kazanır (§4.3, M20): yalnız mod Off'ta ve özgün kopyada — geri yüklenmemiş (PreviousReplicaId boş),
        // klonu görülmemiş (HardwareFingerprintConflictAt boş; hello'da yazılır, hiç silinmez). Kopya satırı bu
        // istekle doğduysa (hello'suz ilk istek) geçmişi bilinmez: sunucu kazanır.
        var localWinsAllowed = !modeOn
            && context.ReplicaBefore is { PreviousReplicaId: null, FingerprintConflict: false };

        IReadOnlySet<Guid>? predecessors = null;
        async Task<bool> IsSilentPredecessorAsync(Guid owner)
        {
            predecessors ??= await _replicas.SilentPredecessorsAsync(licenseId, caller, context.ReplicaBefore, ct);
            return predecessors.Contains(owner);
        }

        var inserted = new Dictionary<Guid, Order>();
        var changedOrders = new HashSet<Guid>();
        var diverged = new List<BackfillDivergedDto>();
        var affectedShipments = new List<Guid>();
        foreach (var item in orders)
        {
            if (rejected.ContainsKey(item.Id)) continue;

            if (serverOrders.TryGetValue(item.Id, out var current))
            {
                var server = current.ToServerRow();
                var rule = OrderBackfillRule.Apply(server, item.ToUnitValues());
                if (rule.Values != server.Values)
                {
                    current.ApplyUnits(rule.Values);
                    changedOrders.Add(current.Id);
                    if (current.ShipmentId != server.Values.ShipmentId && current.ShipmentId is { } filledLink)
                        affectedShipments.Add(filledLink);
                }
                // Kaynak kopya kargo bağının bilindiğini söyler (M22).
                if (current.OriginReplicaId is null)
                {
                    current.OriginReplicaId = caller;
                    changedOrders.Add(current.Id);
                }
                if (rule.Diverged.Count > 0)
                    diverged.Add(new BackfillDivergedDto(
                        item.Id,
                        rule.Diverged.Select(u => u.WireName()).ToList(),
                        localWinsAllowed && !rule.DivergedStamped));
                continue;
            }

            var session = item.SessionId is { } sessionId ? serverSessions[sessionId] : null;
            // §4.2 ekleme dalı (Y1): başka kopyanın CANLI yayınına ekleme reddedilir; önceki kopyası susan geri
            // yüklenmiş kopya o yayının kuyruğunu taşır (§8.7).
            if (session is { EndedAt: null, OwnerReplicaId: { } owner } && owner != caller
                && !await IsSilentPredecessorAsync(owner))
            {
                rejected[item.Id] = LicensesSessionsSyncController.ReasonOwnedElsewhere;
                continue;
            }

            var order = item.ToNewOrder(
                licenseId, canonicalHex.GetValueOrDefault(item.CustomerId, item.CustomerId), v2: true, now);
            // DM3: devredilmiş oturuma ekleme devir anını kopyalar; bu partide devredilecek oturumun siparişlerini
            // devir kendisi damgalar.
            order.SessionReleasedAt = session?.ReleasedAt;
            order.OriginReplicaId = caller;
            order.ShipmentId = item.ShipmentId;
            if (order.ShipmentId is { } link) affectedShipments.Add(link);
            if (OrderTextScrub.AppliesOnInsert(
                    _options.ScrubOrderTextOnPurge, purgeDirectory.Resolve(item.CustomerId).Canonical?.PurgedAt))
                OrderTextScrub.Apply(order, now);
            _db.Orders.Add(order);
            inserted[order.Id] = order;
        }

        // ── Dosya bağları ──
        var linkConflicts = 0;
        var unknownLabels = 0;
        foreach (var link in links)
        {
            foreach (var labelId in (link.LabelIds ?? []).Distinct())
            {
                var order = inserted.GetValueOrDefault(labelId) ?? serverOrders.GetValueOrDefault(labelId);
                if (order is null)
                {
                    unknownLabels++;
                    continue;
                }
                if (order.ShipmentId == link.Id) continue;
                // Başka dosyaya bağlı ya da bağı komutla değişmiş sipariş: yalnız boşu doldururuz. Bugün bağ yalnız
                // etiketin kendi bilgisayarında kurulabildiği için beklenmez; sayılır.
                if (order.ShipmentId is not null || order.ShipmentSetAt is not null)
                {
                    linkConflicts++;
                    continue;
                }
                order.ShipmentId = link.Id;
                if (!inserted.ContainsKey(order.Id)) changedOrders.Add(order.Id);
                affectedShipments.Add(link.Id);
            }

            // Dosyanın mod Off'taki tek yazıcısı (§3.1); dolu kaynağı yalnız disable turu yeniden yazar.
            if (shipments.TryGetValue(link.Id, out var shipment) && shipment.OriginReplicaId is null)
            {
                shipment.OriginReplicaId = caller;
                shipment.UpdatedAt = now;
                shipment.Version++;
            }
        }

        // Satır değiştiyse sürüm bir kez ilerler: jeton eşzamanlı yazıcıyı durdurur, komutlar yeni hâli bekler.
        foreach (var id in changedOrders)
        {
            var order = serverOrders[id];
            order.UpdatedAt = now;
            order.SyncVersion++;
        }

        // K7 (mod On): yazılan bağ ilgili türetilmiş dosyaları aynı yazımda yeniden türetir.
        var derivedShipments = modeOn
            ? await _shipmentAmounts.DeriveAsync(
                licenseId, shipments, affectedShipments, serverOrders.Values.Concat(inserted.Values), now, ct)
            : Array.Empty<Guid>();

        // Stok defteri yalnız eklemede (mevcut satırın deftere giren durumu geri doldurmayla değişmez) ve katalog
        // bilen istekte — orders/sync'in gerekçesiyle, siparişlerle aynı SaveChanges'te.
        if (req.CatalogAware && inserted.Count > 0)
            await _ledger.ApplyAsync(
                licenseId,
                inserted.Values.Select(o => new LedgerOrderInput(
                    new LedgerOrderState(
                        o.Id, o.ProductId, o.ProductVariantId, o.IsShippingFee, o.CancelledAt is not null,
                        o.IsTentativeBackup),
                    o.AddedAt,
                    o.CancelledAt)).ToList(),
                DateTimeOffset.UtcNow,
                ct);

        var localWins = diverged.Count(d => d.LocalWins);
        var serverWins = diverged.Count - localWins;
        IReadOnlyList<Guid> released = Array.Empty<Guid>();
        try
        {
            // Yazım, devir ve ayrışma sayısı tek işlemde: biri düşerse hiçbiri yazılmaz, istemci partiyi yeniden
            // gönderir (geri doldurma idempotent).
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;
            await _db.SaveChangesAsync(ct);
            if (toRelease.Count > 0)
                released = await _releaser.ReleaseAsync(licenseId, toRelease, now, ct);
            await _replicas.AddBackfillDivergenceAsync(licenseId, caller, localWins, serverWins, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Sipariş (SyncVersion) ya da dosya (Version) jetonu: biz okuduktan sonra başka bir yazım geldi. İşlem
            // tümüyle geri alındı; orders/sync'in deseni.
            _log.LogInformation(
                "sales/backfill çakışması, istemci yeniden denemeli (license={LicenseId}, replica={ReplicaId})",
                licenseId, caller);
            return Problem(
                title: "sync-conflict",
                detail: "Bu satırlar eşzamanlı başka bir istekle yazıldı; partiyi yeniden gönderin.",
                statusCode: 409);
        }

        // Gözlem (§10.5): yalnız sayı ve kimlik; ad ya da metin yazılmaz.
        var foreignSessionItems = sessions.Count(s => foreignSessions.Contains(s.Id));
        _log.LogInformation(
            "sales/backfill: {Inserted} sipariş eklendi, {Filled} siparişte boş alan doldu, {Rejected} ekleme reddedildi "
            + "({ForeignOrders} başka lisansın); ayrışma {LocalWins} yerel / {ServerWins} sunucu kazanır; {Released} oturum "
            + "devredildi, {OwnedElsewhere} oturum başka kopyanın, {ForeignSessions} oturum başka lisansın; "
            + "{LinkConflicts} bağ çakışması, {UnknownLabels} bilinmeyen etiket, {Derived} dosya türetildi "
            + "(license={LicenseId}, replica={ReplicaId})",
            inserted.Count, changedOrders.Count, rejected.Count, rejected.Values.Count(r => r == ReasonForeignLicense),
            localWins, serverWins, released.Count, sessionsOwnedElsewhere, foreignSessionItems, linkConflicts,
            unknownLabels, derivedShipments.Count, licenseId, caller);

        // Devir koşullu UPDATE ile yazıldıysa izlenen oturum onu görmez: değerler veritabanından.
        var sessionRows = requestSessionIds.Count == 0
            ? new Dictionary<Guid, BackfillSessionResultDto>()
            : await _db.StreamSessions.AsNoTracking()
                .Where(s => s.LicenseId == licenseId && requestSessionIds.Contains(s.Id))
                .Select(s => new BackfillSessionResultDto(s.Id, s.OwnerReplicaId, s.EndedAt, s.ReleasedAt, null))
                .ToDictionaryAsync(s => s.Id, ct);

        return Ok(new BackfillResponse(
            sessions
                .Select(s => foreignSessions.Contains(s.Id)
                    ? new BackfillSessionResultDto(s.Id, null, null, null, ReasonForeignLicense)
                    : sessionRows.GetValueOrDefault(s.Id))
                .OfType<BackfillSessionResultDto>()
                .ToList(),
            orders.Select(item =>
                rejected.TryGetValue(item.Id, out var reason)
                    ? new BackfillOrderResultDto(item.Id, null, BackfillRejected, reason)
                    : inserted.TryGetValue(item.Id, out var added)
                        ? new BackfillOrderResultDto(item.Id, added.SyncVersion, BackfillInserted, null)
                        : new BackfillOrderResultDto(
                            item.Id,
                            serverOrders[item.Id].SyncVersion,
                            changedOrders.Contains(item.Id) ? BackfillFilled : BackfillUnchanged,
                            null))
                .ToList(),
            diverged));
    }

    /// <summary>
    /// <paramref name="ids"/>'ten başka lisansta var olanlar (yalnız Id; satırlar izlenmez, içerikleri okunmaz).
    /// <paramref name="foreignIds"/> çağıranın lisansı dışındaki satırların Id sorgusudur.
    /// </summary>
    private static async Task<IReadOnlySet<Guid>> ForeignIdsAsync(
        IQueryable<Guid> foreignIds, List<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? new HashSet<Guid>()
            : (await foreignIds.Where(id => ids.Contains(id)).ToListAsync(ct)).ToHashSet();

    /// <summary>
    /// Okuma uçlarının kalp atışı: <c>replicaId</c> ya da <c>seenMode</c> verildiyse ikisi birlikte v2 zarfıdır ve
    /// kopya satırına yazılır; hiçbiri yoksa hiçbir şey yazılmaz (protokolsüz okuma iz bırakmaz). Yarım zarf 400.
    /// </summary>
    private async Task<IActionResult?> HeartbeatAsync(
        Guid licenseId, Guid? replicaId, int? seenMode, string endpoint, CancellationToken ct)
    {
        if (replicaId is null && seenMode is null) return null;
        if (!SalesEnvelope.TryRead(replicaId, SalesEnvelope.V2, seenMode, out var envelope, out var error))
            return Problem(title: "invalid-envelope", detail: error, statusCode: 400);
        await _replicas.AcceptAsync(licenseId, envelope, Request.Headers.UserAgent.ToString(), endpoint, ct);
        return null;
    }

    /// <summary>
    /// Eski sürüm makineler: son 14 günde görülmüş, devre dışı bırakılmamış etkinleştirmeler (§4.10-1).
    /// <c>MinClientVersion</c> tanımlıysa sürümü bilinmeyen ya da ondan eski olanlar. Tanımsızsa (Ws henüz
    /// yayımlanmadı) sürümle ayırmak mümkün değil: aynı donanım parmak izinde v2 konuşmuş bir kopyası
    /// olmayanlar.
    /// </summary>
    private async Task<IReadOnlyList<LegacyMachineDto>> LegacyMachinesAsync(Guid licenseId, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - RecentWindow;
        var activations = await _db.Activations.AsNoTracking()
            .Where(a => a.LicenseId == licenseId && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .OrderByDescending(a => a.LastSeenAt)
            .Select(a => new { a.HardwareFingerprint, a.MachineName, a.AppVersion, a.LastSeenAt })
            .ToListAsync(ct);
        if (activations.Count == 0) return Array.Empty<LegacyMachineDto>();

        var min = _options.ParseMinClientVersion();
        IEnumerable<LegacyMachineDto> legacy;
        if (min is not null)
        {
            legacy = activations
                .Where(a => !ClientVersion.IsAtLeast(ClientVersion.Parse(a.AppVersion), min))
                .Select(a => new LegacyMachineDto(a.MachineName, a.AppVersion, a.LastSeenAt));
        }
        else
        {
            var v2Fingerprints = (await _db.WpfReplicas.AsNoTracking()
                    .Where(r => r.LicenseId == licenseId
                        && r.Protocol == SalesEnvelope.V2
                        && r.HardwareFingerprint != null)
                    .Select(r => r.HardwareFingerprint!)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            legacy = activations
                .Where(a => !v2Fingerprints.Contains(a.HardwareFingerprint))
                .Select(a => new LegacyMachineDto(a.MachineName, a.AppVersion, a.LastSeenAt));
        }

        return legacy.ToList();
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        return await _db.Licenses.AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
    }
}
