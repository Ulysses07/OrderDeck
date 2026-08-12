using System.Text;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Eksen değerinin görünen adından barkoda girebilecek kod parçasını türetir.
///
/// Neden gerekli: barkod sembolü Code128 ve Code128 <b>ASCII</b> kodlar.
/// "Yeşil" doğrudan barkoda giremez. Görünen ad serbest kalır, kod parçası
/// buradan türetilir ve kullanıcı isterse elle düzeltir.
///
/// Kural: ilk boşluksuz parçadan ASCII harf/rakam süz, büyük harfe çevir,
/// 4 karaktere kısalt. İlk parçadan hiçbir şey çıkmazsa sonrakine geçilir
/// ("— Mavi" → MAVI).
///
/// Çıktı her zaman büyük harf olduğu için sorguda <c>ToUpper()</c> kullanmaya
/// gerek kalmaz — PostgreSQL göçünde eşleştirme davranışı değişmez.
/// </summary>
public static class AxisCodeDeriver
{
    public const int MaxLength = 4;

    public static string Derive(string? displayValue)
    {
        if (string.IsNullOrWhiteSpace(displayValue)) return string.Empty;

        foreach (var token in displayValue.Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var code = Squeeze(token);
            if (code.Length > 0) return code;
        }

        return string.Empty;
    }

    private static string Squeeze(string token)
    {
        var sb = new StringBuilder(MaxLength);

        foreach (var ch in token.ToUpperInvariant())
        {
            var mapped = Map(ch);
            if (mapped is null) continue;

            sb.Append(mapped.Value);
            if (sb.Length == MaxLength) break;
        }

        return sb.ToString();
    }

    private static char? Map(char c) => c switch
    {
        'Ç' => 'C',
        'Ğ' => 'G',
        'İ' => 'I',   // U+0130: ToUpperInvariant bunu korur
        '\u0131' => 'I', // ı: ToUpperInvariant U+0131'i küçük bırakır
        'Ö' => 'O',
        'Ş' => 'S',
        'Ü' => 'U',
        >= 'A' and <= 'Z' => c,
        >= '0' and <= '9' => c,
        _ => null,
    };
}
