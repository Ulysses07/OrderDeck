using System.Text;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Arama için karşılaştırılabilir biçim üretir: büyük harf + Türkçe harfler
/// ASCII'ye katlanmış + boşluklar sadeleşmiş.
///
/// Neden gerekli: <c>Name.Contains(q)</c> SQL'de <c>LIKE '%…%'</c>'ye çevriliyor
/// ve büyük/küçük harf duyarlılığını <b>veritabanının collation'ı</b> belirliyor.
/// SQL Server varsayılanı duyarsız, PostgreSQL ise duyarlı — yani göç günü arama
/// sessizce bozulurdu ("tişört" yazan "Tişört"ü bulamazdı). Hem saklanan değer
/// (<c>Product.NameSearch</c>) hem aranan iğne <b>aynı</b> fonksiyondan geçtiği
/// için eşleşme collation'dan bağımsız hâle gelir; göçte davranış değişmez.
///
/// Türkçe harf katlaması ayrıca gerçek bir kullanıcı şikâyetini kapatıyor:
/// "tisort" → "Tişört", "kirmizi" → "Kırmızı" artık eşleşiyor.
///
/// Harf/rakam dışı karakterler (tire, nokta, parantez) <b>korunur</b>: atmak
/// sürpriz eşleşmeler üretir ("A-1" ile "A1" aynı şey değil).
/// </summary>
public static class SearchNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(ch))
            {
                // Baştaki boşluk hiç yazılmaz, ardışık boşluklar tek boşluğa iner;
                // sondaki de yazılmadan kalır (bayrak asla boşaltılmaz).
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(TurkishAscii.Fold(ch));
        }

        return sb.ToString();
    }
}
