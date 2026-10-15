using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>attach-shipment</c> komutu — spec 2026-10-13 §4.6 tablosu, §7.1, §7.2, DM7, M22, §15-44, §15-53. Yük
/// <c>{customerId, proposedShipmentId}</c>; <c>expected</c> istemcinin gördüğü bağlanmamış aday kümesidir (Id ve sürüm).
/// Yerel ayna <c>ShipmentService.GetOrCreateOpenShipment</c> + <c>AttachLabels</c>, adaylar
/// <c>LabelRepository.GetUnattachedByCustomer</c>. Mod On'da dekont ekranının kargo adımı budur; kargo bağı her oturum
/// hâlinde sunucunundur (K4), bu yüzden siparişlerin yayın görünürlüğü yerine aday süzgeci uygulanır (§4.6 adım 3).
/// <list type="number">
///   <item><b>Müşteri</b> asıl kayda çözülür (<see cref="CustomerIdResolver"/>; GUID olmayan Id ham kalır) ve
///   <c>ship:{lisans}:{asıl müşteri}</c> işlem kilidi alınır (<see cref="SalesCommandLock.ShipmentResource"/>): aynı
///   müşteriye eşzamanlı iki komut tek dosya açar (§10.1-6).</item>
///   <item><b>Dosyalar önce</b>: müşterinin açık (Pending/Held) dosyaları izlenerek yüklenir — adaylardan ve türetmenin
///   okuduğu bağlı siparişlerden ÖNCE (<see cref="ShipmentAmountDeriver"/>'ın sıra ve jeton gerekçesi).</item>
///   <item><b>Adaylar</b> (<see cref="SalesCommandScope.LoadAttachCandidatesAsync"/>): bağsız, iptal edilmemiş, geçici
///   yedek olmayan, kargo bağı bilinen (<c>OriginReplicaId</c> dolu, M22) siparişler; yayını devredilmiş ya da
///   çağıranın kendisinin. Kargo ücreti satırı dahil. Aday kümesi (Id ve sürüm) <c>expected</c>'den farklıysa 409
///   <c>stale</c> ve güncel küme; hiçbir şey yazılmaz. Aday yoksa (ve <c>expected</c> boşsa) komut etkisizdir: dosya
///   açılmaz.</item>
///   <item><b>Hedef</b>: en yeni (<see cref="SalesCommandScope.NewestFirst"/>) türetilmiş (<c>AmountDerivedAt</c> dolu)
///   Pending/Held dosya. <c>--accept-shipment</c> ile kabul edilmiş (türetilmemiş) dosya hiç seçilmez (§4.10-6). Yoksa
///   <c>proposedShipmentId</c> ile açılır: Pending, sunucu saatiyle <c>CreatedAt</c> ve <c>AmountDerivedAt</c>,
///   <c>OriginReplicaId</c> = çağıran, asıl müşteri. Id sunucuda (herhangi bir lisansta) varsa 409
///   <c>content-conflict</c>; eşzamanlı çakışmayı servis aynı rete çevirir (500 olmaz).</item>
///   <item><b>Bağlama</b>: adaylara <c>ShipmentId</c>, birim damgası <c>ShipmentSetAt</c> (sunucu saati), sürüm bir
///   artar. Tutar türetilir (DM7; <see cref="SalesCommandScope.DeriveShipmentsAsync"/>), dosyanın sürümü bir artar,
///   <c>CommandedAt</c> basılır.</item>
/// </list>
/// Sonuç: <c>shipmentId</c> ve havuz — müşterinin bütün Pending/Held dosyalarının toplamı (kabul edilmiş dosya saklanan
/// tutarıyla); yanıtın <c>shipments</c>'ı havuzun dosyalarıdır. Yanıttaki havuzu servis commit'ten sonra bu güncel
/// dosyalardan yeniden hesaplar (<see cref="SalesCommandResult.Pool"/>): hedef dışındaki açık dosya ship kilidi
/// almayan bir yazıcıyla (<c>cancel</c>, sahibin push'u; K7) eşzamanlı türetilebilir. Bildirim yok (durum değişmez).
/// </summary>
public sealed class AttachShipmentCommandHandler : ISalesCommandHandler
{
    public const string KindName = "attach-shipment";

    /// <summary><c>Shipments.CustomerId</c> ve <c>Orders.CustomerId</c> kolonlarının uzunluğu.</summary>
    public const int MaxCustomerIdLength = 64;

    private readonly CustomerIdResolver _customerIds;

    public AttachShipmentCommandHandler(CustomerIdResolver customerIds) => _customerIds = customerIds;

    public string Kind => KindName;

    /// <summary>Kanonik yük: müşteri Id'si verildiği gibi (asıl kayda uygulamada çözülür) ve önerilen dosya Id'si.</summary>
    public sealed record Payload(string CustomerId, Guid ProposedShipmentId);

    private sealed record Body(string? CustomerId, Guid? ProposedShipmentId);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (string.IsNullOrWhiteSpace(body.CustomerId) || body.CustomerId.Length > MaxCustomerIdLength)
        {
            error = $"customerId boş olamaz, en çok {MaxCustomerIdLength} karakter.";
            return false;
        }
        if (body.ProposedShipmentId is not { } proposed || proposed == Guid.Empty)
        {
            error = "proposedShipmentId boş olamaz.";
            return false;
        }

        // Yanıtın (ve yeniden oynatmanın) satırları: istemcinin gördüğü aday kümesi. Küme uyuşursa bağlananlar budur.
        var orderIds = expected.Select(e => e.OrderId).Order().ToList();
        command = new Command(new Payload(body.CustomerId, proposed), orderIds, _customerIds);
        return true;
    }

    private sealed class Command(Payload payload, IReadOnlyList<Guid> orderIds, CustomerIdResolver customerIds)
        : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds => orderIds;

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            var raw = payload.CustomerId;
            var customer = (await customerIds.CanonicalHexOfAsync(scope.LicenseId, [raw], ct))[raw];
            await scope.AcquireLockAsync(SalesCommandLock.ShipmentResource(scope.LicenseId, customer), ct);

            // Dosyalar adaylardan ve bağlı siparişlerden ÖNCE (jeton sırası).
            var open = (await scope.LoadOpenShipmentsAsync(customer, null, ct)).ToList();
            var candidates = await scope.LoadAttachCandidatesAsync(customer, ct);
            if (!scope.MatchesExpected(candidates))
                throw SalesCommandRejection.CandidatesStale(
                    candidates.Select(o => new SalesCommandExpected(o.Id, o.SyncVersion)).ToList(),
                    candidates.Select(o => o.Id).Concat(orderIds).Distinct().ToList());

            if (candidates.Count == 0)
                return Pooled(new SalesCommandOutcome([], []), open);

            var target = open.FirstOrDefault(s => s.AmountDerivedAt is not null);
            if (target is null)
            {
                if (await scope.ShipmentIdTakenAsync(payload.ProposedShipmentId, ct))
                    throw SalesCommandRejection.ShipmentContentConflict(payload.ProposedShipmentId);

                target = new Shipment
                {
                    Id = payload.ProposedShipmentId,
                    LicenseId = scope.LicenseId,
                    CustomerId = customer,
                    Status = ShipmentStatus.Pending,
                    CumulativeAmount = 0m,
                    CreatedAt = scope.Now,
                    UpdatedAt = scope.Now,
                    AmountDerivedAt = scope.Now,
                    OriginReplicaId = scope.ReplicaId,
                };
                scope.AddShipment(target);
                open.Insert(0, target);
            }

            foreach (var order in candidates)
            {
                order.ShipmentId = target.Id;
                order.ShipmentSetAt = scope.Now;
                scope.MarkChanged(order, OrderUnit.Shipment);
            }

            // Bağlar yazıldıktan sonra tek türetme (sürüm bir artar); ardından komut damgası.
            await scope.DeriveShipmentsAsync([target], ct);
            target.CommandedAt = scope.Now;
            scope.MarkShipmentChanged(target);

            return Pooled(
                new SalesCommandOutcome(candidates.Select(o => o.Id).ToList(), []) { ShipmentId = target.Id },
                open);
        }

        /// <summary>Havuz (§7.1): müşterinin bütün Pending/Held dosyaları, komuttan sonraki tutarlarıyla.</summary>
        private static SalesCommandOutcome Pooled(SalesCommandOutcome outcome, IReadOnlyList<Shipment> open)
            => outcome with
            {
                Pool = open.Sum(s => s.CumulativeAmount),
                PoolShipmentIds = open.Select(s => s.Id).ToList(),
            };
    }
}
