using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Türetilmiş kargo dosyası (K7, DM7) — spec 2026-10-13 §4.2, §7.2. Mod On'da kabul edilen fiyat, iptal, yedek ya da
/// kargo bağı değişikliği, aynı SaveChanges'te ilgili dosyaların tutarını yeniden türetir: bağ değiştiyse eski ve yeni
/// dosya. Yalnız <c>AmountDerivedAt</c> dolu ve Shipped olmayan dosyada; Shipped dosyanın tutarı kargoya verildiği
/// anda donar, <c>--accept-shipment</c> ile kabul edilmiş (türetilmemiş) dosya saklanan tutarıyla kalır. Tutar kuralı
/// ortak koddadır (<see cref="LabelRules.DerivedShipmentAmount"/>); <c>orders/sync</c>, <c>sales/backfill</c> ve
/// komutlar aynı yolu kullanır.
///
/// <para><b>İzlenen birleşik durum.</b> Tutar veritabanındaki satırlardan değil, bu isteğin izlediği siparişlerle
/// birleştirilmiş hâlden hesaplanır: aynı SaveChanges'te yazılacak iptal, yeni bağ ya da yeni ekleme veritabanında
/// henüz yok. İzleyen sorgu zaten izlenen siparişin bellekteki hâlini döndürür; eklenen (veritabanında olmayan)
/// sipariş ayrıca verilir.</para>
///
/// <para><b>Sıra ve jeton.</b> Türetilen her dosyada <see cref="Shipment.Version"/> artar — tutar değişmese bile.
/// Dosyalar, türetmenin okuduğu bağlı siparişlerden ÖNCE yüklenmelidir (<see cref="LoadAsync"/>): bağlı bir siparişi
/// eşzamanlı değiştiren her yazıcı aynı dosyayı da türetir ve sürümünü artırır; o yazıcı bizim dosya okumamızdan
/// sonra kaydettiyse bizim dosya yazımımız jetona çarpar (409, istemci yeniden gönderir), önce kaydettiyse siparişleri
/// onun yazımından sonra okuruz. Sürüm artmasaydı, tutarı aynı çıkan bir türetme eşzamanlı ötekinin bayat okumasını
/// fark ettirmezdi.</para>
/// </summary>
public sealed class ShipmentAmountDeriver
{
    private readonly LicenseDbContext _db;

    public ShipmentAmountDeriver(LicenseDbContext db) => _db = db;

    /// <summary>Lisansın verilen dosyalarını izleyerek yükler; başka lisansın ya da var olmayan dosya dönmez.</summary>
    public async Task<Dictionary<Guid, Shipment>> LoadAsync(
        Guid licenseId, IEnumerable<Guid> shipmentIds, CancellationToken ct)
    {
        var ids = shipmentIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, Shipment>();
        return await _db.Shipments
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
    }

    /// <summary>
    /// Bir sipariş değişikliğinin etkilediği dosyalar: fiyat, iptal, yedek ya da bağ değiştiyse önceki ve sonraki bağ.
    /// Baskı tutara girmez (DM7 basılmamış etiketi de sayar).
    /// </summary>
    public static IEnumerable<Guid> AffectedBy(
        Guid? shipmentBefore, Guid? shipmentAfter, IReadOnlyCollection<OrderUnit> changedUnits)
    {
        if (!changedUnits.Any(u => u is OrderUnit.Price or OrderUnit.Cancel or OrderUnit.Backup or OrderUnit.Shipment))
            yield break;
        if (shipmentBefore is { } before) yield return before;
        if (shipmentAfter is { } after && after != shipmentBefore) yield return after;
    }

    /// <summary>
    /// Etkilenen dosyalardan türetilmiş ve Shipped olmayanların tutarını yeniden türetir; <c>UpdatedAt</c> ve
    /// <c>Version</c> ilerler. Kaydetmez: çağıranın SaveChanges'i sipariş yazımıyla birlikte yazar.
    /// </summary>
    /// <param name="shipments"><see cref="LoadAsync"/>'in yüklediği (izlenen) dosyalar.</param>
    /// <param name="affected">Etkilenen dosya Id'leri; yüklenmemiş ya da başka lisansın dosyası atlanır.</param>
    /// <param name="tracked">Bu isteğin izlediği siparişler (güncellenen ve eklenen), birleşik hâlleriyle.</param>
    /// <returns>Türetilen dosyalar.</returns>
    public async Task<IReadOnlyList<Guid>> DeriveAsync(
        Guid licenseId,
        IReadOnlyDictionary<Guid, Shipment> shipments,
        IEnumerable<Guid> affected,
        IEnumerable<Order> tracked,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var targets = affected.Distinct()
            .Select(id => shipments.GetValueOrDefault(id))
            .Where(s => s is { AmountDerivedAt: not null } && s.Status != ShipmentStatus.Shipped)
            .Select(s => s!)
            .ToList();
        if (targets.Count == 0) return Array.Empty<Guid>();

        var targetIds = targets.Select(s => s.Id).ToList();
        // İzleyen sorgu: izlenen siparişin bellekteki (birleşik) hâli döner; bağı bu istekte başka dosyaya geçen
        // sipariş veritabanındaki bağıyla gelir ama bellekteki bağıyla sayılır.
        var linked = await _db.Orders
            .Where(o => o.LicenseId == licenseId && o.ShipmentId != null && targetIds.Contains(o.ShipmentId.Value))
            .ToListAsync(ct);
        var facts = linked.Concat(tracked)
            .DistinctBy(o => o.Id)
            .Select(o => o.ToLabelFacts())
            .ToList();

        foreach (var shipment in targets)
        {
            shipment.CumulativeAmount = LabelRules.DerivedShipmentAmount(shipment.Id.ToString("N"), facts);
            shipment.UpdatedAt = now;
            shipment.Version++;
        }

        return targetIds;
    }
}
