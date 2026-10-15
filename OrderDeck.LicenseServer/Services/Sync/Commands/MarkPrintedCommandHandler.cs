using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>mark-printed</c> komutu — spec 2026-10-13 §4.6 tablosu, §5.5, §5.6, K3. Yük <c>{batchId, items [{orderId, seq}]}</c>;
/// yerel ayna <c>LabelService.MarkPrintedAndRecord</c> (R7-04: zaten basılmış ya da iptal edilmiş satır atlanır).
/// Devredilmiş yayından kalan kuyruk etiketinin ilk baskısı (W2'nin kalıntı kuyruğu ve bekleyen komutu).
/// <list type="bullet">
///   <item><b>İlk yazan kazanır.</b> <c>PrintedAt</c> NULL ve iptal edilmemiş siparişe <c>PrintedAt</c> = sunucu saati,
///   <c>PrintBatchId</c> ve kâğıttaki sırası <c>PrintSeq</c> yazılır, sürüm bir artar → <c>marked</c>. Zaten basılmış
///   sipariş hiç değişmez → <c>alreadyPrinted</c> (basış grubu ve ilk baskı anı korunur; K3: <c>PrintedAt</c> hiçbir
///   yoldan silinmez ve kaymaz). Basılmamış ama iptal edilmiş sipariş basılmaz → <c>cancelled</c>; istemci
///   "kâğıdı imha et" der (K3'ün <c>cancelled-elsewhere</c>'iyle aynı sonuç). Geçici yedek de basılır (kâğıdı çıkar).</item>
///   <item>Baskı birimi için damga YOK (§2: damgalar fiyat, iptal, yedek ve kargo bağı içindir); ilk yazanın kazanması
///   bayat bir push'a karşı K3 ile korunur. Baskı dosya tutarını (K7) ve stok defterini değiştirmez.</item>
///   <item><b>Görünürlük:</b> her sipariş devredilmiş bir yayında olmalı (409 <c>session-live</c>). §4.6 adım 3 baskıyı
///   saymıyor; ama §2 tablosunda devredilmemiş yayının bütün birimlerinin (baskı dahil) tek yazıcısı sahibin push'udur
///   ve canlı yayının kuyruğu başka bilgisayara hiç teklif edilmez (§5.5). Bu yüzden baskı birimi de adım 3'e girer.</item>
///   <item><b>Beklenen sürüm:</b> <c>expected</c> her öğenin güncel sürümünü taşımalı; uyuşmazlık 409 <c>stale</c>
///   (istemci taze sürümlerle bir kez kendiliğinden yeniden dener, §5.3; ikinci denemede başka bilgisayarın baskısı
///   <c>alreadyPrinted</c> olarak döner).</item>
/// </list>
/// Bildirim yok: basılmadan gönderilip sonra basılan etiket bugün de bildirim üretmez (risk 13).
/// </summary>
public sealed class MarkPrintedCommandHandler : ISalesCommandHandler
{
    public const string KindName = "mark-printed";

    /// <summary><c>Orders.PrintBatchId</c> kolonunun uzunluğu.</summary>
    public const int MaxBatchIdLength = 40;

    public string Kind => KindName;

    /// <summary>Bir sipariş ve kâğıttaki sırası (1'den).</summary>
    public sealed record Item(Guid OrderId, int Seq);

    /// <summary>Kanonik yük: basış kimliği ve sipariş Id'sine göre sıralı öğeler.</summary>
    public sealed record Payload(string BatchId, IReadOnlyList<Item> Items);

    private sealed record ItemBody(Guid? OrderId, int? Seq);

    private sealed record Body(string? BatchId, List<ItemBody?>? Items);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (string.IsNullOrWhiteSpace(body.BatchId) || body.BatchId.Length > MaxBatchIdLength)
        {
            error = $"batchId boş olamaz, en çok {MaxBatchIdLength} karakter.";
            return false;
        }
        if (body.Items is null || body.Items.Count == 0 || body.Items.Count > SalesCommandService.MaxOrders)
        {
            error = $"items en az bir, en çok {SalesCommandService.MaxOrders} öğe taşır.";
            return false;
        }

        var items = new List<Item>(body.Items.Count);
        foreach (var item in body.Items)
        {
            if (item?.OrderId is not { } orderId || orderId == Guid.Empty || item.Seq is not { } seq || seq < 1)
            {
                error = "items öğesi orderId ve 1 ya da daha büyük bir seq taşımalı.";
                return false;
            }
            items.Add(new Item(orderId, seq));
        }
        if (items.Select(i => i.OrderId).Distinct().Count() != items.Count)
        {
            error = "items bir siparişi iki kez taşıyamaz.";
            return false;
        }

        var ordered = items.OrderBy(i => i.OrderId).ToList();
        if (!SalesCommandPayload.TryMatchExpected(ordered.Select(i => i.OrderId).ToList(), expected, out error))
            return false;

        command = new Command(new Payload(body.BatchId, ordered));
        return true;
    }

    private sealed class Command(Payload payload) : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds { get; } = payload.Items.Select(i => i.OrderId).ToList();

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            var orders = await scope.LoadOrdersAsync(OrderIds, ct);
            await scope.EnsureReleasedAsync(orders.Values, ct);
            scope.EnsureExpectedVersions(orders.Values);

            var marked = new List<Guid>();
            var alreadyPrinted = new List<Guid>();
            var cancelled = new List<Guid>();
            foreach (var item in payload.Items)
            {
                var order = orders[item.OrderId];
                if (order.PrintedAt is not null)
                {
                    alreadyPrinted.Add(order.Id);
                    continue;
                }
                if (order.CancelledAt is not null)
                {
                    cancelled.Add(order.Id);
                    continue;
                }

                order.PrintedAt = scope.Now;
                order.PrintBatchId = payload.BatchId;
                order.PrintSeq = item.Seq;
                scope.MarkChanged(order, OrderUnit.Print);
                marked.Add(order.Id);
            }

            return new SalesCommandOutcome(marked, alreadyPrinted.Concat(cancelled).ToList())
            {
                Marked = marked,
                AlreadyPrinted = alreadyPrinted,
                Cancelled = cancelled,
            };
        }
    }
}
