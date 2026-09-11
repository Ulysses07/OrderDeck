using System.Linq;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Müşteri arama kutusunun eşleştirme kuralı. Tek yerde duruyor çünkü aynı
/// kutu iki ayrı yoldan besleniyor: normal arama (repo sorgusu) ve "son
/// yayında alışveriş yapanlar" süzgeci (bellekteki liste).
///
/// NEDEN AYRI SINIF:
/// - Arama yalnız <c>Username</c>'e bakıyordu; kartta görünen ad ise
///   <c>DisplayName</c>/<c>FullName</c>'den geliyor → operatör ekranda gördüğü
///   ismi yazınca sonuç boş dönüyordu.
/// - SQLite'ın <c>LOWER()</c>'ı yalnız ASCII'yi küçültür: "Şeyma" ile "şeyma"
///   eşleşmez. Ayrıca Türkçe'de i/İ/ı/I ordinal olarak dört ayrı harf.
/// - Telefonla arama da buradan geçer: iki taraf da rakamlara indirgenmeden
///   "0555..." ile kayıttaki "+90555..." eşleşmez.
///
/// <para><b>R3-04 (2026-09-12): eşleştirme artık SQL'de koşuyor</b> ama kural
/// hâlâ BURADA. Çözüm "SQL'e Türkçe öğretmek" değil — katlamayı yazma anında
/// C#'ta yapıp <c>Customer.SearchKey</c>/<c>PhoneKey</c> kolonlarına koymak
/// (göç 035). SQL yalnızca önceden katlanmış iki metni <c>INSTR</c> ile
/// karşılaştırıyor; bu, <c>Contains(..., Ordinal)</c> ile bayt bayt aynı iş.
/// <see cref="BuildSearchKey"/>/<see cref="NormalizePhoneKey"/> ile
/// <see cref="Matches"/> aynı <see cref="Fold"/>'u kullandığı için iki yol
/// ayrışamaz; ayrışmadıklarını CustomerSearchSqlTests rastgele terimlerle
/// her koşuda yeniden kanıtlıyor.</para>
/// </summary>
public static class CustomerSearch
{
    /// <summary>Telefon eşleşmesi için gereken en az rakam sayısı. Altında
    /// arama yüzlerce numarayı getirir, bu yüzden hiç eşleşmez sayılır.</summary>
    public const int MinPhoneDigits = 4;

    /// <summary>FTS5 <c>trigram</c> belirteçleyicisinin alt sınırı: 3 karakterden
    /// kısa metni indeksleyemez ve MATCH sorgusu HATA VERMEZ, sessizce BOŞ döner.
    /// Bu yüzden kısa terimli sorgular indeksi hiç kullanmaz, tarama yoluna
    /// düşer (bkz. CustomerRepository.Search). Sessiz yanlış-boş sonuç R3-03'ün
    /// hata sınıfıydı; aynı tuzağa indeksle geri düşmüyoruz.
    ///
    /// <para>Birim <b>kod noktası</b>, UTF-16 birimi DEĞİL — bkz.
    /// <see cref="CodePointCount"/>.</para></summary>
    public const int MinTrigramLength = 3;

    /// <summary>Metnin Unicode <b>kod noktası</b> sayısı.
    ///
    /// <para><b>R5-01 (2026-09-12).</b> Eşik <c>string.Length</c> ile ölçülüyordu;
    /// o UTF-16 birimi sayar. "a😀" 3 birim ama yalnızca 2 kod noktasıdır ve
    /// FTS5'in trigram belirteçleyicisi kod noktası okur (<c>READ_UTF8</c>) —
    /// yani ondan trigram üretemez. Sonuç: sorgu indekse yönlendiriliyor, MATCH
    /// hata vermeden boş dönüyor, taze pencerede BULUNMUŞ olan doğru satırlar da
    /// bu boş sonuçla değiştirildiği için arama yanlışlıkla boş kalıyordu.
    /// Ölçüyü kod noktasına çevirmek bu sorguları tarama yoluna geri gönderir.</para></summary>
    public static int CodePointCount(string s)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1) count++;
        return count;
    }

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

    /// <summary>Arama anahtarı: <see cref="Matches"/>'taki "haystack" ile
    /// BİREBİR aynı metin. <c>Customer.SearchKey</c> kolonuna yazılır (göç 035,
    /// <c>od_search_key</c> SQL fonksiyonu üzerinden tetikleyiciyle). Biçimi
    /// değiştirirsen <see cref="Matches"/>'i de değiştirmen gerekir — parite
    /// testi ikisini birbirine kilitliyor.</summary>
    public static string BuildSearchKey(string? username, string? displayName, string? fullName) =>
        $"{Fold(username)} {Fold(displayName)} {Fold(fullName)}";

    /// <summary>Telefon arama anahtarı: <c>Customer.PhoneKey</c> kolonuna yazılır.
    /// <see cref="Matches"/>'in kayıt tarafında uyguladığı normalizasyonun aynısı.</summary>
    public static string NormalizePhoneKey(string? phone) => NormalizePhone(phone);

    /// <summary>Girdi bir telefon numarası mı? Rakam içeriyor ve rakam dışında
    /// yalnız numara yazımında kullanılan işaretler var demektir.</summary>
    public static bool IsPhoneQuery(string q) =>
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
        return digits.Length >= MinPhoneDigits
               && normalizedPhone.Contains(digits, StringComparison.Ordinal);
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
    /// indirilir, kalanı <c>ToLowerInvariant</c> ile küçültülür (ş/ğ/ö/ç/ü dahil).
    ///
    /// <para>Bu SQL'de YAZILAMAZ: SQLite'ın <c>lower()</c>'ı yalnız ASCII'yi
    /// küçültür, <c>ToLowerInvariant</c> ise tüm Unicode'u. Tetikleyicide
    /// SQL ifadesiyle taklit etmeye çalışmak "Ş" gibi harflerde sessizce
    /// ayrışırdı; bu yüzden tetikleyici bu metodu <c>od_search_key</c> olarak
    /// çağırıyor (bkz. SqliteSearchFunctions).</para></summary>
    public static string Fold(string? s) =>
        string.IsNullOrEmpty(s)
            ? ""
            : s.Replace('İ', 'i').Replace('I', 'i').Replace('ı', 'i').ToLowerInvariant();
}
