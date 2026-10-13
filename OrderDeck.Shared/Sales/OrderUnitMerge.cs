namespace OrderDeck.Shared.Sales;

/// <summary>
/// Sipariş güncellemesinin birim kuralı (spec 2026-10-13 §4.2, §4.11): sunucudaki satırın hâlinden ve gelen
/// push'tan, satıra <b>hangi değerlerin yazılacağını</b> ve hangi birimin yok sayıldığını çıkaran SAF fonksiyon.
/// Veritabanı, saat ya da istek bilmez; <c>orders/sync</c> ve sonraki yazıcılar aynı kararı buradan alır.
///
/// <para><b>K1 — oluşum alanları.</b> SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
/// AddedAt, IsShippingFee, IsBackupPromoted, ProductId ve ProductVariantId yalnız eklemede yazılır; bu yüzden
/// girdi ve çıktı tiplerinde hiç yoklar (yapısal kural: çağıran onları güncellemede yazamaz). Tek istisna
/// <see cref="OrderUnitValues.ParentOrderId"/>: yalnız boşsa doldurulur.</para>
///
/// <para><b>Biçim.</b> v1 (≤0.9.10) yalnız <see cref="OrderUnitValues.Price"/>, iptal alanları,
/// <see cref="OrderUnitValues.IsTentativeBackup"/> ve <see cref="OrderUnitValues.PrintedAt"/> taşır; öbür alanlar
/// v1'de "gönderilmedi" demektir ve sunucudaki değer korunur (NULL'lanmaz).</para>
/// </summary>
public static class OrderUnitMerge
{
    /// <summary>Sunucu satırı ile push'u birleştirir.</summary>
    /// <param name="server">Sunucudaki satırın güncel hâli.</param>
    /// <param name="push">Gelen push'un değerleri.</param>
    /// <param name="context">Push'un biçimi ve koşulları. K1'in okuduğu tek alan
    /// <see cref="OrderMergeContext.Format"/>.</param>
    public static OrderMergeResult Merge(OrderServerRow server, OrderPush push, OrderMergeContext context)
    {
        var s = server.Values;
        var p = push.Values;
        var v2 = context.Format == OrderPushFormat.V2;

        var merged = s with
        {
            Price = p.Price,
            PriceCorrectedAt = v2 ? p.PriceCorrectedAt : s.PriceCorrectedAt,
            PaidPrice = v2 ? p.PaidPrice : s.PaidPrice,
            CancelledAt = p.CancelledAt,
            CancelReason = p.CancelReason,
            IsTentativeBackup = p.IsTentativeBackup,
            PrintedAt = p.PrintedAt,
            PrintBatchId = v2 ? p.PrintBatchId : s.PrintBatchId,
            PrintSeq = v2 ? p.PrintSeq : s.PrintSeq,
            // K1: ebeveyn yalnız boşsa doldurulur.
            ParentOrderId = s.ParentOrderId ?? (v2 ? p.ParentOrderId : null),
        };

        return new OrderMergeResult(merged, ChangedUnits(s, merged), Array.Empty<IgnoredOrderUnit>());
    }

    private static IReadOnlyList<OrderUnit> ChangedUnits(OrderUnitValues before, OrderUnitValues after)
    {
        var changed = new List<OrderUnit>();
        if (before.Price != after.Price
            || before.PriceCorrectedAt != after.PriceCorrectedAt
            || before.PaidPrice != after.PaidPrice)
            changed.Add(OrderUnit.Price);
        if (before.CancelledAt != after.CancelledAt
            || !string.Equals(before.CancelReason, after.CancelReason, StringComparison.Ordinal))
            changed.Add(OrderUnit.Cancel);
        if (before.IsTentativeBackup != after.IsTentativeBackup)
            changed.Add(OrderUnit.Backup);
        if (before.ShipmentId != after.ShipmentId)
            changed.Add(OrderUnit.Shipment);
        if (before.PrintedAt != after.PrintedAt
            || !string.Equals(before.PrintBatchId, after.PrintBatchId, StringComparison.Ordinal)
            || before.PrintSeq != after.PrintSeq)
            changed.Add(OrderUnit.Print);
        return changed;
    }
}

/// <summary>
/// Siparişin güncellemede yazılabilen alanları: birimler (spec §2) ve yalnız boşsa dolan
/// <see cref="ParentOrderId"/>. Sunucu satırında, push'ta ve birleşik sonuçta aynı biçim.
/// </summary>
public sealed record OrderUnitValues
{
    // Fiyat birimi
    public decimal Price { get; init; }
    public DateTimeOffset? PriceCorrectedAt { get; init; }
    public decimal? PaidPrice { get; init; }

    // İptal birimi
    public DateTimeOffset? CancelledAt { get; init; }
    public string? CancelReason { get; init; }

    // Yedek birimi
    public bool IsTentativeBackup { get; init; }

    // Kargo bağı birimi
    public Guid? ShipmentId { get; init; }

    // Baskı birimi
    public DateTimeOffset? PrintedAt { get; init; }
    public string? PrintBatchId { get; init; }
    public int? PrintSeq { get; init; }

    /// <summary>Yedeğin ebeveyni — oluşum alanı; yalnız boşsa doldurulur (K1).</summary>
    public Guid? ParentOrderId { get; init; }
}

/// <summary>Sunucudaki satırın birleştirmeye giren hâli: değerler, birim damgaları ve sürüm.</summary>
public sealed record OrderServerRow
{
    public required OrderUnitValues Values { get; init; }

    /// <summary>Birim damgaları (sunucu saati; yalnız komut basar, hiçbir yol silmez).</summary>
    public DateTimeOffset? PriceSetAt { get; init; }
    public DateTimeOffset? CancelSetAt { get; init; }
    public DateTimeOffset? BackupSetAt { get; init; }
    public DateTimeOffset? ShipmentSetAt { get; init; }

    /// <summary>Satırın sürümü (sunucuda <c>SyncVersion</c>, telde <c>Version</c>).</summary>
    public int Version { get; init; }
}

/// <summary>Gelen push'un birleştirmeye giren hâli.</summary>
public sealed record OrderPush
{
    public required OrderUnitValues Values { get; init; }

    /// <summary>v2: istemcinin bu satırda gördüğü son sunucu sürümü; yeni satırda ve v1'de boş.</summary>
    public int? BaseVersion { get; init; }
}

/// <summary>
/// Push'un biçimi ve koşulları. Kural eklendikçe girdisi buraya özellik olarak eklenir; <see cref="OrderUnitMerge.Merge"/>
/// imzası değişmez. Doldurulmamış özellik varsayılanını taşır (kip Off, oturumsuz, sahip değil, açılış penceresi
/// dışında).
/// </summary>
public sealed record OrderMergeContext
{
    public required OrderPushFormat Format { get; init; }

    /// <summary>Lisansın satış senkronu kipi On.</summary>
    public bool ModeOn { get; init; }

    /// <summary>Sunucu satırının oturumunun hâli (spec §2).</summary>
    public OrderSessionPhase SessionPhase { get; init; }

    /// <summary>Çağıran kopya oturumun sahibi.</summary>
    public bool CallerOwnsSession { get; init; }

    /// <summary>Oturum zorla bitirildi (<c>ForceEndedBy</c> dolu).</summary>
    public bool SessionForceEnded { get; init; }

    /// <summary>Çağıran kopya açılış penceresinde (spec §4.0).</summary>
    public bool OpeningWindow { get; init; }
}

/// <summary>Push biçimi: v1 ≤0.9.10'un satırı, v2 satış senkronunu bilen istemcinin satırı.</summary>
public enum OrderPushFormat
{
    V1 = 1,
    V2 = 2,
}

/// <summary>Siparişin oturumunun hâli (spec §2 tablosu).</summary>
public enum OrderSessionPhase
{
    /// <summary>Siparişin oturumu yok.</summary>
    None = 0,

    /// <summary><c>EndedAt</c> boş.</summary>
    Live = 1,

    /// <summary><c>EndedAt</c> dolu, <c>ReleasedAt</c> boş.</summary>
    HandingOver = 2,

    /// <summary><c>ReleasedAt</c> dolu.</summary>
    Released = 3,
}

/// <summary>Birlikte yazılan alan grubu (spec §2).</summary>
public enum OrderUnit
{
    Price,
    Cancel,
    Backup,
    Shipment,
    Print,
}

/// <summary>Push'un değiştirmek istediği ama yazılmayan birim ve nedeni.</summary>
public sealed record IgnoredOrderUnit(OrderUnit Unit, string Reason);

/// <summary>Satır başına sonuç.</summary>
public enum OrderMergeStatus
{
    /// <summary>Push'un taşıdığı her değer yazıldı ya da zaten sunucudaydı.</summary>
    Applied,

    /// <summary>En az bir birim yazılmadı; istemci sunucunun güncel satırını uygulamalı.</summary>
    Ignored,
}

/// <summary>Birleştirmenin sonucu.</summary>
/// <param name="Merged">Satıra yazılacak değerler (yazılmayan alan sunucudaki değeri taşır).</param>
/// <param name="ChangedUnits">Değeri sunucudakinden farklı olan birimler — yazılanlar.</param>
/// <param name="Ignored">Push'un farklı değer taşıdığı ama yazılmayan birimler.</param>
public sealed record OrderMergeResult(
    OrderUnitValues Merged,
    IReadOnlyList<OrderUnit> ChangedUnits,
    IReadOnlyList<IgnoredOrderUnit> Ignored)
{
    public OrderMergeStatus Status => Ignored.Count == 0 ? OrderMergeStatus.Applied : OrderMergeStatus.Ignored;

    /// <summary>Satırın nedeni: yok sayılan ilk birimin nedeni; hiçbiri yoksa null.</summary>
    public string? Reason => Ignored.Count == 0 ? null : Ignored[0].Reason;
}
