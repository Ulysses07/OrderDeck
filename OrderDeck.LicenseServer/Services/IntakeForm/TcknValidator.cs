namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// TC Kimlik No doğrulaması (resmî kontrol basamağı algoritması).
///
/// NEDEN: Form yalnız <c>^\d{11}$</c> arıyordu. Prod verisinde 162 TCKN'nin
/// 9'u bu kalıptan geçtiği hâlde geçersiz — dört tanesi "11111111111" gibi
/// baştan savma dolgular. Dönem raporunun ürettiği e-Fatura sayfası TCKN'yi
/// alıcı sütununa yazdığı için hatalı numara faturayı entegratörde reddettirir.
///
/// KURAL: 11 hane, ilk hane 0 olamaz.
///   d10 = ((d1+d3+d5+d7+d9) * 7 - (d2+d4+d6+d8)) mod 10
///   d11 = (d1..d10 toplamı) mod 10
/// </summary>
public static class TcknValidator
{
    /// <summary>Dış boşlukları temizler, boş girdiyi <c>null</c>'a çevirir.</summary>
    public static string? Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Geçerliyse <c>null</c>, değilse Türkçe hata mesajı döner. Boş girdi
    /// hata değildir — TCKN opsiyonel alan.
    /// </summary>
    public static string? Validate(string? tckn)
    {
        if (string.IsNullOrWhiteSpace(tckn)) return null;

        if (tckn.Length != 11 || !tckn.All(char.IsAsciiDigit))
            return "TC Kimlik No 11 rakamdan oluşmalı.";

        if (tckn[0] == '0')
            return "TC Kimlik No 0 ile başlayamaz.";

        var d = new int[11];
        for (var i = 0; i < 11; i++) d[i] = tckn[i] - '0';

        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];

        // C#'ta % negatif dönebilir; ((x % 10) + 10) % 10 ile normalize edilir.
        var check10 = ((odd * 7 - even) % 10 + 10) % 10;
        if (d[9] != check10) return InvalidMessage;

        var check11 = d.Take(10).Sum() % 10;
        if (d[10] != check11) return InvalidMessage;

        return null;
    }

    private const string InvalidMessage =
        "TC Kimlik No geçersiz. Kimliğindeki numarayı kontrol et.";
}
