using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>confirm-backup</c> komutu — spec 2026-10-13 §4.6 tablosu, §7.2. Yük <c>{backupOrderId, newPrice?}</c>; yerel ayna
/// <c>LabelService.ConfirmBackup</c>.
/// <list type="bullet">
///   <item>Geçici yedek olmayan sipariş (zaten onaylanmış ya da hiç yedek değil) etkisizdir: hiçbir alanı, damgası ve
///   sürümü değişmez, durum <c>unchanged</c> — yerelde de "zaten onaylı" sessizce döner.</item>
///   <item>Geçici yedekte <c>IsTentativeBackup</c> = 0 ve birim damgası <c>BackupSetAt</c> (sunucu saati; bayat push
///   yedeği geri getiremez — zaten tek yönlü — ve K2). Yeni fiyat verildiyse ve farklıysa <c>Price</c> ve
///   <c>PriceSetAt</c> da; <c>PriceCorrectedAt</c> ve <c>PaidPrice</c> yazılmaz (yereldeki onay fiyatı düz yazar, bu bir
///   düzeltme değil pazarlıktır).</item>
///   <item>Kargoya verilmiş dosyaya bağlı siparişin fiyatı değişmez (K6, <see cref="LabelRules.Classify"/>'ın
///   <see cref="LabelPriceClass.Shipped"/> sınıfı): fiyatlı onay 409 <c>shipment-shipped</c> ile reddedilir, hiçbir şey
///   yazılmaz — onayı istenmeyen bir fiyatla sessizce yapmak yerine. Geçici yedek normalde hiçbir dosyaya bağlanmaz
///   (bağlanmamış etiket süzgeci onu dışarıda bırakır), bu bir savunmadır.</item>
///   <item>Stok defteri birleşik durumdan (onaylanan yedek artık satıştır) ve bağlı türetilmiş dosya (K7) servis
///   tarafından; yedek normalde bağsız olduğu için dosya değişmez (§7.2).</item>
/// </list>
/// Sipariş devredilmiş bir yayında olmalı (409 <c>session-live</c>) ve <c>expected</c> tam olarak onun güncel sürümünü
/// taşımalı (409 <c>stale</c>).
/// </summary>
public sealed class ConfirmBackupCommandHandler : ISalesCommandHandler
{
    public const string KindName = "confirm-backup";

    public string Kind => KindName;

    /// <summary>Kanonik yük: yedek ve (verildiyse) iki ondalıklı fiyat.</summary>
    public sealed record Payload(Guid BackupOrderId, decimal? NewPrice);

    private sealed record Body(Guid? BackupOrderId, decimal? NewPrice);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (body.BackupOrderId is not { } backupOrderId || backupOrderId == Guid.Empty)
        {
            error = "backupOrderId boş olamaz.";
            return false;
        }

        decimal? newPrice = null;
        if (body.NewPrice is { } raw)
        {
            if (!SalesCommandPayload.TryReadPrice(raw, "newPrice", out var price, out error)) return false;
            newPrice = price;
        }
        if (!SalesCommandPayload.TryMatchExpected([backupOrderId], expected, out error)) return false;

        command = new Command(new Payload(backupOrderId, newPrice));
        return true;
    }

    private sealed class Command(Payload payload) : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds { get; } = [payload.BackupOrderId];

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            var orders = await scope.LoadOrdersAsync(OrderIds, ct);
            var order = orders[payload.BackupOrderId];
            await scope.EnsureReleasedAsync([order], ct);
            scope.EnsureExpectedVersions([order]);

            if (!order.IsTentativeBackup) return new SalesCommandOutcome([], OrderIds);

            var priceChanges = payload.NewPrice is { } newPrice && newPrice != order.Price;
            if (priceChanges && order.ShipmentId is { } shipmentId)
            {
                var shipments = await scope.LoadShipmentsAsync([shipmentId], ct);
                var shipped = LabelRules.Classify(
                    order.ToLabelFacts(),
                    _ => shipments.TryGetValue(shipmentId, out var s) ? (SalesShipmentStatus)(int)s.Status : null);
                if (shipped == LabelPriceClass.Shipped)
                    throw SalesCommandRejection.ShipmentShipped(order.Id, shipmentId);
            }

            order.IsTentativeBackup = false;
            order.BackupSetAt = scope.Now;
            if (priceChanges)
            {
                order.Price = payload.NewPrice!.Value;
                order.PriceSetAt = scope.Now;
                scope.MarkChanged(order, OrderUnit.Backup, OrderUnit.Price);
            }
            else
            {
                scope.MarkChanged(order, OrderUnit.Backup);
            }

            return new SalesCommandOutcome(OrderIds, []);
        }
    }
}
