using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>Geri doldurmanın mevcut siparişteki kararı.</summary>
/// <param name="Values">Satıra yazılacak değerler: sunucudaki değerler, yalnız boş yeni alanlar dolmuş.</param>
/// <param name="Diverged">Yerel değeri sunucudakinden (doldurmadan sonra) farklı olan birimler. Yazılmazlar.</param>
/// <param name="DivergedStamped">Ayrışan birimlerden en az biri komutla damgalı: yerel kazanamaz.</param>
public sealed record OrderBackfillResult(
    OrderUnitValues Values,
    IReadOnlyList<OrderUnit> Diverged,
    bool DivergedStamped);

/// <summary>
/// <c>sales/backfill</c>'in mevcut sipariş kuralı (spec 2026-10-13 §4.3, §8.2-3/-4, M20) — saf fonksiyon. Geri
/// doldurma yalnız boşu doldurur; sunucuda dolu bir alan ya da damgalı bir birim yazılmaz.
///
/// <para><b>Dolan alanlar.</b> Yerelde kalmış yeni alanlar, sunucuda boşsa ve ait oldukları birimle tutarlıysa:
/// <list type="bullet">
///   <item>ebeveyn (<c>ParentOrderId</c>, oluşum alanı, K1);</item>
///   <item>fiyat biriminin düzeltme damgası ve ödenen fiyatı — birim damgasız ve fiyat yereldekiyle aynıysa. Fiyat
///   farklıysa bunlar yerelin fiyat geçmişini anlatır; sunucu satırına yazılsalar satır "düzeltilmiş" görünüp eski
///   fiyatı taşırdı. Fark ayrışmadır, birim bütün olarak ya yerelden (yerel kazanırsa, v2 push'u) ya sunucudan
///   gelir;</item>
///   <item>baskı grubu (<c>PrintBatchId</c>, <c>PrintSeq</c>) — yalnız sunucuda basılmış satıra (K3'ün kuralı);</item>
///   <item>kargo bağı — sunucuda boş ve <c>ShipmentSetAt</c> boşsa (komutun çözdüğü bağ geri gelmez).</item>
/// </list>
/// Kaynak kopya (<c>OriginReplicaId</c>) birim değildir; çağıran doldurur.</para>
///
/// <para><b>Ayrışma.</b> Fiyat, iptal, yedek ve baskı birimleri doldurmadan sonraki sunucu değeriyle karşılaştırılır,
/// yazılmaz. Nedeni F05 öncesinin kayıp güncellemeleri: push uçuştayken yapılan iptal, baskı ya da fiyat değişikliği
/// yerelde kaldı, sunucuya hiç gitmedi (<c>032_sync_ack_revision.sql</c>). Damgalı birimin farkı da döner
/// (<see cref="OrderBackfillResult.DivergedStamped"/>): komutun kararıdır, yerel kazanamaz; sayısı denetim raporuna
/// sunucu kazanan olarak girer. Zaman damgaları saniyeye göre karşılaştırılır — masaüstü Unix saniyesi saklar,
/// sunucunun saniye altı kesri fark değildir (<see cref="OrderUnitMerge"/> ile aynı karşılaştırma).</para>
/// </summary>
public static class OrderBackfillRule
{
    /// <param name="server">Sunucudaki satır (değerler ve birim damgaları).</param>
    /// <param name="local">Geri doldurma öğesindeki yerel değerler.</param>
    public static OrderBackfillResult Apply(OrderServerRow server, OrderUnitValues local)
    {
        var s = server.Values;
        var filled = s with
        {
            ParentOrderId = s.ParentOrderId ?? local.ParentOrderId,
            ShipmentId = s.ShipmentId is null && server.ShipmentSetAt is null ? local.ShipmentId : s.ShipmentId,
        };
        if (server.PriceSetAt is null && s.Price == local.Price)
            filled = filled with
            {
                PriceCorrectedAt = s.PriceCorrectedAt ?? local.PriceCorrectedAt,
                PaidPrice = s.PaidPrice ?? local.PaidPrice,
            };
        if (s.PrintedAt is not null)
            filled = filled with
            {
                PrintBatchId = s.PrintBatchId ?? local.PrintBatchId,
                PrintSeq = s.PrintSeq ?? local.PrintSeq,
            };

        var diverged = new List<OrderUnit>();
        if (filled.Price != local.Price
            || !SameSecond(filled.PriceCorrectedAt, local.PriceCorrectedAt)
            || filled.PaidPrice != local.PaidPrice)
            diverged.Add(OrderUnit.Price);
        if (!SameSecond(filled.CancelledAt, local.CancelledAt)
            || !string.Equals(filled.CancelReason, local.CancelReason, StringComparison.Ordinal))
            diverged.Add(OrderUnit.Cancel);
        if (filled.IsTentativeBackup != local.IsTentativeBackup)
            diverged.Add(OrderUnit.Backup);
        if (!SameSecond(filled.PrintedAt, local.PrintedAt)
            || !string.Equals(filled.PrintBatchId, local.PrintBatchId, StringComparison.Ordinal)
            || filled.PrintSeq != local.PrintSeq)
            diverged.Add(OrderUnit.Print);

        var stamped = diverged.Any(unit => unit switch
        {
            OrderUnit.Price => server.PriceSetAt is not null,
            OrderUnit.Cancel => server.CancelSetAt is not null,
            OrderUnit.Backup => server.BackupSetAt is not null,
            _ => false,
        });

        return new OrderBackfillResult(filled, diverged, stamped);
    }

    /// <summary>Aynı Unix saniyesi mi (ikisi de boşsa da aynı).</summary>
    private static bool SameSecond(DateTimeOffset? a, DateTimeOffset? b)
        => a is null || b is null ? a is null && b is null : a.Value.ToUnixTimeSeconds() == b.Value.ToUnixTimeSeconds();
}
