using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>cancel</c> komutu — spec 2026-10-13 §4.6 tablosu, §7.2 (sahip kararı Y5). Yük <c>{items, reason}</c>; yerel ayna
/// <c>LabelService.Cancel</c>.
/// <list type="bullet">
///   <item>Zaten iptal edilmiş sipariş atlanır: hiçbir alanı ve sürümü değişmez.</item>
///   <item>Öbürlerinde <c>CancelledAt</c> = sunucu saati (istemci saati yok), <c>CancelReason</c> ve birim damgası
///   <c>CancelSetAt</c> (aynı an; damga hiç silinmez, §2). Damgalı birimi bayat bir push değiştiremez (K2).</item>
///   <item>Kargo bağı KALIR (Y5: bağ "dekontla ödendi"nin tek işaretidir). Bağlı dosya türetilmiş ve Shipped değilse
///   tutarı siparişin fiyatı kadar düşer (K7, DM7); Shipped dosyanın tutarı donmuştur, kabul edilmiş (türetilmemiş)
///   dosya saklanan tutarıyla kalır.</item>
///   <item>Stok defteri birleşik durumdan, yalnız ürünü bilinen siparişte (servis yazar).</item>
/// </list>
/// Her öğe devredilmiş bir yayında olmalı (görünürlük) ve <c>expected</c> her öğenin güncel sürümünü taşımalı.
/// </summary>
public sealed class CancelCommandHandler : ISalesCommandHandler
{
    public const string KindName = "cancel";

    /// <summary><c>Orders.CancelReason</c> kolonunun uzunluğu.</summary>
    public const int MaxReasonLength = 500;

    public string Kind => KindName;

    /// <summary>Kanonik yük: tekil, sıralı öğeler ve neden.</summary>
    public sealed record Payload(IReadOnlyList<Guid> Items, string Reason);

    private sealed record Body(List<Guid>? Items, string? Reason);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (!SalesCommandPayload.TryReadItems(body.Items, out var items, out error)) return false;
        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > MaxReasonLength)
        {
            error = $"reason boş olamaz, en çok {MaxReasonLength} karakter.";
            return false;
        }
        if (!SalesCommandPayload.TryMatchExpected(items, expected, out error)) return false;

        command = new Command(new Payload(items, body.Reason));
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
                if (order.CancelledAt is not null)
                {
                    skipped.Add(id);
                    continue;
                }

                order.CancelledAt = scope.Now;
                order.CancelReason = payload.Reason;
                order.CancelSetAt = scope.Now;
                scope.MarkChanged(order, OrderUnit.Cancel);
                changed.Add(id);
            }

            return new SalesCommandOutcome(changed, skipped);
        }
    }
}
