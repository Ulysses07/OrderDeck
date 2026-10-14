using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// <see cref="Order"/> ile ortak birim kuralının (<see cref="OrderUnitMerge"/>) tipleri arasındaki eşleme. Birleştirme
/// kararını kural verir; bu sınıf yalnız taşır.
/// </summary>
public static class OrderUnitMapping
{
    /// <summary>Satırın birleştirmeye giren hâli.</summary>
    public static OrderServerRow ToServerRow(this Order order) => new()
    {
        Values = new OrderUnitValues
        {
            Price = order.Price,
            PriceCorrectedAt = order.PriceCorrectedAt,
            PaidPrice = order.PaidPrice,
            CancelledAt = order.CancelledAt,
            CancelReason = order.CancelReason,
            IsTentativeBackup = order.IsTentativeBackup,
            ShipmentId = order.ShipmentId,
            PrintedAt = order.PrintedAt,
            PrintBatchId = order.PrintBatchId,
            PrintSeq = order.PrintSeq,
            ParentOrderId = order.ParentOrderId,
        },
        PriceSetAt = order.PriceSetAt,
        CancelSetAt = order.CancelSetAt,
        BackupSetAt = order.BackupSetAt,
        ShipmentSetAt = order.ShipmentSetAt,
        Version = order.SyncVersion,
    };

    /// <summary>Birimin teldeki adı (<c>orders/sync</c> v2 yanıtının <c>ignoredUnits</c>'i, <c>sales/backfill</c>'in
    /// <c>diverged.units</c>'i).</summary>
    public static string WireName(this OrderUnit unit) => unit switch
    {
        OrderUnit.Price => "price",
        OrderUnit.Cancel => "cancel",
        OrderUnit.Backup => "backup",
        OrderUnit.Shipment => "shipment",
        OrderUnit.Print => "print",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>
    /// Ortak para kurallarının (<see cref="LabelRules"/>) girdisi. Id'ler masaüstüyle aynı "N" biçiminde metin.
    /// </summary>
    public static LabelFacts ToLabelFacts(this Order order) => new(
        Id: order.Id.ToString("N"),
        Code: order.Code,
        Price: order.Price,
        Printed: order.PrintedAt is not null,
        Cancelled: order.CancelledAt is not null,
        Tentative: order.IsTentativeBackup,
        ShippingFee: order.IsShippingFee,
        ShipmentId: order.ShipmentId?.ToString("N"));

    /// <summary>
    /// Birleştirmenin sonucunu satıra yazar. Yalnız birimleri ve <see cref="Order.ParentOrderId"/>'yi yazar; oluşum
    /// alanlarına (K1) ve damgalara dokunmaz. Değeri değişmeyen alan EF'te değişiklik üretmez.
    /// </summary>
    public static void ApplyUnits(this Order order, OrderUnitValues merged)
    {
        order.Price = merged.Price;
        order.PriceCorrectedAt = merged.PriceCorrectedAt;
        order.PaidPrice = merged.PaidPrice;
        order.CancelledAt = merged.CancelledAt;
        order.CancelReason = merged.CancelReason;
        order.IsTentativeBackup = merged.IsTentativeBackup;
        order.ShipmentId = merged.ShipmentId;
        order.PrintedAt = merged.PrintedAt;
        order.PrintBatchId = merged.PrintBatchId;
        order.PrintSeq = merged.PrintSeq;
        order.ParentOrderId = merged.ParentOrderId;
    }
}
