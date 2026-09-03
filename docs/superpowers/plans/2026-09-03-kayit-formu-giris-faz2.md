# Kayıt Formu Faz 2 — Google/Facebook ile Giriş Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kayıt formuna "Google ile YouTube kanalını bağla" ve "Facebook ile bağlan" akışı eklemek — kimlik sağlayıcıdan gelsin, elle yazım hatası sınıfı tamamen kapansın.

**Architecture:** Tam sayfa OAuth yönlendirmesi (pop-up yok). `GET /musteri-kayit/{slug}/baglan/{platform}` başlatır, sabit `GET /musteri-kayit/baglanti-donusu` döner. State tek kullanımlık (10 dk, IMemoryCache), tarayıcıya `od.link` çereziyle bağlı. Token takası sunucuda; token SAKLANMAZ, yalnız kimlik (ad + handle + channelId) çereze bağlı 30 dk'lık kayda yazılır. POST'ta sunucu kimliği KENDİ kaydından okur — istemciden gelen değere asla güvenmez (Faz 1 kuralının aynısı). YouTube butonu `youtube.readonly` kapsamının Google onayına kilitli: kod şimdi yazılır, `IntakeLogin__YouTubeEnabled` bayrağı kapalı deploy edilir. Facebook (`public_profile`, review istemez) hemen açılabilir.

**Tech Stack:** ASP.NET Core 10 Razor Pages + MVC controller, IMemoryCache, typed HttpClient, xUnit + FluentAssertions + `ApiFactory` (InMemory EF).

**Branch:** `feat/kayit-formu-giris` (master'dan aç — mevcut `feat/kayit-formu-kimlik` merge edildi).

---

## Karar kaydı (neden böyle)

- **`youtube.readonly` + yeniden doğrulama (B yolu), onaylı geniş `youtube` kapsamı değil.** Onay ekranında "kanalınızı görüntüleyin" vs "kanalınızı yönetin" — elle girdi ileride kalkacağı için giriş tek yol olacak; korkutucu ifade kaydın kendisini kaçırır. Google "minimum scope" politikası geniş kapsamı ileride WPF'in iznini de riske atar. readonly token sızarsa okunur, yönetim token'ı sızarsa değiştirilir.
- **Masaüstü `FacebookOAuthController`/`FacebookOAuthExchanger`'a DOKUNULMAZ** — o akış `[Authorize(Bearer-Customer)]` + long-lived token + sabit RedirectUri; form akışı anonim + kısa ömürlü + farklı dönüş adresi. Ayrı küçük istemci yazılır.
- **Sırlar (client_secret) HTTP isteğinin GÖVDESİNDE, asla URL'de** — `AddHttpClient` varsayılan logger'ı giden URI'yi Information'da yazıyor (YouTubeChannelResolver ve FacebookOAuthExchanger'daki mevcut kural).
- **`od.link` çerezi `Path=/`** — form hem `/musteri-kayit/{slug}` hem eski `/r/{slug}` route'undan açılıyor; dar path (`/musteri-kayit`) eski route'ta kimliği görünmez yapar. Çerez içeriği yalnız rastgele nonce, PII yok.
- **Dönüş banner'ı sabit kod → sabit metin** (`ok|iptal|kanalyok|saglayici`); query'den serbest metin ekrana basılmaz (XSS).
- **Taslak koruması sessionStorage** (spec "çerez" diyordu — bilinçli sapma): ad/adres/telefon içeren taslağın her istekle sunucuya gitmesine gerek yok, yeri tarayıcı.
- **`/musteri-kayit/baglanti-donusu` route'u sayfa route'u `/musteri-kayit/{slug}` ile çakışmaz:** literal segment parametreyi yener (ASP.NET Core route precedence). Yine de test çiviliyor (Task 6).
- **Unlink POST'u `intake-form-submit` bütçesinden yer** (politika sınıf düzeyinde, tüm POST'ları sayıyor). Kabul: gerçek müşteri 1-2 kez kaldırır, limit env'den yükseltilebilir (`ORDERDECK_INTAKE_RATELIMIT_PER_HOUR`).
- **Rate limit testlerde otomatik kapalı:** `ApiFactory` `[EnableRateLimiting]` özniteliklerini derlemeden tarayıp her politikaya no-limiter kaydediyor — yeni `intake-link` politikası kendiliğinden kapsanır, elle bir şey gerekmez.

## Dosya haritası

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Services/IntakeForm/Login/IntakeLoginOptions.cs` | YENİ — config (client id/secret, redirect, bayraklar) |
| `OrderDeck.LicenseServer/Services/IntakeForm/Login/IntakeLinkStore.cs` | YENİ — state + bağlı kimlik saklama (IMemoryCache) |
| `OrderDeck.LicenseServer/Services/IntakeForm/Login/GoogleChannelClient.cs` | YENİ — code→token→`channels?mine=true` |
| `OrderDeck.LicenseServer/Services/IntakeForm/Login/FacebookNameClient.cs` | YENİ — code→token→`/me?fields=id,name` |
| `OrderDeck.LicenseServer/Controllers/IntakeLinkController.cs` | YENİ — başlat + dönüş uçları |
| `OrderDeck.LicenseServer/Program.cs` | DI + `intake-link` rate limit politikası |
| `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs` | bağlı kimlik okuma, POST override, unlink |
| `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml` | chip/buton/banner + JS (taslak, submitter guard) |
| `OrderDeck.LicenseServer.Tests/Services/IntakeForm/IntakeLinkStoreTests.cs` | YENİ |
| `OrderDeck.LicenseServer.Tests/Services/IntakeForm/GoogleChannelClientTests.cs` | YENİ |
| `OrderDeck.LicenseServer.Tests/Services/IntakeForm/FacebookNameClientTests.cs` | YENİ |
| `OrderDeck.LicenseServer.Tests/TestHelpers/FakeIntakeLoginClients.cs` | YENİ — iki fake istemci |
| `OrderDeck.LicenseServer.Tests/Controllers/IntakeLinkEndpointTests.cs` | YENİ — başlat/dönüş + form entegrasyon testleri |
| `docs/kayit-formu-giris.md` | YENİ — ops + Google başvuru gerekçe metni |

---

### Task 1: IntakeLoginOptions + DI + rate limit politikası

**Files:**
- Create: `OrderDeck.LicenseServer/Services/IntakeForm/Login/IntakeLoginOptions.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI ~satır 248 sonrası, rate limit ~satır 516 sonrası)

- [ ] **Step 1: Options sınıfını yaz**

```csharp
namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

/// <summary>
/// Kayıt formundaki "hesabını bağla" girişlerinin yapılandırması.
/// VPS .env: <c>IntakeLogin__GoogleClientId</c>, <c>IntakeLogin__GoogleClientSecret</c>,
/// <c>IntakeLogin__YouTubeEnabled</c>, <c>IntakeLogin__FacebookEnabled</c>.
///
/// YouTube bayrağı Google'ın <c>youtube.readonly</c> kapsam onayına kilitli:
/// kod karanlıkta yatar, onay gelince bayrak açılır — deploy gerekmez, restart yeter.
/// Facebook app'i (masaüstüyle aynı, <c>OrderDeck:Facebook</c>) <c>public_profile</c>
/// için review istemez; o bayrak hemen açılabilir.
/// </summary>
public sealed class IntakeLoginOptions
{
    public const string SectionName = "IntakeLogin";

    /// <summary>Google OAuth istemcisi — WPF'in kullandığı Cloud projesinde
    /// AYRI bir "Web application" client oluşturulur (masaüstü client'ı
    /// redirect URI kabul etmez).</summary>
    public string? GoogleClientId { get; set; }

    /// <summary>Yalnız sunucuda; log'a ve istemciye asla çıkmaz.</summary>
    public string? GoogleClientSecret { get; set; }

    /// <summary>İki sağlayıcı için ortak dönüş adresi. Google Cloud Console'da
    /// "Authorized redirect URIs"e, Meta app'inde "Valid OAuth Redirect URIs"e
    /// BİREBİR bu değer yazılmalı.</summary>
    public string RedirectUri { get; set; } = "https://orderdeckapp.com/musteri-kayit/baglanti-donusu";

    public bool YouTubeEnabled { get; set; }
    public bool FacebookEnabled { get; set; }

    /// <summary>Bayrak açık AMA kimlik bilgisi eksikse buton yine gizli kalır —
    /// yarım yapılandırma müşteriye kırık link olarak yansımasın.</summary>
    public bool YouTubeLoginReady =>
        YouTubeEnabled
        && !string.IsNullOrWhiteSpace(GoogleClientId)
        && !string.IsNullOrWhiteSpace(GoogleClientSecret);
}
```

- [ ] **Step 2: Program.cs — DI kaydı**

Satır ~248'deki Facebook `AddHttpClient` bloğunun hemen ALTINA:

```csharp
// Kayıt formu "hesabını bağla" girişleri (Faz 2). Store singleton: state ve
// bağlı kimlik süreç içi IMemoryCache'te yaşıyor — YouTubeChannelResolver'la
// aynı gerekçe, tek konteyner var, dağıtık cache gerekmez.
builder.Services.Configure<OrderDeck.LicenseServer.Services.IntakeForm.Login.IntakeLoginOptions>(
    builder.Configuration.GetSection(
        OrderDeck.LicenseServer.Services.IntakeForm.Login.IntakeLoginOptions.SectionName));
builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.IntakeForm.Login.IntakeLinkStore>();
builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.IntakeForm.Login.IGoogleChannelClient,
        OrderDeck.LicenseServer.Services.IntakeForm.Login.GoogleChannelClient>(
        c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.IntakeForm.Login.IFacebookNameClient,
        OrderDeck.LicenseServer.Services.IntakeForm.Login.FacebookNameClient>(
        c => c.Timeout = TimeSpan.FromSeconds(15));
```

**NOT:** Bu adım Task 2-4'teki tipler yazılana kadar derlenmez. Task 1'de yalnız `Configure<IntakeLoginOptions>` satırını ekle; `AddSingleton`/`AddHttpClient` satırlarını ilgili tip geldiği task'ın commit'ine dahil et (her commit derlenir kuralı).

- [ ] **Step 3: Program.cs — rate limit politikası**

`youtube-verify` politikasının (satır ~516) hemen ALTINA:

```csharp
// Kayıt formu OAuth bağlama uçları (başlat + dönüş) — anonim ve state
// üretiyorlar; sınırsız bırakmak cache'i state ile doldurtur. Gerçek müşteri
// bir kayıtta 1-2 kez bağlanır; 10/10dk bol bol yeter.
opt.AddPolicy("intake-link", ctx =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(10)
        }));
```

- [ ] **Step 4: Derle**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: başarılı (options sınıfı + Configure + politika tek başına derlenir).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/Login/IntakeLoginOptions.cs OrderDeck.LicenseServer/Program.cs
git commit -m "feat(kayit-formu): giris yapilandirmasi ve baglama hiz siniri"
```
(Tüm commit'lere `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>` ekle.)

---

### Task 2: IntakeLinkStore (TDD)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/IntakeForm/Login/IntakeLinkStore.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/IntakeForm/IntakeLinkStoreTests.cs`

- [ ] **Step 1: Failing test'i yaz**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class IntakeLinkStoreTests
{
    private static IntakeLinkStore NewStore() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void State_kaydedilir_ve_bir_kez_okunur()
    {
        var store = NewStore();
        var state = new IntakeLinkState("nonce1", "slug1", "youtube", "/musteri-kayit/slug1");

        var token = store.SaveState(state);

        token.Should().MatchRegex("^[0-9a-f]{64}$", "tahmin edilemez, URL-güvenli olmalı");
        store.ConsumeState(token).Should().Be(state);
        // TEK KULLANIMLIK: aynı state ile dönüş ucu iki kez çağrılırsa
        // (tarayıcı geri tuşu, tekrar oynatılan istek) ikincisi reddedilmeli.
        store.ConsumeState(token).Should().BeNull();
    }

    [Fact]
    public void Bilinmeyen_state_null_doner()
    {
        NewStore().ConsumeState("yok-boyle-bir-token").Should().BeNull();
    }

    [Fact]
    public void Kimlik_platforma_gore_ayri_saklanir()
    {
        var store = NewStore();
        var yt = new IntakeLinkedIdentity("Kanal Adı", "@kanal", "UCabc");
        var fb = new IntakeLinkedIdentity("Musa Sevinç", null, null);

        store.SaveIdentity("nonce1", "youtube", yt);
        store.SaveIdentity("nonce1", "facebook", fb);

        store.GetIdentity("nonce1", "youtube").Should().Be(yt);
        store.GetIdentity("nonce1", "facebook").Should().Be(fb);
        // Başka tarayıcının nonce'u başkasının kimliğini GÖREMEZ.
        store.GetIdentity("nonce2", "youtube").Should().BeNull();
    }

    [Fact]
    public void Kimlik_silinebilir()
    {
        var store = NewStore();
        store.SaveIdentity("n", "youtube", new IntakeLinkedIdentity("K", null, "UC1"));

        store.RemoveIdentity("n", "youtube");

        store.GetIdentity("n", "youtube").Should().BeNull();
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter IntakeLinkStoreTests`
Expected: derleme hatası (`IntakeLinkStore` yok).

- [ ] **Step 3: Implementasyonu yaz**

```csharp
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

/// <summary>OAuth state kaydı: dönüşte hangi tarayıcıya (CookieNonce), hangi
/// forma (Slug) ve hangi platforma ait olduğunu söyler.</summary>
public sealed record IntakeLinkState(string CookieNonce, string Slug, string Platform, string ReturnPath);

/// <summary>Sağlayıcıdan alınan kimlik. Token BURADA YOK — bilerek: takas
/// sonrası tek kullanımlık, saklamak yalnız sızma yüzeyi açar.</summary>
public sealed record IntakeLinkedIdentity(string DisplayName, string? Handle, string? ChannelId);

/// <summary>
/// İki kısa ömürlü kayıt türü, tek süreç içi depo:
///
///   "ils:{token}"          → state, 10 dk, TEK kullanımlık (Consume siler).
///   "ili:{nonce}:{platform}" → bağlı kimlik, 30 dk (form doldurma süresi).
///
/// Önekler AYRI kalmalı — YouTubeChannelResolver'daki "ytv:"/"ytid:" dersi:
/// tek anahtar uzayında bir tür diğerinin cevabı yerine geçebilir.
/// </summary>
public sealed class IntakeLinkStore
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdentityTtl = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;
    public IntakeLinkStore(IMemoryCache cache) => _cache = cache;

    /// <summary>32 bayt CSPRNG → 64 hex karakter. Hem state token'ı hem çerez
    /// nonce'u bunu kullanır.</summary>
    public static string RandomToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public string SaveState(IntakeLinkState state)
    {
        var token = RandomToken();
        _cache.Set("ils:" + token, state, StateTtl);
        return token;
    }

    public IntakeLinkState? ConsumeState(string token)
    {
        var key = "ils:" + token;
        if (!_cache.TryGetValue(key, out IntakeLinkState? state) || state is null)
            return null;
        _cache.Remove(key); // tek kullanımlık — tekrar oynatma burada ölür
        return state;
    }

    public void SaveIdentity(string cookieNonce, string platform, IntakeLinkedIdentity identity)
        => _cache.Set($"ili:{cookieNonce}:{platform}", identity, IdentityTtl);

    public IntakeLinkedIdentity? GetIdentity(string cookieNonce, string platform)
        => _cache.TryGetValue($"ili:{cookieNonce}:{platform}", out IntakeLinkedIdentity? id) ? id : null;

    public void RemoveIdentity(string cookieNonce, string platform)
        => _cache.Remove($"ili:{cookieNonce}:{platform}");
}
```

- [ ] **Step 4: Testleri geçir**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter IntakeLinkStoreTests`
Expected: 4 PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/Login/IntakeLinkStore.cs OrderDeck.LicenseServer.Tests/Services/IntakeForm/IntakeLinkStoreTests.cs
git commit -m "feat(kayit-formu): baglama state ve kimlik deposu"
```

---

### Task 3: GoogleChannelClient (TDD)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/IntakeForm/Login/GoogleChannelClient.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/IntakeForm/GoogleChannelClientTests.cs`

- [ ] **Step 1: Failing test'i yaz**

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class GoogleChannelClientTests
{
    // Repo public: kimlik bilgisi ASLA sabit yazılmaz, üretilir.
    private readonly string _clientId = $"cid-{Guid.NewGuid():N}";
    private readonly string _clientSecret = $"cs-{Guid.NewGuid():N}";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, string? AuthHeader)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return Respond(request);
        }
    }

    private GoogleChannelClient NewClient(StubHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new IntakeLoginOptions
        {
            GoogleClientId = _clientId,
            GoogleClientSecret = _clientSecret,
            RedirectUri = "https://orderdeckapp.com/musteri-kayit/baglanti-donusu"
        }),
        NullLogger<GoogleChannelClient>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task Basarili_akista_kanal_kimligi_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json("""{"access_token":"tok-abc"}""")
                : Json("""
                    {"items":[{"id":"UCkanal000000000000000ab",
                      "snippet":{"title":"Kanalım","customUrl":"@kanalim"}}]}
                    """)
        };

        var result = await NewClient(handler).FetchChannelAsync("code-1", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Identity.Should().Be(
            new IntakeLinkedIdentity("Kanalım", "@kanalim", "UCkanal000000000000000ab"));

        // SIR HİJYENİ: secret gövdede taşınmalı, URI'de asla — AddHttpClient'ın
        // varsayılan logger'ı giden URI'yi Information seviyesinde yazıyor.
        var tokenReq = handler.Requests[0];
        tokenReq.Uri.ToString().Should().NotContain(_clientSecret);
        tokenReq.Body.Should().Contain(_clientSecret).And.Contain("code-1")
            .And.Contain("grant_type=authorization_code");

        // Kanal çağrısı Bearer başlıkla gitmeli; token URI'ye sızmamalı.
        var chReq = handler.Requests[1];
        chReq.Uri.ToString().Should().Contain("mine=true").And.NotContain("tok-abc");
        chReq.AuthHeader.Should().Be("Bearer tok-abc");
    }

    [Fact]
    public async Task Hesapta_kanal_yoksa_kanalyok_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json("""{"access_token":"tok-abc"}""")
                : Json("""{"items":[]}""")
        };

        var result = await NewClient(handler).FetchChannelAsync("code-1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("kanalyok");
    }

    [Fact]
    public async Task Token_takasi_duserse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler(); // her şey 500
        var result = await NewClient(handler).FetchChannelAsync("code-1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("saglayici");
        handler.Requests.Should().HaveCount(1, "takas düştüyse kanal çağrısı hiç yapılmamalı");
    }

    [Fact]
    public async Task Kanal_cagrisi_duserse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json("""{"access_token":"tok-abc"}""")
                : new HttpResponseMessage(HttpStatusCode.Forbidden)
        };

        var result = await NewClient(handler).FetchChannelAsync("code-1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("saglayici");
    }
}
```

- [ ] **Step 2: Çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter GoogleChannelClientTests`
Expected: derleme hatası (tipler yok).

- [ ] **Step 3: Implementasyonu yaz**

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

/// <summary>Bağlama akışının sonucu. <c>ErrorCode</c> dönüş URL'ine query
/// olarak yazılır — SABİT kod kümesi: "kanalyok" | "saglayici". Serbest metin
/// buraya asla girmez; ekrandaki karşılığını IntakeForm.cshtml.cs çevirir.</summary>
public sealed record IntakeLoginResult(bool Ok, string? ErrorCode, IntakeLinkedIdentity? Identity);

public interface IGoogleChannelClient
{
    Task<IntakeLoginResult> FetchChannelAsync(string code, CancellationToken ct);
}

/// <summary>
/// Authorization code'u access token'a çevirir, <c>channels?mine=true</c> ile
/// GİRİŞ YAPAN hesabın kanalını okur. Token değişkende yaşar ve metot bitince
/// atılır — hiçbir yere yazılmaz: ihtiyaç tek seferlik, saklamak yalnız risk.
///
/// Kapsam <c>youtube.readonly</c> — kanalı OKUMAYA yeter, yönetmeye yetmez.
/// Sırlar (client_secret, access token) gövde/başlıkta taşınır, URI'de değil:
/// AddHttpClient'ın günlükleyicisi URI'yi Information'da yazıyor.
/// </summary>
public sealed class GoogleChannelClient : IGoogleChannelClient
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string ChannelUrl =
        "https://www.googleapis.com/youtube/v3/channels?part=id,snippet&mine=true";

    private readonly HttpClient _http;
    private readonly IntakeLoginOptions _options;
    private readonly ILogger<GoogleChannelClient> _log;

    public GoogleChannelClient(
        HttpClient http, IOptions<IntakeLoginOptions> options, ILogger<GoogleChannelClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<IntakeLoginResult> FetchChannelAsync(string code, CancellationToken ct)
    {
        try
        {
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = _options.GoogleClientId ?? "",
                    ["client_secret"] = _options.GoogleClientSecret ?? "",
                    ["redirect_uri"] = _options.RedirectUri,
                    ["grant_type"] = "authorization_code"
                })
            };
            using var tokenResp = await _http.SendAsync(tokenReq, ct).ConfigureAwait(false);
            if (!tokenResp.IsSuccessStatusCode)
            {
                // Gövde loglanmaz: hata gövdesi bizim koddan değil Google'dan
                // gelir ve code parametresini yankılayabilir.
                _log.LogWarning("Google token takası başarısız — HTTP {Status}", (int)tokenResp.StatusCode);
                return new(false, "saglayici", null);
            }

            string? accessToken;
            await using (var s = await tokenResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false))
                accessToken = doc.RootElement.TryGetProperty("access_token", out var at)
                    ? at.GetString() : null;

            if (string.IsNullOrEmpty(accessToken))
            {
                _log.LogWarning("Google token yanıtında access_token yok");
                return new(false, "saglayici", null);
            }

            using var chReq = new HttpRequestMessage(HttpMethod.Get, ChannelUrl);
            chReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var chResp = await _http.SendAsync(chReq, ct).ConfigureAwait(false);
            if (!chResp.IsSuccessStatusCode)
            {
                _log.LogWarning("YouTube mine=true çağrısı başarısız — HTTP {Status}", (int)chResp.StatusCode);
                return new(false, "saglayici", null);
            }

            await using var cs = await chResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var cdoc = await JsonDocument.ParseAsync(cs, cancellationToken: ct).ConfigureAwait(false);

            if (!cdoc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                // Hesap gerçek ama kanalı yok (yalnız izleyici hesabı).
                // "saglayici" DEĞİL: müşteriye "başka hesapla dene" denmeli,
                // "sorun oldu tekrar dene" değil — ikisi farklı eylem ister.
                return new(false, "kanalyok", null);
            }

            var channelId = items[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            string? title = null, handle = null;
            if (items[0].TryGetProperty("snippet", out var sn))
            {
                title = sn.TryGetProperty("title", out var t) ? t.GetString() : null;
                handle = sn.TryGetProperty("customUrl", out var cu) ? cu.GetString() : null;
            }

            if (string.IsNullOrEmpty(channelId))
            {
                _log.LogWarning("YouTube mine=true yanıtında kanal kimliği yok");
                return new(false, "saglayici", null);
            }

            return new(true, null, new IntakeLinkedIdentity(title ?? "", handle, channelId));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Google kanal bağlama başarısız");
            return new(false, "saglayici", null);
        }
    }
}
```

- [ ] **Step 4: Program.cs'e `AddHttpClient<IGoogleChannelClient, GoogleChannelClient>` satırını ekle** (Task 1 Step 2'deki blok), testleri geçir

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter GoogleChannelClientTests`
Expected: 4 PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/Login/GoogleChannelClient.cs OrderDeck.LicenseServer.Tests/Services/IntakeForm/GoogleChannelClientTests.cs OrderDeck.LicenseServer/Program.cs
git commit -m "feat(kayit-formu): google ile kanal cozumleme istemcisi"
```

---

### Task 4: FacebookNameClient (TDD)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/IntakeForm/Login/FacebookNameClient.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/IntakeForm/FacebookNameClientTests.cs`

Masaüstündeki `FacebookOAuthExchanger` KULLANILMAZ: o long-lived token üretir,
RedirectUri'si sabittir ve `[Authorize]` akışına aittir. Burada kısa ömürlü
token + `/me?fields=id,name` yeter. Aynı Meta app'i (`OrderDeck:Facebook`)
kullanılır — `public_profile` review istemez.

- [ ] **Step 1: Failing test'i yaz**

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class FacebookNameClientTests
{
    private readonly string _appId = $"fbid-{Guid.NewGuid():N}";
    private readonly string _appSecret = $"fbs-{Guid.NewGuid():N}";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, string? AuthHeader)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return Respond(request);
        }
    }

    private FacebookNameClient NewClient(StubHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new FacebookOptions { AppId = _appId, AppSecret = _appSecret }),
        Options.Create(new IntakeLoginOptions
        {
            RedirectUri = "https://orderdeckapp.com/musteri-kayit/baglanti-donusu"
        }),
        NullLogger<FacebookNameClient>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task Basarili_akista_gorunen_ad_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("oauth/access_token")
                ? Json("""{"access_token":"fbtok-1"}""")
                : Json("""{"id":"123","name":"Musa Sevinç"}""")
        };

        var result = await NewClient(handler).FetchNameAsync("code-1", CancellationToken.None);

        result.Ok.Should().BeTrue();
        // Handle/ChannelId YOK: canlı yorumlarda Facebook'tan gelen şey görünen
        // ad — eşleştirme de o adla yapılıyor (HandleValidator'da FB kuralı
        // olmamasıyla aynı gerekçe).
        result.Identity.Should().Be(new IntakeLinkedIdentity("Musa Sevinç", null, null));

        var tokenReq = handler.Requests[0];
        tokenReq.Uri.ToString().Should().NotContain(_appSecret, "sır URI'ye sızmamalı");
        tokenReq.Body.Should().Contain(_appSecret).And.Contain("code-1");

        handler.Requests[1].Uri.ToString().Should().Contain("/me").And.Contain("fields=id%2Cname");
        handler.Requests[1].AuthHeader.Should().Be("Bearer fbtok-1");
    }

    [Fact]
    public async Task Takas_duserse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler();
        var result = await NewClient(handler).FetchNameAsync("code-1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("saglayici");
    }

    [Fact]
    public async Task Ad_bos_gelirse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("oauth/access_token")
                ? Json("""{"access_token":"fbtok-1"}""")
                : Json("""{"id":"123"}""")
        };

        var result = await NewClient(handler).FetchNameAsync("code-1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("saglayici");
    }
}
```

- [ ] **Step 2: Çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FacebookNameClientTests`
Expected: derleme hatası.

- [ ] **Step 3: Implementasyonu yaz**

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;

namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

public interface IFacebookNameClient
{
    Task<IntakeLoginResult> FetchNameAsync(string code, CancellationToken ct);
}

/// <summary>
/// Kayıt formu için Facebook görünen adı: code → kısa ömürlü token →
/// <c>/me?fields=id,name</c>. Masaüstü akışındaki FacebookOAuthExchanger'dan
/// AYRI — o long-lived token üretir ve kendi RedirectUri'sine bağlıdır; burada
/// token metot bitince atılır. App aynı (<c>OrderDeck:Facebook</c>).
/// GraphBaseUrl testlerde override edilebilsin diye FacebookOptions'tan gelir.
/// </summary>
public sealed class FacebookNameClient : IFacebookNameClient
{
    private readonly HttpClient _http;
    private readonly FacebookOptions _fb;
    private readonly IntakeLoginOptions _login;
    private readonly ILogger<FacebookNameClient> _log;

    public FacebookNameClient(
        HttpClient http,
        IOptions<FacebookOptions> fb,
        IOptions<IntakeLoginOptions> login,
        ILogger<FacebookNameClient> log)
    {
        _http = http;
        _fb = fb.Value;
        _login = login.Value;
        _log = log;
    }

    public async Task<IntakeLoginResult> FetchNameAsync(string code, CancellationToken ct)
    {
        try
        {
            // Sırlar GÖVDEDE — Graph, oauth/access_token için POST form kabul
            // ediyor ve FacebookOAuthExchanger da aynı yolu kullanıyor.
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post,
                $"{_fb.GraphBaseUrl}/{_fb.GraphApiVersion}/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _fb.AppId,
                    ["client_secret"] = _fb.AppSecret,
                    ["redirect_uri"] = _login.RedirectUri,
                    ["code"] = code
                })
            };
            using var tokenResp = await _http.SendAsync(tokenReq, ct).ConfigureAwait(false);
            if (!tokenResp.IsSuccessStatusCode)
            {
                _log.LogWarning("Facebook token takası başarısız — HTTP {Status}", (int)tokenResp.StatusCode);
                return new(false, "saglayici", null);
            }

            string? accessToken;
            await using (var s = await tokenResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false))
                accessToken = doc.RootElement.TryGetProperty("access_token", out var at)
                    ? at.GetString() : null;

            if (string.IsNullOrEmpty(accessToken))
            {
                _log.LogWarning("Facebook token yanıtında access_token yok");
                return new(false, "saglayici", null);
            }

            using var meReq = new HttpRequestMessage(HttpMethod.Get,
                $"{_fb.GraphBaseUrl}/{_fb.GraphApiVersion}/me?fields=id%2Cname");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var meResp = await _http.SendAsync(meReq, ct).ConfigureAwait(false);
            if (!meResp.IsSuccessStatusCode)
            {
                _log.LogWarning("Facebook /me çağrısı başarısız — HTTP {Status}", (int)meResp.StatusCode);
                return new(false, "saglayici", null);
            }

            await using var ms = await meResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var mdoc = await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
            var name = mdoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                _log.LogWarning("Facebook /me yanıtında ad yok");
                return new(false, "saglayici", null);
            }

            // Handle/ChannelId yok: FB eşleştirmesi görünen adla yürüyor
            // (HandleValidator'da FB kuralı olmamasıyla aynı gerekçe).
            return new(true, null, new IntakeLinkedIdentity(name.Trim(), null, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Facebook ad bağlama başarısız");
            return new(false, "saglayici", null);
        }
    }
}
```

- [ ] **Step 4: Program.cs'e `AddHttpClient<IFacebookNameClient, FacebookNameClient>` satırını ekle, testleri geçir**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FacebookNameClientTests`
Expected: 3 PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/Login/FacebookNameClient.cs OrderDeck.LicenseServer.Tests/Services/IntakeForm/FacebookNameClientTests.cs OrderDeck.LicenseServer/Program.cs
git commit -m "feat(kayit-formu): facebook ile ad cozumleme istemcisi"
```

---

### Task 5: Başlatma ucu — `GET /musteri-kayit/{slug}/baglan/{platform}` (TDD)

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/IntakeLinkController.cs`
- Create: `OrderDeck.LicenseServer.Tests/TestHelpers/FakeIntakeLoginClients.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/IntakeLinkEndpointTests.cs`

- [ ] **Step 1: Fake istemcileri yaz** (`FakeIntakeLoginClients.cs`)

```csharp
using OrderDeck.LicenseServer.Services.IntakeForm.Login;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>Sağlayıcıya gitmeden bağlama akışını test etmek için. Result
/// test başında kurulur; Codes, hangi authorization code'un iletildiğini çiviler.</summary>
public sealed class FakeGoogleChannelClient : IGoogleChannelClient
{
    public IntakeLoginResult Result { get; set; } = new(false, "saglayici", null);
    public List<string> Codes { get; } = new();

    public Task<IntakeLoginResult> FetchChannelAsync(string code, CancellationToken ct)
    {
        Codes.Add(code);
        return Task.FromResult(Result);
    }
}

public sealed class FakeFacebookNameClient : IFacebookNameClient
{
    public IntakeLoginResult Result { get; set; } = new(false, "saglayici", null);
    public List<string> Codes { get; } = new();

    public Task<IntakeLoginResult> FetchNameAsync(string code, CancellationToken ct)
    {
        Codes.Add(code);
        return Task.FromResult(Result);
    }
}
```

- [ ] **Step 2: Failing testleri yaz** (`IntakeLinkEndpointTests.cs` — dosyanın Task 5 bölümü)

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers;

/// <summary>
/// YouTubeIdentityFactory ile aynı desen: ApiFactory'ye ConfigureTestServices
/// ile fake sağlayıcı istemcileri takılır, bayraklar PostConfigure ile açılır.
/// Kimlik bilgileri ÜRETİLİR (repo public — sabit yazılmaz).
/// </summary>
public sealed class IntakeLinkFactory : ApiFactory
{
    public FakeGoogleChannelClient Google { get; } = new();
    public FakeFacebookNameClient Facebook { get; } = new();
    public FakeYouTubeChannelResolver Resolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGoogleChannelClient>();
            services.AddSingleton<IGoogleChannelClient>(Google);
            services.RemoveAll<IFacebookNameClient>();
            services.AddSingleton<IFacebookNameClient>(Facebook);
            services.RemoveAll<IYouTubeChannelResolver>();
            services.AddSingleton<IYouTubeChannelResolver>(Resolver);
            services.PostConfigure<IntakeLoginOptions>(o =>
            {
                o.GoogleClientId = $"cid-{Guid.NewGuid():N}";
                o.GoogleClientSecret = $"cs-{Guid.NewGuid():N}";
                o.YouTubeEnabled = true;
                o.FacebookEnabled = true;
            });
            services.PostConfigure<FacebookOptions>(o =>
            {
                o.AppId = $"fbid-{Guid.NewGuid():N}";
                o.AppSecret = $"fbs-{Guid.NewGuid():N}";
            });
        });
    }
}

/// <summary>Bayraklar kapalıyken uçların YOK gibi davrandığını çivilemek için.</summary>
public sealed class IntakeLinkDisabledFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<IntakeLoginOptions>(o =>
            {
                o.YouTubeEnabled = false;
                o.FacebookEnabled = false;
            }));
    }
}

public sealed class IntakeLinkEndpointTests : IClassFixture<IntakeLinkFactory>
{
    private readonly IntakeLinkFactory _factory;
    public IntakeLinkEndpointTests(IntakeLinkFactory factory) => _factory = factory;

    private async Task<string> SeedSlugAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"ilk-{Guid.NewGuid():N}@x",
            Name = "Ilk",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-ILK-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"l-{Guid.NewGuid():N}"[..10];
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
        return slug;
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, // sağlayıcıya gerçekten gitmeyelim
        HandleCookies = true
    });

    [Fact]
    public async Task Youtube_baslat_googlea_yonlendirir()
    {
        var slug = await SeedSlugAsync();
        var client = NewClient();

        var resp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/youtube");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var loc = resp.Headers.Location!.ToString();
        loc.Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth");
        // Kapsam readonly — geniş "youtube" kapsamına sessizce genişlemek
        // tam da bu testin yakalaması gereken regresyon.
        loc.Should().Contain(Uri.EscapeDataString("https://www.googleapis.com/auth/youtube.readonly"));
        loc.Should().Contain("state=").And.Contain("response_type=code");
        // Nonce çerezi dönüş ucunun state'i tarayıcıya bağlaması için şart.
        resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.StartsWith("od.link="));
    }

    [Fact]
    public async Task Facebook_baslat_metaya_yonlendirir()
    {
        var slug = await SeedSlugAsync();
        var client = NewClient();

        var resp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/facebook");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var loc = resp.Headers.Location!.ToString();
        loc.Should().StartWith("https://www.facebook.com/");
        loc.Should().Contain("scope=public_profile").And.Contain("state=");
    }

    [Fact]
    public async Task Bilinmeyen_platform_404()
    {
        var slug = await SeedSlugAsync();
        (await NewClient().GetAsync($"/musteri-kayit/{slug}/baglan/instagram"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Olmayan_slug_404()
    {
        (await NewClient().GetAsync("/musteri-kayit/yok-boyle-slug/baglan/youtube"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class IntakeLinkDisabledTests : IClassFixture<IntakeLinkDisabledFactory>
{
    private readonly IntakeLinkDisabledFactory _factory;
    public IntakeLinkDisabledTests(IntakeLinkDisabledFactory factory) => _factory = factory;

    [Fact]
    public async Task Bayrak_kapaliysa_baslatma_404()
    {
        // Slug'ın var olup olmaması önemsiz: bayrak kontrolü DB'den önce —
        // kapalı özellik slug taramaya bile izin vermemeli.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        (await client.GetAsync("/musteri-kayit/herhangi/baglan/youtube"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/musteri-kayit/herhangi/baglan/facebook"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 3: Çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeLinkEndpointTests|IntakeLinkDisabledTests"`
Expected: derleme hatası (controller yok).

- [ ] **Step 4: Controller'ı yaz** (`IntakeLinkController.cs` — Callback Task 6'da eklenecek)

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Kayıt formu "hesabını bağla" akışı (Faz 2). Anonim uçlar — koruma katmanları:
/// bayrak kontrolü (kapalı özellik 404), slug doğrulaması (aktif form şart),
/// IP hız sınırı, tek kullanımlık state + çerez nonce eşleşmesi.
///
/// Dönüş route'u SABİT (<c>/musteri-kayit/baglanti-donusu</c>) — sağlayıcı
/// panellerine slug'lı joker adres yazılamaz. Sayfa route'u
/// <c>/musteri-kayit/{slug}</c> ile çakışmaz: literal segment parametreyi yener.
/// </summary>
[EnableRateLimiting("intake-link")]
public sealed class IntakeLinkController : ControllerBase
{
    public const string CookieName = "od.link";
    private const string GoogleAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string YouTubeReadonlyScope = "https://www.googleapis.com/auth/youtube.readonly";

    private readonly IntakeFormService _service;
    private readonly IntakeLinkStore _store;
    private readonly IOptions<IntakeLoginOptions> _login;
    private readonly IOptions<FacebookOptions> _facebook;
    private readonly IGoogleChannelClient _google;
    private readonly IFacebookNameClient _fb;
    private readonly ILogger<IntakeLinkController> _log;

    public IntakeLinkController(
        IntakeFormService service,
        IntakeLinkStore store,
        IOptions<IntakeLoginOptions> login,
        IOptions<FacebookOptions> facebook,
        IGoogleChannelClient google,
        IFacebookNameClient fb,
        ILogger<IntakeLinkController> log)
    {
        _service = service;
        _store = store;
        _login = login;
        _facebook = facebook;
        _google = google;
        _fb = fb;
        _log = log;
    }

    [HttpGet("/musteri-kayit/{slug}/baglan/{platform}")]
    public async Task<IActionResult> Start(string slug, string platform, CancellationToken ct)
    {
        var opt = _login.Value;
        var isYouTube = platform == "youtube";
        var isFacebook = platform == "facebook";
        // Bayrak kontrolü DB'den ÖNCE: kapalı özellik slug taramaya alet olmasın.
        if (isYouTube && !opt.YouTubeLoginReady) return NotFound();
        if (isFacebook && (!opt.FacebookEnabled || !_facebook.Value.IsConfigured)) return NotFound();
        if (!isYouTube && !isFacebook) return NotFound();

        var config = await _service.GetActiveBySlugAsync(slug, ct);
        if (config is null) return NotFound();

        // Nonce: state'i TARAYICIYA bağlar. Var olan (ve makul görünen) nonce
        // korunur — müşteri iki platformu peş peşe bağlarken kimliklerin
        // aynı nonce altında birikmesi gerekir.
        var nonce = Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(nonce) || nonce.Length > 128)
            nonce = IntakeLinkStore.RandomToken();

        // Path="/" bilinçli: form eski /r/{slug} route'undan da açılıyor; dar
        // path orada kimliği görünmez yapardı. Çerezde PII yok, yalnız nonce.
        Response.Cookies.Append(CookieName, nonce, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax, // Strict olmaz: sağlayıcıdan dönüş cross-site
            Path = "/",
            MaxAge = TimeSpan.FromHours(1)
        });

        var returnPath = "/musteri-kayit/" + Uri.EscapeDataString(slug);
        var state = _store.SaveState(new IntakeLinkState(nonce, slug, platform, returnPath));

        var authUrl = isYouTube
            ? GoogleAuthUrl +
              "?client_id=" + Uri.EscapeDataString(opt.GoogleClientId!) +
              "&redirect_uri=" + Uri.EscapeDataString(opt.RedirectUri) +
              "&response_type=code" +
              "&scope=" + Uri.EscapeDataString(YouTubeReadonlyScope) +
              // Hesap seçtir: yayıncı telefonda çoğu kez birden çok Google
              // hesabına girili; sessizce ilkine bağlamak yanlış kanal demek.
              "&prompt=select_account" +
              "&state=" + state
            : "https://www.facebook.com/" + _facebook.Value.GraphApiVersion + "/dialog/oauth" +
              "?client_id=" + Uri.EscapeDataString(_facebook.Value.AppId) +
              "&redirect_uri=" + Uri.EscapeDataString(opt.RedirectUri) +
              "&response_type=code" +
              "&scope=public_profile" +
              "&state=" + state;

        return Redirect(authUrl);
    }
}
```

- [ ] **Step 5: Program.cs'e `AddSingleton<IntakeLinkStore>` satırını ekle** (Task 1 Step 2'deki blok), Task 5 testlerini geçir

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeLinkEndpointTests|IntakeLinkDisabledTests"`
Expected: 5 PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/IntakeLinkController.cs OrderDeck.LicenseServer.Tests/TestHelpers/FakeIntakeLoginClients.cs OrderDeck.LicenseServer.Tests/Controllers/IntakeLinkEndpointTests.cs OrderDeck.LicenseServer/Program.cs
git commit -m "feat(kayit-formu): hesap baglama baslatma ucu"
```

---

### Task 6: Dönüş ucu — `GET /musteri-kayit/baglanti-donusu` (TDD)

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/IntakeLinkController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/IntakeLinkEndpointTests.cs` (aynı sınıfa ekle)

- [ ] **Step 1: Failing testleri ekle** (`IntakeLinkEndpointTests` sınıfının içine)

```csharp
    /// <summary>Başlatma 302'sinin Location'ından state'i söker — testin
    /// sunucuyla paylaştığı tek şey gerçek akışın da taşıdığı değer.</summary>
    private static string StateFrom(HttpResponseMessage startResp)
    {
        var query = startResp.Headers.Location!.Query.TrimStart('?').Split('&');
        return query.First(p => p.StartsWith("state=")).Substring("state=".Length);
    }

    private async Task<(HttpClient Client, string Slug, string State)> StartYouTubeAsync()
    {
        var slug = await SeedSlugAsync();
        var client = NewClient();
        var startResp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/youtube");
        startResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return (client, slug, StateFrom(startResp));
    }

    [Fact]
    public async Task Donus_basarili_olunca_forma_ok_ile_yonlendirir_ve_kimlik_gorunur()
    {
        _factory.Google.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bağlı Kanal", "@baglikanal", "UCbagli00000000000000abc"));
        var (client, slug, state) = await StartYouTubeAsync();

        var resp = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=gcode-1");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=ok");
        _factory.Google.Codes.Should().Contain("gcode-1");

        // Kimlik SUNUCU tarafında, çereze bağlı kayıtta — sayfa onu çizer.
        var html = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        html.Should().Contain("Bağlı Kanal");
    }

    [Fact]
    public async Task Izin_reddi_iptal_koduyla_forma_doner()
    {
        var (client, slug, state) = await StartYouTubeAsync();

        var resp = await client.GetAsync(
            $"/musteri-kayit/baglanti-donusu?state={state}&error=access_denied");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=iptal");
    }

    [Fact]
    public async Task Kanalsiz_hesap_kanalyok_koduyla_forma_doner()
    {
        _factory.Google.Result = new IntakeLoginResult(false, "kanalyok", null);
        var (client, slug, state) = await StartYouTubeAsync();

        var resp = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c");

        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=kanalyok");
    }

    [Fact]
    public async Task State_yoksa_veya_bilinmiyorsa_suresi_doldu_sayfasi()
    {
        var client = NewClient();

        var noState = await client.GetAsync("/musteri-kayit/baglanti-donusu?code=c");
        noState.StatusCode.Should().Be(HttpStatusCode.OK);
        (await noState.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");

        var badState = await client.GetAsync("/musteri-kayit/baglanti-donusu?state=uydurma&code=c");
        (await badState.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");
    }

    [Fact]
    public async Task State_tek_kullanimlik()
    {
        _factory.Google.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Tek Kanal", null, "UCtek0000000000000000abc"));
        var (client, _, state) = await StartYouTubeAsync();

        (await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Tekrar oynatma: aynı state ikinci kez GEÇMEZ (geri tuşu, kopyalanan URL).
        var replay = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c");
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");
    }

    [Fact]
    public async Task Nonce_eslesmezse_reddedilir()
    {
        _factory.Google.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Çalıntı Kanal", null, "UCcalinti000000000000abc"));
        var (_, _, state) = await StartYouTubeAsync();

        // Farklı tarayıcı (çerezsiz istemci) çalınan state ile dönüyor —
        // state gerçek ama O TARAYICIYA ait değil. CSRF/oturum sabitleme kapısı.
        var attacker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var resp = await attacker.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");
    }

    [Fact]
    public async Task Facebook_donusu_kimligi_kaydeder()
    {
        _factory.Facebook.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Musa Sevinç", null, null));
        var slug = await SeedSlugAsync();
        var client = NewClient();
        var startResp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/facebook");
        var state = StateFrom(startResp);

        var resp = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=fbcode");

        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=ok");
        var html = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        html.Should().Contain("Musa Sevinç");
    }
```

**NOT:** `Donus_basarili_olunca...` ve `Facebook_donusu...` testlerindeki
"sayfa kimliği çizer" iddiaları Task 7 bitene kadar KIRMIZI kalır. Task 6'da
bu iki iddia satırını `// Task 7'de açılacak` yorumuyla kapalı yaz, Task 7
Step 5'te aç. Kalan iddialar (redirect + kod) Task 6'da yeşil olmalı.

- [ ] **Step 2: Çalıştır, düştüğünü gör** (Callback ucu yok → 404'ler)

- [ ] **Step 3: Callback'i controller'a ekle**

```csharp
    /// <summary>
    /// Sağlayıcıdan dönüş. Sıra bilinçli: önce state (yoksa hiçbir şeye
    /// güvenilmez), sonra nonce (state gerçek ama bu tarayıcının değilse
    /// çalıntıdır), sonra error/code, en son sağlayıcı çağrısı.
    /// Sessiz başarısızlık YOK: her dal ya forma kodla döner ya açık sayfa basar.
    /// </summary>
    [HttpGet("/musteri-kayit/baglanti-donusu")]
    public async Task<IActionResult> Callback(
        string? state, string? code, string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 128)
            return ExpiredPage();

        var st = _store.ConsumeState(state);
        var nonce = Request.Cookies[CookieName];
        if (st is null || string.IsNullOrEmpty(nonce) ||
            !string.Equals(st.CookieNonce, nonce, StringComparison.Ordinal))
        {
            // state süresi dolmuş, tekrar oynatılmış ya da başka tarayyıcıdan
            // geliyor. Slug'ı bilmiyoruz (state'in içindeydi) — forma
            // yönlendiremeyiz, açık bir sayfa basarız.
            return ExpiredPage();
        }

        // İzin reddi (error=access_denied) ya da code'suz dönüş: hata değil,
        // müşteri kararı. Kenar durum tablosu: "hatasız forma dönülür".
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return LocalRedirect(st.ReturnPath + "?baglanti=iptal");

        var result = st.Platform == "youtube"
            ? await _google.FetchChannelAsync(code, ct)
            : await _fb.FetchNameAsync(code, ct);

        if (!result.Ok || result.Identity is null)
        {
            _log.LogWarning("Hesap bağlama başarısız — platform={Platform}, kod={Kod}",
                st.Platform, result.ErrorCode);
            return LocalRedirect(st.ReturnPath + "?baglanti=" + (result.ErrorCode ?? "saglayici"));
        }

        _store.SaveIdentity(nonce, st.Platform, result.Identity);
        return LocalRedirect(st.ReturnPath + "?baglanti=ok");
    }

    /// <summary>Slug'sız çıkmaz sayfası. Razor sayfası değil: bu uca yalnız
    /// bozuk/bayat state ile gelinir, tam sayfa altyapısı kurmaya değmez.</summary>
    private ContentResult ExpiredPage() => Content(
        "<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>Bağlantının süresi doldu</title></head><body style=\"font-family:sans-serif;" +
        "max-width:28rem;margin:4rem auto;padding:0 1rem;text-align:center\">" +
        "<h1 style=\"font-size:1.2rem\">Bağlantının süresi doldu</h1>" +
        "<p>Kayıt formuna geri dön ve hesabını bağlamayı tekrar dene.</p>" +
        "</body></html>",
        "text/html; charset=utf-8");
```

- [ ] **Step 4: Testleri geçir** (Task 7'ye kilitli iki iddia hariç)

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeLinkEndpointTests|IntakeLinkDisabledTests"`
Expected: hepsi PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/IntakeLinkController.cs OrderDeck.LicenseServer.Tests/Controllers/IntakeLinkEndpointTests.cs
git commit -m "feat(kayit-formu): hesap baglama donus ucu — tek kullanimlik state ve nonce kapisi"
```

---

### Task 7: Form sayfası — bağlı kimlik çipi, banner, unlink, JS kapıları

**Files:**
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml`
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/IntakeLinkEndpointTests.cs`

- [ ] **Step 1: Kırmızı testleri yaz** — `IntakeLinkEndpointTests` sınıfına ekle:

```csharp
    /// <summary>Bağlama akışını sonuna kadar koşturur: start → callback.
    /// Dönen client'ın çerezinde nonce, store'da kimlik var.</summary>
    private async Task<(HttpClient Client, string Slug)> LinkAsync(
        string platform, IntakeLoginResult result)
    {
        if (platform == "youtube") _factory.Google.Result = result;
        else _factory.Facebook.Result = result;
        var slug = await SeedSlugAsync();
        var client = NewClient();
        var startResp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/{platform}");
        await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={StateFrom(startResp)}&code=c");
        return (client, slug);
    }

    [Fact]
    public async Task Bagli_youtube_chip_cizer_input_gizler()
    {
        var (client, slug) = await LinkAsync("youtube", new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCbagli0001")));

        var html = await (await client.GetAsync($"/musteri-kayit/{slug}?baglanti=ok"))
            .Content.ReadAsStringAsync();

        html.Should().Contain("linked-chip");
        html.Should().Contain("Bilal Kanal");
        html.Should().Contain("Hesabın bağlandı");    // ok banner'ı
        html.Should().NotContain("id=\"ytUser\"");    // elle giriş kutusu çizilmedi
    }

    [Fact]
    public async Task Ok_banneri_kimlik_yoksa_cizilmez()
    {
        // ?baglanti=ok elle de yazılabilir — kimliksizken "bağlandı" diye
        // yalan söylemek müşteriyi boş kayda götürür.
        var slug = await SeedSlugAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}?baglanti=ok"))
            .Content.ReadAsStringAsync();
        html.Should().NotContain("Hesabın bağlandı");
    }

    [Fact]
    public async Task Kanalyok_banneri_cizilir()
    {
        var slug = await SeedSlugAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}?baglanti=kanalyok"))
            .Content.ReadAsStringAsync();
        html.Should().Contain("YouTube kanalı yok");
    }

    [Fact]
    public async Task Unlink_kimligi_siler_ve_kutuyu_geri_getirir()
    {
        var (client, slug) = await LinkAsync("youtube", new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCbagli0002")));
        var html = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        var token = AdminLoginHelper.ExtractAntiForgeryToken(html);

        var resp = await client.PostAsync($"/musteri-kayit/{slug}?handler=Unlink&platform=youtube",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Slug"] = slug
            }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var after = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        after.Should().NotContain("linked-chip");
        after.Should().Contain("id=\"ytUser\"");
    }

    [Fact]
    public async Task Baglama_linkleri_bayrak_acikken_cizilir()
    {
        var slug = await SeedSlugAsync();
        var html = await (await NewClient().GetAsync($"/musteri-kayit/{slug}"))
            .Content.ReadAsStringAsync();
        html.Should().Contain($"/musteri-kayit/{slug}/baglan/youtube");
        html.Should().Contain($"/musteri-kayit/{slug}/baglan/facebook");
    }
```

`IntakeLinkDisabledTests` sınıfına ekle (bayrak kapalıyken link HİÇ çizilmemeli
— 404 veren uca görünür link koymak müşteriyi çıkmaza sokar):

```csharp
    [Fact]
    public async Task Bayrak_kapaliysa_formda_baglama_linki_yok()
    {
        string slug;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"kapali-{Guid.NewGuid():N}@x",
                Name = "Kapali",
                PasswordHash = "x",
                CreatedAt = DateTimeOffset.UtcNow,
                EmailConfirmedAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(customer);
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-KPL-" + Guid.NewGuid().ToString("N"),
                CustomerId = customer.Id,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
            slug = $"k-{Guid.NewGuid():N}"[..10];
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
        }

        var html = await (await _factory.CreateClient().GetAsync($"/musteri-kayit/{slug}"))
            .Content.ReadAsStringAsync();
        html.Should().NotContain("/baglan/");
    }
```

- [ ] **Step 2: Çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeLinkEndpointTests|IntakeLinkDisabledTests"`
Expected: yeni testler FAIL (chip/banner/unlink yok).

- [ ] **Step 3: PageModel'i genişlet** — `IntakeForm.cshtml.cs`

Using'lere ekle:

```csharp
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Controllers;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
```

Ctor'a üç bağımlılık ekle (mevcut dört alan kalır):

```csharp
    private readonly IntakeLinkStore _linkStore;
    private readonly IOptions<IntakeLoginOptions> _loginOptions;
    private readonly IOptions<FacebookOptions> _facebookOptions;

    public IntakeFormModel(
        IntakeFormService service,
        WhatsAppLinkBuilder linkBuilder,
        ILogger<IntakeFormModel> log,
        IYouTubeChannelResolver youTube,
        IntakeLinkStore linkStore,
        IOptions<IntakeLoginOptions> loginOptions,
        IOptions<FacebookOptions> facebookOptions)
    {
        _service = service;
        _linkBuilder = linkBuilder;
        _log = log;
        _youTube = youTube;
        _linkStore = linkStore;
        _loginOptions = loginOptions;
        _facebookOptions = facebookOptions;
    }
```

Property'ler (`YouTubeChannelThumbnail`'ın altına):

```csharp
    // Faz 2 — OAuth ile bağlanmış kimlikler. Çerezdeki nonce üzerinden
    // sunucunun KENDİ kaydından okunur; istemciden kimlik kabul edilmez.
    public IntakeLinkedIdentity? LinkedYouTube { get; private set; }
    public IntakeLinkedIdentity? LinkedFacebook { get; private set; }

    // Dönüş banner'ı. Metin SABİT koddan seçilir; query'deki serbest
    // metin ASLA ekrana yazılmaz (XSS).
    public string? LinkBanner { get; private set; }
    public bool LinkBannerIsError { get; private set; }

    // Link yalnız özellik gerçekten çalışır durumdayken çizilir — 404 veren
    // uca görünür link koymak müşteriyi çıkmaza sokar.
    public bool ShowYouTubeLink => _loginOptions.Value.YouTubeLoginReady;
    public bool ShowFacebookLink =>
        _loginOptions.Value.FacebookEnabled && _facebookOptions.Value.IsConfigured;
```

`OnGetAsync` şöyle olur:

```csharp
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);
        LoadLinkedIdentities();

        // "ok" yalnız kimlik GERÇEKTEN varsa çizilir: kod elle URL'e yazılmış
        // ya da kimliğin 30 dakikası dolmuş olabilir.
        (LinkBanner, LinkBannerIsError) = Request.Query["baglanti"].ToString() switch
        {
            "ok" when LinkedYouTube is not null || LinkedFacebook is not null
                => ("Hesabın bağlandı. Kalan bilgileri doldurup formu gönder.", false),
            "iptal" => ("Bağlantı iptal edildi. İstersen kullanıcı adını elle yazabilirsin.", true),
            "kanalyok" => ("Bu Google hesabında YouTube kanalı yok. Kanalın olan hesabı seç.", true),
            "saglayici" => ("Bağlantı sırasında bir sorun oldu. Tekrar dene ya da kullanıcı adını elle yaz.", true),
            _ => (null, false)
        };
        return Page();
    }
```

Sınıfın sonuna (Trim helper'ın üstüne) iki üye ekle:

```csharp
    /// <summary>
    /// Unlink de bu sayfaya POST'lanır (ayrı controller'a değil): anti-forgery
    /// token'ı zaten formda ve dönüş adresi Request.Path ile geldiği route'a
    /// (/musteri-kayit/{slug} veya eski /r/{slug}) gider.
    /// </summary>
    public async Task<IActionResult> OnPostUnlinkAsync(string platform, CancellationToken ct)
    {
        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);

        var nonce = Request.Cookies[IntakeLinkController.CookieName];
        if (!string.IsNullOrEmpty(nonce) && platform is "youtube" or "facebook")
            _linkStore.RemoveIdentity(nonce, platform);
        return LocalRedirect(Request.Path.Value ?? $"/musteri-kayit/{Slug}");
    }

    private void LoadLinkedIdentities()
    {
        var nonce = Request.Cookies[IntakeLinkController.CookieName];
        if (string.IsNullOrEmpty(nonce)) return;
        LinkedYouTube = _linkStore.GetIdentity(nonce, "youtube");
        LinkedFacebook = _linkStore.GetIdentity(nonce, "facebook");
    }
```

- [ ] **Step 4: Markup** — `IntakeForm.cshtml`

**4a — Banner:** `<form id="intakeForm" ...>` satırının hemen ÜSTÜNE:

```cshtml
        @if (Model.LinkBanner is not null)
        {
            <div class="banner @(Model.LinkBannerIsError ? "banner-err" : "banner-ok")">@Model.LinkBanner</div>
        }
```

**4b — YouTube alanı:** mevcut `<div class="field">` (YouTube) içeriğini şöyle
sar — bağlıyken çip, değilken MEVCUT blok aynen + link:

```cshtml
            <div class="field">
                <label class="lbl" asp-for="Input.YouTubeUsername"><span class="platform-ico">📺</span>YouTube</label>
                @if (Model.LinkedYouTube is not null)
                {
                    <div class="linked-chip">
                        <span>✓ @Model.LinkedYouTube.DisplayName@(string.IsNullOrEmpty(Model.LinkedYouTube.Handle) ? "" : $" ({Model.LinkedYouTube.Handle})")</span>
                        @* Ayrı handler'a giden submit; e.submitter guard'ı JS'te —
                           yoksa form doğrulama kapılarına takılır. formnovalidate:
                           tarayıcının required kontrolü de atlanmalı. *@
                        <button type="submit" class="unlink" formnovalidate
                                asp-page-handler="Unlink" asp-route-platform="youtube">Bağlantıyı kaldır</button>
                    </div>
                }
                else
                {
                    @* ——— MEVCUT içerik AYNEN buraya: with-prefix input (#ytUser),
                       #ytStatus, #ytConfirmId, #ytConfirmWrap, iki .err span'i ——— *@
                    @if (Model.ShowYouTubeLink)
                    {
                        <a class="link-start" href="/musteri-kayit/@Model.Slug/baglan/youtube">Google ile bağla — kanal adın otomatik gelsin</a>
                        <div class="hint">Yalnızca kanal adını ve kimliğini görürüz; hesabına başka erişim almayız.</div>
                    }
                }
            </div>
```

**4c — Facebook alanı:** aynı desen:

```cshtml
            <div class="field">
                <label class="lbl" asp-for="Input.FacebookUsername"><span class="platform-ico">👥</span>Facebook</label>
                @if (Model.LinkedFacebook is not null)
                {
                    <div class="linked-chip">
                        <span>✓ @Model.LinkedFacebook.DisplayName</span>
                        <button type="submit" class="unlink" formnovalidate
                                asp-page-handler="Unlink" asp-route-platform="facebook">Bağlantıyı kaldır</button>
                    </div>
                }
                else
                {
                    <input class="inp uname" asp-for="Input.FacebookUsername" maxlength="64" placeholder="kullanıcı adı" />
                    <span class="err" asp-validation-for="Input.FacebookUsername"></span>
                    @if (Model.ShowFacebookLink)
                    {
                        <a class="link-start" href="/musteri-kayit/@Model.Slug/baglan/facebook">Facebook ile bağla — adın otomatik gelsin</a>
                    }
                }
            </div>
```

**4d — KVKK paragrafı:** mevcut `<p class="kvkk">` içine son cümle olarak ekle:

```
Google veya Facebook ile bağlanırsan yalnızca herkese açık profil adını
(YouTube'da kanal adı ve kimliği) alırız; şifren bize gelmez, hesabına başka
erişimimiz olmaz.
```

**4e — CSS:** `<style>` bloğuna ekle (renkleri sayfanın mevcut paletine uydur;
sınıf ADLARI sabit — testler ve JS `linked-chip`/`unlink`/`link-start`'a bakıyor):

```css
    .banner { border-radius:10px; padding:10px 12px; margin:0 0 14px; font-size:.9rem; }
    .banner-ok { background:#e8f7ee; color:#116b3a; }
    .banner-err { background:#fdecec; color:#8c1c1c; }
    .linked-chip { display:flex; align-items:center; gap:8px; padding:10px 12px;
                   border:1px solid #d4e8d9; background:#f2fbf5; border-radius:10px; }
    .linked-chip .unlink { margin-left:auto; background:none; border:none; color:#8c1c1c;
                           font-size:.85rem; cursor:pointer; text-decoration:underline; }
    .link-start { display:inline-block; margin-top:6px; font-size:.9rem; }
```

- [ ] **Step 5: JS** — `IntakeForm.cshtml` script bloğunda üç değişiklik:

**5a — Unlink guard + hasLinked:** submit handler'ın (`form.addEventListener('submit', ...)`)
İLK satırı olarak:

```js
        // "Bağlantıyı kaldır" da bu formun submit'i — doğrulama kapılarına
        // takılırsa müşteri bağlantıyı hiç kaldıramaz.
        if (e.submitter && e.submitter.classList.contains('unlink')) return;
```

ve `if (!anyUser)` kapısını şöyle değiştir (bağlıyken input YOK — kapı aynen
kalsaydı bağlı müşteri formu hiç gönderemezdi):

```js
        var hasLinked = !!document.querySelector('.linked-chip');
        if (!anyUser && !hasLinked) {
```

**5b — Taslak koruması:** IIFE içine, `fillDistricts(citySel.value, districtSel.value);`
satırından SONRA ekle:

```js
    // Bağlanmaya giden müşteri sayfadan ayrılır; yazdıkları kaybolursa formu
    // yarıda bırakır. sessionStorage: sekme kapanınca ölür, PII diske inmez.
    // Anahtar SLUG bazlı, pathname değil: form iki route'tan açılıyor
    // (/musteri-kayit/{slug} ve /r/{slug}) ama dönüş hep /musteri-kayit'a —
    // pathname anahtarı taslağı dönüşte bulamazdı.
    var slugEl = document.querySelector('input[name="Slug"]');
    var DRAFT_KEY = 'odIntakeDraft:' + (slugEl ? slugEl.value : '');
    var DRAFT_FIELDS = ['Input.FullName', 'Input.Email', 'Input.City', 'Input.District',
                        'Input.Address', 'Input.Phone', 'Input.Tckn',
                        'Input.InstagramUsername', 'Input.TikTokUsername'];

    function fieldByName(n) { return document.getElementsByName(n)[0]; }

    Array.prototype.forEach.call(document.querySelectorAll('.link-start'), function (a) {
        a.addEventListener('click', function () {
            var d = {};
            DRAFT_FIELDS.forEach(function (n) {
                var el = fieldByName(n);
                if (el && el.value) d[n] = el.value;
            });
            try { sessionStorage.setItem(DRAFT_KEY, JSON.stringify(d)); } catch (_) { }
        });
    });

    try {
        var draftRaw = sessionStorage.getItem(DRAFT_KEY);
        if (draftRaw) {
            var draft = JSON.parse(draftRaw);
            DRAFT_FIELDS.forEach(function (n) {
                var el = fieldByName(n);
                // Dolu alanın üstüne yazma: sunucu doğrulaması geri döndürdüyse
                // postalanan değer taslaktan daha taze.
                if (el && !el.value && draft[n]) el.value = draft[n];
            });
            // İlçe listesi ile bağlı — il taslaktan geldiyse yeniden daralt.
            if (draft['Input.City'] && citySel.value === draft['Input.City'])
                fillDistricts(citySel.value, draft['Input.District'] || '');
            sessionStorage.removeItem(DRAFT_KEY); // tek kullanımlık
        }
    } catch (_) { }
```

- [ ] **Step 6: Task 6'nın yorumlu iddialarını aç** —
`Donus_basarili_olunca_kimlik_kaydedilir_ve_formda_gorunur` ile
`Facebook_donusu_kimligi_kaydeder` testlerindeki `// Task 7'de açılacak`
satırlarını etkinleştir.

- [ ] **Step 7: Testleri geçir**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeLink"`
Expected: hepsi PASS. Ek: `dotnet test .../OrderDeck.LicenseServer.Tests.csproj --filter "IntakeForm"`
de yeşil kalmalı (ctor değişti — DI çözülüyor mu bu kanıtlar).

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml OrderDeck.LicenseServer.Tests/Controllers/IntakeLinkEndpointTests.cs
git commit -m "feat(kayit-formu): bagli kimlik cipi, donus banneri ve baglantiyi kaldirma"
```

---

### Task 8: Gönderim entegrasyonu — bağlı kimlik elle girdiyi yener

**Files:**
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs` (OnPostSubmitAsync)
- Create: `OrderDeck.LicenseServer.Tests/Pages/Public/IntakeLinkSubmitTests.cs`

- [ ] **Step 1: Kırmızı testleri yaz** — yeni dosya, `IntakeLinkFactory`'yi
(Task 5'te tanımlandı, public) paylaşır:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using OrderDeck.LicenseServer.Tests.Controllers;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// Faz 2'nin kayıt tarafı: OAuth ile bağlanmış kimlik gönderime nasıl yansır.
/// Kural: bağlı kimlik elle girdiyi YENER ve API'ye tekrar sorulmaz.
/// </summary>
public sealed class IntakeLinkSubmitTests : IClassFixture<IntakeLinkFactory>
{
    private readonly IntakeLinkFactory _factory;
    public IntakeLinkSubmitTests(IntakeLinkFactory factory) => _factory = factory;

    private async Task<(string Slug, Guid CustomerId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"lnk-{Guid.NewGuid():N}@x",
            Name = "Lnk",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-LNK-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"s-{Guid.NewGuid():N}"[..10];
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

    private static string StateFrom(HttpResponseMessage startResp)
    {
        var query = System.Web.HttpUtility.ParseQueryString(startResp.Headers.Location!.Query);
        return query["state"]!;
    }

    /// <summary>Start → callback koşturur; client'ın çerezinde kimlik kalır.</summary>
    private async Task<HttpClient> LinkAsync(string platform, string slug, IntakeLoginResult result)
    {
        if (platform == "youtube") _factory.Google.Result = result;
        else _factory.Facebook.Result = result;
        var client = NewClient();
        var startResp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/{platform}");
        await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={StateFrom(startResp)}&code=c");
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client, string slug)
        => AdminLoginHelper.ExtractAntiForgeryToken(
            await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync());

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

    [Fact]
    public async Task Bagli_youtube_tek_basina_yeter_channelId_ve_handle_yazilir()
    {
        var (slug, customerId) = await SeedAsync();
        var client = await LinkAsync("youtube", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCsubmit0001")));
        var callsBefore = _factory.Resolver.Calls.Count;

        // Hiçbir kullanıcı adı alanı gönderilmiyor — bağlı kimlik platform
        // şartını TEK BAŞINA sağlamalı.
        var resp = await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sub = await LatestAsync(customerId);
        sub!.YouTubeChannelId.Should().Be("UCsubmit0001");
        sub.YouTubeUsername.Should().Be("bilalkanal");
        // OAuth kimliği kanıtlı — resolver'a HİÇ gidilmemeli (kota + tutarlılık).
        _factory.Resolver.Calls.Count.Should().Be(callsBefore);
    }

    [Fact]
    public async Task Bagliyken_elle_yazilan_youtube_yok_sayilir()
    {
        var (slug, customerId) = await SeedAsync();
        var client = await LinkAsync("youtube", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCsubmit0002")));
        var callsBefore = _factory.Resolver.Calls.Count;

        // JS'siz istek / eski sekme elle değer gönderebilir. Bağlı kimlik yener:
        // çözülmez, doğrulanmaz, kayda girmez.
        var resp = await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug,
                ("Input.YouTubeUsername", "baskasininkanali")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sub = await LatestAsync(customerId);
        sub!.YouTubeChannelId.Should().Be("UCsubmit0002");
        sub.YouTubeUsername.Should().Be("bilalkanal");
        _factory.Resolver.Calls.Count.Should().Be(callsBefore);
    }

    [Fact]
    public async Task Bagli_facebook_gorunen_adi_bosluklu_kaydeder()
    {
        var (slug, customerId) = await SeedAsync();
        var client = await LinkAsync("facebook", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Musa Sevinç", null, null)));

        var resp = await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        // Görünen ad HandleValidator'dan GEÇMEZ: boşluk/Türkçe karakter serbest.
        // Chat satırı da görünen adla düşüyor — eşleşme bunun üzerinden.
        (await LatestAsync(customerId))!.FacebookUsername.Should().Be("Musa Sevinç");
    }

    [Fact]
    public async Task Kayit_sonrasi_kimlikler_temizlenir()
    {
        var (slug, _) = await SeedAsync();
        var client = await LinkAsync("youtube", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCsubmit0003")));

        (await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug)))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Aynı tarayıcıdan ikinci kayıt (örn. aile üyesi) öncekinin kimliğiyle
        // AÇILMAMALI — kimlik tek gönderimlik.
        var html = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        html.Should().NotContain("linked-chip");
    }
}
```

- [ ] **Step 2: Çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeLinkSubmitTests"`
Expected: FAIL — "en az bir platform" hatası (bağlı kimlik henüz sayılmıyor).

- [ ] **Step 3: OnPostSubmitAsync'i bağla** — beş nokta değişir:

**3a —** `Config` kontrolünden hemen sonra (satır ~170):

```csharp
        LoadLinkedIdentities();
```

**3b —** dört `Resolve` satırından yt ve fb'ninkiler (bağlıyken Resolve HİÇ
çağrılmaz — hata mesajı üretmesin, kota tüketmesin):

```csharp
        var (yt, channelIdFromUrl) = LinkedYouTube is null
            ? Resolve("Input.YouTubeUsername", HandleValidator.YouTube, Input.YouTubeUsername)
            : (null, null);
        var (ig, _) = Resolve("Input.InstagramUsername", HandleValidator.Instagram, Input.InstagramUsername);
        var (fb, _) = LinkedFacebook is null
            ? Resolve("Input.FacebookUsername", HandleValidator.Facebook, Input.FacebookUsername)
            : (null, null);
        var (tt, _) = Resolve("Input.TikTokUsername", HandleValidator.TikTok, Input.TikTokUsername);

        // Facebook: OAuth'tan gelen GÖRÜNEN ad. HandleValidator BYPASS bilinçli —
        // görünen ad boşluk/Türkçe karakter içerir ve chat satırı da görünen adla
        // düştüğü için eşleşme tam bu değer üzerinden.
        if (LinkedFacebook is not null)
        {
            var fbName = LinkedFacebook.DisplayName.Trim();
            fb = fbName.Length > 64 ? fbName[..64] : fbName;
        }
```

**3c —** "en az bir platform" kontrolü bağlı kimlikleri de saysın:

```csharp
        if (LinkedYouTube is null && LinkedFacebook is null &&
            yt is null && channelIdFromUrl is null && ig is null && fb is null && tt is null)
            ModelState.AddModelError("Input.InstagramUsername",
                "En az bir platform kullanıcı adı girin (Instagram, YouTube, Facebook veya TikTok).");
```

**3d —** YouTube çözüm bloğunun başı (satır ~228'deki `resolvedChannelId`
tanımından sonra) — bağlı kimlik varsa API'ye sorulmaz, onay kutusu istenmez
(müşteri kendi hesabıyla giriş yaptı, "bu kanal bana ait" zaten kanıtlı):

```csharp
        string? resolvedChannelId = null;
        var fromUrl = channelIdFromUrl is not null;
        YouTubeChannel? ch = null;

        if (LinkedYouTube is not null)
        {
            resolvedChannelId = LinkedYouTube.ChannelId;
            // Handle aynı normalize/doğrulama kapısından geçer (mevcut customUrl
            // kuralıyla bire bir); geçemezse sessizce boş kalır.
            var linkedHandle = HandleValidator.Normalize(LinkedYouTube.Handle);
            if (HandleValidator.Validate(HandleValidator.YouTube, linkedHandle) is null)
                yt = linkedHandle;
        }
        else if (fromUrl)
            ch = await _youTube.ResolveChannelIdAsync(channelIdFromUrl, ct);
        else if (yt is not null && HandleValidator.Validate(HandleValidator.YouTube, yt) is null)
            ch = await _youTube.ResolveHandleAsync(yt, ct);
```

(Devamındaki `if (ch is not null)` bloğu ve API-handle doldurma bloğu AYNEN
kalır — `ch` bağlı yolda null olduğu için kendiliğinden atlanır.)

`legacyUsername` satırına `resolvedChannelId` yedeği ekle (bağlı kanalın
handle'ı doğrulamadan geçemezse eski WPF sync'i yine bir kimlik görsün):

```csharp
        var legacyUsername = yt ?? ig ?? fb ?? tt ?? channelIdFromUrl ?? resolvedChannelId ?? "";
```

**3e —** `SaveSubmissionAsync` çağrısından sonra (WhatsAppUrl bloğundan önce):

```csharp
        // Kimlik tek gönderimlik: bırakılsaydı aynı tarayıcıdan ikinci kayıt
        // (örn. aile üyesi) öncekinin kanalıyla açılırdı.
        var linkNonce = Request.Cookies[IntakeLinkController.CookieName];
        if (!string.IsNullOrEmpty(linkNonce))
        {
            _linkStore.RemoveIdentity(linkNonce, "youtube");
            _linkStore.RemoveIdentity(linkNonce, "facebook");
        }
```

- [ ] **Step 4: Testleri geçir**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeLink"`
Expected: hepsi PASS.

- [ ] **Step 5: Regresyon** — mevcut form testleri kırılmamalı:

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "IntakeForm"`
Expected: hepsi PASS (bağlı kimlik yokken davranış bire bir eski davranış).

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs OrderDeck.LicenseServer.Tests/Pages/Public/IntakeLinkSubmitTests.cs
git commit -m "feat(kayit-formu): bagli kimlik gonderime islenir — elle girdiyi yener, tek gonderimlik"
```

---

### Task 9: Ops dokümanı + Google doğrulama gerekçesi

**Files:**
- Create: `docs/kayit-formu-giris.md`

- [ ] **Step 1: Dokümanı yaz** — içerik:

````markdown
# Kayıt formu — "Hesabınla bağlan" (Faz 2) kurulum ve yayına alma

## Env değişkenleri (VPS `.env`)

| Değişken | Anlamı |
|---|---|
| `IntakeLogin__GoogleClientId` | Google OAuth istemci kimliği (AYRI "Web application" client — masaüstünün client'ı DEĞİL) |
| `IntakeLogin__GoogleClientSecret` | Aynı client'ın sırrı |
| `IntakeLogin__YouTubeEnabled` | `true` yapılınca YouTube bağlama açılır (Google doğrulaması ONAYLANMADAN açma) |
| `IntakeLogin__FacebookEnabled` | `true` yapılınca Facebook bağlama açılır (review istemez, hemen açılabilir) |

Facebook app kimliği/sırrı MEVCUT `OrderDeck__Facebook__*` değişkenlerinden
okunur — yeni değişken yok. Redirect URI kodda sabit:
`https://orderdeckapp.com/musteri-kayit/baglanti-donusu`.

## Google Cloud kurulumu (proje 876199969087 — mevcut onaylı proje)

1. **APIs & Services → Credentials → Create Credentials → OAuth client ID →
   Web application.** Masaüstü client'ına DOKUNMA; sunucu akışı için ayrı client.
2. Authorized redirect URI: `https://orderdeckapp.com/musteri-kayit/baglanti-donusu`
3. **OAuth consent screen → Scopes → Add scope:**
   `https://www.googleapis.com/auth/youtube.readonly` ekle → doğrulama başvurusu
   tetiklenir (aşağıdaki gerekçe metnini kullan).
4. Doğrulama varlıkları önceki başvurudan hazır:
   `C:\Users\burak\Documents\OrderDeck\youtube-audit\` (demo video, ekran
   görüntüleri). Yeni video kayıt formundaki akışı göstermeli: forma gir →
   "Google ile bağla" → hesap seç → formda kanal adı çipi → kaydı gönder.
5. Onay gelene KADAR: kod prod'da, `IntakeLogin__YouTubeEnabled` yazılMAMIŞ
   (bayrak kapalı) — uçlar 404, formda link yok. Onay gelince `.env`'e
   `IntakeLogin__YouTubeEnabled=true` ekle + `docker compose up -d license-server`.

## Meta (app 3939617702835404) kurulumu

1. **Facebook Login → Settings → Valid OAuth Redirect URIs**'e
   `https://orderdeckapp.com/musteri-kayit/baglanti-donusu` EKLE (mevcut
   masaüstü redirect'i kalır).
2. `public_profile` için App Review GEREKMEZ. `.env`'e
   `IntakeLogin__FacebookEnabled=true` → `docker compose up -d license-server`.

## Google doğrulama başvurusu — gerekçe metni

**Scope justification (EN — başvuru formuna):**

> OrderDeck is a live-stream commerce tool for Turkish broadcasters. Viewers
> buy items by typing a product code into the live chat; the broadcaster then
> matches the chat message to a shipping-info registration the viewer filled
> in on our public web form.
>
> Today viewers type their YouTube handle into that form by hand, and roughly
> half of the entries are misspelled or incomplete, so their chat messages can
> never be matched to their shipping registration and their orders are lost.
>
> We request the `youtube.readonly` scope solely so a viewer can press
> "Sign in with Google" on the registration form and we can read the channel
> title, handle (customUrl) and channel ID of THEIR OWN channel via
> `channels.list(mine=true)` — one API call at sign-in, nothing else. This is
> the minimum scope that includes `channels.list` with `mine=true`. We do not
> read subscriptions, playlists, videos, analytics or any other data; we do
> not store the OAuth tokens (the access token is used once, server-side, and
> discarded); we never post, modify or delete anything.
>
> The retrieved channel identity is stored only as part of that viewer's own
> shipping registration, visible only to the broadcaster they are registering
> with, and is covered by our privacy policy at
> https://orderdeckapp.com/privacy.

**Aynı metnin TR özeti (kendi kaydımız için):** izleyici formda "Google ile
bağla"ya basar; `channels.list(mine=true)` ile YALNIZ kendi kanalının adı,
handle'ı ve kimliği okunur; token saklanmaz, başka veri okunmaz, hiçbir şey
yazılmaz. Amaç: elle yazılan hatalı kullanıcı adları yüzünden sohbetle
eşleşemeyen kayıtların (taban ölçüm: hareketsiz oran %46-95, bkz.
`docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md`) önüne geçmek.

## Yayın sonrası doğrulama

1. Deploy sonrası `IntakeLogin__*` yazılmadan: form eskisi gibi, `/baglan/...`
   404 (karanlık deploy kanıtı).
2. Facebook açılınca: gerçek telefonla forma gir → Facebook ile bağla →
   çipte adını gör → kaydı gönder → panelde kaydın `FacebookUsername`'inde
   görünen adı doğrula.
3. YouTube onayı gelince aynı akış + WPF müşteri listesinde kanal `UC…`
   channelId'siyle eşleşiyor mu kontrol et.
4. Bir süre sonra `docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md`
   sorgularını tekrar koştur — oran düşüyor mu, özelliğin varlık sebebi bu.
````

- [ ] **Step 2: Commit**

```bash
git add docs/kayit-formu-giris.md
git commit -m "docs(kayit-formu): giris fazi kurulum rehberi ve Google dogrulama gerekcesi"
```

---

## Doğrulama (plan sonu)

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

- Testcontainers gerektiren testler için Docker açık olmalı (CLAUDE.md).
- WPF tarafına dokunulmadı — `OrderDeck.Tests` koşturmak şart değil, ama
  şüphede `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`.
- PR: `feat/kayit-formu-giris` → master, başlık
  `feat(kayit-formu): Google/Facebook ile hesap baglama (karanlik deploy)`.
  **Merge kullanıcıya ait.** Merge sonrası VPS'te env İŞLEMİ YOK — bayraklar
  yazılmadıkça özellik kapalı.

## Self-Review notları

- Spec kapsaması: başlatma (T5), dönüş (T6), UI/banner/unlink/taslak (T7),
  gönderim (T8), ops+gerekçe (T9) — kenar durum tablosundaki her satırın
  testi var; "bayrak kapalı" hem uç (T5) hem UI (T7) düzeyinde çivili.
- Tip tutarlılığı: `IntakeLoginResult(bool Ok, string? ErrorCode,
  IntakeLinkedIdentity? Identity)` ve `IntakeLinkedIdentity(DisplayName,
  Handle, ChannelId)` T2'de tanımlanıp T3-T8'de aynı imzayla kullanılıyor;
  `IntakeLinkController.CookieName` T5'te tanımlı, T7/T8 oradan okuyor.
- Bilinçli sapmalar karar kaydında (sessionStorage, görünen ad validator
  bypass'ı, unlink'in `intake-form-submit` bütçesini paylaşması).

