using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>uncancel</c> komutu — spec 2026-10-13 §4.6 tablosu, §7.2 (sahip kararı Y5). Yük <c>{items}</c>; yerel ayna
/// <c>LabelService.Uncancel</c>.
/// <list type="bullet">
///   <item>İptal edilmemiş sipariş atlanır: hiçbir alanı ve sürümü değişmez.</item>
///   <item>Öbürlerinde <c>CancelledAt</c> ve <c>CancelReason</c> boşalır; <c>CancelSetAt</c> yeni sunucu anıyla basılır
///   — damga hiçbir yolda silinmez, "bu birime komut dokundu" demektir (§2). Bayat bir "iptalli" push geri alınmış
///   iptali geri getiremez (K2); K3 ise iptali şu anda olmayan siparişin baskısını alır.</item>
///   <item>Kargo bağı ve <c>PaidPrice</c> KALIR: etiket aynı dosyaya döner, "Ödendi" rozeti yerinde. Dosya türetilmiş ve
///   Shipped değilse tutar eski hâline döner (K7); Shipped dosya donmuştur.</item>
///   <item>Stok defteri birleşik durumdan, yalnız ürünü bilinen siparişte (servis yazar).</item>
/// </list>
/// Her öğe devredilmiş bir yayında olmalı ve <c>expected</c> her öğenin güncel sürümünü taşımalı.
/// </summary>
public sealed class UncancelCommandHandler : ISalesCommandHandler
{
    public const string KindName = "uncancel";

    public string Kind => KindName;

    /// <summary>Kanonik yük: tekil, sıralı öğeler.</summary>
    public sealed record Payload(IReadOnlyList<Guid> Items);

    private sealed record Body(List<Guid>? Items);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (!SalesCommandPayload.TryReadItems(body.Items, out var items, out error)) return false;
        if (!SalesCommandPayload.TryMatchExpected(items, expected, out error)) return false;

        command = new Command(new Payload(items));
        return true;
    }

    private sealed class Command(Payload payload) : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds => payload.Items;

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            var orders = await scope.LoadOrdersAsync(payload.Items, ct);
            await scope.EnsureReleasedAsync(orders.Values, ct);
            scope.EnsureExpectedVersions(orders.Values);

            var changed = new List<Guid>();
            var skipped = new List<Guid>();
            foreach (var id in payload.Items)
            {
                var order = orders[id];
                if (order.CancelledAt is null)
                {
                    skipped.Add(id);
                    continue;
                }

                order.CancelledAt = null;
                order.CancelReason = null;
                order.CancelSetAt = scope.Now;
                scope.MarkChanged(order, OrderUnit.Cancel);
                changed.Add(id);
            }

            return new SalesCommandOutcome(changed, skipped);
        }
    }
}
