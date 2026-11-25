using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>shipment-decision</c> komutu — spec 2026-10-13 §4.6 tablosu, §7.2 (sahip kararı Y5), §7.3, U12, M13. Yük
/// <c>{shipmentId, decision: ship-now|hold|recipient-pays, expectedVersion}</c>; <c>expected</c> (sipariş sürümleri) boş
/// olmalı. Yerel ayna <c>ShipmentService.ApplyDecision</c> / <c>Decide</c>.
/// <list type="number">
///   <item><b>Kilit</b>: dosyanın müşterisinin (asıl kayda çözülmüş) <c>ship:{lisans}:{müşteri}</c> işlem kilidi —
///   <c>attach-shipment</c> ile aynı: karar ile bağlama aynı havuzda sırayla çalışır. Dosya bu lisansta yoksa 400
///   <c>unknown-shipment</c>.</item>
///   <item><b>Doğrulama</b> (kilitten sonra, taze): dosya Shipped ise 409 <c>shipment-shipped</c> (Shipped son
///   durumdur, §7.1); sürümü <c>expectedVersion</c> değilse 409 <c>stale</c> ve güncel dosya
///   (<c>currentShipments</c>). Hiçbir şey yazılmaz.</item>
///   <item><b>Karar</b> (<c>Decide</c> aynası): ship-now → Shipped ve <c>ShippedAt</c>; hold → Held ve <c>HeldAt</c>
///   yalnız ilk bekletmede; recipient-pays → RecipientPays. Hedef karardan önce açıksa (Pending/Held) müşterinin öbür
///   açık dosyaları da aynı karara uyar (U12, tek havuz).</item>
///   <item><b>Kargoya verilen her türetilmiş dosyada</b> (§7.2) bağlı ve ŞU ANDA iptal edilmiş siparişlerin bağı
///   çözülür (<c>ShipmentId</c> boş, damga <c>ShipmentSetAt</c>, sürüm bir artar) — yayını canlı olsa da (kargo bağı her
///   oturum hâlinde sunucunundur, K4; canlı sahibin aynası devirde akıştan alır). Ürün kolide değildir; sonradan iptali
///   geri alınırsa bağsız kalır ve sonraki dosyaya girer. Ardından dosya bir kez türetilir ve tutarı o anda donar
///   (DM7). Kabul edilmiş dosya saklanan tutarıyla donar ve bağları kalır: tutarı iptalle düşmediği için iptalli
///   siparişi de içerir (<see cref="ShipmentShipping"/>).</item>
///   <item>Kararın uygulandığı her dosyada sürüm bir artar (durum değişmese de, yerel <c>ApplyDecision</c> gibi),
///   <c>CommandedAt</c> ve <c>UpdatedAt</c> sunucu saatidir.</item>
/// </list>
/// Bir dosyanın durumu gerçekten değiştiyse servis, commit'ten sonra müşteriye <c>ShipmentStatusChanged</c> etiket
/// kuralını uygular (<c>shipments/sync</c> ile eşlik); başka bildirim yok.
/// </summary>
public sealed class ShipmentDecisionCommandHandler : ISalesCommandHandler
{
    public const string KindName = "shipment-decision";

    public const string ShipNow = "ship-now";
    public const string Hold = "hold";
    public const string RecipientPays = "recipient-pays";

    private readonly CustomerIdResolver _customerIds;

    public ShipmentDecisionCommandHandler(CustomerIdResolver customerIds) => _customerIds = customerIds;

    public string Kind => KindName;

    /// <summary>Kanonik yük: karar teldeki küçük harfli adıyla.</summary>
    public sealed record Payload(Guid ShipmentId, string Decision, int ExpectedVersion);

    private sealed record Body(Guid? ShipmentId, string? Decision, int? ExpectedVersion);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (body.ShipmentId is not { } shipmentId || shipmentId == Guid.Empty)
        {
            error = "shipmentId boş olamaz.";
            return false;
        }
        if (body.Decision is not (ShipNow or Hold or RecipientPays))
        {
            error = $"decision {ShipNow}, {Hold} ya da {RecipientPays} olmalı.";
            return false;
        }
        if (body.ExpectedVersion is not { } expectedVersion || expectedVersion < 0)
        {
            error = "expectedVersion 0 ya da daha büyük olmalı.";
            return false;
        }
        if (expected.Count > 0)
        {
            error = "shipment-decision sipariş sürümü taşımaz; expected boş olmalı (dosyanın sürümü expectedVersion).";
            return false;
        }

        command = new Command(new Payload(shipmentId, body.Decision, expectedVersion), _customerIds);
        return true;
    }

    private sealed class Command(Payload payload, CustomerIdResolver customerIds) : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds => [];

        public IReadOnlyList<Guid> ShipmentIds { get; } = [payload.ShipmentId];

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            // Kilit adı için dosyanın müşterisi (izlemeden); dosya kilitten SONRA taze okunur.
            var owner = await scope.PeekShipmentCustomerAsync(payload.ShipmentId, ct);
            var customer = (await customerIds.CanonicalHexOfAsync(scope.LicenseId, [owner], ct))[owner];
            await scope.AcquireLockAsync(SalesCommandLock.ShipmentResource(scope.LicenseId, customer), ct);

            // Dosyalar bağlı siparişlerden ÖNCE (jeton sırası): hedef burada, müşterinin öbür açık dosyaları çekirdekte.
            var target = (await scope.LoadShipmentsAsync([payload.ShipmentId], ct)).GetValueOrDefault(payload.ShipmentId)
                         ?? throw SalesCommandRejection.UnknownShipment(payload.ShipmentId);
            if (target.Status == ShipmentStatus.Shipped)
                throw SalesCommandRejection.ShipmentAlreadyShipped(target.Id);
            if (target.Version != payload.ExpectedVersion)
                throw SalesCommandRejection.ShipmentStale(target.Id);

            var decided = await ShipmentDecisionCore.ApplyAsync(scope, customer, target, payload.Decision, ct);

            return new SalesCommandOutcome(decided.UnlinkedOrderIds, [])
            {
                DecidedShipmentIds = decided.ShipmentIds,
                ShipmentStatusChangedCustomers = decided.StatusChanged ? [customer] : [],
            };
        }
    }
}
