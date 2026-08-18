namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Otomatik etiket kuralının bağlanabileceği SABİT olaylar.
///
/// <para>Etiketin kendisi dinamik (yayıncı yazar), olay listesi değil: kural
/// "şu olduğunda şu etiketi yapıştır" diyor ve "şu olduğunda" kısmını kod
/// üretiyor. Bu yüzden enum, DB'de int olarak saklanır — yeni olay eklemek
/// kod değişikliği gerektirir, kasten.</para>
///
/// <para>Değerler AÇIKÇA yazıldı: DB'de int duruyorlar, araya yeni bir üye
/// eklenmesi mevcut satırların anlamını kaydırmasın.</para>
/// </summary>
public enum WaLabelEvent
{
    /// <summary>Yayıncı dekontu onayladı (panel).</summary>
    PaymentApproved = 0,

    /// <summary>Yayıncı dekontu reddetti (panel).</summary>
    PaymentRejected = 1,

    /// <summary>WPF'ten yeni basılmış (iptal olmayan, kargo ücreti olmayan) sipariş geldi.</summary>
    OrderReceived = 2,

    /// <summary>Kargo dosyasının durumu değişti (beklet / alıcı ödemeli / kargolandı).</summary>
    ShipmentStatusChanged = 3,

    /// <summary>
    /// Müşteri WhatsApp'tan belge ya da görsel gönderdi. Tek olay: dekontu kimi
    /// PDF kimi ekran görüntüsü yolluyor ve gelenin gerçekten dekont olduğu
    /// bilinemez. Yanlış etiketin bedeli bir tık, kaçırmanın bedeli kayıp para.
    /// </summary>
    CustomerSentDocument = 4,
}

/// <summary>
/// Etiket rengi serbest metin değil: panel ve WPF aynı rengi aynı görsün diye
/// sabit palet. Değerler küçük harf hex, '#' ile.
/// </summary>
public static class WaLabelColors
{
    public static readonly string[] Palette =
    {
        "#ef4444", // kırmızı
        "#f97316", // turuncu
        "#eab308", // sarı
        "#22c55e", // yeşil
        "#14b8a6", // turkuaz
        "#3b82f6", // mavi
        "#8b5cf6", // mor
        "#6b7280", // gri
    };

    /// <summary>Paletteki kanonik (küçük harfli) hâli, yoksa <c>null</c>.
    /// Kaydedilen değer daima buradan geçer ki panelde renkler karşılaştırılabilsin.
    ///
    /// <para>Büyük/küçük harf duyarsız — panel <c>#EF4444</c> gönderdiğinde
    /// reddetmek kullanıcıya hiçbir şey anlatmayan bir hata olurdu.</para></summary>
    public static string? Normalize(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var lower = color.Trim().ToLowerInvariant();
        return Array.IndexOf(Palette, lower) >= 0 ? lower : null;
    }
}
