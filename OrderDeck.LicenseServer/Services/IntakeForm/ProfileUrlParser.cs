namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>Parse sonucunun türü.</summary>
public enum ProfileInputKind
{
    /// <summary>Girdi bir kullanıcı adı (ya da adres olmadığı için dokunulmadan geçti).</summary>
    Handle,
    /// <summary>Girdi <c>youtube.com/channel/UC…</c> idi; değer doğrudan kanal kimliği.</summary>
    YouTubeChannelId,
    /// <summary>Girdi bir adres ama profil adresi değil; <see cref="ProfileParseResult.Error"/> ne yapılacağını söyler.</summary>
    Error
}

public sealed record ProfileParseResult(ProfileInputKind Kind, string? Value, string? Error)
{
    public static ProfileParseResult Handle(string? h) => new(ProfileInputKind.Handle, h, null);
    public static ProfileParseResult ChannelId(string id) => new(ProfileInputKind.YouTubeChannelId, id, null);
    public static ProfileParseResult Fail(string msg) => new(ProfileInputKind.Error, null, msg);
}

/// <summary>
/// Müşterinin kullanıcı adı kutusuna yapıştırdığı profil ADRESİNİ handle'a
/// (ya da YouTube kanal kimliğine) çevirir.
///
/// NEDEN: sahada müşteriler adresi yapıştırıyor, HandleValidator reddediyor,
/// müşteri elle kırpıyor ve orada yanlış yazıyor. Kırpmayı biz yapıyoruz.
///
/// KURAL: bu sınıf AĞA ÇIKMAZ. Herkese açık bir formdan tetiklenen dış HTTP
/// isteği istemiyoruz (SSRF yüzeyi + gönderim gecikmesi). Bu yüzden vm./vt.
/// kısa linkleri çözülmez, müşteriye yönlendirme yapılır.
///
/// Facebook bilerek kapsam dışı: FB eşleşmesi görünen ada dayalı, adresteki
/// slug (profile.php?id=…) işimize yaramıyor.
/// </summary>
public static class ProfileUrlParser
{
    private static readonly string[] InstagramNonProfile =
        ["p", "reel", "reels", "stories", "tv", "explore"];

    public static ProfileParseResult Parse(string platform, string? raw)
    {
        var s = (raw ?? "").Trim();

        // Adres değilse dokunma: "@bilalcanli" HandleValidator'a olduğu gibi gitsin.
        if (s.Length == 0 || !s.Contains('/'))
            return ProfileParseResult.Handle(raw);

        // Facebook parser'a girmiyor.
        if (platform == HandleValidator.Facebook)
            return ProfileParseResult.Handle(raw);

        var rest = s;
        var schemeAt = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeAt >= 0) rest = rest[(schemeAt + 3)..];

        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0) rest = rest[..cut];
        rest = rest.TrimEnd('/');

        var slash = rest.IndexOf('/');
        var host = (slash < 0 ? rest : rest[..slash]).ToLowerInvariant();
        var path = slash < 0 ? "" : rest[(slash + 1)..];

        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        else if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];

        var hostPlatform = host switch
        {
            "youtube.com" or "youtu.be" => HandleValidator.YouTube,
            "instagram.com" => HandleValidator.Instagram,
            "tiktok.com" or "vm.tiktok.com" or "vt.tiktok.com" => HandleValidator.TikTok,
            _ => null
        };

        // Tanımadığımız adres: HandleValidator'ın mevcut mesajına düşsün.
        if (hostPlatform is null)
            return ProfileParseResult.Handle(raw);

        if (hostPlatform != platform)
            return ProfileParseResult.Fail(
                $"Bu bir {DisplayName(hostPlatform)} adresi. {DisplayName(platform)} kutusuna "
                + $"{DisplayName(platform)} kullanıcı adını yaz.");

        return hostPlatform switch
        {
            HandleValidator.YouTube => ParseYouTube(host, path),
            HandleValidator.Instagram => ParseInstagram(path),
            _ => ParseTikTok(host, path)
        };
    }

    private static ProfileParseResult ParseYouTube(string host, string path)
    {
        const string ytHelp = "Kanal sayfanı aç, adres çubuğundaki @ ile başlayan adresi yapıştır.";

        if (host == "youtu.be" || path.Length == 0)
            return ProfileParseResult.Fail("Bu bir video adresi, kanal adresi değil. " + ytHelp);

        var seg = path.Split('/');

        if (seg[0].StartsWith('@'))
        {
            var handle = seg[0][1..];
            return handle.Length == 0
                ? ProfileParseResult.Fail("Adreste kanal adı görünmüyor. " + ytHelp)
                : ProfileParseResult.Handle(handle);
        }

        if (seg[0] == "channel")
        {
            var id = seg.Length > 1 ? seg[1] : "";
            return IsChannelId(id)
                ? ProfileParseResult.ChannelId(id)
                : ProfileParseResult.Fail("Kanal adresi eksik ya da bozuk görünüyor. " + ytHelp);
        }

        // /c/, /user/, /watch, /shorts … hepsinde yapılacak iş aynı.
        return ProfileParseResult.Fail("Bu adresten kanalı bulamıyoruz. " + ytHelp);
    }

    /// <summary>YouTube kanal kimliği: "UC" + 22 karakter (harf/rakam/_/-).</summary>
    private static bool IsChannelId(string id)
    {
        if (id.Length != 24 || id[0] != 'U' || id[1] != 'C') return false;
        for (var i = 2; i < id.Length; i++)
        {
            var c = id[i];
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    private static ProfileParseResult ParseInstagram(string path)
    {
        const string igHelp = "Profil sayfanı aç, adres çubuğundakini yapıştır.";

        if (path.Length == 0)
            return ProfileParseResult.Fail("Adreste kullanıcı adı görünmüyor. " + igHelp);

        var first = path.Split('/')[0];

        if (InstagramNonProfile.Contains(first, StringComparer.OrdinalIgnoreCase))
            return ProfileParseResult.Fail("Bu bir gönderi adresi, profil adresi değil. " + igHelp);

        return ProfileParseResult.Handle(first);
    }

    private static ProfileParseResult ParseTikTok(string host, string path)
    {
        if (host is "vm.tiktok.com" or "vt.tiktok.com")
            return ProfileParseResult.Fail(
                "Bu kısa link. Linki tarayıcıda aç, adres çubuğundaki uzun adresi yapıştır.");

        var first = path.Length == 0 ? "" : path.Split('/')[0];

        if (!first.StartsWith('@'))
            return ProfileParseResult.Fail(
                "Bu bir profil adresi değil. Profil sayfanı aç, adres çubuğundakini yapıştır.");

        var handle = first[1..];
        return handle.Length == 0
            ? ProfileParseResult.Fail("Adreste kullanıcı adı görünmüyor.")
            : ProfileParseResult.Handle(handle);
    }

    private static string DisplayName(string platform) => platform switch
    {
        HandleValidator.YouTube => "YouTube",
        HandleValidator.Instagram => "Instagram",
        HandleValidator.TikTok => "TikTok",
        HandleValidator.Facebook => "Facebook",
        _ => platform
    };
}
