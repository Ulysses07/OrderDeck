using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>reprint</c> komutu — spec 2026-10-13 §4.6 tablosu, §5.6 "Yeniden baskı". Yük <c>{orderIds}</c>; yerel ayna
/// <c>LabelService.PrepareReprint</c>.
/// <list type="bullet">
///   <item><b>Yazma yok:</b> hiçbir alan, damga ve sürüm değişmez — yeniden baskı ciroyu ikinci kez artırmaz, ilk
///   <c>PrintedAt</c>'i ve basış grubunu bozmaz (K3; <c>048_label_print_batch.sql</c>). Durum hep
///   <c>unchanged</c>.</item>
///   <item><b>Yanıt:</b> istenen siparişlerin GÜNCEL satırları (istenen sırayla; başka bilgisayarın iptali ya da fiyat
///   düzeltmesi kâğıda yansır) ve sonuçta <c>reprintIds</c> — yerel <c>PrepareReprint</c> gibi basılmış ve iptal
///   edilmemiş olanlar, kâğıt sırasıyla — ile <c>correctionIds</c>, bunlardan fiyatı düzeltilmiş olanlar ("DÜZELTME"
///   işareti).</item>
///   <item><b>Görünürlük:</b> her sipariş devredilmiş bir yayında ya da çağıranın kendi yayınında olmalı
///   (<see cref="SalesCommandScope.EnsureVisibleAsync"/>); başka kopyanın canlı ya da devrediliyor yayını 409
///   <c>session-live</c> — o yayının satırları başka bilgisayara inmez (Y1).</item>
///   <item>Denetim izi: her yeniden baskı günlüğe bir satır yazar (<c>SalesCommandLogs</c>; yalnız Id).
///   <c>expected</c> boş olmalı: yazmayan komutun beklenen sürümü yoktur.</item>
/// </list>
/// </summary>
public sealed class ReprintCommandHandler : ISalesCommandHandler
{
    public const string KindName = "reprint";

    public string Kind => KindName;

    /// <summary>Kanonik yük: tekil Id'ler, İSTENEN sırayla — kâğıt sırasıdır, sıralanmaz.</summary>
    public sealed record Payload(IReadOnlyList<Guid> OrderIds);

    private sealed record Body(List<Guid>? OrderIds);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (body.OrderIds is null || body.OrderIds.Count == 0 || body.OrderIds.Count > SalesCommandService.MaxOrders
            || body.OrderIds.Contains(Guid.Empty))
        {
            error = $"orderIds en az bir, en çok {SalesCommandService.MaxOrders} dolu Id taşır.";
            return false;
        }
        if (expected.Count > 0)
        {
            error = "reprint yazmaz; expected boş olmalı.";
            return false;
        }

        command = new Command(new Payload(body.OrderIds.Distinct().ToList()));
        return true;
    }

    private sealed class Command(Payload payload) : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds => payload.OrderIds;

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            var orders = await scope.LoadOrdersAsync(OrderIds, ct);
            await scope.EnsureVisibleAsync(orders.Values, ct);

            var reprint = OrderIds
                .Where(id => orders[id].PrintedAt is not null && orders[id].CancelledAt is null)
                .ToList();
            return new SalesCommandOutcome([], OrderIds)
            {
                ReprintIds = reprint,
                CorrectionIds = reprint.Where(id => orders[id].PriceCorrectedAt is not null).ToList(),
            };
        }
    }
}
