using System.Linq;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Müşteri arama kutusunun eşleştirme kuralı. Tek yerde duruyor çünkü aynı
/// kutu iki ayrı yoldan besleniyor: normal arama (repo sorgusu) ve "son
/// yayında alışveriş yapanlar" süzgeci (bellekteki liste).
///
/// NEDEN AYRI SINIF / NEDEN SQL DEĞİL:
/// - Arama yalnız <c>Username</c>'e bakıyordu; kartta görünen ad ise
///   <c>DisplayName</c>/<c>FullName</c>'den geliyor → operatör ekranda gördüğü
///   ismi yazınca sonuç boş dönüyordu.
/// - SQLite'ın <c>LOWER()</c>'ı yalnız ASCII'yi küçültür: "Şeyma" ile "şeyma"
///   eşleşmez. Ayrıca Türkçe'de i/İ/ı/I ordinal olarak dört ayrı harf.
/// - Telefonla arama da buradan geçer: iki taraf da rakamlara indirgenmeden
///   "0555..." ile kayıttaki "+90555..." eşleşmez.
/// </summary>
public static class CustomerSearch
{
    /// <summary>Müşteri, arama metnine uyuyor mu? Metin boşlukla ayrılmış
    /// parçalara bölünür ve HEPSİ eşleşmelidir — "delikurt bilal" da
    /// "Bilal Delikurt"u bulur, araya fazladan boşluk kaçması sorun olmaz.
    /// Her parça ya ad/kullanıcı adı alanlarında ya da telefonda tutmalı.</summary>
    public static bool Matches(Customer c, string query)
    {
        var phone = NormalizePhone(c.Phone);

        // Salt numara girildiyse parçalara bölme: "0555 111 22 33" dört ayrı
        // terime düşerse "22"/"33" tek başına anlamsız kalır ve arama boş döner.
        if (IsPhoneQuery(query)) return MatchesPhone(phone, query);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries
                                  | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return false;

        var haystack = $"{Fold(c.Username)} {Fold(c.DisplayName)} {Fold(c.FullName)}";

        foreach (var term in terms)
        {
            if (haystack.Contains(Fold(term), StringComparison.Ordinal)) continue;
            if (MatchesPhone(phone, term)) continue;
            return false;
        }
        return true;
    }

    /// <summary>Girdi bir telefon numarası mı? Rakam içeriyor ve rakam dışında
    /// yalnız numara yazımında kullanılan işaretler var demektir.</summary>
    private static bool IsPhoneQuery(string q) =>
        q.Any(char.IsAsciiDigit) &&
        q.All(ch => char.IsAsciiDigit(ch) || ch is ' ' or '+' or '-' or '(' or ')' or '/' or '.');

    /// <summary>Telefon eşleşmesi. Operatör numarayı "0555 111 22 33",
    /// "+90 555...", "5551112233" gibi farklı yazıyor; kayıtta ise tek bir
    /// biçim var. İki taraf da rakamlara indirgenip ülke kodu/baştaki sıfır
    /// atıldıktan sonra karşılaştırılır. En az 4 rakam istenir — yoksa "12"
    /// gibi bir girdi yüzlerce numarayı getirir.</summary>
    private static bool MatchesPhone(string normalizedPhone, string term)
    {
        if (normalizedPhone.Length == 0) return false;
        var digits = NormalizePhone(term);
        return digits.Length >= 4 && normalizedPhone.Contains(digits, StringComparison.Ordinal);
    }

    /// <summary>Rakamları süzer, baştaki 90 ülke kodunu ve sıfırları atar
    /// ("+90 555 111 22 33" → "5551112233").</summary>
    private static string NormalizePhone(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var digits = new string(s.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length > 10 && digits.StartsWith("90", StringComparison.Ordinal))
            digits = digits[2..];
        return digits.TrimStart('0');
    }

    /// <summary>Karşılaştırma anahtarı: Türkçe'nin i ailesi (i/İ/ı/I) tek harfe
    /// indirilir, kalanı <c>ToLowerInvariant</c> ile küçültülür (ş/ğ/ö/ç/ü dahil).</summary>
    private static string Fold(string? s) =>
        string.IsNullOrEmpty(s)
            ? ""
            : s.Replace('İ', 'i').Replace('I', 'i').Replace('ı', 'i').ToLowerInvariant();
}
