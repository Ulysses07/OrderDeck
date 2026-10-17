namespace OrderDeck.Core.Sales;

/// <summary>Geri doldurmanın aşaması (spec 2026-10-13 §8.2-3): sırayla oturumlar, etiketler, dosya bağları; sonra
/// onarımın onayını bekleme ve tamamlanma.</summary>
public enum SalesBackfillPhase
{
    /// <summary>Yerel oturumlar, <c>StreamSession.rowid</c> sırasıyla.</summary>
    Sessions = 0,
    /// <summary>Yerel oturumların etiketleri, <c>Label.rowid</c> sırasıyla.</summary>
    Labels = 1,
    /// <summary>Kargo dosyaları ve bağlı etiketleri, <c>Shipment.rowid</c> sırasıyla.</summary>
    ShipmentLinks = 2,
    /// <summary>Üç aşama gönderildi; yeniden kuyruğa alınan satırların <c>applied</c> onayı ve
    /// <c>hello {backfillCompleted: true}</c> bekleniyor (§8.2-5).</summary>
    Done = 3,
    /// <summary>Tamamlandı: <c>backfillCompleted</c> sunucuya gitti. Bu kopya ve bu lisans için bir daha istek
    /// yok.</summary>
    Completed = 4,
}

/// <summary>
/// <c>SyncCursor('sales-backfill-out', LicenseKey)</c> satırının anlamı (spec 2026-10-13 §3.2, §8.2; W1-09). Tek satır
/// aşamayı ve aşamanın son gönderilen <c>rowid</c>'ini birlikte taşır.
///
/// <para><b>Kodlama:</b> <c>Seq = aşama × 10^12 + rowid</c> (<see cref="PhaseFactor"/>). Ondalık çarpan
/// bilinçli: SQLite tarayıcısında <c>2000000000123</c> "dosya bağları, rowid 123" diye okunur. rowid 10^12'nin altında
/// kalır (bir bilgisayarın bütün geçmişi bunun milyonda biri). <c>Seq = 4 × 10^12</c> tamamlanma işaretidir
/// (<see cref="SalesBackfillPhase.Completed"/>). <c>LastId</c> imlecin ait olduğu kopya kimliğidir
/// (<c>_meta.ReplicaId</c>): geri yüklemeden sonra kimlik döner (§8.7) ve imleç yeni kopya için baştan başlar —
/// sunucu tamamlanmayı kopya başına tutar. <c>UpdatedAt</c> yalnız tanı içindir.</para>
///
/// <para><b>Satır yoksa</b>, <c>Seq</c> çözülemiyorsa ya da kopya başkasıysa imleç baştandır
/// (<see cref="Start"/>): geri doldurma idempotent, baştan koşmak zararsızdır (§8.2). rowid kararlıdır çünkü uygulama
/// <c>VACUUM</c> koşmaz; yedek ve geri yükleme sayfaları olduğu gibi taşır.</para>
/// </summary>
public readonly record struct SalesBackfillCursor(SalesBackfillPhase Phase, long RowId)
{
    /// <summary><c>SyncCursor.Name</c>.</summary>
    public const string Name = "sales-backfill-out";

    /// <summary>Aşamanın çarpanı.</summary>
    public const long PhaseFactor = 1_000_000_000_000L;

    public static SalesBackfillCursor Start => new(SalesBackfillPhase.Sessions, 0);

    public static SalesBackfillCursor Completed => new(SalesBackfillPhase.Completed, 0);

    public long Encode() => (long)Phase * PhaseFactor + RowId;

    /// <summary>Çözülemeyen değer (negatif, bilinmeyen aşama) <c>null</c>.</summary>
    public static SalesBackfillCursor? Decode(long seq)
    {
        if (seq < 0) return null;
        var phase = seq / PhaseFactor;
        if (phase > (long)SalesBackfillPhase.Completed) return null;
        var rowId = seq % PhaseFactor;
        // Bitmiş aşamalar rowid taşımaz.
        if (phase >= (long)SalesBackfillPhase.Done && rowId != 0) return null;
        return new SalesBackfillCursor((SalesBackfillPhase)phase, rowId);
    }

    /// <summary>Aynı aşamada <paramref name="rowId"/>'ye kadar gönderildi.</summary>
    public SalesBackfillCursor After(long rowId) => this with { RowId = rowId };

    /// <summary>Sonraki aşamanın başı.</summary>
    public SalesBackfillCursor NextPhase() => new(Phase + 1, 0);
}
