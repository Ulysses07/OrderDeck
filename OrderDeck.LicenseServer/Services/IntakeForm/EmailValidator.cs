using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Kayıt formuna girilen e-postayı normalize eder ve doğrular.
///
/// NEDEN: Form eskiden yalnız <c>[EmailAddress]</c> kullanıyordu. Prod verisindeki
/// 500 e-postanın SIFIRINI reddediyor — pratikte hiçbir koruma sağlamıyor.
/// Gerçek hatalar iki grupta:
///   (a) format bozuk  → "...@gmail", "...@gamil"        (2 kayıt)
///   (b) alan adı yazım hatası → "gmail.con", "gml.com",
///       "qmail.com", "hotmail.comtr"                     (5 kayıt)
/// (b) grubu HER regex'ten geçer; yalnız bilinen sağlayıcılara olan düzenleme
/// mesafesiyle yakalanabilir. E-posta yanlışsa fatura/bildirim gitmiyor.
///
/// YANLIŞ POZİTİF RİSKİ: gerçek veride nadir ama meşru alan adları var
/// (arpas.com, metu.edu.tr, penti.com.tr, hotmail.de, mehmetmert.com). Bu
/// yüzden mesafe kontrolü YALNIZ uzun (≥9 karakter) ve yaygın sağlayıcılara
/// karşı yapılır, önce <see cref="KnownGood"/> kısa devre listesine bakılır.
/// Listede olmayan tanımadığımız alan adları serbest bırakılır — amaç açık
/// yazım hatasını yakalamak, beyaz liste dayatmak değil.
/// </summary>
public static class EmailValidator
{
    // Yerel kısım + alan adı. TLD en az 2 harf. Türkçe karakterler bilerek
    // dışarıda: ayrı ve daha anlaşılır bir mesajla yakalanıyor.
    private static readonly Regex Pattern = new(
        @"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,}$",
        RegexOptions.Compiled);

    private static readonly Regex TurkishChars = new(
        @"[çğıöşüÇĞİÖŞÜ]", RegexOptions.Compiled);

    /// <summary>
    /// Yazım hatası aranan yaygın sağlayıcılar. Hepsi ≥9 karakter: kısa alan
    /// adlarında (msn.com gibi) 1 harflik mesafe meşru bir adresi vurabilir.
    /// </summary>
    private static readonly string[] Popular =
    {
        "gmail.com", "googlemail.com", "hotmail.com", "outlook.com",
        "yahoo.com", "icloud.com", "yandex.com", "windowslive.com",
        "hotmail.com.tr", "yahoo.com.tr", "outlook.com.tr", "yandex.com.tr",
    };

    /// <summary>
    /// Mesafe kontrolünü kısa devre eden, bilinen doğru alan adları. Yaygın
    /// listedekilere yakın düşen meşru Türk sağlayıcıları burada
    /// (ttmail.com ↔ gmail.com mesafesi 2).
    /// </summary>
    private static readonly HashSet<string> KnownGood = new(StringComparer.Ordinal)
    {
        "gmail.com", "googlemail.com", "hotmail.com", "hotmail.com.tr",
        "hotmail.co.uk", "hotmail.de", "hotmail.fr", "outlook.com",
        "outlook.com.tr", "live.com", "msn.com", "windowslive.com",
        "yahoo.com", "yahoo.com.tr", "yahoo.co.uk", "yahoo.de", "yahoo.fr",
        "ymail.com", "rocketmail.com", "icloud.com", "me.com", "mac.com",
        "yandex.com", "yandex.com.tr", "yandex.ru", "mail.ru",
        "mail.com", "email.com", "ttmail.com", "mynet.com",
        "superonline.com", "turk.net", "protonmail.com", "proton.me",
        "aol.com", "gmx.com", "gmx.de", "gmx.net", "web.de",
    };

    /// <summary>
    /// Dış boşlukları temizler, alan adını küçük harfe çevirir. Yerel kısma
    /// dokunulmaz (RFC'ye göre büyük/küçük harf duyarlı olabilir).
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var at = s.LastIndexOf('@');
        if (at < 0) return s;
        return s[..(at + 1)] + s[(at + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// Normalize edilmiş e-postayı doğrular. Geçerliyse <c>null</c>, değilse
    /// kullanıcıya gösterilecek Türkçe hata mesajı döner. Boş girdi burada
    /// hata değildir — zorunluluk <c>[Required]</c> ile ayrıca uygulanır.
    /// </summary>
    public static string? Validate(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        if (TurkishChars.IsMatch(email))
            return "E-posta adresinde Türkçe karakter (ç, ğ, ı, ö, ş, ü) olamaz.";

        if (email.Contains(' '))
            return "E-posta adresinde boşluk olamaz.";

        if (!Pattern.IsMatch(email))
            return "Geçerli bir e-posta adresi girin (örnek: adiniz@gmail.com).";

        var domain = email[(email.LastIndexOf('@') + 1)..];
        var suggestion = SuggestDomain(domain);
        return suggestion is null
            ? null
            : $"E-posta adresindeki \"{domain}\" hatalı görünüyor. "
            + $"\"{suggestion}\" mi olacaktı?";
    }

    /// <summary>
    /// Alan adı yaygın bir sağlayıcıya çok yakınsa (ama aynısı değilse)
    /// doğrusunu döner; şüphe yoksa <c>null</c>.
    /// </summary>
    public static string? SuggestDomain(string domain)
    {
        if (KnownGood.Contains(domain)) return null;
        if (domain.Length < 5) return null;

        foreach (var known in Popular)
        {
            // Uzunluk farkı eşiği aşıyorsa mesafe hesaplamaya gerek yok.
            if (Math.Abs(known.Length - domain.Length) > 2) continue;
            if (Distance(domain, known) <= 2) return known;
        }
        return null;
    }

    /// <summary>Levenshtein mesafesi (iki satırlık klasik DP).</summary>
    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(
                    Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
