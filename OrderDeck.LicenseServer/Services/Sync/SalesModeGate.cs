using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Mod kapısı (mühendislik kararı M1) — spec 2026-10-13 §4.10 "<c>enable</c> ön koşulları", §8.1 adım 5–7, §8.3. Bir
/// lisansın satış senkronunu açmadan önce yedi maddeyi değerlendirir; <c>sales-sync enable</c>'ın kuru koşusu ve
/// <c>--apply</c>'ı aynı sonucu kullanır. Salt okunur: bütün sorgular izlemesiz, hiçbir şey yazılmaz.
/// <list type="number">
///   <item><b>İstemci sürümü.</b> <c>SalesSync:MinClientVersion</c> ayrıştırılabilir olmalı (boş ya da bozuk değer
///   tanımsızdır, §2 "Ws"). Son 14 günde görülmüş ve devre dışı bırakılmamış her etkinleştirmenin <c>AppVersion</c>'ı
///   <see cref="ClientVersion.IsAtLeast"/> ile alt sınırı karşılamalı; sürüm bildirmeyen (null) ya da ayrıştırılamayan
///   sürüm eski sayılır.</item>
///   <item><b>Kopyalar.</b> Son 14 günde görülmüş her <c>WpfReplicas</c> satırında protokol 2, <c>featureLevel</c> ≥ 3
///   (yalnız W3'ü içeren derleme 3 bildirir, §15-29), geri doldurma ve ödeme geçmişi tamamlanmış, parmak izi çakışması
///   (disk klonu, §8.7) yok. Her neden ayrı listelenir.</item>
///   <item><b>Eski sürüm izi.</b> <c>SalesLegacyTraces.LastLegacySalesPushAt</c> yok ya da 7 günden eski.</item>
///   <item><b>Lisans geneli kargo ayarı</b> (Y4, §6.2): satır var ve <c>UpdatedBy</c> dolu.</item>
///   <item><b>sales-audit</b> (<see cref="SalesAuditReport"/>): 24 saatten eski devredilmemiş oturum yok,
///   <c>SessionReleasedAt</c>'i eksik sipariş yok.</item>
///   <item><b>Kargo bağları tam</b> (§15-5): 14 gün penceresinden bağımsız olarak Shipped olmayan her dosyanın
///   <c>OriginReplicaId</c>'si dolu ve o kopyanın (aynı lisans) geri doldurması tamamlanmış; saklanan tutarla bağlı
///   siparişlerden türetilen (<see cref="LabelRules.DerivedShipmentAmount"/>) tutarın farkı Y5 ile açıklanıyor:
///   0 ≤ fark ≤ bağlı ve iptal edilmiş siparişlerin fiyat toplamı. Tutarı &gt; 0 olup hiç bağlı siparişi olmayan
///   dosyanın farkı tutarın kendisidir ve iptal toplamı 0'dır: kural onu da açıklanamayan sayar. <c>--accept-shipment</c>
///   ile kabul edilen dosya bu maddenin iki parçasından da muaftır: hiçbir CLI dosyanın kaynağını düzeltemez, emekli
///   PC'nin açık dosyası aksi hâlde hiç geçemezdi (§15-53).</item>
///   <item><b>Rapor</b> (koşul değil, bilgi): açıklanan farkı olan dosyalar ve toplam fark; saklanan ve türetilen
///   tutarı eşiğin iki yanına düşen dosyalar (iki yön ayrı); kargo bağı bilinmeyen siparişler
///   (<see cref="SalesAuditReport.Result.UnknownLinkCandidates"/>); kopya başına geri doldurma ayrışması.</item>
/// </list>
///
/// <para><b>Kişisel veri yok.</b> Sonuç yalnız Id, sayı, tutar, sürüm ve zaman taşır; makine adı, müşteri, ad ve metin
/// okunmaz.</para>
/// </summary>
public static class SalesModeGate
{
    /// <summary>"Son 14 günde görülmüş" etkinleştirme ve kopya penceresi (§4.10-1, -2).</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    /// <summary>Eski sürüm izinin sessiz kalması gereken süre (§4.10-3).</summary>
    public static readonly TimeSpan LegacyQuietPeriod = TimeSpan.FromDays(7);

    /// <summary>Kopyanın bildirmesi gereken en düşük yetenek düzeyi (W3, §4.10-2, §15-29).</summary>
    public const byte RequiredFeatureLevel = 3;

    // ── 1. İstemci sürümü ─────────────────────────────────────────────────────

    /// <param name="AppVersion">Etkinleştirmenin bildirdiği ham sürüm; null = sürüm bildirmeyen eski istemci.</param>
    public sealed record OldActivation(Guid Id, string? AppVersion);

    /// <param name="MinClientVersion">Ayrıştırılabilen alt sınır (kırpılmış); tanımsızsa null.</param>
    /// <param name="RecentActivations">Son 14 günde görülmüş, devre dışı bırakılmamış etkinleştirme sayısı.</param>
    /// <param name="Old">Bunlardan alt sınırı karşılamayanlar; alt sınır tanımsızsa karşılaştırılmaz, boş.</param>
    public sealed record ClientVersionCheck(string? MinClientVersion, int RecentActivations, IReadOnlyList<OldActivation> Old)
    {
        public bool Passes => MinClientVersion is not null && Old.Count == 0;
    }

    // ── 2. Kopyalar ───────────────────────────────────────────────────────────

    /// <param name="Recent">Son 14 günde görülmüş kopya sayısı.</param>
    public sealed record ReplicaCheck(
        int Recent,
        IReadOnlyList<Guid> WrongProtocol,
        IReadOnlyList<Guid> LowFeatureLevel,
        IReadOnlyList<Guid> BackfillIncomplete,
        IReadOnlyList<Guid> PaymentHistoryIncomplete,
        IReadOnlyList<Guid> FingerprintConflict)
    {
        public bool Passes =>
            WrongProtocol.Count == 0 && LowFeatureLevel.Count == 0 && BackfillIncomplete.Count == 0
            && PaymentHistoryIncomplete.Count == 0 && FingerprintConflict.Count == 0;
    }

    // ── 3. Eski sürüm izi ─────────────────────────────────────────────────────

    /// <param name="QuietSince">İz bundan eski olmalı (şimdi − 7 gün).</param>
    public sealed record LegacyCheck(DateTimeOffset? LastLegacySalesPushAt, DateTimeOffset QuietSince)
    {
        public bool Passes => LastLegacySalesPushAt is null || LastLegacySalesPushAt < QuietSince;
    }

    // ── 4. Lisans geneli kargo ayarı ──────────────────────────────────────────

    /// <param name="Settings">Ayar satırı; yoksa null.</param>
    public sealed record ShippingCheck(LicenseShippingSettingsStore.Snapshot? Settings)
    {
        public bool Passes => Settings is { UpdatedBy: { } by } && !string.IsNullOrWhiteSpace(by);
    }

    // ── 5. sales-audit ────────────────────────────────────────────────────────

    public sealed record AuditCheck(
        IReadOnlyList<SalesAuditReport.StaleSession> StaleSessions, int OrdersMissingReleaseStamp)
    {
        public bool Passes => StaleSessions.Count == 0 && OrdersMissingReleaseStamp == 0;
    }

    // ── 6. Kargo bağları ──────────────────────────────────────────────────────

    /// <summary>Shipped olmayan bir dosyanın 6. madde açısından hâli.</summary>
    /// <param name="Stored">Saklanan tutar (<c>CumulativeAmount</c>).</param>
    /// <param name="Derived">Bağlı siparişlerden türetilen tutar (DM7).</param>
    /// <param name="CancelledLinked">Bağlı ve iptal edilmiş siparişlerin fiyat toplamı (Y5'in açıklayabildiği fark).</param>
    /// <param name="LinkedOrders">Bağlı sipariş sayısı.</param>
    /// <param name="OriginBackfilled">Kaynak kopyanın (aynı lisans) geri doldurması tamamlanmış.</param>
    /// <param name="Accepted"><c>--accept-shipment</c> ile kabul edildi: 6. maddeden muaf, türetilmez.</param>
    public sealed record ShipmentCheck(
        Guid Id,
        decimal Stored,
        decimal Derived,
        decimal CancelledLinked,
        int LinkedOrders,
        Guid? OriginReplicaId,
        bool OriginBackfilled,
        bool Accepted)
    {
        /// <summary>Saklanan − türetilen.</summary>
        public decimal Diff => Stored - Derived;

        /// <summary>Y5 açıklıyor: 0 ≤ fark ≤ bağlı iptallerin toplamı.</summary>
        public bool DiffExplained => Diff >= 0m && Diff <= CancelledLinked;

        public bool Passes => Accepted || (OriginReplicaId is not null && OriginBackfilled && DiffExplained);
    }

    public sealed record ShipmentLinksCheck(IReadOnlyList<ShipmentCheck> Shipments)
    {
        public IReadOnlyList<ShipmentCheck> Accepted => Shipments.Where(s => s.Accepted).ToList();

        public IReadOnlyList<ShipmentCheck> MissingOrigin =>
            Shipments.Where(s => !s.Accepted && s.OriginReplicaId is null).ToList();

        public IReadOnlyList<ShipmentCheck> OriginNotBackfilled =>
            Shipments.Where(s => !s.Accepted && s.OriginReplicaId is not null && !s.OriginBackfilled).ToList();

        public IReadOnlyList<ShipmentCheck> Unexplained =>
            Shipments.Where(s => !s.Accepted && !s.DiffExplained).ToList();

        public bool Passes => Shipments.All(s => s.Passes);
    }

    // ── 7. Rapor ──────────────────────────────────────────────────────────────

    /// <param name="Explained">Kabul edilmemiş, farkı Y5 ile açıklanan ve sıfırdan büyük dosyalar.</param>
    /// <param name="Threshold">Lisans geneli ücretsiz kargo eşiği; tanımsızsa null (eşik karşılaştırması yapılmaz).</param>
    /// <param name="FallBelowThreshold">Saklanan tutarı eşikte ya da üstünde, türetilen tutarı altında olan dosyalar.</param>
    /// <param name="RiseToThreshold">Saklanan tutarı eşiğin altında, türetilen tutarı eşikte ya da üstünde olan dosyalar.</param>
    /// <param name="OrdersWithUnknownLink">Kargo bağı bilinmeyen (OriginReplicaId boş) bütün siparişler.</param>
    /// <param name="UnknownLinkCandidates">Bunlardan bağsız, iptal edilmemiş, geçici yedek olmayanlar.</param>
    public sealed record Report(
        IReadOnlyList<ShipmentCheck> Explained,
        decimal? Threshold,
        IReadOnlyList<ShipmentCheck> FallBelowThreshold,
        IReadOnlyList<ShipmentCheck> RiseToThreshold,
        int OrdersWithUnknownLink,
        int UnknownLinkCandidates,
        decimal UnknownLinkCandidateAmount,
        IReadOnlyList<SalesAuditReport.ReplicaDivergence> BackfillDivergence)
    {
        public decimal ExplainedDiffTotal => Explained.Sum(s => s.Diff);
    }

    public sealed record Result(
        ClientVersionCheck ClientVersions,
        ReplicaCheck Replicas,
        LegacyCheck Legacy,
        ShippingCheck Shipping,
        AuditCheck Audit,
        ShipmentLinksCheck ShipmentLinks,
        Report Information)
    {
        /// <summary>Tutmayan koşulların numaraları (1–6), artan sırada.</summary>
        public IReadOnlyList<int> Failing =>
            new (int Number, bool Passes)[]
                {
                    (1, ClientVersions.Passes), (2, Replicas.Passes), (3, Legacy.Passes),
                    (4, Shipping.Passes), (5, Audit.Passes), (6, ShipmentLinks.Passes),
                }
                .Where(c => !c.Passes)
                .Select(c => c.Number)
                .ToList();

        public bool Passes => Failing.Count == 0;
    }

    /// <summary>Yedi maddeyi değerlendirir.</summary>
    /// <param name="accepted"><c>--accept-shipment</c> ile kabul edilen dosyalar; çağıran bunların lisansın Shipped
    /// olmayan dosyaları olduğunu denetlemiştir.</param>
    /// <returns>Lisans yoksa null.</returns>
    public static async Task<Result?> EvaluateAsync(
        LicenseDbContext db,
        Guid licenseId,
        SalesSyncOptions options,
        IReadOnlySet<Guid> accepted,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var audit = await SalesAuditReport.BuildAsync(db, licenseId, now, ct);
        if (audit is null) return null;

        var since = now - RecentWindow;
        var clientVersions = await ClientVersionsAsync(db, licenseId, options, since, ct);
        var replicas = await ReplicasAsync(db, licenseId, since, ct);

        var legacyAt = await db.SalesLegacyTraces.AsNoTracking()
            .Where(t => t.LicenseId == licenseId)
            .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
            .SingleOrDefaultAsync(ct);

        var settings = await db.LicenseShippingSettings.AsNoTracking()
            .Where(s => s.LicenseId == licenseId)
            .Select(s => new LicenseShippingSettingsStore.Snapshot(
                s.Version, s.FreeShippingThreshold, s.ShippingFee, s.UpdatedAt, s.UpdatedBy))
            .SingleOrDefaultAsync(ct);

        var shipments = await OpenShipments(db, licenseId).AsNoTracking().ToListAsync(ct);
        var linked = await LinkedOrdersAsync(db, licenseId, shipments.Select(s => s.Id).ToList(), ct);
        var backfill = await ReplicaBackfillAsync(db, licenseId, ct);
        var links = new ShipmentLinksCheck(CheckShipments(shipments, linked, backfill, accepted));

        var threshold = settings?.FreeShippingThreshold;
        var derivable = links.Shipments.Where(s => !s.Accepted).ToList();
        var report = new Report(
            derivable.Where(s => s.DiffExplained && s.Diff > 0m).ToList(),
            threshold,
            threshold is { } t ? derivable.Where(s => s.Stored >= t && s.Derived < t).ToList() : [],
            threshold is { } u ? derivable.Where(s => s.Stored < u && s.Derived >= u).ToList() : [],
            audit.OrdersWithUnknownLink,
            audit.UnknownLinkCandidates,
            audit.UnknownLinkCandidateAmount,
            audit.BackfillDivergence);

        return new Result(
            clientVersions,
            replicas,
            new LegacyCheck(legacyAt, now - LegacyQuietPeriod),
            new ShippingCheck(settings),
            new AuditCheck(audit.StaleSessions, audit.OrdersMissingReleaseStamp),
            links,
            report);
    }

    /// <summary>Lisansın Shipped olmayan dosyaları, Id sırasıyla. İzleme çağırana kalmıştır: <c>enable --apply</c>
    /// türetmek için izleyerek yükler (sürüm jetonu, <see cref="ShipmentAmountDeriver"/> sırası).</summary>
    public static IQueryable<Shipment> OpenShipments(LicenseDbContext db, Guid licenseId) =>
        db.Shipments
            .Where(s => s.LicenseId == licenseId && s.Status != ShipmentStatus.Shipped)
            .OrderBy(s => s.Id);

    /// <summary>Verilen dosyalara bağlı siparişler, izlemesiz.</summary>
    public static async Task<List<Order>> LinkedOrdersAsync(
        LicenseDbContext db, Guid licenseId, IReadOnlyCollection<Guid> shipmentIds, CancellationToken ct)
    {
        if (shipmentIds.Count == 0) return [];
        return await db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId && o.ShipmentId != null && shipmentIds.Contains(o.ShipmentId.Value))
            .ToListAsync(ct);
    }

    /// <summary>Lisansın her kopyası ve geri doldurmasını tamamlayıp tamamlamadığı (14 gün penceresi yok, §4.10-6).</summary>
    public static async Task<Dictionary<Guid, bool>> ReplicaBackfillAsync(
        LicenseDbContext db, Guid licenseId, CancellationToken ct) =>
        await db.WpfReplicas.AsNoTracking()
            .Where(r => r.LicenseId == licenseId)
            .ToDictionaryAsync(r => r.ReplicaId, r => r.BackfillCompletedAt != null, ct);

    /// <summary>
    /// 6. maddenin saf hâli: her dosya için saklanan ve türetilen tutar, bağlı iptallerin toplamı, kaynak ve kaynağın
    /// geri doldurması. <c>enable --apply</c> türetmeden önce aynı işlemde yüklediği satırlarla yeniden çağırır.
    /// </summary>
    /// <param name="linked">Dosyalara bağlı siparişler (başka dosyanınki de olabilir; dosya Id'siyle süzülür).</param>
    /// <param name="replicaBackfill">Lisansın kopyaları → geri doldurma tamam mı; listede olmayan kopya tamamlanmamış
    /// sayılır.</param>
    public static IReadOnlyList<ShipmentCheck> CheckShipments(
        IEnumerable<Shipment> shipments,
        IReadOnlyCollection<Order> linked,
        IReadOnlyDictionary<Guid, bool> replicaBackfill,
        IReadOnlySet<Guid> accepted)
    {
        var facts = linked.Select(o => o.ToLabelFacts()).ToList();
        var byShipment = linked
            .Where(o => o.ShipmentId is not null)
            .GroupBy(o => o.ShipmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        return shipments
            .OrderBy(s => s.Id)
            .Select(s =>
            {
                var orders = byShipment.GetValueOrDefault(s.Id) ?? [];
                return new ShipmentCheck(
                    s.Id,
                    s.CumulativeAmount,
                    LabelRules.DerivedShipmentAmount(s.Id.ToString("N"), facts),
                    orders.Where(o => o.CancelledAt is not null).Sum(o => o.Price),
                    orders.Count,
                    s.OriginReplicaId,
                    s.OriginReplicaId is { } origin && replicaBackfill.GetValueOrDefault(origin),
                    accepted.Contains(s.Id));
            })
            .ToList();
    }

    private static async Task<ClientVersionCheck> ClientVersionsAsync(
        LicenseDbContext db, Guid licenseId, SalesSyncOptions options, DateTimeOffset since, CancellationToken ct)
    {
        // Makine adı ve parmak izi okunmaz.
        var recent = await db.Activations.AsNoTracking()
            .Where(a => a.LicenseId == licenseId && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .OrderBy(a => a.Id)
            .Select(a => new OldActivation(a.Id, a.AppVersion))
            .ToListAsync(ct);

        var min = options.ParseMinClientVersion();
        if (min is null) return new ClientVersionCheck(null, recent.Count, []);

        var old = recent.Where(a => !ClientVersion.IsAtLeast(ClientVersion.Parse(a.AppVersion), min)).ToList();
        return new ClientVersionCheck(options.MinClientVersion!.Trim(), recent.Count, old);
    }

    private static async Task<ReplicaCheck> ReplicasAsync(
        LicenseDbContext db, Guid licenseId, DateTimeOffset since, CancellationToken ct)
    {
        // Makine adı ve parmak izi okunmaz.
        var recent = await db.WpfReplicas.AsNoTracking()
            .Where(r => r.LicenseId == licenseId && r.LastSeenAt >= since)
            .OrderBy(r => r.ReplicaId)
            .Select(r => new
            {
                r.ReplicaId, r.Protocol, r.FeatureLevel, r.BackfillCompletedAt, r.PaymentHistoryCompletedAt,
                r.HardwareFingerprintConflictAt,
            })
            .ToListAsync(ct);

        return new ReplicaCheck(
            recent.Count,
            recent.Where(r => r.Protocol != SalesEnvelope.V2).Select(r => r.ReplicaId).ToList(),
            recent.Where(r => r.FeatureLevel < RequiredFeatureLevel).Select(r => r.ReplicaId).ToList(),
            recent.Where(r => r.BackfillCompletedAt is null).Select(r => r.ReplicaId).ToList(),
            recent.Where(r => r.PaymentHistoryCompletedAt is null).Select(r => r.ReplicaId).ToList(),
            recent.Where(r => r.HardwareFingerprintConflictAt is not null).Select(r => r.ReplicaId).ToList());
    }
}
