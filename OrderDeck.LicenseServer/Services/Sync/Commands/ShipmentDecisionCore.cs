using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// Kargo kararını uygulamanın çekirdeği — spec 2026-10-13 §7.2 (Y5), §7.3, U12, M13. Kilidi, hedefin yüklenmesini ve
/// denetimini (kargolanmış, bayat sürüm) çağıran yapar (<see cref="ShipmentDecisionCommandHandler"/>). Kaydetmez: işlem,
/// SaveChanges ve jeton çakışması da çağıranındır.
/// </summary>
internal static class ShipmentDecisionCore
{
    /// <param name="ShipmentIds">Kararın uygulandığı dosyalar: hedef, sonra öbürleri en yenisi başta.</param>
    /// <param name="UnlinkedOrderIds">Kargoya verirken bağı çözülen siparişler (Id sırasıyla).</param>
    /// <param name="StatusChanged">En az bir dosyanın durumu gerçekten değişti.</param>
    internal sealed record Result(
        IReadOnlyList<Guid> ShipmentIds, IReadOnlyList<Guid> UnlinkedOrderIds, bool StatusChanged);

    /// <summary>
    /// Kararı hedefe ve hedef açıksa (Pending/Held) müşterinin öbür açık dosyalarına uygular (U12, tek havuz). Kargoya
    /// verirken türetilmiş dosyalarda şu anda iptal edilmiş bağlar çözülür ve tutar, durum değişmeden önce, bir kez
    /// türetilip donar; kabul edilmiş (türetilmemiş) dosyada bağ kalır, tutar saklanan değeriyle donar
    /// (<see cref="ShipmentShipping"/>). Her dosyada komut damgası basılır; dosyaya kapsamda ilk dokunan buysa sürüm bir
    /// artar (durum değişmese de; bağlamadan sonrası için <see cref="ShipmentBindCore.BindAsync"/>).
    /// </summary>
    /// <param name="customer">Asıl müşteri (çözülmüş); hedefin müşterisi taşınmamış bir kopya olabilir.</param>
    /// <param name="target">İzlenen, kargolanmamış hedef; bağlı siparişlerden ÖNCE yüklenmiş olmalı (jeton sırası).</param>
    /// <param name="decision"><see cref="ShipmentDecisionCommandHandler.ShipNow"/>,
    /// <see cref="ShipmentDecisionCommandHandler.Hold"/> ya da
    /// <see cref="ShipmentDecisionCommandHandler.RecipientPays"/>.</param>
    public static async Task<Result> ApplyAsync(
        SalesCommandScope scope, string customer, Shipment target, string decision, CancellationToken ct)
    {
        var decided = new List<Shipment> { target };
        if (target.Status is ShipmentStatus.Pending or ShipmentStatus.Held)
            decided.AddRange((await scope.LoadOpenShipmentsAsync(customer, target.CustomerId, ct))
                .Where(s => s.Id != target.Id));

        // Kargoya verme (§7.2): o anda iptal edilmiş bağlar çözülür, sonra tutar bir kez türetilip donar.
        var unlinked = new List<Guid>();
        if (decision == ShipmentDecisionCommandHandler.ShipNow)
        {
            var linked = await scope.LoadLinkedOrdersAsync(decided.Select(s => s.Id).ToList(), ct);
            foreach (var order in ShipmentShipping.UnlinkCancelled(linked, decided, scope.Now))
            {
                scope.MarkChanged(order, OrderUnit.Shipment);
                unlinked.Add(order.Id);
            }
            await scope.DeriveShipmentsAsync(decided, ct);
        }

        var statusChanged = false;
        foreach (var shipment in decided)
        {
            var before = shipment.Status;
            Decide(shipment, decision, scope.Now);
            statusChanged |= shipment.Status != before;
            shipment.CommandedAt = scope.Now;
            scope.MarkShipmentChanged(shipment);
        }

        return new Result(decided.Select(s => s.Id).ToList(), unlinked, statusChanged);
    }

    /// <summary>Yerel <c>ShipmentService.Decide</c>'ın aynası.</summary>
    private static void Decide(Shipment shipment, string decision, DateTimeOffset now)
    {
        switch (decision)
        {
            case ShipmentDecisionCommandHandler.ShipNow:
                shipment.Status = ShipmentStatus.Shipped;
                shipment.ShippedAt = now;
                break;
            case ShipmentDecisionCommandHandler.Hold:
                shipment.Status = ShipmentStatus.Held;
                // Yalnız ilk bekletmede; sonraki kararlar korur.
                shipment.HeldAt ??= now;
                break;
            case ShipmentDecisionCommandHandler.RecipientPays:
                shipment.Status = ShipmentStatus.RecipientPays;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision, null);
        }
    }
}
