using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>set-code-price</c> komutu — spec 2026-10-13 §4.6 tablosu, §4.11, DM5 (#508 F1: bir yayında bir kodun tek fiyatı).
/// Yük <c>{sessionId, code, alsoSelectedIds, newPrice, paidFromBalanceIds}</c>; yerel ayna <c>LabelService.CorrectPrices</c>
/// (<c>PlanCodeCorrection</c> planıyla) ve <c>LabelRepository.UpdatePriceForCorrection</c>.
///
/// <para><b>Planı sunucu kurar.</b> Adaylar: yayının <see cref="LabelRules.SameCode"/> ile (bellekte, harf duyarsız,
/// saklanan kod kırpılmadan) koda eşleşen siparişleri — <c>code</c> boşsa hiçbiri (kodsuz seçim, yerelde
/// <c>CodeCorrectionPlan(null, …)</c>) — ∪ <c>alsoSelectedIds</c> (ör. önceki yayından kuyruğa taşınmış aynı kodlu
/// satır). Plan, adaylardan <see cref="LabelRules.Classify"/>'ın düzeltilebilir saydıklarıdır: iptal edilmiş, kargo ücreti
/// ve kargoya verilmiş dosyadaki (K6) siparişler planın dışında kalır ve değişmez.</para>
///
/// <para><b>Beklenen sürüm planın kendisidir.</b> <c>expected</c>, istemcinin gösterdiği planın satırları ve sürümleridir;
/// sunucunun planı satır ya da sürümce farklıysa (satır eklendi, çıktı, değişti) 409 <c>plan-stale</c>: güncel plan ve
/// güncel satırlar döner, hiçbir şey yazılmaz. Bu türde her uyuşmazlık <c>plan-stale</c>'dir (<c>stale</c> değil): istemci
/// sessizce yeniden denemez, yeni planı gösterir (§5.3).</para>
///
/// <para><b>Kilit.</b> Plan, işlem kapsamlı yayın kilitleri (<see cref="SalesCommandLock.SessionResource"/>; komutun ve
/// seçilen siparişlerin yayınları, sıralı) alındıktan SONRA kurulur. Aynı yayında eşzamanlı ikinci komut ilki bitene
/// kadar bekler, sonra planı ilkinin yazımından sonra kurar ve <c>plan-stale</c> alır (§10.1-4); iki komut aynı bayat
/// planı okuyup jetonda çarpışmaz. Kilit koda değil yayına bağlıdır: harf duyarsız kodun kilit adında katlanması
/// gerekirdi.</para>
///
/// <para><b>Etki</b> (fiyatı farklı her plan satırında, sunucu saatiyle): <c>Price</c> = yeni fiyat;
/// <c>PriceCorrectedAt</c> yalnız basılmış satırda (yığında eski fiyatlı kâğıt var); <c>PaidPrice</c>
/// <see cref="LabelRules.PaidPriceOnCorrection"/> ile — boşsa ve sipariş ödenmişse (dosyaya bağlı ya da
/// <c>paidFromBalanceIds</c>'te) düzeltmeden önceki fiyat; birim damgası <c>PriceSetAt</c> (bayat push fiyatı geri
/// alamaz, K2). Bağlı türetilmiş açık dosyanın tutarını servis yeniden türetir (K7, DM7). Fiyatı zaten yeni fiyat olan
/// satır yazılmaz, sürümü ilerlemez.</para>
///
/// <para><b>Görünürlük.</b> Komutun yayını ve her plan satırının yayını devredilmiş olmalı (409 <c>session-live</c>).</para>
/// </summary>
public sealed class SetCodePriceCommandHandler : ISalesCommandHandler
{
    public const string KindName = "set-code-price";

    /// <summary><c>Orders.Code</c> kolonunun uzunluğu.</summary>
    public const int MaxCodeLength = 64;

    public string Kind => KindName;

    /// <summary>
    /// Kanonik yük: kırpılmış kod (kodsuz seçimde null), tekil ve sıralı Id listeleri, iki ondalıklı fiyat.
    /// </summary>
    public sealed record Payload(
        Guid SessionId,
        string? Code,
        IReadOnlyList<Guid> AlsoSelectedIds,
        decimal NewPrice,
        IReadOnlyList<Guid> PaidFromBalanceIds);

    private sealed record Body(
        Guid? SessionId,
        string? Code,
        List<Guid>? AlsoSelectedIds,
        decimal? NewPrice,
        List<Guid>? PaidFromBalanceIds);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (body.SessionId is not { } sessionId || sessionId == Guid.Empty)
        {
            error = "sessionId boş olamaz.";
            return false;
        }

        string? code = null;
        if (body.Code is not null)
        {
            code = body.Code.Trim();
            if (code.Length == 0 || code.Length > MaxCodeLength)
            {
                error = $"code kodsuz seçimde null olmalı; değilse boş olmayan, en çok {MaxCodeLength} karakter.";
                return false;
            }
        }

        if (!SalesCommandPayload.TryReadOptionalIds(body.AlsoSelectedIds, "alsoSelectedIds", out var alsoSelected,
                out error)) return false;
        if (code is null && alsoSelected.Count == 0)
        {
            error = "code null ise alsoSelectedIds düzeltilecek siparişleri taşımalı.";
            return false;
        }
        if (body.NewPrice is not { } rawPrice)
        {
            error = "newPrice gerekli.";
            return false;
        }
        if (!SalesCommandPayload.TryReadPrice(rawPrice, "newPrice", out var newPrice, out error)) return false;
        if (!SalesCommandPayload.TryReadOptionalIds(body.PaidFromBalanceIds, "paidFromBalanceIds",
                out var paidFromBalance, out error)) return false;

        // Yanıtın (ve yeniden oynatmanın) satırları: istemcinin gördüğü plan ve seçimi. Plan uyuşursa sunucunun planı
        // da budur.
        var orderIds = expected.Select(e => e.OrderId).Concat(alsoSelected).Distinct().Order().ToList();
        command = new Command(new Payload(sessionId, code, alsoSelected, newPrice, paidFromBalance), orderIds);
        return true;
    }

    private sealed class Command(Payload payload, IReadOnlyList<Guid> orderIds) : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds => orderIds;

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            await scope.EnsureSessionReleasedAsync(payload.SessionId, ct);

            // Kilit: komutun yayını ve seçilen siparişlerin yayınları (yayın oluşum alanıdır, kilitten önce okunabilir).
            var selected = await scope.PeekOrdersAsync(payload.AlsoSelectedIds, ct);
            await scope.AcquireSessionLocksAsync(
                selected.Values.Select(o => o.SessionId).OfType<Guid>().Append(payload.SessionId), ct);

            // Adaylar kilitten SONRA ve taze: eşzamanlı komut planı bizim yazımımızdan sonra kurar. Dosyalar adaylardan
            // ÖNCE yüklenir (SalesCommandScope.LoadShipmentsAsync'in sıra gerekçesi): yazmadığımız ama türettiğimiz
            // dosyaya bağlı bir plan siparişini okumamızdan sonra değiştiren eşzamanlı yazıcı dosya jetonunda yakalanır
            // (409 stale) — dosyanın tutarına o siparişin bayat hâli girmez (DM7).
            IReadOnlyList<Guid> codeOrders = payload.Code is { } code
                ? await scope.FindSessionCodeOrdersAsync(payload.SessionId, code, ct)
                : [];
            var candidates = codeOrders.Concat(payload.AlsoSelectedIds).Distinct().ToList();
            var peeked = await scope.PeekOrdersAsync(candidates, ct);
            var shipments = new Dictionary<Guid, Shipment>(
                await scope.LoadShipmentsAsync(peeked.Values.Select(o => o.ShipmentId).OfType<Guid>(), ct));
            var orders = await scope.LoadOrdersAsync(candidates, ct);

            // Ön okumadan sonra bağlanan sipariş (eşzamanlı bağlama, ör. geri doldurma): dosyası yalnız sınıflama için
            // şimdi yüklenir — yüklenmeseydi "bilinmiyor" sayılır, kargolanmış dosyadaki sipariş düzeltilebilir görünürdü.
            // Bağı yazan her yol sürümü de ilerletir: o sipariş planda kalırsa istemcinin beklediği sürümü taşıyamaz,
            // plan uyuşmaz ve hiçbir şey yazılmaz; geç yüklenen dosya türetmeye girmez.
            var late = orders.Values.Select(o => o.ShipmentId).OfType<Guid>().Where(id => !shipments.ContainsKey(id));
            foreach (var (id, shipment) in await scope.LoadShipmentsAsync(late.ToList(), ct))
                shipments[id] = shipment;

            var statuses = shipments.Values.ToDictionary(
                s => s.Id.ToString("N"), s => (SalesShipmentStatus)(int)s.Status, StringComparer.Ordinal);
            var plan = orders.Values
                .Where(o => LabelRules.Classify(
                    o.ToLabelFacts(),
                    sid => statuses.TryGetValue(sid, out var status) ? status : null) == LabelPriceClass.Correctable)
                .OrderBy(o => o.Id)
                .ToList();

            await scope.EnsureReleasedAsync(plan, ct);
            if (!scope.MatchesExpected(plan))
                throw SalesCommandRejection.PlanStale(
                    plan.Select(o => new SalesCommandExpected(o.Id, o.SyncVersion)).ToList(),
                    plan.Select(o => o.Id).Concat(orderIds).Distinct().ToList());

            var paidFromBalance = payload.PaidFromBalanceIds.ToHashSet();
            var changed = new List<Guid>();
            foreach (var order in plan)
            {
                if (order.Price == payload.NewPrice) continue;

                order.PaidPrice = LabelRules.PaidPriceOnCorrection(
                    order.PaidPrice, order.Price, linkedToShipment: order.ShipmentId is not null,
                    paidFromBalance: paidFromBalance.Contains(order.Id));
                order.Price = payload.NewPrice;
                if (order.PrintedAt is not null) order.PriceCorrectedAt = scope.Now;
                order.PriceSetAt = scope.Now;
                scope.MarkChanged(order, OrderUnit.Price);
                changed.Add(order.Id);
            }

            return new SalesCommandOutcome(changed, orderIds.Except(changed).ToList());
        }
    }
}
