# Kayıt Formu Kimlik Doğrulama — Faz 1 Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kayıt formunda müşteri profil adresini yapıştırabilsin (YouTube/Instagram/TikTok), YouTube kanalı doğrulanmadan ya da müşteri "bu benim kanalım" demeden form geçmesin ve sunucu istemciden gelen `channelId`'ye asla güvenmesin.

**Architecture:** Üç parça. (1) `ProfileUrlParser` — saf, ağa çıkmayan bir statik sınıf; yapıştırılan adresi handle'a ya da `UC…` kanal kimliğine çevirir, çeviremediğinde ne yapılacağını söyleyen Türkçe hata döner. (2) `YouTubeChannelResolver` — bugün `YouTubeVerifyController` içinde gömülü olan `channels.list?forHandle` çağrısı arayüz arkasına çıkarılır; hem controller hem sayfa aynı örneği (ve aynı 1 saatlik cache'i) kullanır, böylece sunucu tarafı yeniden çözüm bedava olur. (3) `IntakeForm` sayfası — parser'ı ve resolver'ı `OnPostSubmitAsync` içinde çağırır; `channelId`'yi **kendisi** üretir, forma gelen gizli alanı tamamen siler. İstemci JS'i sunucu kurallarının aynasıdır (mevcut `RULES` deseninin devamı), yetkili kaynak sunucudur.

**Tech Stack:** ASP.NET Core 10 Razor Pages, xUnit + FluentAssertions, `WebApplicationFactory<Program>` (`ApiFactory`), vanilya JS (derleme adımı yok).

**Spec:** `docs/superpowers/specs/2026-09-02-kayit-formu-kimlik-dogrulama-design.md` (onaylı, commit `3a495a1`)

**Kapsam dışı:** Faz 2 (Google/Facebook ile giriş) bu planda YOK. Google uygulama doğrulaması onaylanınca ayrı plan yazılacak. Facebook alanı Faz 1'de hiç değişmiyor — FB eşleşmesi isim tabanlı, elle girdi doğru veri üretiyor.

---

## File Structure

**Yeni dosyalar:**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Services/IntakeForm/ProfileUrlParser.cs` | Yapıştırılan metni handle / `UC…` kimliği / hata olarak sınıflandırır. Ağa çıkmaz, DI almaz. |
| `OrderDeck.LicenseServer/Services/IntakeForm/IYouTubeChannelResolver.cs` | `ResolveHandleAsync(handle, ct)` arayüzü + `YouTubeChannel` kaydı. Testlerde sahtelenecek tek nokta. |
| `OrderDeck.LicenseServer/Services/IntakeForm/YouTubeChannelResolver.cs` | Gerçek uygulama; controller'dan taşınan `channels.list?forHandle` + `IMemoryCache`. |
| `OrderDeck.LicenseServer.Tests/Services/IntakeForm/ProfileUrlParserTests.cs` | Spec'teki kabul/ret tablolarının tamamı `[Theory]` olarak. |
| `OrderDeck.LicenseServer.Tests/Services/IntakeForm/YouTubeChannelResolverTests.cs` | `ScriptedHandler` ile HTTP sahtesi: bulundu / bulunamadı / API hatası / cache. |
| `OrderDeck.LicenseServer.Tests/TestHelpers/FakeYouTubeChannelResolver.cs` | Sayfa testlerinin resolver'ı yönlendirmesi için. |
| `docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md` | Tek seferlik, salt-okunur ölçüm sorgusu + nasıl çalıştırılacağı. |

**Değişen dosyalar:**

| Dosya | Değişiklik |
|---|---|
| `OrderDeck.LicenseServer/Controllers/YouTubeVerifyController.cs` | İçi boşalır; resolver'ı çağıran ince sarmalayıcıya döner. **JSON tel biçimi aynen korunur** (`available/exists/title/thumbnail/channelId`) — JS bu adları okuyor. |
| `OrderDeck.LicenseServer/Program.cs` | `AddSingleton<IYouTubeChannelResolver, YouTubeChannelResolver>()`. |
| `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs` | Parser + resolver bağlanır; `Input.YouTubeChannelId` **silinir**, `Input.YouTubeConfirmed` eklenir. |
| `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml` | Gizli `ytChannelId` input'u silinir, onay kutusu eklenir, JS'e `parseProfileUrl` + `ytState` kapısı girer. |

`OrderDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs` **değişmiyor** — resolver'ı sahtelemek için ondan türeyen bir fabrika yazılıyor (Task 4).

**Neden ayrı dosyalar:** `ProfileUrlParser` saf fonksiyon — ağı olmayan, tek başına tablo testiyle kapatılabilen bir birim. Resolver ise ağa çıkıyor; ikisini aynı dosyaya koymak saf olanı da sahtelemeye muhtaç hâle getirirdi.

---

### Task 1: ProfileUrlParser — sözleşme ve testler

**Files:**
- Create: `OrderDeck.LicenseServer/Services/IntakeForm/ProfileUrlParser.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/IntakeForm/ProfileUrlParserTests.cs`

Bu görev tamamen saf mantık — ağ yok, DI yok. Mevcut `HandleValidatorTests.cs` dosyası biçim şablonu: `[Theory]`/`[InlineData]`, FluentAssertions, her kuralın **niçin** var olduğunu anlatan Türkçe `<summary>`.

- [ ] **Step 1: Testleri yaz (henüz kod yok)**

`OrderDeck.LicenseServer.Tests/Services/IntakeForm/ProfileUrlParserTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

/// <summary>
/// Müşteri "kullanıcı adı" kutusuna çoğu zaman profil ADRESİNİ yapıştırıyor.
/// HandleValidator bunu bugün reddediyor ("sadece kullanıcı adını yaz"), yani
/// müşteri elle kırpmak zorunda kalıyor ve orada yanlış yazıyor. Parser adresi
/// kabul edip handle'ı kendisi çıkarır; çıkaramadığında ne yapılacağını söyler.
/// </summary>
public sealed class ProfileUrlParserTests
{
    /// <summary>Adres olmayan girdi olduğu gibi geçer — HandleValidator'ın işi bozulmasın.</summary>
    [Theory]
    [InlineData("bilalcanli")]
    [InlineData("@bilalcanli")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Duz_kullanici_adi_degistirilmeden_gecer(string? raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(raw);
    }

    /// <summary>YouTube'un @ biçimi: şema, www./m. öneki, sondaki yol ve sorgu atılır.</summary>
    [Theory]
    [InlineData("https://www.youtube.com/@orderdeck", "orderdeck")]
    [InlineData("http://youtube.com/@orderdeck", "orderdeck")]
    [InlineData("youtube.com/@orderdeck", "orderdeck")]
    [InlineData("www.youtube.com/@orderdeck/", "orderdeck")]
    [InlineData("https://m.youtube.com/@orderdeck/videos", "orderdeck")]
    [InlineData("https://www.youtube.com/@orderdeck?si=abc", "orderdeck")]
    public void YouTube_handle_adresinden_handle_cikar(string raw, string expected)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(expected);
    }

    /// <summary>
    /// /channel/UC… biçimi kanal kimliğini DOĞRUDAN veriyor. API'ye gitmeye gerek yok:
    /// eşleştirmede kullandığımız değer zaten bu. Yanlış yazılmış bir UC… hiçbir
    /// kanala denk gelmez, yani sessizce bir yabancıya bağlanma riski yok.
    /// </summary>
    [Theory]
    [InlineData("https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv")]
    [InlineData("youtube.com/channel/UCabcdefghijklmnopqrstuv/")]
    [InlineData("https://m.youtube.com/channel/UCabcdefghijklmnopqrstuv?si=x")]
    public void YouTube_channel_adresi_kanal_kimligi_olarak_donser(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.YouTubeChannelId);
        r.Value.Should().Be("UCabcdefghijklmnopqrstuv");
    }

    /// <summary>UC + 22 karakter dışındaki her şey kanal kimliği değildir.</summary>
    [Theory]
    [InlineData("https://www.youtube.com/channel/UCkisa")]
    [InlineData("https://www.youtube.com/channel/XXabcdefghijklmnopqrstuv")]
    public void YouTube_bozuk_kanal_kimligi_reddedilir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// /c/ ve /user/ eski biçimler; handle'a çevrilemiyorlar (API'de karşılığı yok).
    /// youtu.be bir VİDEO adresi, kanal değil. Üçünde de yapılacak iş aynı:
    /// müşteriyi kanal sayfasındaki @ adresine yönlendir.
    /// </summary>
    [Theory]
    [InlineData("https://www.youtube.com/c/OrderDeck")]
    [InlineData("https://www.youtube.com/user/OrderDeck")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void YouTube_cozulemeyen_adresler_yonlendirici_hata_verir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().Contain("@");
    }

    /// <summary>Instagram: ?igsh= paylaşım eki ve sondaki eğik çizgi atılır.</summary>
    [Theory]
    [InlineData("https://instagram.com/bilalcanli", "bilalcanli")]
    [InlineData("https://www.instagram.com/bilalcanli/", "bilalcanli")]
    [InlineData("https://instagram.com/bilalcanli?igsh=MWx5", "bilalcanli")]
    [InlineData("instagram.com/bilalcanli", "bilalcanli")]
    public void Instagram_profil_adresinden_handle_cikar(string raw, string expected)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Instagram, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(expected);
    }

    /// <summary>
    /// Gönderi/reel/hikâye adresi profil DEĞİL — içindeki kod kullanıcı adı sanılırsa
    /// kayıt tamamen alakasız bir değere bağlanır. Ret.
    /// </summary>
    [Theory]
    [InlineData("https://instagram.com/p/Cxyz123")]
    [InlineData("https://www.instagram.com/reel/Cxyz123")]
    [InlineData("https://instagram.com/stories/bilalcanli/123456")]
    [InlineData("https://instagram.com/explore/tags/moda")]
    public void Instagram_gonderi_adresi_reddedilir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Instagram, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>TikTok: yol @ ile başlamalı; sondaki /video/… kırpılır.</summary>
    [Theory]
    [InlineData("https://www.tiktok.com/@edanur", "edanur")]
    [InlineData("https://tiktok.com/@edanur/video/7412345678901234567", "edanur")]
    [InlineData("tiktok.com/@edanur?lang=tr", "edanur")]
    public void TikTok_profil_adresinden_handle_cikar(string raw, string expected)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.TikTok, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(expected);
    }

    /// <summary>
    /// vm./vt. kısa linkleri hedefi ancak HTTP isteğiyle açılır. Herkese açık bir
    /// formdan dışarı istek atmıyoruz (SSRF yüzeyi + yavaşlık). Müşteriye linki
    /// tarayıcıda açıp adres çubuğundakini yapıştırmasını söylüyoruz.
    /// </summary>
    [Theory]
    [InlineData("https://vm.tiktok.com/ZMabc123/")]
    [InlineData("https://vt.tiktok.com/ZSabc123/")]
    public void TikTok_kisa_link_cozulmez_ve_yonlendirici_hata_verir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.TikTok, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().Contain("adres çubuğ");
    }

    /// <summary>TikTok'ta @ olmayan yol profil değil (keşfet, etiket, müzik sayfası).</summary>
    [Theory]
    [InlineData("https://www.tiktok.com/tag/moda")]
    [InlineData("https://www.tiktok.com/foryou")]
    public void TikTok_profil_olmayan_adres_reddedilir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.TikTok, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Yanlış kutuya yapıştırma sık: Instagram kutusuna YouTube adresi. Sessizce
    /// handle çıkarsak kayıt yanlış platforma yazılır — hangi kutuya ait olduğunu söyle.
    /// </summary>
    [Theory]
    [InlineData(HandleValidator.Instagram, "https://www.youtube.com/@orderdeck", "YouTube")]
    [InlineData(HandleValidator.YouTube, "https://www.instagram.com/bilalcanli", "Instagram")]
    [InlineData(HandleValidator.TikTok, "https://www.instagram.com/bilalcanli", "Instagram")]
    public void Yanlis_kutuya_yapistirilan_adres_dogru_kutuyu_soyler(
        string platform, string raw, string expectedPlatformName)
    {
        var r = ProfileUrlParser.Parse(platform, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().Contain(expectedPlatformName);
    }

    /// <summary>
    /// Tanımadığımız bir adres parser'a takılmaz; olduğu gibi geçer ve
    /// HandleValidator'ın mevcut "sadece kullanıcı adını yaz" mesajına düşer.
    /// Tek hata mesajı, tek yer.
    /// </summary>
    [Fact]
    public void Bilinmeyen_alan_adi_oldugu_gibi_gecer()
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Instagram, "https://ornek.com/bilal");

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be("https://ornek.com/bilal");
    }

    /// <summary>Facebook parser'a hiç girmiyor: FB eşleşmesi ada dayalı, elle girdi doğru.</summary>
    [Fact]
    public void Facebook_platformu_girdiyi_degistirmez()
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Facebook, "https://facebook.com/bilal.canli");

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be("https://facebook.com/bilal.canli");
    }
}
```

- [ ] **Step 2: Testleri çalıştır, derlenmediğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~ProfileUrlParserTests
```

Beklenen: derleme hatası — `ProfileUrlParser`, `ProfileInputKind` bulunamıyor (CS0246).

- [ ] **Step 3: ProfileUrlParser'ı yaz**

`OrderDeck.LicenseServer/Services/IntakeForm/ProfileUrlParser.cs`:

```csharp
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
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~ProfileUrlParserTests
```

Beklenen: `Passed!` — başarısız yok.

> `HandleValidator.Instagram` gibi sabitlerin `[InlineData]` içinde kullanılabilmesi `const` olmalarına bağlı — doğrulandı (`HandleValidator.cs:23-26`, dördü de `public const string`).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/ProfileUrlParser.cs \
        OrderDeck.LicenseServer.Tests/Services/IntakeForm/ProfileUrlParserTests.cs
git commit -m "$(cat <<'EOF'
feat(kayit-formu): profil adresinden kullanıcı adı çıkaran ayrıştırıcı ekle

Müşteriler kutuya profil adresini yapıştırıyor; elle kırpmaya kalkınca
kullanıcı adını yanlış yazıyorlar. Kırpmayı sunucu yapsın.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: YouTube çözümleyicisini controller'dan çıkar

**Files:**
- Create: `OrderDeck.LicenseServer/Services/IntakeForm/IYouTubeChannelResolver.cs`
- Create: `OrderDeck.LicenseServer/Services/IntakeForm/YouTubeChannelResolver.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/YouTubeVerifyController.cs` (tamamı yeniden yazılır)
- Modify: `OrderDeck.LicenseServer/Program.cs` (~satır 185-188 civarı, intake kayıtlarının yanı)
- Test: `OrderDeck.LicenseServer.Tests/Services/IntakeForm/YouTubeChannelResolverTests.cs`

**Neden:** Sayfa `OnPostSubmitAsync` içinde aynı çağrıyı yapacak. Controller'ı sayfadan HTTP ile çağırmak saçma; mantığı arayüz arkasına alıp ikisine de aynı tekil örneği veriyoruz. `IMemoryCache` paylaşıldığı için istemcinin az önce yaptığı çağrının sonucu sunucu tarafında bedava.

`YouTubeVerifyController`'ın **hiç testi yok** (`grep verify/youtube` test projesinde boş dönüyor), yani bu çıkarma hiçbir testi kırmıyor. Karşılığında controller'ın döndüğü JSON alan adları (`available`, `exists`, `title`, `thumbnail`, `channelId`) `IntakeForm.cshtml` içindeki `verifyYouTube()` tarafından okunuyor — **bu adlar aynen korunmalı.**

- [ ] **Step 1: Arayüzü ve kaydı yaz**

`OrderDeck.LicenseServer/Services/IntakeForm/IYouTubeChannelResolver.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <param name="Available">
/// API'ye ulaşılabildi mi. false ise sonuç hakkında HİÇBİR ŞEY bilmiyoruz
/// (key yok, kota bitti, ağ düştü) — çağıran bunu müşteriyi engellemek için
/// KULLANMAMALI, yoksa bizim arızamız müşteriye fatura edilmiş olur.
/// </param>
/// <param name="Exists">Handle gerçekten bir kanala karşılık geliyor mu.</param>
public sealed record YouTubeChannel(
    bool Available, bool Exists, string? Title, string? Thumbnail, string? ChannelId);

public interface IYouTubeChannelResolver
{
    Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct);
}
```

`OrderDeck.LicenseServer/Services/IntakeForm/YouTubeChannelResolver.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// <c>channels.list?forHandle</c> ile handle → kanal çözümü (1 kota birimi/çağrı;
/// <c>search.list</c> 100 birim olduğu için KULLANILMAZ). Sonuçlar handle bazında
/// 1 saat cache'lenir, böylece istemcinin canlı doğrulaması ile gönderim anındaki
/// sunucu doğrulaması tek çağrıya iner.
///
/// API key <c>YouTube:ApiKey</c> (VPS .env: <c>YouTube__ApiKey</c>). Key yoksa ya da
/// çağrı düşerse <c>Available:false</c> döner — yumuşak degrade.
/// </summary>
public sealed class YouTubeChannelResolver : IYouTubeChannelResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly YouTubeChannel Unavailable = new(false, false, null, null, null);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly string? _apiKey;

    public YouTubeChannelResolver(IHttpClientFactory httpFactory, IMemoryCache cache, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _apiKey = config["YouTube:ApiKey"];
    }

    public async Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Unavailable;

        var h = (handle ?? "").Trim().TrimStart('@').Trim().ToLowerInvariant();
        if (h.Length == 0 || h.Length > 64)
            return new YouTubeChannel(true, false, null, null, null);

        if (_cache.TryGetValue("ytv:" + h, out YouTubeChannel? cached) && cached is not null)
            return cached;

        YouTubeChannel result;
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var url = "https://www.googleapis.com/youtube/v3/channels" +
                      $"?part=id,snippet&forHandle={Uri.EscapeDataString(h)}&key={_apiKey}";
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Unavailable;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            {
                // items[0].id = kanalın channelId'si (UCxxx). WPF'teki chat kaydıyla
                // BİREBİR eşleşen değer bu.
                var channelId = items[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var snippet = items[0].GetProperty("snippet");
                var title = snippet.TryGetProperty("title", out var t) ? t.GetString() : null;
                string? thumb = null;
                if (snippet.TryGetProperty("thumbnails", out var th) &&
                    th.TryGetProperty("default", out var def) &&
                    def.TryGetProperty("url", out var u))
                    thumb = u.GetString();
                result = new YouTubeChannel(true, true, title, thumb, channelId);
            }
            else
            {
                result = new YouTubeChannel(true, false, null, null, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return Unavailable;
        }

        _cache.Set("ytv:" + h, result, CacheTtl);
        return result;
    }
}
```

- [ ] **Step 2: Controller'ı ince sarmalayıcıya indir**

`OrderDeck.LicenseServer/Controllers/YouTubeVerifyController.cs` — dosyanın tamamı:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OrderDeck.LicenseServer.Services.IntakeForm;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Public (anonim) YouTube handle doğrulama — intake formundaki canlı geri bildirim.
/// İşin tamamını <see cref="IYouTubeChannelResolver"/> yapar; burada yalnız IP başına
/// rate-limit ve JSON biçimi var.
///
/// DİKKAT: Bu uç yalnız GÖSTERİM içindir. Kaydedilen channelId'yi sunucu gönderim
/// anında KENDİSİ yeniden çözer (IntakeForm.cshtml.cs) — buradan dönen değere
/// istemci üzerinden güvenilmez.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class YouTubeVerifyController : ControllerBase
{
    private readonly IYouTubeChannelResolver _resolver;

    public YouTubeVerifyController(IYouTubeChannelResolver resolver) => _resolver = resolver;

    public sealed record VerifyResult(bool Available, bool Exists, string? Title, string? Thumbnail, string? ChannelId);

    [HttpGet("api/public/verify/youtube")]
    [EnableRateLimiting("youtube-verify")]
    public async Task<IActionResult> Verify([FromQuery] string? handle, CancellationToken ct)
    {
        var ch = await _resolver.ResolveHandleAsync(handle, ct);
        return Ok(new VerifyResult(ch.Available, ch.Exists, ch.Title, ch.Thumbnail, ch.ChannelId));
    }
}
```

- [ ] **Step 3: DI kaydını ekle**

`OrderDeck.LicenseServer/Program.cs` — `builder.Services.AddSingleton<WhatsAppLinkBuilder>();` satırının hemen altına:

```csharp
// Tekil: bağımlılıkları (IHttpClientFactory/IMemoryCache/IConfiguration) tekil.
// Cache'in paylaşılması önemli — istemcinin canlı doğrulaması ile gönderimdeki
// sunucu doğrulaması aynı 1 saatlik girdiyi kullansın.
builder.Services.AddSingleton<IYouTubeChannelResolver,
    OrderDeck.LicenseServer.Services.IntakeForm.YouTubeChannelResolver>();
```

- [ ] **Step 4: Resolver testlerini yaz**

`OrderDeck.LicenseServer.Tests/Services/IntakeForm/YouTubeChannelResolverTests.cs`:

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class YouTubeChannelResolverTests
{
    // Sabit anahtar YAZMA (repo public, tarayıcı fixture ile gerçeği ayırt edemez).
    private static string NewApiKey() => $"ytkey-{Guid.NewGuid():N}";

    private const string FoundJson = """
    {"items":[{"id":"UCabcdefghijklmnopqrstuv","snippet":{"title":"OrderDeck",
    "thumbnails":{"default":{"url":"https://yt3.example/a.jpg"}}}}]}
    """;

    private const string EmptyJson = """{"items":[]}""";

    private static YouTubeChannelResolver Build(ScriptedHandler handler, string? apiKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["YouTube:ApiKey"] = apiKey })
            .Build();
        return new YouTubeChannelResolver(
            new SingleHandlerFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            config);
    }

    [Fact]
    public async Task Kanal_bulununca_kimlik_ve_baslik_doner()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        var r = await sut.ResolveHandleAsync("@OrderDeck", CancellationToken.None);

        r.Available.Should().BeTrue();
        r.Exists.Should().BeTrue();
        r.ChannelId.Should().Be("UCabcdefghijklmnopqrstuv");
        r.Title.Should().Be("OrderDeck");
        r.Thumbnail.Should().Be("https://yt3.example/a.jpg");
        // Handle @ atılıp küçük harfe indirilerek sorgulanır.
        handler.Requests[0].RequestUri!.Query.Should().Contain("forHandle=orderdeck");
    }

    [Fact]
    public async Task Kanal_yoksa_Exists_false_ama_Available_true()
    {
        var sut = Build(new ScriptedHandler((HttpStatusCode.OK, EmptyJson)), NewApiKey());

        var r = await sut.ResolveHandleAsync("yokboylekanal", CancellationToken.None);

        r.Available.Should().BeTrue();
        r.Exists.Should().BeFalse();
        r.ChannelId.Should().BeNull();
    }

    /// <summary>
    /// Kota/ağ arızasında Available:false. Bu ayrım kritik: çağıran taraf
    /// "bulunamadı" ile "bakamadık"ı karıştırırsa bizim arızamız müşteriyi kilitler.
    /// </summary>
    [Fact]
    public async Task Api_hatasinda_Available_false_doner()
    {
        var sut = Build(new ScriptedHandler((HttpStatusCode.Forbidden, "quota")), NewApiKey());

        var r = await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);

        r.Available.Should().BeFalse();
        r.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Api_anahtari_yoksa_cagri_yapilmaz()
    {
        var handler = new ScriptedHandler();
        var sut = Build(handler, apiKey: null);

        var r = await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);

        r.Available.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Cache olmadan her gönderim ikinci bir kota birimi harcardı; istemci zaten
    /// aynı handle'ı az önce sormuş oluyor.
    /// </summary>
    [Fact]
    public async Task Ikinci_cagri_cache_ten_gelir()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);
        var r = await sut.ResolveHandleAsync("@OrderDeck", CancellationToken.None);

        r.ChannelId.Should().Be("UCabcdefghijklmnopqrstuv");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Bos_handle_cagri_yapmadan_bulunamadi_doner()
    {
        var handler = new ScriptedHandler();
        var sut = Build(handler, NewApiKey());

        var r = await sut.ResolveHandleAsync("  ", CancellationToken.None);

        r.Available.Should().BeTrue();
        r.Exists.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    private sealed class SingleHandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedHandler(params (HttpStatusCode, string)[] script)
            => _script = new Queue<(HttpStatusCode, string)>(script);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (status, body) = _script.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
```

- [ ] **Step 5: Çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~YouTubeChannelResolverTests
```

Beklenen: `Passed!`

- [ ] **Step 6: Tüm sunucu takımını çalıştır (regresyon yok mu)**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: `Passed!` — controller çıkarması hiçbir testi etkilememeli (uç zaten testsizdi).

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/IYouTubeChannelResolver.cs \
        OrderDeck.LicenseServer/Services/IntakeForm/YouTubeChannelResolver.cs \
        OrderDeck.LicenseServer/Controllers/YouTubeVerifyController.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/IntakeForm/YouTubeChannelResolverTests.cs
git commit -m "$(cat <<'EOF'
refactor(kayit-formu): YouTube kanal çözümünü servise çıkar

Gönderim anında sunucunun da aynı çözümü yapması gerekiyor; controller'ı
HTTP ile çağırmak yerine mantık arayüz arkasına alındı. Cache paylaşıldığı
için ikinci çözüm ek kota harcamıyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Parser'ı gönderim akışına bağla

**Files:**
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs:132-150`
- Test: `OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormPageTests.cs` (mevcut dosyaya ekleme)

Bu görevde YouTube kimlik doğrulaması **yok** — yalnız adres → handle çevrimi. YouTube adresi yapıştırılırsa da çalışır ama kanal varlığı sorulmaz (Task 4).

- [ ] **Step 1: Testleri yaz**

`IntakeFormPageTests.cs` dosyasının sonuna, son `}` işaretinden önce ekle:

```csharp
    /// <summary>
    /// Sahadaki en sık hata: müşteri profil adresini yapıştırıyor. Eskiden
    /// HandleValidator bunu reddediyordu; artık sunucu kullanıcı adını kendisi
    /// çıkarıyor ve DB'ye temiz handle düşüyor.
    /// </summary>
    [Fact]
    public async Task Post_submit_instagram_profil_adresini_kullanici_adina_cevirir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "https://www.instagram.com/bilalcanli/?igsh=MWx5",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.InstagramUsername.Should().Be("bilalcanli");
    }

    [Fact]
    public async Task Post_submit_tiktok_video_adresini_kullanici_adina_cevirir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.TikTokUsername"] = "https://www.tiktok.com/@edanur/video/7412345678901234567",
            ["Input.FullName"] = "Eda Nur",
            ["Input.Email"] = "eda@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.TikTokUsername.Should().Be("edanur");
    }

    /// <summary>
    /// Çözülemeyen adres SESSİZCE geçmemeli. Gönderi adresindeki kod kullanıcı adı
    /// sanılırsa kayıt tamamen alakasız bir değere bağlanır.
    /// </summary>
    [Fact]
    public async Task Post_submit_instagram_gonderi_adresi_hata_ile_doner()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "https://www.instagram.com/p/Cxyz123",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await postResp.Content.ReadAsStringAsync());
        html.Should().Contain("gönderi adresi");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.IntakeFormSubmissions.CountAsync(s => s.Config.CustomerId == customerId);
        count.Should().Be(0);
    }
```

- [ ] **Step 2: Çalıştır, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IntakeFormPageTests
```

Beklenen: yeni üç test FAIL. Adres yapıştırma testleri `HandleValidator`'ın "sadece kullanıcı adını yaz" hatası yüzünden 200 dönüyor (Redirect beklenirken); gönderi adresi testi 200 dönüyor ama "gönderi adresi" metnini içermiyor.

- [ ] **Step 3: Sayfayı bağla**

`IntakeForm.cshtml.cs` dosyasında 132-150 arası bloğu bununla değiştir:

```csharp
        // Girdi profil ADRESİ olabilir — kullanıcı adını sunucu çıkarır.
        // Sahada müşteriler adresi yapıştırıp elle kırpmaya çalışıyor ve orada
        // yanlış yazıyorlar; kırpmayı biz yapıyoruz. Facebook parser'a girmiyor:
        // FB eşleşmesi görünen ada dayalı, elle girdi doğru veri üretiyor.
        var ytParsed = ProfileUrlParser.Parse(HandleValidator.YouTube, Input.YouTubeUsername);
        var igParsed = ProfileUrlParser.Parse(HandleValidator.Instagram, Input.InstagramUsername);
        var ttParsed = ProfileUrlParser.Parse(HandleValidator.TikTok, Input.TikTokUsername);

        AddParseError("Input.YouTubeUsername", ytParsed);
        AddParseError("Input.InstagramUsername", igParsed);
        AddParseError("Input.TikTokUsername", ttParsed);

        // youtube.com/channel/UC… kanal kimliğini doğrudan verir; handle yok.
        var channelIdFromUrl = ytParsed.Kind == ProfileInputKind.YouTubeChannelId ? ytParsed.Value : null;

        // Kullanıcı adları: baştaki @ + dış boşluk temizlenir, sonra her
        // platformun kendi kurallarına göre doğrulanır. Kurala uymayan kayıt
        // sohbetteki kişiyle eşleşemeyeceği için kabul edilmez.
        var yt = HandleValidator.Normalize(HandleOf(ytParsed));
        var ig = HandleValidator.Normalize(HandleOf(igParsed));
        var fb = HandleValidator.Normalize(Input.FacebookUsername);
        var tt = HandleValidator.Normalize(HandleOf(ttParsed));

        // Temizlenmiş hâli forma geri yaz — hata varsa kullanıcı düzelteceği
        // metni görsün, geçerliyse gönderilen değerle kaydedilen aynı olsun.
        // Adres çözülemediyse (Error) yazdığı metin dursun ki neyi düzelteceğini görsün.
        if (ytParsed.Kind != ProfileInputKind.Error) Input.YouTubeUsername = yt ?? channelIdFromUrl;
        if (igParsed.Kind != ProfileInputKind.Error) Input.InstagramUsername = ig;
        Input.FacebookUsername = fb;
        if (ttParsed.Kind != ProfileInputKind.Error) Input.TikTokUsername = tt;

        AddHandleError("Input.YouTubeUsername", HandleValidator.YouTube, yt);
        AddHandleError("Input.InstagramUsername", HandleValidator.Instagram, ig);
        AddHandleError("Input.FacebookUsername", HandleValidator.Facebook, fb);
        AddHandleError("Input.TikTokUsername", HandleValidator.TikTok, tt);
```

Dosyanın sonuna, `AddHandleError` metodunun yanına iki yardımcı ekle:

```csharp
    private void AddParseError(string key, ProfileParseResult parsed)
    {
        if (parsed.Kind == ProfileInputKind.Error)
            ModelState.AddModelError(key, parsed.Error!);
    }

    /// <summary>Yalnız Handle sonucu handle'dır; kanal kimliği ve hata değildir.</summary>
    private static string? HandleOf(ProfileParseResult parsed)
        => parsed.Kind == ProfileInputKind.Handle ? parsed.Value : null;
```

- [ ] **Step 4: "En az bir platform" kontrolünü ve legacyUsername'i kanal kimliğini görecek hâle getir**

`youtube.com/channel/UC…` yapıştırıldığında `yt` boş kalır. Bu iki yeri düzeltmezsek müşteri geçerli bir kanal adresi verdiği hâlde "en az bir platform girin" hatası alır.

`if (yt is null && ig is null && fb is null && tt is null)` satırını değiştir:

```csharp
        if (yt is null && channelIdFromUrl is null && ig is null && fb is null && tt is null)
            ModelState.AddModelError("Input.InstagramUsername",
                "En az bir platform kullanıcı adı girin (Instagram, YouTube, Facebook veya TikTok).");
```

`var legacyUsername = yt ?? ig ?? fb ?? tt ?? "";` satırını değiştir:

```csharp
        var legacyUsername = yt ?? ig ?? fb ?? tt ?? channelIdFromUrl ?? "";
```

- [ ] **Step 5: Alan uzunluk sınırını yükselt — YOKSA ADRES YAPIŞTIRMA HİÇ ÇALIŞMAZ**

`IntakeFormInput` alanlarında `[StringLength(64)]` var ve doğrulama model binding'de, yani parser çalışmadan ÖNCE koşuyor. `https://www.tiktok.com/@birazuzunkullaniciadi/video/7412345678901234567` 64'ü aşar ve müşteri "En fazla 64 karakter" hatası alır — tam da çözmeye çalıştığımız şey.

`Input.YouTubeUsername`, `Input.InstagramUsername`, `Input.TikTokUsername` üçünde `[StringLength(64, ...)]` yerine:

```csharp
        // 200: yapıştırılan profil ADRESİ de bu alandan geçiyor (uzunluk kontrolü
        // model binding'de, parser'dan ÖNCE koşuyor). Kullanıcı adının kendi
        // sınırını HandleValidator uyguluyor — 64 karakter kuralı orada duruyor.
        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
```

`Input.FacebookUsername` **64'te kalır** — parser'a girmiyor, adres kabul etmiyor.

Doğrulayan test — `IntakeFormPageTests.cs` sonuna ekle:

```csharp
    /// <summary>
    /// Alan sınırı 64'te kalsaydı uzun profil adresleri model binding'de,
    /// yani parser daha çalışmadan reddedilirdi.
    /// </summary>
    [Fact]
    public async Task Post_submit_uzun_profil_adresi_uzunluk_hatasina_takilmaz()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var longUrl = "https://www.tiktok.com/@birazuzunkullaniciadiburada/video/7412345678901234567?lang=tr";
        longUrl.Length.Should().BeGreaterThan(64);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.TikTokUsername"] = longUrl,
            ["Input.FullName"] = "Eda Nur",
            ["Input.Email"] = "eda@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub!.TikTokUsername.Should().Be("birazuzunkullaniciadiburada");
    }
```

- [ ] **Step 6: Çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IntakeFormPageTests
```

Beklenen: `Passed!` — mevcut testler dahil hepsi geçiyor.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs \
        OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormPageTests.cs
git commit -m "$(cat <<'EOF'
feat(kayit-formu): yapıştırılan profil adresini sunucuda kullanıcı adına çevir

Çözülemeyen adres sessizce geçmiyor; müşteriye ne yapacağı söyleniyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: YouTube kimlik doğrulaması — kanal onayı ve sunucunun kendi çözümü

**Files:**
- Create: `OrderDeck.LicenseServer.Tests/TestHelpers/FakeYouTubeChannelResolver.cs`
- Create: `OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormYouTubeIdentityTests.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs` (ctor, `IntakeFormInput`, `OnPostSubmitAsync`)

**Bu görevin özü tek bir değişmezdir:** sunucu istemciden gelen `channelId`'ye güvenmez, handle'ı kendisi çözer ve kendi bulduğu değeri kaydeder. Bu olmadan onay kutusu süstür — JS'i atlayan her istek istediği kimliği yazdırır.

| Durum | Davranış |
|---|---|
| Kanal bulundu | Kanal kartı gösterilir, **"Bu benim kanalım"** onayı zorunlu; onaysız gönderim engellenir |
| Kanal bulunamadı | Gönderim **engellenir** ("kanal sayfanı aç, @ ile başlayan adresi yapıştır") |
| API'ye ulaşılamadı (`Available:false`) | **Engellenmez** — bizim kota/ağ arızamız müşteriye fatura edilmez |
| `channel/UC…` yapıştırıldı | Varlık sorulmaz, onay kutusu çıkmaz — kimlik zaten adresin kendisi |

- [ ] **Step 1: Test sahtesini yaz**

`OrderDeck.LicenseServer.Tests/TestHelpers/FakeYouTubeChannelResolver.cs`:

```csharp
using OrderDeck.LicenseServer.Services.IntakeForm;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Sayfa testlerinde YouTube API'sinin yerine geçer. Handle bazında senaryo
/// tanımlanır; tanımsız handle "bulunamadı" sayılır.
/// </summary>
public sealed class FakeYouTubeChannelResolver : IYouTubeChannelResolver
{
    public Dictionary<string, YouTubeChannel> ByHandle { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>true ise her çağrı Available:false döner (kota/ağ arızası benzetimi).</summary>
    public bool ForceUnavailable { get; set; }

    public List<string> Calls { get; } = [];

    public Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct)
    {
        var h = (handle ?? "").Trim().TrimStart('@').Trim();
        Calls.Add(h);

        if (ForceUnavailable)
            return Task.FromResult(new YouTubeChannel(false, false, null, null, null));

        return Task.FromResult(ByHandle.TryGetValue(h, out var ch)
            ? ch
            : new YouTubeChannel(true, false, null, null, null));
    }
}
```

- [ ] **Step 2: Testleri yaz**

`OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormYouTubeIdentityTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// Gerçek YouTube API'sinin yerine sahteyi koyar. ApiFactory'de servis geçersiz
/// kılma için hazır bir kanca YOK (yalnız ExtraConfig / ConfigureDatabase var),
/// bu yüzden ConfigureWebHost genişletiliyor. ConfigureTestServices uygulamanın
/// kayıtlarından SONRA koştuğu için tekil kayıt güvenle değiştirilebiliyor.
/// ApiFactory'nin kendisine dokunulmuyor — 40+ test dosyası ona bağlı.
/// </summary>
public sealed class YouTubeIdentityFactory : ApiFactory
{
    public FakeYouTubeChannelResolver Resolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IYouTubeChannelResolver>();
            services.AddSingleton<IYouTubeChannelResolver>(Resolver);
        });
    }
}

public sealed class IntakeFormYouTubeIdentityTests : IClassFixture<YouTubeIdentityFactory>
{
    private const string RealChannelId = "UCabcdefghijklmnopqrstuv";

    private readonly YouTubeIdentityFactory _factory;
    public IntakeFormYouTubeIdentityTests(YouTubeIdentityFactory factory) => _factory = factory;

    private async Task<(string slug, Guid customerId)> SeedConfigAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"yti-{Guid.NewGuid():N}@x",
            Name = "Yti",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-YTI-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"y-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = "+905551234567",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (slug, customer.Id);
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> TokenAsync(HttpClient client, string slug)
        => AdminLoginHelper.ExtractAntiForgeryToken(
            await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync());

    private static FormUrlEncodedContent Form(string token, string slug, params (string Key, string Value)[] extra)
    {
        var d = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Slug"] = slug,
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        };
        foreach (var (k, v) in extra) d[k] = v;
        return new FormUrlEncodedContent(d);
    }

    private async Task<IntakeFormSubmission?> LatestAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .OrderByDescending(s => s.SubmittedAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// PLANIN EN ÖNEMLİ TESTİ. İstemci uydurma bir channelId gönderiyor; sunucu
    /// onu YOK SAYIP handle'ı kendisi çözmeli. Bu olmadan onay kutusu süs:
    /// JS'i atlayan her istek kaydı istediği kimliğe bağlar.
    /// </summary>
    [Fact]
    public async Task Sunucu_istemciden_gelen_channelId_ye_guvenmez()
    {
        _factory.Resolver.ByHandle["orderdeck"] =
            new YouTubeChannel(true, true, "OrderDeck", null, RealChannelId);

        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "orderdeck"),
            ("Input.YouTubeConfirmed", "true"),
            ("Input.YouTubeChannelId", "UCzzzzzzzzzzzzzzzzzzzzzz")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var sub = await LatestAsync(customerId);
        sub.Should().NotBeNull();
        sub!.YouTubeChannelId.Should().Be(RealChannelId);
    }

    /// <summary>
    /// "test1234" yerine "test" yazan müşteri sorunu: "test" GERÇEK bir yabancının
    /// kanalı, yani doğrulama yeşil ✓ verir ve kayıt yabancıya bağlanır. Onay
    /// kutusu tam bu yüzden zorunlu — kartta gördüğü ad kendisine ait değilse
    /// onaylamaz ve hatayı yakalar.
    /// </summary>
    [Fact]
    public async Task Onay_kutusu_isaretlenmeden_gonderim_engellenir()
    {
        _factory.Resolver.ByHandle["yabanci"] =
            new YouTubeChannel(true, true, "Yabancı Kanal", null, RealChannelId);

        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "yabanci")));

        // 200 = Page() döndü, yani ModelState geçersiz. Hata METNİNİ burada
        // aramıyoruz: Input.YouTubeConfirmed için doğrulama alanı Task 5'te
        // ekleniyor, mesajın ekranda göründüğü orada elle doğrulanıyor.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LatestAsync(customerId)).Should().BeNull();
    }

    [Fact]
    public async Task Kanal_bulunamazsa_gonderim_engellenir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "boylebirkanalyok"),
            ("Input.YouTubeConfirmed", "true")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        html.Should().Contain("kanalı bulunamadı");

        (await LatestAsync(customerId)).Should().BeNull();
    }

    /// <summary>
    /// Kota bitmesi/ağ arızası BİZİM sorunumuz. Müşteriyi kilitlemek yerine kayıt
    /// alınır; channelId boş kalır, eşleştirme handle üzerinden yürür (bugünkü hâl).
    /// </summary>
    [Fact]
    public async Task Api_ulasilamazsa_gonderim_engellenmez()
    {
        _factory.Resolver.ForceUnavailable = true;
        try
        {
            var (slug, customerId) = await SeedConfigAsync();
            var client = NewClient();
            var token = await TokenAsync(client, slug);

            var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
                ("Input.YouTubeUsername", "orderdeck")));

            resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

            var sub = await LatestAsync(customerId);
            sub.Should().NotBeNull();
            sub!.YouTubeUsername.Should().Be("orderdeck");
            sub.YouTubeChannelId.Should().BeNull();
        }
        finally
        {
            _factory.Resolver.ForceUnavailable = false;
        }
    }

    /// <summary>
    /// channel/UC… adresi kimliğin KENDİSİ; API'ye gitmeye ve onay istemeye gerek yok.
    /// Yanlış yazılmış bir UC… hiçbir kanala denk gelmez, sessizce yabancıya bağlanamaz.
    /// </summary>
    [Fact]
    public async Task Kanal_adresi_yapistirilinca_api_ye_gidilmez()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);
        var callsBefore = _factory.Resolver.Calls.Count;

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", $"https://www.youtube.com/channel/{RealChannelId}")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        _factory.Resolver.Calls.Count.Should().Be(callsBefore);

        var sub = await LatestAsync(customerId);
        sub.Should().NotBeNull();
        sub!.YouTubeChannelId.Should().Be(RealChannelId);
    }

    /// <summary>
    /// YouTube kutusu boşken hiçbir doğrulama tetiklenmemeli — Instagram'la kayıt
    /// olan müşteri YouTube yüzünden engellenemez.
    /// </summary>
    [Fact]
    public async Task Youtube_bos_ise_dogrulama_calismaz()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);
        var callsBefore = _factory.Resolver.Calls.Count;

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.InstagramUsername", "bilalcanli")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        _factory.Resolver.Calls.Count.Should().Be(callsBefore);
        (await LatestAsync(customerId)).Should().NotBeNull();
    }
}
```

> `ApiFactory`, yeni bir rate-limit politikası adını `[EnableRateLimiting]` niteliklerini yansıtarak kendiliğinden testlerde devre dışı bırakıyor — bu görevde yeni politika eklenmediği için ek bir iş yok.

- [ ] **Step 3: Çalıştır, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IntakeFormYouTubeIdentityTests
```

Beklenen: derleme hatası — `Input.YouTubeConfirmed` yok. Derleme geçerse testler FAIL (onaysız gönderim hâlâ Redirect dönüyor).

- [ ] **Step 4: Sayfa modelini değiştir**

**4a.** Ctor'a resolver'ı ekle. Mevcut alanların yanına `private readonly IYouTubeChannelResolver _youTube;`, ctor imzasına `IYouTubeChannelResolver youTube` parametresi, gövdeye `_youTube = youTube;`.

**4b.** `IntakeFormInput` içindeki gizli alanı **sil**:

```csharp
        // JS doğrulaması başarılıysa doldurulan gizli alan (channels.list'ten).
        [StringLength(48)]
        public string? YouTubeChannelId { get; set; }
```

yerine:

```csharp
        /// <summary>
        /// "Bu benim kanalım" onayı. Kanal bulunduğunda ZORUNLU.
        ///
        /// channelId burada YOK ve bilerek yok: istemciden gelen kimliğe
        /// güvenilmiyor, sunucu handle'ı kendisi çözüyor. Alanı postalamaya
        /// devam etmek "bu değer kullanılıyor" izlenimi verirdi.
        /// </summary>
        public bool YouTubeConfirmed { get; set; }
```

**4c.** Sayfa modeline (`Config` özelliğinin yanına) kanal kartını yeniden çizmek için iki salt-görünüm özelliği ekle:

```csharp
    // Hatalı gönderimden sonra kanal kartını tekrar çizmek için. Kalıcı değil.
    public string? YouTubeChannelTitle { get; private set; }
    public string? YouTubeChannelThumbnail { get; private set; }
```

**4d.** `if (!ModelState.IsValid) return Page();` satırının **hemen üstüne** kimlik bloğunu ekle (böylece müşteri tüm hataları tek turda görür):

```csharp
        // YouTube kimliği: sunucu handle'ı KENDİSİ çözer. İstemciden channelId
        // kabul edilmiyor — JS'i atlayan bir istek kaydı istediği kimliğe bağlardı.
        // Kanal bulunduğunda onay zorunlu: "test1234" yerine "test" yazan müşteri
        // için doğrulama yeşil ✓ verir (test gerçek bir yabancının kanalı) ve kayıt
        // yabancıya bağlanır; kartta gördüğü adı onaylatmak bunu yakalayan tek şey.
        string? resolvedChannelId = channelIdFromUrl;
        if (channelIdFromUrl is null && yt is not null
            && HandleValidator.Validate(HandleValidator.YouTube, yt) is null)
        {
            var ch = await _youTube.ResolveHandleAsync(yt, ct);
            YouTubeChannelTitle = ch.Title;
            YouTubeChannelThumbnail = ch.Thumbnail;

            if (!ch.Available)
            {
                // Kota/ağ arızası bizim sorunumuz; müşteriyi kilitlemiyoruz.
                resolvedChannelId = null;
            }
            else if (!ch.Exists)
            {
                ModelState.AddModelError("Input.YouTubeUsername",
                    "Bu kullanıcı adına ait bir YouTube kanalı bulunamadı. Kanal sayfanı aç, "
                    + "adres çubuğundaki @ ile başlayan adresi yapıştır.");
            }
            else if (!Input.YouTubeConfirmed)
            {
                ModelState.AddModelError("Input.YouTubeConfirmed",
                    $"\"{ch.Title}\" kanalının sana ait olduğunu onayla.");
            }
            else
            {
                resolvedChannelId = ch.ChannelId;
            }
        }

        if (!ModelState.IsValid) return Page();
```

**4e.** `SaveSubmissionAsync` çağrısında `youTubeChannelId: Trim(Input.YouTubeChannelId)` satırını `youTubeChannelId: resolvedChannelId,` ile değiştir.

- [ ] **Step 5: Çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IntakeForm
```

Beklenen: `Passed!` — hem yeni kimlik testleri hem mevcut `IntakeFormPageTests`.

- [ ] **Step 6: Tüm takım**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: `Passed!`

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/FakeYouTubeChannelResolver.cs \
        OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormYouTubeIdentityTests.cs
git commit -m "$(cat <<'EOF'
feat(kayit-formu): YouTube kanalını sunucu kendisi çözsün, müşteri onaylasın

İstemciden gelen channelId artık yok sayılıyor; gizli alan tamamen kalktı.
Kanal bulunduğunda "bu benim kanalım" onayı zorunlu — doğrulama yeşil ✓
verirken kaydın bir yabancıya bağlanmasını yakalayan tek şey bu.
API'ye ulaşılamadığında engelleme yok: arızamızı müşteriye fatura etmiyoruz.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Formun kendisi — adres kabulü, kanal kartı ve zorunlu onay

**Files:**
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml`

Sunucu artık tek yetkili. Bu görev istemciyi **onun aynası** yapıyor: aynı ayrıştırma, aynı kapı. Amaç müşteriye geri bildirimi anında vermek; JS kapalıysa da sunucu aynı kararı veriyor (Task 3-4 testleri bunu kanıtlıyor).

JS'in derleme adımı yok, bu dosya elle test edilir. Bu yüzden değişiklikler küçük ve tek yönlü: mevcut `RULES`/`handleError` yapısına dokunma, **önüne** bir ayrıştırma adımı ekle.

- [ ] **Step 1: YouTube alanının işaretlemesini değiştir (satır 138-148)**

Mevcut blok:

```html
            <div class="field">
                <label class="lbl" asp-for="Input.YouTubeUsername"><span class="platform-ico">📺</span>YouTube</label>
                <div class="with-prefix">
                    <span class="pfx">@@</span>
                    <input class="inp uname" id="ytUser" asp-for="Input.YouTubeUsername" maxlength="64"
                           inputmode="text" placeholder="kanalhandle" />
                </div>
                <div class="yt-status" id="ytStatus"></div>
                <input type="hidden" asp-for="Input.YouTubeChannelId" id="ytChannelId" />
                <span class="err" asp-validation-for="Input.YouTubeUsername"></span>
            </div>
```

yerine:

```html
            <div class="field">
                <label class="lbl" asp-for="Input.YouTubeUsername"><span class="platform-ico">📺</span>YouTube</label>
                <div class="with-prefix">
                    <span class="pfx">@@</span>
                    <input class="inp uname" id="ytUser" asp-for="Input.YouTubeUsername" maxlength="200"
                           inputmode="text" placeholder="kanalhandle veya kanal adresi" />
                </div>
                <div class="yt-status" id="ytStatus">
                    @if (!string.IsNullOrEmpty(Model.YouTubeChannelTitle))
                    {
                        @* Hatalı gönderimden sonra kart tekrar çizilsin; JS de aynısını üretiyor. *@
                        @if (!string.IsNullOrEmpty(Model.YouTubeChannelThumbnail))
                        {
                            <img src="@Model.YouTubeChannelThumbnail" alt="" />
                        }
                        <span class="yt-ok">✓ @Model.YouTubeChannelTitle</span>
                    }
                </div>
                <label class="yt-confirm" id="ytConfirmWrap"
                       hidden="@(string.IsNullOrEmpty(Model.YouTubeChannelTitle))">
                    <input type="checkbox" id="ytConfirm" asp-for="Input.YouTubeConfirmed" />
                    <span>Bu kanal bana ait.</span>
                </label>
                <span class="err" asp-validation-for="Input.YouTubeConfirmed"></span>
                <span class="err" asp-validation-for="Input.YouTubeUsername"></span>
            </div>
```

Instagram (satır 132) ve TikTok (satır 160) input'larında `maxlength="64"` → `maxlength="200"`, placeholder `"kullaniciadi veya profil adresi"`. Facebook input'u **hiç değişmiyor**.

`hidden="@(...)"` Razor'da boolean nitelik olarak doğru davranır: `false` ise nitelik hiç basılmaz.

- [ ] **Step 2: `.yt-confirm` stilini ekle**

`<style>` bloğunda `.yt-status` kurallarının hemen ardına:

```css
        .yt-confirm { display: flex; align-items: center; gap: 8px; margin-top: 8px;
                      font-size: 14px; cursor: pointer; }
        .yt-confirm input { width: 18px; height: 18px; flex: none; cursor: pointer; }
```

- [ ] **Step 3: İstemci ayrıştırıcısını ekle (sunucunun aynası)**

`normalizeHandle` fonksiyonunun (satır ~286-291) hemen ardına ekle:

```js
    // Sunucudaki ProfileUrlParser'ın aynası. Yetkili kaynak SUNUCU — burası
    // yalnızca anında geri bildirim için. İkisi ayrışırsa sunucu kazanır ve
    // müşteri gönderim sonrası hatayı görür; sessiz kabul yok.
    var IG_NONPROFILE = ['p', 'reel', 'reels', 'stories', 'tv', 'explore'];

    function isChannelId(id) { return /^UC[A-Za-z0-9_-]{22}$/.test(id); }

    // Döner: { kind: 'handle'|'channelId'|'error', value: string, error: string }
    function parseProfileUrl(platform, raw) {
        var s = (raw || '').trim();
        if (s.length === 0 || s.indexOf('/') < 0 || platform === 'FacebookUsername')
            return { kind: 'handle', value: s, error: '' };

        var rest = s;
        var sch = rest.indexOf('://');
        if (sch >= 0) rest = rest.slice(sch + 3);
        var cut = rest.search(/[?#]/);
        if (cut >= 0) rest = rest.slice(0, cut);
        rest = rest.replace(/\/+$/, '');

        var sl = rest.indexOf('/');
        var host = (sl < 0 ? rest : rest.slice(0, sl)).toLowerCase();
        var path = sl < 0 ? '' : rest.slice(sl + 1);
        host = host.replace(/^www\./, '').replace(/^m\./, '');

        var hostPlatform =
            (host === 'youtube.com' || host === 'youtu.be') ? 'YouTubeUsername' :
            (host === 'instagram.com') ? 'InstagramUsername' :
            (host === 'tiktok.com' || host === 'vm.tiktok.com' || host === 'vt.tiktok.com')
                ? 'TikTokUsername' : null;

        // Tanımadığımız adres: mevcut "sadece kullanıcı adını yaz" mesajına düşsün.
        if (!hostPlatform) return { kind: 'handle', value: s, error: '' };

        if (hostPlatform !== platform)
            return { kind: 'error', value: '',
                     error: 'Bu bir ' + RULES[hostPlatform].name + ' adresi. '
                          + RULES[platform].name + ' kutusuna ' + RULES[platform].name
                          + ' kullan\u0131c\u0131 ad\u0131n\u0131 yaz.' };

        var seg = path.length === 0 ? [] : path.split('/');

        if (hostPlatform === 'YouTubeUsername') {
            var ytHelp = 'Kanal sayfan\u0131 a\u00e7, adres \u00e7ubu\u011fundaki '
                       + AT + ' ile ba\u015flayan adresi yap\u0131\u015ft\u0131r.';
            if (host === 'youtu.be' || seg.length === 0)
                return { kind: 'error', value: '',
                         error: 'Bu bir video adresi, kanal adresi de\u011fil. ' + ytHelp };
            if (seg[0].charAt(0) === AT) {
                var h = seg[0].slice(1);
                return h.length === 0
                    ? { kind: 'error', value: '', error: 'Adreste kanal ad\u0131 g\u00f6r\u00fcnm\u00fcyor. ' + ytHelp }
                    : { kind: 'handle', value: h, error: '' };
            }
            if (seg[0] === 'channel') {
                var id = seg[1] || '';
                return isChannelId(id)
                    ? { kind: 'channelId', value: id, error: '' }
                    : { kind: 'error', value: '',
                        error: 'Kanal adresi eksik ya da bozuk g\u00f6r\u00fcn\u00fcyor. ' + ytHelp };
            }
            return { kind: 'error', value: '', error: 'Bu adresten kanal\u0131 bulam\u0131yoruz. ' + ytHelp };
        }

        if (hostPlatform === 'InstagramUsername') {
            var igHelp = 'Profil sayfan\u0131 a\u00e7, adres \u00e7ubu\u011fundakini yap\u0131\u015ft\u0131r.';
            if (seg.length === 0)
                return { kind: 'error', value: '',
                         error: 'Adreste kullan\u0131c\u0131 ad\u0131 g\u00f6r\u00fcnm\u00fcyor. ' + igHelp };
            if (IG_NONPROFILE.indexOf(seg[0].toLowerCase()) >= 0)
                return { kind: 'error', value: '',
                         error: 'Bu bir g\u00f6nderi adresi, profil adresi de\u011fil. ' + igHelp };
            return { kind: 'handle', value: seg[0], error: '' };
        }

        if (host === 'vm.tiktok.com' || host === 'vt.tiktok.com')
            return { kind: 'error', value: '',
                     error: 'Bu k\u0131sa link. Linki taray\u0131c\u0131da a\u00e7, adres '
                          + '\u00e7ubu\u011fundaki uzun adresi yap\u0131\u015ft\u0131r.' };
        if (seg.length === 0 || seg[0].charAt(0) !== AT)
            return { kind: 'error', value: '',
                     error: 'Bu bir profil adresi de\u011fil. Profil sayfan\u0131 a\u00e7, '
                          + 'adres \u00e7ubu\u011fundakini yap\u0131\u015ft\u0131r.' };
        var tth = seg[0].slice(1);
        return tth.length === 0
            ? { kind: 'error', value: '', error: 'Adreste kullan\u0131c\u0131 ad\u0131 g\u00f6r\u00fcnm\u00fcyor.' }
            : { kind: 'handle', value: tth, error: '' };
    }

    function platformOf(el) {
        return (el.getAttribute('name') || '').split('.').pop();
    }

    // Alanı yerinde ayrıştırır; parse sonucunu döner.
    function applyParse(el) {
        var p = parseProfileUrl(platformOf(el), el.value);
        if (p.kind === 'handle') el.value = normalizeHandle(p.value);
        else if (p.kind === 'channelId') el.value = p.value;
        return p;
    }
```

`RULES`'a `FacebookUsername` girdisi **eklenmiyor** (eşleşmeyi kırar). `parseProfileUrl` yanlış-kutu mesajında `RULES[platform].name` okuyor ama Facebook alanı ilk satırda erken dönüyor ve hiçbir adres Facebook'a yönlenmiyor — o okuma hiç gerçekleşmez.

- [ ] **Step 4: Blur işleyicisini ayrıştırmadan sonra doğrulasın (satır 344-350)**

```js
    document.querySelectorAll('.uname').forEach(function (el) {
        el.addEventListener('blur', function () {
            var p = applyParse(el);
            if (p.kind === 'error') { showError(el, p.error); return; }
            if (p.kind === 'channelId') { showError(el, ''); return; } // handle kuralı geçerli değil
            showError(el, handleError(el, el.value));
        });
        el.addEventListener('input', function () { showError(el, ''); });
    });
```

- [ ] **Step 5: YouTube doğrulamasını durum makinesine çevir (satır 352-389'un tamamı)**

```js
    // (b) YouTube kimliği — kanal kartı + zorunlu onay.
    // channelId ARTIK POSTALANMIYOR: sunucu handle'ı kendisi çözüyor. Buradaki
    // doğrulama yalnız müşteriye "hangi kanala bağlanıyorsun" diye göstermek için.
    var yt = document.getElementById('ytUser');
    var ytStatus = document.getElementById('ytStatus');
    var ytConfirmWrap = document.getElementById('ytConfirmWrap');
    var ytConfirm = document.getElementById('ytConfirm');
    var ytTimer = null, lastChecked = null;
    // '' | 'checking' | 'ok' | 'missing' | 'unavailable' | 'urlid' | 'parse-error'
    var ytState = '';

    function setStatus(html) { ytStatus.innerHTML = html; }
    function esc(s) { return (s || '').replace(/[&<>"]/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

    function showConfirm(on) {
        if (!ytConfirmWrap) return;
        ytConfirmWrap.hidden = !on;
        if (!on && ytConfirm) ytConfirm.checked = false;
    }

    function verifyYouTube() {
        if (!yt) return;
        var p = applyParse(yt);

        if (p.kind === 'error') {
            ytState = 'parse-error'; lastChecked = null;
            setStatus(''); showConfirm(false); showError(yt, p.error);
            return;
        }
        if (p.kind === 'channelId') {
            // Kanal adresi kimliğin kendisi — varlık sorgusu ve onay gereksiz.
            ytState = 'urlid'; lastChecked = null;
            setStatus('<span class="yt-ok">\u2713 Kanal adresi al\u0131nd\u0131</span>');
            showConfirm(false); showError(yt, '');
            return;
        }

        var h = yt.value;
        if (h.length === 0) { ytState = ''; lastChecked = null; setStatus(''); showConfirm(false); return; }
        if (h === lastChecked) return;

        showConfirm(false);      // handle değişti → önceki onay geçersiz
        lastChecked = h;
        ytState = 'checking';
        setStatus('<span class="spinner"></span><span class="yt-checking">kontrol ediliyor\u2026</span>');
        fetch('/api/public/verify/youtube?handle=' + encodeURIComponent(h))
            .then(function (r) { return r.json(); })
            .then(function (d) {
                if (yt.value !== h) return;              // kullanıcı arada değiştirdi
                if (!d || d.available === false) {
                    // Bizim kota/ağ arızamız müşteriyi engellemesin.
                    ytState = 'unavailable'; setStatus(''); return;
                }
                if (d.exists) {
                    ytState = 'ok';
                    var img = d.thumbnail ? '<img src="' + esc(d.thumbnail) + '" alt="">' : '';
                    setStatus(img + '<span class="yt-ok">\u2713 ' + esc(d.title || 'Kanal bulundu') + '</span>');
                    showConfirm(true);
                } else {
                    ytState = 'missing';
                    setStatus('<span class="yt-warn">\u26a0 Bu kullan\u0131c\u0131 ad\u0131na ait kanal bulunamad\u0131</span>');
                    showConfirm(false);
                }
            })
            .catch(function () { ytState = 'unavailable'; setStatus(''); });
    }

    if (yt) {
        yt.addEventListener('blur', function () { clearTimeout(ytTimer); ytTimer = setTimeout(verifyYouTube, 200); });
        yt.addEventListener('input', function () {
            showConfirm(false);                          // yazarken eski onay geçersiz
            clearTimeout(ytTimer); ytTimer = setTimeout(verifyYouTube, 700);
        });
        // Sunucu hatasıyla dönen sayfada alan dolu gelir; durum makinesini ve
        // onay kutusunu yeniden kur (sunucu kartı zaten çizdi).
        if (yt.value) verifyYouTube();
    }
```

- [ ] **Step 6: Gönderim kapısına YouTube durumunu ekle**

`form.addEventListener('submit', ...)` içindeki alan döngüsünde şu üç satır:

```js
            el.value = normalizeHandle(el.value);
            if (el.value.length > 0) anyUser = true;
            var msg = handleError(el, el.value);
```

yerine:

```js
            var p = applyParse(el);
            if (el.value.length > 0) anyUser = true;
            var msg = p.kind === 'error' ? p.error
                    : p.kind === 'channelId' ? ''
                    : handleError(el, el.value);
```

Ardından `if (firstBad) { ... }` bloğunun **hemen üstüne**:

```js
        // YouTube kimlik kapısı. Sunucu aynı kararı veriyor (IntakeForm.cshtml.cs);
        // buradaki amaç müşteriyi sunucuya gidip dönmeden uyarmak.
        if (yt && yt.value && ytState !== 'urlid' && ytState !== 'unavailable') {
            if (ytState === 'missing') {
                e.preventDefault();
                showError(yt, 'Bu kullan\u0131c\u0131 ad\u0131na ait bir YouTube kanal\u0131 '
                            + 'bulunamad\u0131. Kanal sayfan\u0131 a\u00e7, adres \u00e7ubu\u011fundaki '
                            + AT + ' ile ba\u015flayan adresi yap\u0131\u015ft\u0131r.');
                formErr.textContent = 'YouTube kullan\u0131c\u0131 ad\u0131n\u0131 d\u00fczelt.';
                yt.focus();
                return;
            }
            if (ytState === 'ok' && ytConfirm && !ytConfirm.checked) {
                e.preventDefault();
                formErr.textContent = 'YouTube kanal\u0131n\u0131n sana ait oldu\u011funu onayla.';
                ytConfirm.focus();
                return;
            }
        }
```

`ytState === 'checking'` bilerek engellenmiyor: müşteriyi ağ gecikmesi yüzünden bekletmiyoruz, sunucu aynı kontrolü gönderimde zaten yapıyor.

- [ ] **Step 7: Kalan `ytChannelId` izlerini temizle**

```bash
grep -n "ytChannelId\|YouTubeChannelId" \
  OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml \
  OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs
```

Beklenen: `.cshtml.cs` içinde yalnız `youTubeChannelId: resolvedChannelId` (servis parametresi). `.cshtml` içinde **hiç eşleşme olmamalı**.

- [ ] **Step 8: Testleri çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: `Passed!`

- [ ] **Step 9: Formu elle dene**

```bash
dotnet run --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Yerelde intake config'i olan bir slug ile `/musteri-kayit/{slug}` açıp doğrula (tarayıcı konsolunda hata olmamalı):

1. Instagram kutusuna `https://www.instagram.com/bilalcanli/?igsh=MWx5` yapıştır → odak çıkınca alan `bilalcanli` olur.
2. Instagram kutusuna `https://www.instagram.com/p/Cxyz` → "gönderi adresi" hatası.
3. YouTube kutusuna gerçek bir handle yaz → kanal kartı + **"Bu kanal bana ait."** kutusu belirir; işaretlemeden "Tamamla" → gönderim engellenir.
4. Handle'ı değiştir → onay kutusu kaybolur, işaret sıfırlanır.
5. YouTube kutusuna gerçek bir `https://www.youtube.com/channel/UC…` → "Kanal adresi alındı", onay kutusu **çıkmaz**, gönderim geçer.
6. YouTube kutusu boş, yalnız Instagram ile gönder → geçer.

- [ ] **Step 10: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml
git commit -m "$(cat <<'EOF'
feat(kayit-formu): forma adres yapıştırma ve zorunlu kanal onayı ekle

İstemci sunucudaki ayrıştırıcının aynası; gizli channelId alanı kalktı.
Kanal bulunduğunda onay kutusu zorunlu, handle değişince sıfırlanıyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Eşleşmeyen eski kayıtların ölçümü

**Files:**
- Create: `docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md`

Spec'te bu iş **yalnız ölçüm** olarak kabul edildi: eski kayıtları toplu düzeltmiyoruz (hangi handle'ın doğrusu ne, bilmiyoruz), sorunun boyutunu öğreniyoruz. Sorgular salt-okunur ve DB'nin **kopyası** üzerinde çalıştırılır.

- [ ] **Step 1: Ölçüm belgesini yaz**

`docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md`:

````markdown
# Eşleşmeyen kayıt ölçümü — 2026-09-02

Kayıt formundan gelen kullanıcı adlarının ne kadarı sohbetteki kişiyle hiç
eşleşmemiş? Faz 1 öncesi taban ölçüm.

**Bu bir DÜZELTME değil.** Eski kayıtlarda doğru handle'ın ne olduğunu bilmiyoruz;
toplu düzeltme yanlış tahminleri veriye yazardı. Yalnız sayıyoruz.

## Nasıl çalıştırılır

Yayıncı PC'sinde, uygulama KAPALIYKEN, veritabanının **kopyası** üzerinde:

```bash
cp ~/Documents/OrderDeck/orderdeck.db /tmp/olcum.db
sqlite3 /tmp/olcum.db
```

Kopya üzerinde çalışmanın sebebi: canlı dosyaya açılan okuma bile WAL kilidi
tutabiliyor ve uygulama yeniden açıldığında yazma hatası veriyor.

## Sorgular

### 1. Hiç etkileşime girmemiş kayıtlar

Forma kaydolmuş ama sohbette bir kez bile görünmemiş kişiler. Kullanıcı adı
yanlışsa beklenen iz tam olarak bu: kayıt var, hareket yok.

```sql
SELECT Platform,
       COUNT(*) AS Toplam,
       SUM(CASE WHEN TotalLabelsPrinted = 0
                 AND TotalAmount = 0
                 AND LastSeenAt = FirstSeenAt THEN 1 ELSE 0 END) AS HicHareketYok
FROM Customer
GROUP BY Platform
ORDER BY Toplam DESC;
```

`HicHareketYok / Toplam` oranı, platform bazında sorunun büyüklüğü.

### 2. YouTube: handle satırı var, channelId satırı ayrı

YouTube sohbet satırları `Username = channelId`, `DisplayName = @handle` olarak
düşüyor. Formdan `@handle` girilip kaydedilen satır channelId'li satırla
birleşemediyse aynı kişi iki kayıt olarak duruyor.

```sql
SELECT COUNT(*) AS AyriKalanHandleSatiri
FROM Customer AS f
WHERE f.Platform = 'youtube'
  AND f.Username NOT LIKE 'UC%'
  AND NOT EXISTS (
      SELECT 1 FROM Customer AS c
      WHERE c.Platform = 'youtube'
        AND c.Username LIKE 'UC%'
        AND LTRIM(c.DisplayName, '@') = LTRIM(f.Username, '@') COLLATE NOCASE
  );
```

### 3. Şüpheli kısa/uzun handle çiftleri

"test1234 yerine test" hatasının izi: aynı platformda bir kaydın kullanıcı adı,
başka bir kaydın kullanıcı adının ön eki.

```sql
SELECT a.Platform, a.Username AS Kisa, b.Username AS Uzun
FROM Customer AS a
JOIN Customer AS b
  ON b.Platform = a.Platform
 AND b.Username <> a.Username
 AND b.Username LIKE a.Username || '%' COLLATE NOCASE
WHERE LENGTH(a.Username) >= 4
ORDER BY a.Platform, a.Username;
```

Bu liste **kanıt değil, ipucu**: `ayse` ve `ayse_moda` gerçekten iki ayrı kişi
olabilir. Elle bakılır.

## Sonuç

| Tarih | Platform | Toplam | Hareketsiz | Oran |
|---|---|---|---|---|
| | | | | |

Ölçüm alındığında tablo doldurulur; Faz 1 yayına girdikten bir süre sonra aynı
sorgular tekrar çalıştırılıp karşılaştırılır.
````

- [ ] **Step 2: Commit**

```bash
git add docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md
git commit -m "$(cat <<'EOF'
docs(kayit-formu): eşleşmeyen kayıtlar için taban ölçüm sorgularını yaz

Toplu düzeltme yok — doğru handle'ı bilmiyoruz. Yalnız sorunun boyutu
ölçülüyor ki Faz 1'in etkisi karşılaştırılabilsin.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Bitirme

- [ ] **Tüm sunucu takımını çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: `Passed!`

- [ ] **PR aç** — dal `feat/kayit-formu-kimlik`, hedef `master`

Başlık: `feat(kayit-formu): profil adresi kabulü ve YouTube kanal onayı`

Gövdede mutlaka geçmesi gerekenler:
- Sunucu istemciden gelen `channelId`'ye artık güvenmiyor; gizli alan kaldırıldı.
- Kanal bulunduğunda onay zorunlu; bulunamazsa gönderim engelli; API'ye
  ulaşılamazsa engel **yok**.
- Faz 2 (Google/Facebook ile giriş) bu PR'da yok; Google uygulama doğrulaması bekleniyor.

**Merge kullanıcıya ait.** `master`'a merge otomatik prod deploy'u tetikliyor.

- [ ] **Merge sonrası uçtan doğrulama**

Prod formunda: Instagram adresi yapıştır → handle'a dönüyor mu; gerçek bir YouTube
handle'ı yaz → kart ve onay kutusu çıkıyor mu; onaysız gönderim engelleniyor mu.
Ardından DB'de yeni kaydın `YouTubeChannelId` sütunu dolu mu — kod doğru görünmesi
kanıt değil, satıra bak.
