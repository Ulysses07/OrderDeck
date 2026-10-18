using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Services.Sales;

/// <summary>
/// Yalnız yerel kargo işlemleri (spec 2026-10-13 §5.3, mod Off yolu): bugünkü <see cref="ShipmentService"/> çağrıları,
/// kip okunmadan, çağıranın iş parçacığında, beklemeden — dönen görev tamamlanmış olarak döner, hata çağırana
/// fırlar. Düzenleyici verilmeyen dekont ekranı (testlerin çoğu) bunu kullanır; uygulamada DI
/// <see cref="SalesEditRouter"/>'ı verir, onun mod Off yolu da bu sınıftır.
/// </summary>
public sealed class LocalShipmentEditor(ShipmentService shipments) : IShipmentEditor
{
    public Task<SalesShipmentStepResult> RunDekontShipmentStepAsync(
        string customerId, ShipmentDirective directive, CancellationToken ct = default) =>
        Task.FromResult(SalesShipmentStepResult.Local(shipments.AttachAfterPayment(customerId, directive)));

    public Task<SalesShipmentDecisionResult> DecideShipmentAsync(
        string shipmentId, ShipmentDecision decision, CancellationToken ct = default)
    {
        var updated = shipments.ApplyDecision(shipmentId, decision, out var pooled);
        return Task.FromResult(new SalesShipmentDecisionResult(updated, pooled, null, []));
    }
}
