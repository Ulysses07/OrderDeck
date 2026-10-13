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
/// <para><b>K2 — fiyat, iptal, yedek.</b> Birimin damgası (<c>*SetAt</c>) boşsa push'taki değer yazılır: bugünkü
/// son-gelen-kazanır. Damga doluysa birimi yalnız satırın güncel sürümünü gördüğünü kanıtlayan v2 push'u
/// (<see cref="OrderPush.BaseVersion"/> = <see cref="OrderServerRow.Version"/>) yazar; v1 damgalı birimi hiç yazamaz.
/// Yedek tek yönlüdür: <see cref="OrderUnitValues.IsTentativeBackup"/> yalnız 1'den 0'a gider.</para>
///
/// <para><b>K3 — baskı.</b> Sunucuda <see cref="OrderUnitValues.PrintedAt"/> boşsa baskı push'tan alınır (v2'de grup
/// alanlarıyla birlikte); ancak sipariş şu anda komutla iptal edilmişse (iptal birimi birleştirildikten sonra
/// <c>CancelledAt</c> dolu ∧ <c>CancelSetAt</c> dolu) alınmaz: <see cref="ReasonCancelledElsewhere"/>, istemci kâğıdı
/// imha eder. Sunucuda PrintedAt doluysa hiçbir yoldan silinmez ve değişmez; v2 yalnız boş grup alanlarını
/// doldurur.</para>
///
/// <para><b>Yok sayma.</b> Bir birim, push o birimde sunucunun yazdığından farklı bir değer taşıdıysa yok sayılmış
/// sayılır; aynı değeri taşıyan push uygulanmış sayılır. Zaman damgaları saniyeye göre karşılaştırılır: masaüstü
/// zamanı Unix saniyesi olarak saklar (<c>FromUnixTimeSeconds</c>), sunucunun komutla bastığı saniye altı kesir
/// masaüstüne geri dönünce kaybolur — bu fark yeni bir değer değildir. Yazılan değer ise her zaman olduğu gibi
/// yazılır.</para>
///
/// <para><b>Biçim.</b> v1 (≤0.9.10) yalnız <see cref="OrderUnitValues.Price"/>, iptal alanları,
/// <see cref="OrderUnitValues.IsTentativeBackup"/> ve <see cref="OrderUnitValues.PrintedAt"/> taşır; öbür alanlar
/// v1'de "gönderilmedi" demektir ve sunucudaki değer korunur (NULL'lanmaz).</para>
/// </summary>
public static class OrderUnitMerge
{
    /// <summary>K2: damgalı birim, satırın güncel sürümünü görmemiş push'tan yazılmaz.</summary>
    public const string ReasonUnitStamped = "unit-stamped";

    /// <summary>K2: geçici yedek bayrağı 0'dan 1'e dönmez.</summary>
    public const string ReasonBackupOneWay = "backup-one-way";

    /// <summary>K3: sipariş şu anda komutla iptal edilmiş; baskı alınmaz, kâğıt imha edilmeli.</summary>
    public const string ReasonCancelledElsewhere = "cancelled-elsewhere";

    /// <summary>K3: sunucudaki baskı kalır (silinmez, kaymaz, dolu grup değişmez).</summary>
    public const string ReasonAlreadyPrinted = "already-printed";

    /// <summary>Sunucu satırı ile push'u birleştirir.</summary>
    /// <param name="server">Sunucudaki satırın güncel hâli.</param>
    /// <param name="push">Gelen push'un değerleri.</param>
    /// <param name="context">Push'un biçimi ve koşulları. K1–K3'ün okuduğu tek alan
    /// <see cref="OrderMergeContext.Format"/>.</param>
    public static OrderMergeResult Merge(OrderServerRow server, OrderPush push, OrderMergeContext context)
    {
        var s = server.Values;
        var p = push.Values;
        var v2 = context.Format == OrderPushFormat.V2;
        var merged = s;
        var ignored = new List<IgnoredOrderUnit>();

        // K2: damgasız birim → son gelen kazanır; damgalı birim yalnız güncel sürümü görmüş v2'den.
        bool Writable(DateTimeOffset? setAt) => setAt is null || (v2 && push.BaseVersion == server.Version);

        // Fiyat {Price, PriceCorrectedAt, PaidPrice}; v1 yalnız Price taşır.
        var pushCorrectedAt = v2 ? p.PriceCorrectedAt : s.PriceCorrectedAt;
        var pushPaidPrice = v2 ? p.PaidPrice : s.PaidPrice;
        if (Writable(server.PriceSetAt))
            merged = merged with { Price = p.Price, PriceCorrectedAt = pushCorrectedAt, PaidPrice = pushPaidPrice };
        else if (p.Price != s.Price
                 || !SameSecond(pushCorrectedAt, s.PriceCorrectedAt)
                 || pushPaidPrice != s.PaidPrice)
            ignored.Add(new IgnoredOrderUnit(OrderUnit.Price, ReasonUnitStamped));

        // İptal {CancelledAt, CancelReason}.
        if (Writable(server.CancelSetAt))
            merged = merged with { CancelledAt = p.CancelledAt, CancelReason = p.CancelReason };
        else if (!SameSecond(p.CancelledAt, s.CancelledAt)
                 || !string.Equals(p.CancelReason, s.CancelReason, StringComparison.Ordinal))
            ignored.Add(new IgnoredOrderUnit(OrderUnit.Cancel, ReasonUnitStamped));

        // Yedek {IsTentativeBackup}: yalnız 1 → 0.
        if (p.IsTentativeBackup != s.IsTentativeBackup)
        {
            if (p.IsTentativeBackup)
                ignored.Add(new IgnoredOrderUnit(OrderUnit.Backup, ReasonBackupOneWay));
            else if (Writable(server.BackupSetAt))
                merged = merged with { IsTentativeBackup = false };
            else
                ignored.Add(new IgnoredOrderUnit(OrderUnit.Backup, ReasonUnitStamped));
        }

        // Kargo bağı (K4) bu sürümde push'tan alınmaz: sunucudaki değer kalır.

        // Baskı {PrintedAt, PrintBatchId, PrintSeq} (K3). İptal kararı yukarıda verildi; "şu anda komutla iptal"
        // birleşik iptale ve damgaya bakar.
        if (s.PrintedAt is null)
        {
            if (p.PrintedAt is not null)
            {
                if (merged.CancelledAt is not null && server.CancelSetAt is not null)
                    ignored.Add(new IgnoredOrderUnit(OrderUnit.Print, ReasonCancelledElsewhere));
                else
                    merged = merged with
                    {
                        PrintedAt = p.PrintedAt,
                        PrintBatchId = v2 ? p.PrintBatchId : s.PrintBatchId,
                        PrintSeq = v2 ? p.PrintSeq : s.PrintSeq,
                    };
            }
        }
        else
        {
            if (v2)
                merged = merged with
                {
                    PrintBatchId = s.PrintBatchId ?? p.PrintBatchId,
                    PrintSeq = s.PrintSeq ?? p.PrintSeq,
                };
            if (!SameSecond(p.PrintedAt, s.PrintedAt)
                || (v2 && (!string.Equals(p.PrintBatchId, merged.PrintBatchId, StringComparison.Ordinal)
                           || p.PrintSeq != merged.PrintSeq)))
                ignored.Add(new IgnoredOrderUnit(OrderUnit.Print, ReasonAlreadyPrinted));
        }

        // K1: ebeveyn yalnız boşsa doldurulur.
        if (v2 && s.ParentOrderId is null)
            merged = merged with { ParentOrderId = p.ParentOrderId };

        return new OrderMergeResult(merged, ChangedUnits(s, merged), ignored);
    }

    /// <summary>Aynı Unix saniyesi mi (ikisi de boşsa da aynı).</summary>
    private static bool SameSecond(DateTimeOffset? a, DateTimeOffset? b)
        => a is null || b is null ? a is null && b is null : a.Value.ToUnixTimeSeconds() == b.Value.ToUnixTimeSeconds();

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

    /// <summary>
    /// Satırın nedeni; hiçbir birim yok sayılmadıysa null. İstemcide eylem isteyen neden önce gelir:
    /// <see cref="OrderUnitMerge.ReasonCancelledElsewhere"/> "kâğıdı imha et" uyarısını açar. Yoksa yok sayılan ilk
    /// birimin nedeni.
    /// </summary>
    public string? Reason => Ignored.Count == 0
        ? null
        : Ignored.Any(i => i.Reason == OrderUnitMerge.ReasonCancelledElsewhere)
            ? OrderUnitMerge.ReasonCancelledElsewhere
            : Ignored[0].Reason;
}
