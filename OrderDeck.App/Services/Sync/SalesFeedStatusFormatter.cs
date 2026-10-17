namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Satış akışının durum satırı (spec 2026-10-13 §5.7 "Akış"): "Satış: güncel ✓ (SS:DD)", "Satış: yetişiyor",
/// "Satış: N öğe bekliyor", arkasına "M öğe atlandı". Saf işlev — girdileri <see cref="SalesChangesPullService.Status"/>
/// ve dikkat sayacının atlanan satış öğeleri (<c>SyncAttention.SkippedSalesFeedItems</c>).
///
/// <para><b>Yalnız mod On'da:</b> mod Off'ta (bugün her lisans) satır yoktur — <see cref="Format"/> <c>null</c> döner,
/// kabuk satırı gizler. Saat son tam yetişmenin saatidir: bağlantı koparsa satır son bilinen anı gösterir (bayatlık
/// saatten okunur; çevrimdışı uyarısı genel senkron satırının işidir).</para>
///
/// <para>Metinde kişisel veri yok; yalnız sayı ve saat.</para>
/// </summary>
public static class SalesFeedStatusFormatter
{
    private const string Prefix = "Satış: ";

    /// <summary>Atlanan satış öğesinde ipucunun ek satırı.</summary>
    internal const string SkippedAdvice =
        "Diğer bilgisayarlardan gelen bazı satış değişiklikleri bu bilgisayara uygulanamadı; " +
        "uyarı geçmezse destekle iletişime geç.";

    /// <summary>Akış bir öğede beklerken ipucu.</summary>
    internal const string WaitingAdvice =
        "Bu bilgisayardaki bir kayıt gönderilince, müşteri bilgisi inince ya da açık ödeme isteği bitince " +
        "kendiliğinden sürer; geçmezse destekle iletişime geç.";

    /// <returns>Satırın metni ve sağlığı; mod Off ya da durum yoksa <c>null</c> (satır gizli).</returns>
    public static SyncStatusFormatter.Status? Format(SalesFeedStatus? status, int skipped)
    {
        if (status is not { ModeOn: true }) return null;

        SyncStatusFormatter.Status line;
        if (status.WaitingItems > 0)
            line = new($"{Prefix}{status.WaitingItems} öğe bekliyor", false, WaitingAdvice);
        else if (status.CatchingUp || status.LastCaughtUpAt is null)
            line = new($"{Prefix}yetişiyor", false);
        else
            line = new($"{Prefix}güncel ✓ ({status.LastCaughtUpAt.Value.ToLocalTime():HH:mm})", true);

        return skipped > 0 ? line with { Text = $"{line.Text} · {skipped} öğe atlandı", Healthy = false } : line;
    }

    /// <summary>İpucu: metnin tamamı, durumun ipucu ve atlanan öğede ne yapılacağı (satırlar <c>\n</c> ile).</summary>
    public static string Tooltip(SyncStatusFormatter.Status status, int skipped)
    {
        var lines = new List<string>(3) { status.Text };
        if (status.Advice is { } advice) lines.Add(advice);
        if (skipped > 0) lines.Add(SkippedAdvice);
        return string.Join("\n", lines);
    }
}
