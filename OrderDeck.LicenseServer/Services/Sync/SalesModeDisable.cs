using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Acil kapatmanın (<c>sales-sync disable</c>) kargo planı — spec 2026-10-13 §4.10 "<c>disable</c>", §3.1
/// <c>Shipments.OriginReplicaId</c>, §8.5, §8.7, M19, §12 risk 15, §15-38. Mod Off'ta Shipped olmayan her dosyanın tek
/// yazıcısı <c>OriginReplicaId</c>'deki kopyadır (<c>shipments/sync</c> son-gelen-kazanır, §3.1); <c>disable</c> onu
/// dosyayı açan kopyaya değil, <b>bağlı etiketlerin sahibine</b> göre yeniden yazar: Off'ta dosyayı yönetebilecek
/// bilgisayar etiketlerin yerelde durduğu bilgisayardır. Y2 altında yayın hep aynı PC'den açılır, yayın sonrası işler
/// başka PC'de yapılır; mod On'da PC-B'nin PC-A etiketleri için açtığı dosya olağandır ve açanın tutulması PC-A'yı
/// dosyasız, PC-B'yi etiketsiz bırakırdı (§15-38).
/// <list type="bullet">
///   <item><b>Siparişin sahibi:</b> oturumun <c>OwnerReplicaId</c>'si, boşsa (miras oturum ya da oturumsuz sipariş)
///   siparişin <c>OriginReplicaId</c>'si. İkisi de boşsa sahip bilinmez; sipariş paya girmez (yalnız sayılır).</item>
///   <item><b>Geri yükleme zinciri</b> (§8.7) en yeni kopyaya çözülür (<see cref="Newest"/>): ardıl, bu lisansta
///   <c>PreviousReplicaId</c>'si şimdiki halka olan kopya satırıdır; en çok <see cref="SalesReplicaService.MaxLineageDepth"/>
///   adım. Çatalda (aynı kopyadan iki geri yükleme) son görülen (<c>LastSeenAt</c>) seçilir, eşitlikte küçük Id. Döngüde
///   durulur. Kopya satırı olmayan sahip kendisidir.</item>
///   <item><b>Pay:</b> dosya ve sahip başına bağlı tutar DM7'nin katkısıdır: iptal edilmemiş, geçici yedek olmayan
///   bağlı siparişlerin fiyat toplamı. Yalnız iptal edilmiş etiketi bağlı olan kopya da sahiptir (payı 0): iptal bağı
///   çözmez (Y5).</item>
///   <item><b>Karar:</b> tek sahip → o kopya. Birden çok sahip (<b>karışık</b>, Y2 altında ancak iki PC'den yayın
///   açılmışsa) → payı büyük olan; eşitlikte küçük ReplicaId. Bağlı siparişi olmayan ya da hiçbir bağlı siparişinin
///   sahibi bilinmeyen dosya değişmez.</item>
/// </list>
///
/// <para><b>"Küçük ReplicaId"</b> GUID'in <c>"N"</c> metninin ordinal sırasıdır (<see cref="IdOrder"/>; tire sabit
/// konumlarda olduğu için çıktıdaki <c>"D"</c> metniyle aynı sıra). <see cref="Guid.CompareTo(Guid)"/> ve SQL Server'ın
/// <c>uniqueidentifier</c> sırası bayt düzenine baktığı için farklıdır; kural çıktıyı okuyan Burak'ın göreceği sırayla
/// aynı olsun diye metindir.</para>
///
/// <para>Salt okunur; kişisel veri okunmaz (müşteri, ad, metin ve makine adı yok). Yazımı CLI yapar.</para>
/// </summary>
public static class SalesModeDisable
{
    /// <summary>GUID'lerin <c>"N"</c> metnine göre ordinal sırası (bkz. sınıf dokümanı).</summary>
    public static readonly IComparer<Guid> IdOrder =
        Comparer<Guid>.Create((x, y) => string.CompareOrdinal(x.ToString("N"), y.ToString("N")));

    /// <summary>Shipped olmayan bir dosyaya bağlı siparişin sahiplik ve tutar hâli.</summary>
    /// <param name="SessionOwnerReplicaId">Siparişin oturumunun sahibi; oturum yoksa ya da miras oturumsa null.</param>
    /// <param name="OriginReplicaId">Siparişin kaynağı (kargo bağını bilen kopya).</param>
    public sealed record LinkedOrder(
        Guid Id,
        Guid ShipmentId,
        Guid? SessionOwnerReplicaId,
        Guid? OriginReplicaId,
        decimal Price,
        bool Cancelled,
        bool Tentative)
    {
        /// <summary>Oturumun sahibi, boşsa siparişin kaynağı; ikisi de boşsa bilinmez.</summary>
        public Guid? Owner => SessionOwnerReplicaId ?? OriginReplicaId;

        /// <summary>DM7 katkısı: iptal edilmiş ya da geçici yedekse 0.</summary>
        public decimal LinkedAmount => Cancelled || Tentative ? 0m : Price;
    }

    /// <summary>Lisansın bir kopya satırının geri yükleme zincirine giren hâli.</summary>
    public sealed record ReplicaLink(Guid ReplicaId, Guid? PreviousReplicaId, DateTimeOffset LastSeenAt);

    /// <summary>Bir kopyanın dosyadaki bağlı tutarı (zincir çözüldükten sonra).</summary>
    public sealed record OwnerShare(Guid ReplicaId, decimal Amount);

    public enum ShipmentOutcome
    {
        /// <summary>Yeni sahip şimdiki <c>OriginReplicaId</c>'den farklı: yazılır.</summary>
        Rewrite,

        /// <summary>Şimdiki <c>OriginReplicaId</c> zaten sahip: yazılmaz.</summary>
        AlreadyOwner,

        /// <summary>Bağlı sipariş yok ya da hiçbirinin sahibi bilinmiyor: değer değişmez.</summary>
        NoKnownOwner,
    }

    /// <summary>Bir dosyanın planı.</summary>
    /// <param name="Shares">Sahip başına bağlı tutar, kazanandan başlayarak: tutar büyükten küçüğe, eşitlikte
    /// <see cref="IdOrder"/>.</param>
    /// <param name="LinkedOrders">Bağlı sipariş sayısı (sahibi bilinmeyenler dahil).</param>
    public sealed record ShipmentPlan(
        Guid Id, Guid? CurrentOriginReplicaId, IReadOnlyList<OwnerShare> Shares, int LinkedOrders)
    {
        /// <summary>Kazanan sahip; sahip bilinmiyorsa null.</summary>
        public Guid? NewOriginReplicaId => Shares.Count > 0 ? Shares[0].ReplicaId : null;

        /// <summary>Birden çok kopyanın etiketi bağlı (risk 15).</summary>
        public bool Mixed => Shares.Count > 1;

        public ShipmentOutcome Outcome =>
            NewOriginReplicaId is not { } owner ? ShipmentOutcome.NoKnownOwner
            : owner == CurrentOriginReplicaId ? ShipmentOutcome.AlreadyOwner
            : ShipmentOutcome.Rewrite;
    }

    /// <summary>Lisansın Shipped olmayan dosyalarının planı.</summary>
    /// <param name="Shipments">Dosyalar, Id'ye göre <see cref="IdOrder"/> sırasıyla.</param>
    /// <param name="LinkedOrdersWithUnknownOwner">Oturum sahibi de kaynağı da boş bağlı siparişler.</param>
    public sealed record Plan(IReadOnlyList<ShipmentPlan> Shipments, int LinkedOrdersWithUnknownOwner)
    {
        public int LinkedOrders => Shipments.Sum(s => s.LinkedOrders);

        public IReadOnlyList<ShipmentPlan> Rewrites => Of(ShipmentOutcome.Rewrite);

        public IReadOnlyList<ShipmentPlan> AlreadyOwned => Of(ShipmentOutcome.AlreadyOwner);

        public IReadOnlyList<ShipmentPlan> NoKnownOwner => Of(ShipmentOutcome.NoKnownOwner);

        public IReadOnlyList<ShipmentPlan> Mixed => Shipments.Where(s => s.Mixed).ToList();

        private List<ShipmentPlan> Of(ShipmentOutcome outcome) => Shipments.Where(s => s.Outcome == outcome).ToList();
    }

    /// <summary>
    /// Verilen (Shipped olmayan) dosyaların planı: bağlı siparişleri ve lisansın kopya satırlarını okur. Dosyaları
    /// çağıran yükler — <c>--apply</c> izleyerek, aynı işlemde (sürüm jetonu).
    /// </summary>
    public static async Task<Plan> PlanAsync(
        LicenseDbContext db, Guid licenseId, IReadOnlyCollection<Shipment> shipments, CancellationToken ct)
    {
        var ids = shipments.Select(s => s.Id).ToList();
        var linked = ids.Count == 0
            ? []
            : await db.Orders.AsNoTracking()
                .Where(o => o.LicenseId == licenseId && o.ShipmentId != null && ids.Contains(o.ShipmentId.Value))
                .Select(o => new LinkedOrder(
                    o.Id,
                    o.ShipmentId!.Value,
                    o.Session != null ? o.Session.OwnerReplicaId : null,
                    o.OriginReplicaId,
                    o.Price,
                    o.CancelledAt != null,
                    o.IsTentativeBackup))
                .ToListAsync(ct);
        // Makine adı ve parmak izi okunmaz.
        var replicas = await db.WpfReplicas.AsNoTracking()
            .Where(r => r.LicenseId == licenseId)
            .Select(r => new ReplicaLink(r.ReplicaId, r.PreviousReplicaId, r.LastSeenAt))
            .ToListAsync(ct);
        return Build(shipments, linked, replicas);
    }

    /// <summary>Planın saf hâli; bkz. sınıf dokümanı.</summary>
    /// <param name="linked">Dosyalara bağlı siparişler (başka dosyanınki de olabilir; dosya Id'siyle süzülür).</param>
    /// <param name="replicas">Lisansın kopya satırları.</param>
    public static Plan Build(
        IEnumerable<Shipment> shipments, IReadOnlyCollection<LinkedOrder> linked, IReadOnlyCollection<ReplicaLink> replicas)
    {
        var successors = Successors(replicas);
        var newest = new Dictionary<Guid, Guid>();
        Guid Resolve(Guid replica) =>
            newest.TryGetValue(replica, out var known) ? known : (newest[replica] = Newest(replica, successors));

        var byShipment = linked.GroupBy(o => o.ShipmentId).ToDictionary(g => g.Key, g => g.ToList());
        var plans = shipments
            .OrderBy(s => s.Id, IdOrder)
            .Select(s =>
            {
                var orders = byShipment.GetValueOrDefault(s.Id) ?? [];
                var shares = orders
                    .Where(o => o.Owner is not null)
                    .GroupBy(o => Resolve(o.Owner!.Value))
                    .Select(g => new OwnerShare(g.Key, g.Sum(o => o.LinkedAmount)))
                    .OrderByDescending(share => share.Amount)
                    .ThenBy(share => share.ReplicaId, IdOrder)
                    .ToList();
                return new ShipmentPlan(s.Id, s.OriginReplicaId, shares, orders.Count);
            })
            .ToList();

        var shipmentIds = plans.Select(p => p.Id).ToHashSet();
        var unknownOwner = linked.Count(o => o.Owner is null && shipmentIds.Contains(o.ShipmentId));
        return new Plan(plans, unknownOwner);
    }

    /// <summary>Kopyanın geri yükleme zincirindeki en yeni halkası (bkz. sınıf dokümanı).</summary>
    /// <param name="replicas">Lisansın kopya satırları.</param>
    public static Guid Newest(Guid replicaId, IReadOnlyCollection<ReplicaLink> replicas) =>
        Newest(replicaId, Successors(replicas));

    private static ILookup<Guid, ReplicaLink> Successors(IEnumerable<ReplicaLink> replicas) =>
        replicas.Where(r => r.PreviousReplicaId is not null).ToLookup(r => r.PreviousReplicaId!.Value);

    private static Guid Newest(Guid replicaId, ILookup<Guid, ReplicaLink> successors)
    {
        var current = replicaId;
        var visited = new HashSet<Guid> { current };
        for (var hop = 0; hop < SalesReplicaService.MaxLineageDepth; hop++)
        {
            var next = successors[current]
                .OrderByDescending(r => r.LastSeenAt)
                .ThenBy(r => r.ReplicaId, IdOrder)
                .FirstOrDefault();
            if (next is null || !visited.Add(next.ReplicaId)) break;
            current = next.ReplicaId;
        }
        return current;
    }
}
