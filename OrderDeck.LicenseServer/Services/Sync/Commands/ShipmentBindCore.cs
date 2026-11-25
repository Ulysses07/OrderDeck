using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// Siparişleri müşterinin kargo dosyasına bağlamanın çekirdeği — spec 2026-10-13 §4.6, §7.1, DM7. Kilidi, yüklemeyi
/// (dosyalar adaylardan ÖNCE) ve neyin bağlanacağını (aday kümesi, <c>expected</c>) çağıran yapar
/// (<see cref="AttachShipmentCommandHandler"/>).
/// </summary>
internal static class ShipmentBindCore
{
    /// <summary>
    /// Siparişleri en yeni türetilmiş açık dosyaya bağlar; yoksa dosyayı <paramref name="proposedShipmentId"/> ile açar
    /// (Id sunucuda varsa 409 <c>content-conflict</c>; hiçbir şey yazılmaz). Kabul edilmiş (türetilmemiş) dosya hedef
    /// olmaz (§4.10-6). Bağlardan sonra dosya bir kez türetilir, ardından komut damgası basılır: sürüm bir artar.
    /// </summary>
    /// <param name="customer">Asıl müşteri (çözülmüş).</param>
    /// <param name="open">Müşterinin izlenen açık dosyaları, en yenisi başta. Açılan dosya başına eklenir: çağıranın
    /// havuz hesabı bu listeyi okur.</param>
    /// <param name="orders">Bağlanacak, kapsamın izlediği siparişler; boş olamaz (boş kümede dosya açılmaz kararı
    /// çağıranın).</param>
    /// <param name="originReplicaId">Açılan dosyanın kaynak kopyası.</param>
    /// <returns>Siparişlerin bağlandığı dosya.</returns>
    public static async Task<Shipment> BindAsync(
        SalesCommandScope scope,
        string customer,
        List<Shipment> open,
        IReadOnlyList<Order> orders,
        Guid proposedShipmentId,
        Guid originReplicaId,
        CancellationToken ct)
    {
        var target = open.FirstOrDefault(s => s.AmountDerivedAt is not null);
        if (target is null)
        {
            if (await scope.ShipmentIdTakenAsync(proposedShipmentId, ct))
                throw SalesCommandRejection.ShipmentContentConflict(proposedShipmentId);

            target = new Shipment
            {
                Id = proposedShipmentId,
                LicenseId = scope.LicenseId,
                CustomerId = customer,
                Status = ShipmentStatus.Pending,
                CumulativeAmount = 0m,
                CreatedAt = scope.Now,
                UpdatedAt = scope.Now,
                AmountDerivedAt = scope.Now,
                OriginReplicaId = originReplicaId,
            };
            scope.AddShipment(target);
            open.Insert(0, target);
        }

        foreach (var order in orders)
        {
            order.ShipmentId = target.Id;
            order.ShipmentSetAt = scope.Now;
            scope.MarkChanged(order, OrderUnit.Shipment);
        }

        // Bağlar yazıldıktan sonra tek türetme (sürüm bir artar); ardından komut damgası.
        await scope.DeriveShipmentsAsync([target], ct);
        target.CommandedAt = scope.Now;
        scope.MarkShipmentChanged(target);
        return target;
    }
}
