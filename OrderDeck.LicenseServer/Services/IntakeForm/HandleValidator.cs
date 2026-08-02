using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Kayıt formuna girilen platform kullanıcı adlarını normalize eder ve
/// platformun kendi kurallarına göre doğrular.
///
/// NEDEN: Form eskiden her şeyi kabul ediyordu; kullanıcılar kullanıcı adı
/// yerine ad-soyad ("Musa Sevinç") ya da e-posta yazıyordu. Instagram
/// scraper'ı sohbet yazarını <c>^@?[a-z0-9._]+$</c> ile süzdüğü için böyle
/// bir kayıt sohbetteki kişiyle ASLA eşleşemez — kayıt sessizce ölü doğuyor.
/// Prod verisinde 924 Instagram kaydının 29'u bu yüzden eşleşmiyordu.
///
/// FACEBOOK BİLEREK SERBEST: Facebook canlı yorumlarında DOM'dan gelen şey
/// kullanıcı adı değil, kişinin GÖRÜNEN ADI ("Musa Sevinç"). 80 Facebook
/// kaydının 37'sinde boşluk, 46'sında Türkçe karakter var ve bunlar doğru
/// veri. Buraya handle kuralı koymak Facebook eşleşmesini tamamen kırar.
/// </summary>
public static class HandleValidator
{
    // Platform id'leri sohbet tarafıyla aynı (lowercase).
    public const string Instagram = "instagram";
    public const string TikTok = "tiktok";
    public const string YouTube = "youtube";
    public const string Facebook = "facebook";

    private static readonly Regex InstagramPattern =
        new(@"^[A-Za-z0-9._]+$", RegexOptions.Compiled);

    private static readonly Regex TikTokPattern =
        new(@"^[A-Za-z0-9._]+$", RegexOptions.Compiled);

    // YouTube handle'ı tireye de izin verir.
    private static readonly Regex YouTubePattern =
        new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Baştaki <c>@</c>'leri ve dış boşlukları temizler. İÇ boşluk bilerek
    /// KORUNUR: "Musa Sevinç" sessizce "MusaSevinç"e dönüşürse kullanıcı
    /// hatayı göremez, üstelik "Musa Sevinc" gibi bir girdi geçerli görünen
    /// ama var olmayan bir handle'a dönüşür. Boşluk kalsın ki doğrulama
    /// yakalayıp kullanıcıya söylesin.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        while (s.StartsWith('@')) s = s[1..].TrimStart();
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Normalize edilmiş handle'ı doğrular. Geçerliyse <c>null</c>, değilse
    /// kullanıcıya gösterilecek Türkçe hata mesajı döner.
    /// </summary>
    public static string? Validate(string platform, string? handle)
    {
        if (string.IsNullOrEmpty(handle)) return null; // boş = o platform girilmemiş

        // Facebook: görünen ad kabul edilir, yalnız uzunluk sınırı.
        if (platform == Facebook)
            return handle.Length > 64 ? "En fazla 64 karakter." : null;

        if (handle.Contains(' '))
            return "Kullanıcı adında boşluk olamaz. Ad soyad değil, profilindeki "
                 + "@ ile başlayan kullanıcı adını yaz.";

        if (handle.Contains('@') || handle.Contains('/') || handle.Contains(':'))
            return "Buraya sadece kullanıcı adını yaz — e-posta ya da profil "
                 + "bağlantısı değil.";

        return platform switch
        {
            Instagram => ValidateInstagram(handle),
            TikTok => ValidateTikTok(handle),
            YouTube => ValidateYouTube(handle),
            _ => null
        };
    }

    // Instagram: 1-30, harf/rakam/nokta/alt çizgi, nokta ile başlayamaz/bitemez,
    // art arda iki nokta olamaz.
    private static string? ValidateInstagram(string h)
    {
        if (h.Length > 30) return "Instagram kullanıcı adı en fazla 30 karakter olabilir.";
        if (!InstagramPattern.IsMatch(h)) return CharError("nokta ve alt çizgi");
        if (h.StartsWith('.') || h.EndsWith('.'))
            return "Instagram kullanıcı adı nokta ile başlayamaz veya bitemez.";
        if (h.Contains("..")) return "Instagram kullanıcı adında art arda iki nokta olamaz.";
        return null;
    }

    // TikTok: 2-24, harf/rakam/nokta/alt çizgi, nokta veya alt çizgi ile
    // başlayamaz/bitemez.
    private static string? ValidateTikTok(string h)
    {
        if (h.Length < 2) return "TikTok kullanıcı adı en az 2 karakter olmalı.";
        if (h.Length > 24) return "TikTok kullanıcı adı en fazla 24 karakter olabilir.";
        if (!TikTokPattern.IsMatch(h)) return CharError("nokta ve alt çizgi");
        if (StartsOrEndsWithAny(h, '.', '_'))
            return "TikTok kullanıcı adı nokta veya alt çizgi ile başlayamaz/bitemez.";
        return null;
    }

    // YouTube handle: 3-30, harf/rakam/nokta/alt çizgi/tire, bunlarla
    // başlayamaz/bitemez.
    private static string? ValidateYouTube(string h)
    {
        if (h.Length < 3) return "YouTube kullanıcı adı en az 3 karakter olmalı.";
        if (h.Length > 30) return "YouTube kullanıcı adı en fazla 30 karakter olabilir.";
        if (!YouTubePattern.IsMatch(h)) return CharError("nokta, tire ve alt çizgi");
        if (StartsOrEndsWithAny(h, '.', '_', '-'))
            return "YouTube kullanıcı adı nokta, tire veya alt çizgi ile başlayamaz/bitemez.";
        return null;
    }

    private static bool StartsOrEndsWithAny(string h, params char[] chars)
        => Array.IndexOf(chars, h[0]) >= 0 || Array.IndexOf(chars, h[^1]) >= 0;

    private static string CharError(string extras)
        => $"Kullanıcı adında yalnızca İngilizce harf, rakam, {extras} "
         + "kullanılabilir (ç, ğ, ı, ö, ş, ü gibi Türkçe harfler olmaz).";
}
