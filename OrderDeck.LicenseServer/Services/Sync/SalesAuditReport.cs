using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// <c>sales-audit</c>'in salt okunur raporu — spec 2026-10-13 §4.10. Bir lisansın satış senkronu sağlığını sayılar ve
/// Id'lerle verir; hiçbir şey yazmaz (bütün sorgular izlemesiz, <c>SaveChanges</c> yok). Mod kapısının (S3,
/// <c>sales-sync enable</c>) <see cref="SalesModeGate"/>'i aynı raporu okur (koşul 5 ve rapor 7).
///
/// <para><b>Kişisel veri yok.</b> Rapor kullanıcı adı, görünen ad, mesaj metni, ürün kodu ya da makine adı taşımaz;
/// GUID olmayan (yerel, ham) <c>CustomerId</c>'ler de yalnız sayılır, çünkü ham değerin içeriği bilinmiyor. Kargo ayarı
/// tutar olduğu için yazılır.</para>
///
/// <para><b>Veritabanından ölçülemeyenler.</b> İki kalem bugün kalıcı bir iz bırakmıyor, yalnız günlüğe düşüyor; rapor
/// onları sayı yerine günlük satırının başlığıyla gösterir (<see cref="LogOnlyItems"/>): force-end sonrası K5
/// istisnasıyla alınan birimler ve açılış penceresi dışında On kurallarıyla yargılanan <c>SeenMode = 0</c> istekleri
/// (ikisi de <c>orders/sync</c> ve kopya kaydının sayım günlükleri). Aynı ReplicaId'ye ikinci donanım parmak izi ise
/// kopya satırında kalıcıdır (<see cref="WpfReplica.HardwareFingerprintConflictAt"/>) ve sayılır.</para>
/// </summary>
public static class SalesAuditReport
{
    /// <summary>Bir listede en çok bu kadar Id basılır; sayı her zaman tamdır.</summary>
    public const int MaxListedIds = 20;

    /// <summary>Devredilmemiş oturum için eşik (§4.10, mod kapısı koşulu 5): canlı oturum başlangıcından, devrediliyor
    /// oturum bitişinden bu kadar sonra hâlâ devredilmemişse takılı sayılır.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private const string KindPurchaseDeduction = "purchase-deduction";

    /// <summary>Veritabanında izi olmayan, yalnız günlükte görülen kalemler: başlık ve aranacak günlük metni.</summary>
    public static readonly IReadOnlyList<(string Item, string LogText)> LogOnlyItems =
    [
        ("Force-end sonrası K5 istisnasıyla alınan birim", "K5 istisnasıyla"),
        ("Açılış penceresi dışında On kurallarıyla yargılanan SeenMode = 0", "açılış penceresi dışında"),
    ];

    /// <param name="Live">Canlı (EndedAt boş); değilse devrediliyor.</param>
    public sealed record StaleSession(Guid Id, bool Live, Guid? OwnerReplicaId);

    public sealed record ReplicaShipping(Guid ReplicaId, decimal? Threshold, decimal? Fee);

    /// <param name="TransactionIds">Kapsamın geri alınmamış düşümleri.</param>
    public sealed record ScopeDeductions(Guid WpfCustomerId, IReadOnlyList<Guid> TransactionIds);

    /// <param name="Codes">Bu yayında tek fiyat kuralını bozan kod sayısı.</param>
    public sealed record PriceViolation(Guid SessionId, int Codes);

    public sealed record ReplicaDivergence(Guid ReplicaId, int Local, int Server);

    /// <param name="StaleSessions">24 saatten eski canlı ya da devrediliyor oturumlar.</param>
    /// <param name="OrdersWithoutSession">SessionId boş sipariş sayısı (akışa girmez, §4.4).</param>
    /// <param name="CustomerIdsWithoutProjection">GUID olan ama lisansta projeksiyonu (kopyası dahil) olmayan farklı
    /// CustomerId'ler ("N" biçiminde).</param>
    /// <param name="OrdersWithNonGuidCustomer">CustomerId'si GUID olmayan sipariş sayısı (§6.4: ham Id ile eşlenir).</param>
    /// <param name="OrdersMissingReleaseStamp">Oturumu devredilmiş ama SessionReleasedAt'i boş sipariş (DM3; onarım
    /// işi kapatır).</param>
    /// <param name="ReportedShipping">Kargo ayarı bildiren kopyalar (en az bir değeri dolu).</param>
    /// <param name="ReplicasWithoutShipping">Hiç değer bildirmemiş (ya da kargoyu kapalı bildiren) kopya sayısı.</param>
    /// <param name="PriceViolations">Tek fiyat ihlali olan yayınlar (DM5).</param>
    /// <param name="DuplicateDeductions">Aynı müşteri satırı × kapsamda birden çok geri alınmamış düşüm (§6.6).</param>
    /// <param name="ShipmentsWithoutOrigin">Shipped olmayan ve OriginReplicaId'si boş dosyalar.</param>
    /// <param name="ShipmentsWithAmountWithoutLinks">Shipped olmayan, tutarı &gt; 0 ve bağlı siparişi olmayan dosyalar.</param>
    /// <param name="OrdersWithUnknownLink">Kargo bağı bilinmeyen (OriginReplicaId boş) bütün siparişler.</param>
    /// <param name="UnknownLinkCandidates">Bunlardan bağsız, iptal edilmemiş, geçici yedek olmayanlar — kaynağı bilinse
    /// <c>attach-shipment</c> adayı olurlardı (M22).</param>
    /// <param name="UnknownLinkCandidateAmount">O siparişlerin fiyat toplamı.</param>
    /// <param name="BackfillDivergence">Geri doldurma birim ayrışması olan kopyalar (§4.3).</param>
    /// <param name="FingerprintConflicts">Aynı ReplicaId'ye iki donanım parmak izi görülmüş kopyalar (disk klonu,
    /// §8.7); mod kapısı bunları reddeder (§4.10-2).</param>
    public sealed record Result(
        IReadOnlyList<StaleSession> StaleSessions,
        int OrdersWithoutSession,
        IReadOnlyList<Guid> OrdersWithoutSessionSample,
        IReadOnlyList<string> CustomerIdsWithoutProjection,
        int OrdersWithNonGuidCustomer,
        int OrdersMissingReleaseStamp,
        IReadOnlyList<ReplicaShipping> ReportedShipping,
        int ReplicasWithoutShipping,
        IReadOnlyList<PriceViolation> PriceViolations,
        IReadOnlyList<ScopeDeductions> DuplicateDeductions,
        IReadOnlyList<Guid> ShipmentsWithoutOrigin,
        IReadOnlyList<Guid> ShipmentsWithAmountWithoutLinks,
        int OrdersWithUnknownLink,
        int UnknownLinkCandidates,
        decimal UnknownLinkCandidateAmount,
        IReadOnlyList<ReplicaDivergence> BackfillDivergence,
        IReadOnlyList<Guid> FingerprintConflicts)
    {
        /// <summary>Bildirilen farklı kargo ayarı sayısı; 1'den büyükse kopyalar ayrışıyor (§6.2).</summary>
        public int DistinctReportedShipping => ReportedShipping.Select(r => (r.Threshold, r.Fee)).Distinct().Count();
    }

    /// <summary>Raporu kurar.</summary>
    /// <returns>Lisans yoksa null.</returns>
    public static async Task<Result?> BuildAsync(
        LicenseDbContext db, Guid licenseId, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Licenses.AsNoTracking().AnyAsync(l => l.Id == licenseId, ct)) return null;

        var cutoff = now - StaleAfter;
        var staleSessions = await db.StreamSessions.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && s.ReleasedAt == null && (s.EndedAt ?? s.StartedAt) < cutoff)
            .OrderBy(s => s.StartedAt).ThenBy(s => s.Id)
            .Select(s => new StaleSession(s.Id, s.EndedAt == null, s.OwnerReplicaId))
            .ToListAsync(ct);

        var ordersWithoutSession = await db.Orders.AsNoTracking()
            .CountAsync(o => o.LicenseId == licenseId && o.SessionId == null, ct);
        var ordersWithoutSessionSample = await db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId && o.SessionId == null)
            .OrderBy(o => o.Id)
            .Select(o => o.Id)
            .Take(MaxListedIds)
            .ToListAsync(ct);

        var (withoutProjection, nonGuidOrders) = await CustomerIdsAsync(db, licenseId, ct);

        var missingStamp = await db.Orders.AsNoTracking()
            .CountAsync(o => o.LicenseId == licenseId && o.SessionReleasedAt == null
                && o.Session != null && o.Session.ReleasedAt != null, ct);

        var replicas = await db.WpfReplicas.AsNoTracking()
            .Where(r => r.LicenseId == licenseId)
            .OrderBy(r => r.ReplicaId)
            .Select(r => new
            {
                r.ReplicaId, r.ReportedThreshold, r.ReportedFee, r.BackfillDivergedLocal, r.BackfillDivergedServer,
                r.HardwareFingerprintConflictAt,
            })
            .ToListAsync(ct);
        var reportedShipping = replicas
            .Where(r => r.ReportedThreshold is not null || r.ReportedFee is not null)
            .Select(r => new ReplicaShipping(r.ReplicaId, r.ReportedThreshold, r.ReportedFee))
            .ToList();
        var divergence = replicas
            .Where(r => r.BackfillDivergedLocal > 0 || r.BackfillDivergedServer > 0)
            .Select(r => new ReplicaDivergence(r.ReplicaId, r.BackfillDivergedLocal, r.BackfillDivergedServer))
            .ToList();
        var fingerprintConflicts = replicas
            .Where(r => r.HardwareFingerprintConflictAt is not null)
            .Select(r => r.ReplicaId)
            .ToList();

        var priceViolations = await PriceViolationsAsync(db, licenseId, ct);
        var duplicateDeductions = await DuplicateDeductionsAsync(db, licenseId, ct);

        var shipmentsWithoutOrigin = await db.Shipments.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && s.Status != ShipmentStatus.Shipped && s.OriginReplicaId == null)
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var shipmentsWithAmountWithoutLinks = await db.Shipments.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && s.Status != ShipmentStatus.Shipped && s.CumulativeAmount > 0
                && !db.Orders.Any(o => o.LicenseId == licenseId && o.ShipmentId == s.Id))
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var unknownLink = await db.Orders.AsNoTracking()
            .CountAsync(o => o.LicenseId == licenseId && o.OriginReplicaId == null, ct);
        var candidates = db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId && o.OriginReplicaId == null
                && o.ShipmentId == null && o.CancelledAt == null && !o.IsTentativeBackup);
        var candidateCount = await candidates.CountAsync(ct);
        var candidateAmount = await candidates.SumAsync(o => (decimal?)o.Price, ct) ?? 0m;

        return new Result(
            staleSessions,
            ordersWithoutSession,
            ordersWithoutSessionSample,
            withoutProjection,
            nonGuidOrders,
            missingStamp,
            reportedShipping,
            replicas.Count - reportedShipping.Count,
            priceViolations,
            duplicateDeductions,
            shipmentsWithoutOrigin,
            shipmentsWithAmountWithoutLinks,
            unknownLink,
            candidateCount,
            candidateAmount,
            divergence,
            fingerprintConflicts);
    }

    /// <summary>
    /// Projeksiyonu olmayan GUID CustomerId'ler ve GUID olmayan CustomerId'li sipariş sayısı. GUID "N" biçimidir
    /// (<see cref="SalesCustomerDirectory"/> ile aynı); birleştirilmiş kopya da bir projeksiyondur (akış onu asıl kayda
    /// çözer), bu yüzden sorgu süzgeci kaldırılır.
    /// </summary>
    private static async Task<(IReadOnlyList<string> WithoutProjection, int NonGuidOrders)> CustomerIdsAsync(
        LicenseDbContext db, Guid licenseId, CancellationToken ct)
    {
        var perCustomer = await db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId)
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Orders = g.Count() })
            .ToListAsync(ct);
        var projections = (await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.LicenseId == licenseId)
                .Select(p => p.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var withoutProjection = new List<string>();
        var nonGuidOrders = 0;
        foreach (var row in perCustomer)
        {
            if (!Guid.TryParseExact(row.CustomerId, "N", out var id))
                nonGuidOrders += row.Orders;
            else if (!projections.Contains(id))
                withoutProjection.Add(id.ToString("N"));
        }
        withoutProjection.Sort(StringComparer.Ordinal);
        return (withoutProjection, nonGuidOrders);
    }

    /// <summary>
    /// DM5: bir yayında bir kodun kargoya verilmemiş, iptal edilmemiş, kargo ücreti olmayan etiketlerinin tek fiyatı
    /// vardır (#508 F1). Kurallar ortak koddan (<see cref="LabelRules"/>) ve bellekte: SQL harmanı Türkçe harfleri
    /// katlamaz. Yayının her kodu için (kırpılmış kod, harf duyarsız) kodun etiketleri <see cref="LabelRules.SameCode"/>
    /// ile seçilir, fiyat <see cref="LabelRules.CodePrice(IReadOnlyList{LabelFacts}, Func{string, SalesShipmentStatus?})"/>;
    /// o fiyattan farklı bir düzeltilebilir etiket varsa kod ihlaldedir.
    /// </summary>
    private static async Task<IReadOnlyList<PriceViolation>> PriceViolationsAsync(
        LicenseDbContext db, Guid licenseId, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId && o.SessionId != null && o.Code != null)
            .Select(o => new
            {
                SessionId = o.SessionId!.Value, o.Id, o.Code, o.Price, o.AddedAt, o.PrintedAt, o.CancelledAt,
                o.IsTentativeBackup, o.IsShippingFee, o.ShipmentId,
            })
            .ToListAsync(ct);
        var statuses = await db.Shipments.AsNoTracking()
            .Where(s => s.LicenseId == licenseId)
            .Select(s => new { s.Id, s.Status })
            .ToDictionaryAsync(s => s.Id.ToString("N"), s => (SalesShipmentStatus)(int)s.Status, ct);
        SalesShipmentStatus? StatusOf(string id) => statuses.TryGetValue(id, out var status) ? status : null;

        var violations = new List<PriceViolation>();
        foreach (var session in orders.GroupBy(o => o.SessionId).OrderBy(g => g.Key))
        {
            var labels = session
                .OrderBy(o => o.AddedAt).ThenBy(o => o.Id)
                .Select(o => new LabelFacts(
                    o.Id.ToString("N"), o.Code, o.Price, o.PrintedAt is not null, o.CancelledAt is not null,
                    o.IsTentativeBackup, o.IsShippingFee, o.ShipmentId?.ToString("N")))
                .ToList();
            var codes = labels
                .Select(l => LabelRules.CodeKey(l.Code))
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var violating = 0;
            foreach (var code in codes)
            {
                var ofCode = labels.Where(l => LabelRules.SameCode(l.Code, code)).ToList();
                var price = LabelRules.CodePrice(ofCode, StatusOf);
                if (ofCode.Any(l => LabelRules.Classify(l, StatusOf) == LabelPriceClass.Correctable && l.Price != price))
                    violating++;
            }
            if (violating > 0) violations.Add(new PriceViolation(session.Key, violating));
        }
        return violations;
    }

    /// <summary>
    /// §6.6, DM6: aynı müşteri satırı × kapsamda birden çok geri alınmamış <c>purchase-deduction</c> — <c>scope</c>
    /// ucunun yüklemiyle aynı (aynı satır, aynı kapsam, düşüm, geri alınmamış). Kapsam karşılaştırması harf duyarsız:
    /// SQL'deki eşitlik de öyle.
    /// </summary>
    private static async Task<IReadOnlyList<ScopeDeductions>> DuplicateDeductionsAsync(
        LicenseDbContext db, Guid licenseId, CancellationToken ct)
    {
        var rows = await db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && t.Kind == KindPurchaseDeduction && t.SaleScope != null
                && !db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
            .Select(t => new { t.Id, t.WpfCustomerId, t.SaleScope, t.CreatedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(t => (t.WpfCustomerId, Scope: t.SaleScope!.ToUpperInvariant()))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.WpfCustomerId).ThenBy(g => g.Min(t => t.CreatedAt))
            .Select(g => new ScopeDeductions(
                g.Key.WpfCustomerId, g.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Select(t => t.Id).ToList()))
            .ToList();
    }
}
