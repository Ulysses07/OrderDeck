# WhatsApp Embedded Signup (Sunucu) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bir yayıncı panelde "WhatsApp'ı bağla" dediğinde, Meta'nın Embedded Signup akışından dönen `code` sunucuda kalıcı tenant token'ına çevrilsin ve o lisans için çalışır bir `WhatsAppAccount` satırı oluşsun — elle token yapıştırmadan.

**Architecture:** Panel (tarayıcı) FB JS SDK'yı açar, akış bitince `code` + `waba_id` + `phone_number_id` alır ve bunları **tek bir panel ucuna** POST eder. Sunucu 4 Graph çağrısını sırayla yapar (code→token, app→WABA aboneliği, numara bilgisi, numara register) ve sonucu `WhatsAppAccount`'a yazar. Token asla tarayıcıya dönmez; `code` asla saklanmaz (Meta'da **30 sn** ömrü var). Mevcut admin ucu (elle bağlama) yedek yol olarak kalır ve ikisi **aynı upsert** kodunu paylaşır.

**Tech Stack:** ASP.NET Core 10, EF Core 10, `IDataProtector`, typed `HttpClient`, xUnit + FluentAssertions, `ApiFactory` (WebApplicationFactory + EF InMemory).

**Kapsam dışı (ayrı plan):** Panel arayüzü (FB JS SDK butonu) `OrderDeck-Mobile` reposunda — bu plan bitince ayrı plan yazılacak. Meta tarafı ayarları (App Mode = Live, Configuration ID üretimi, App Review videoları) kod değil, konsol işi.

---

## ⚠️ Uygulama notu — bu plan artık kodun kaynağı DEĞİL

Plan uygulandı ve inceleme turlarında **bilerek değiştirildi**. Aşağıdaki noktalarda kodu oku, planı değil (`Services/WhatsApp/`, `Controllers/Panel/PanelWhatsAppAccountController.cs`):

1. **Code takası GET + sorgu dizesi değil, POST + form gövdesi.** Plandaki `GET /oauth/access_token?...&client_secret=...` **reddedildi**: bu istemci `AddHttpClient` ile kayıtlı, yani `LoggingHttpMessageHandler` istek URI'sini Information seviyesinde log'a yazıyor ve `AddHttpClientInstrumentation` aynı URI'yi ikinci kez tüketiyor. App secret'ı sorgu dizesine koymak onu iki ayrı log yoluna teslim ederdi. **Aşağıdaki kod bloklarında ve Graph tablosunun 1. satırında bu eski tasarım duruyor — kopyalama.**
2. **Meta'nın ham yanıt gövdesi çağırana dönmüyor.** Beklenmedik yanıtlar sabit metne (`Opaque<T>`) indirgeniyor; code-takası gövdesi sunucu log'una da yazılmıyor (form-encoded düşerse token açıkta olurdu).
3. **PIN numaraya ait, lisansa değil.** `ResolvePinAsync` saklı PIN'i yalnız `PhoneNumberId` de eşleşiyorsa yeniden kullanıyor; Meta reddederse (`register.Ok == false`) PIN hiç yazılmıyor.
4. **Ek korumalar:** Graph'tan önce çapraz kiracı sahiplik kontrolü (409), `^[0-9]{1,32}$` id doğrulaması, personel operatöre `owner-only` 403, token ile PIN için ayrı `IDataProtector` purpose'ları, yapılandırılmamış sunucuda `signup-config` → 503.

**Açık kalanlar (ayrı dal):** `waba_id` ↔ `phone_number_id` eşleşmesi doğrulanmıyor; "numarayı kopar" ucu yok.

---

## Referans: Meta Graph çağrıları

Hepsi `https://graph.facebook.com/{version}` altında (`WhatsAppOptions.GraphBaseUrl` + `GraphApiVersion`, bugün `v25.0`).

| # | Çağrı | Kimlik |
|---|---|---|
| 1 | ~~`GET /oauth/access_token?client_id=...&client_secret=...&code=...`~~ → uygulamada **`POST /oauth/access_token`**, aynı üç alan **form gövdesinde** (bkz. yukarıdaki uygulama notu) → `{ "access_token": "...", "token_type": "bearer" }` | app kimliği (gövdede) |
| 2 | `POST /{wabaId}/subscribed_apps` → `{ "success": true }` | Bearer = iş token'ı |
| 3 | `GET /{phoneNumberId}?fields=display_phone_number,verified_name` → `{ "display_phone_number": "+90 555 111 22 33", "verified_name": "..." }` | Bearer = iş token'ı |
| 4 | `POST /{phoneNumberId}/register` gövde `{ "messaging_product": "whatsapp", "pin": "123456" }` → `{ "success": true }` | Bearer = iş token'ı |

Hata gövdesi her zaman `{ "error": { "code": 190, "message": "..." } }` — mevcut `CloudApiWhatsAppSender.PostAsync` ile aynı şekil.

**PIN neden saklanıyor:** (4) numaranın iki adımlı doğrulama PIN'ini *belirler*. İleride numarayı yeniden register etmek gerekirse Meta **aynı PIN'i** ister. Saklamazsak yayıncı kendi numarasından kilitlenir ve tek çıkış Meta desteği olur. Bu yüzden PIN de token gibi `IDataProtector` ile şifrelenip satırda tutuluyor.

---

## File Structure

**Oluşturulacak:**
- `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOnboardingClient.cs` — 4 Graph çağrısı, başka hiçbir şey. `IWhatsAppOnboardingClient` + `GraphResult<T>` + `WhatsAppPhoneNumberInfo` burada.
- `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppAccountController.cs` — panel ucu (POST bağla, GET durum).
- `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppOnboardingClientTests.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppAccountControllerTests.cs`

**Değiştirilecek:**
- `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOptions.cs` — `AppId` + `EmbeddedSignupConfigId`.
- `OrderDeck.LicenseServer/Domain/WhatsAppAccount.cs` — `TwoStepPinProtected`.
- `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppAccountService.cs` — paylaşılan `UpsertAsync`.
- `OrderDeck.LicenseServer/Controllers/Licenses/AdminWhatsAppAccountsController.cs:94-112` — `UpsertAsync`'e devret.
- `OrderDeck.LicenseServer/Program.cs:131-148` — DI.

---

### Task 1: Config alanları (AppId + Configuration ID)

`oauth/access_token` çağrısı `client_id` ister; bugün `WhatsAppOptions`'ta yalnız `AppSecret` var. Configuration ID panelin JS SDK'ya vereceği değer — sunucu onu yalnız panele **söyler**, kendisi kullanmaz.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOptions.cs:28`

- [x] **Step 1: Alanları ekle**

`WhatsAppOptions.cs` içinde `AppSecret` özelliğinin hemen altına:

```csharp
    /// <summary>Meta App ID. Embedded Signup'ta <c>oauth/access_token</c> çağrısının
    /// <c>client_id</c>'si. WhatsApp app'i Facebook chat app'inden AYRI — bu değeri
    /// oradan kopyalama.</summary>
    public string AppId { get; set; } = "";

    /// <summary>Facebook Login for Business "Configuration ID" — panelin JS SDK'ya
    /// verdiği <c>config_id</c>. Sunucu bunu kullanmaz, yalnız panele bildirir;
    /// burada durmasının sebebi tek bir WhatsApp yapılandırma bloğu olması.</summary>
    public string EmbeddedSignupConfigId { get; set; } = "";
```

- [x] **Step 2: Derle**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: Build succeeded, 0 error.

- [x] **Step 3: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOptions.cs
git commit -m "feat(whatsapp): Embedded Signup için AppId ve config id ayarları"
```

---

### Task 2: `WhatsAppAccount.TwoStepPinProtected` + migration

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/WhatsAppAccount.cs:32`
- Create: `OrderDeck.LicenseServer/Data/Migrations/<timestamp>_AddWhatsAppTwoStepPin.cs` (EF üretir)

- [x] **Step 1: Alanı ekle**

`WhatsAppAccount.cs` içinde `AccessTokenProtected` özelliğinin altına:

```csharp
    /// <summary>Numaranın iki adımlı doğrulama PIN'i — <c>IDataProtector</c> ile
    /// şifreli. Register sırasında biz belirliyoruz; Meta yeniden register'da
    /// AYNI PIN'i istiyor, saklamazsak yayıncı kendi numarasından kilitlenir.
    /// Elle bağlanan hesaplarda null (PIN'i biz belirlememişiz).</summary>
    public string? TwoStepPinProtected { get; set; }
```

- [x] **Step 2: Migration üret**

Run: `dotnet ef migrations add AddWhatsAppTwoStepPin --project OrderDeck.LicenseServer --output-dir Data/Migrations`
Expected: iki dosya oluşur (`*_AddWhatsAppTwoStepPin.cs` + `.Designer.cs`) ve `LicenseDbContextModelSnapshot.cs` güncellenir.

- [x] **Step 3: Migration'ın gerçekten tek sütun eklediğini gör**

Run: `git diff --stat && cat OrderDeck.LicenseServer/Data/Migrations/*_AddWhatsAppTwoStepPin.cs`
Expected: `Up` içinde yalnız `migrationBuilder.AddColumn<string>(name: "TwoStepPinProtected", table: "WhatsAppAccounts", nullable: true)`. Başka tabloya dokunuyorsa DUR — model ile snapshot arasında ilgisiz bir sapma var, önce onu araştır.

- [x] **Step 4: Sunucu testleri hâlâ yeşil**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: Failed: 0.

- [x] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/WhatsAppAccount.cs OrderDeck.LicenseServer/Data/Migrations
git commit -m "feat(whatsapp): numara PIN'ini şifreli sakla"
```

---

### Task 3: `WhatsAppOnboardingClient` — code takası

Dört Graph çağrısını tek tek TDD ile ekliyoruz. Önce sözleşme + ilk çağrı.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOnboardingClient.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppOnboardingClientTests.cs`

- [x] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppOnboardingClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Embedded Signup'ın Graph ayağı. Bu sınıf gerçek bir yayıncı bağlanana kadar
/// prod'da hiç çalışmıyor; ilk canlı denemede yanlış parametre adı yüzünden
/// 30 saniyelik <c>code</c>'u yakmamak için istekler burada birebir doğrulanıyor.
/// </summary>
public sealed class WhatsAppOnboardingClientTests
{
    /// <summary>Sıraya konmuş yanıtları teker teker döner ve istekleri kaydeder —
    /// onboarding tek çağrı değil, çağrı ZİNCİRİ; tek yanıtlı sahte yetmez.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();

        public ScriptedHandler(params (HttpStatusCode, string)[] script) =>
            _script = new Queue<(HttpStatusCode, string)>(script);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static WhatsAppOnboardingClient Client(ScriptedHandler handler)
    {
        var opt = Options.Create(new WhatsAppOptions
        {
            GraphBaseUrl = "https://graph.test",
            GraphApiVersion = "v25.0",
            AppId = "APP_1",
            AppSecret = "SECRET_1",
        });
        return new WhatsAppOnboardingClient(
            new HttpClient(handler), opt, NullLogger<WhatsAppOnboardingClient>.Instance);
    }

    [Fact]
    public async Task Exchanging_the_code_sends_app_credentials_and_returns_the_business_token()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, """{ "access_token": "BIZ_TOKEN", "token_type": "bearer" }"""));

        var result = await Client(handler).ExchangeCodeAsync("CODE_123", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Value.Should().Be("BIZ_TOKEN");

        // UYGULAMADA DEĞİŞTİ: secret sorgu dizesinde değil, form gövdesinde
        // gidiyor (bkz. baştaki uygulama notu). Sevk edilen assert şu:
        var sent = handler.Requests[0];
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.ToString().Should().Be("https://graph.test/v25.0/oauth/access_token");
        var form = await sent.Content!.ReadAsStringAsync();
        form.Should().Contain("client_id=APP_1").And.Contain("code=CODE_123");
    }

    [Fact]
    public async Task A_meta_error_becomes_a_structured_failure_not_an_exception()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, """
            { "error": { "code": 100, "message": "Invalid verification code format." } }
            """));

        var result = await Client(handler).ExchangeCodeAsync("EXPIRED", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("100");
        result.ErrorMessage.Should().Contain("Invalid verification code");
    }
}
```

- [x] **Step 2: Testin derlenmediğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~WhatsAppOnboardingClientTests`
Expected: derleme hatası — `WhatsAppOnboardingClient` bulunamıyor (CS0246).

- [x] **Step 3: İstemciyi yaz**

`OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOnboardingClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Graph çağrısının yapısal sonucu. Meta hatası fırlatılmaz — çağıran
/// (panel ucu) hangi adımda takıldığını kullanıcıya söylemek zorunda.</summary>
public sealed record GraphResult<T>(T? Value, string? ErrorCode, string? ErrorMessage)
{
    public bool Ok => ErrorCode is null;
    public static GraphResult<T> Success(T value) => new(value, null, null);
    public static GraphResult<T> Failure(string? code, string? message) =>
        new(default, string.IsNullOrWhiteSpace(code) ? "unknown" : code, message);
}

/// <summary>Numaranın Meta'daki görünen hâli — yalnız UI için.</summary>
public sealed record WhatsAppPhoneNumberInfo(string DisplayPhoneNumber, string? VerifiedName);

/// <summary>Embedded Signup'ın Graph ayağı. Yalnız HTTP yapar; DB'ye dokunmaz,
/// karar vermez — böylece panel ucu testlerinde tek parça sahtelenebilir.</summary>
public interface IWhatsAppOnboardingClient
{
    /// <summary>Embedded Signup'tan dönen <c>code</c>'u tenant'ın kalıcı iş
    /// token'ına çevirir. Kod 30 sn yaşıyor — çağrı gecikmeden yapılmalı.</summary>
    Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken ct);
}

public sealed class WhatsAppOnboardingClient : IWhatsAppOnboardingClient
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<WhatsAppOnboardingClient> _log;

    public WhatsAppOnboardingClient(
        HttpClient http, IOptions<WhatsAppOptions> opt, ILogger<WhatsAppOnboardingClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    private string Root => $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}";

    public async Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        // UYGULAMADA DEĞİŞTİ: app secret ve tek kullanımlık code GÖVDEDE gider,
        // sorgu dizesinde DEĞİL — istek URI'si iki ayrı log yoluna düşüyor
        // (bkz. baştaki uygulama notu). Sevk edilen hâli:
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _opt.AppId,
                ["client_secret"] = _opt.AppSecret,
                ["code"] = code,
            }),
        };
        return await SendAsync(req, "code-exchange", root =>
            root.TryGetProperty("access_token", out var t) ? t.GetString() : null, ct);
    }

    /// <summary>Ortak gövde: gönder, JSON'u ayrıştır, Meta hatasını yapısal sonuca
    /// çevir. <paramref name="step"/> yalnız log içindir — token/code ASLA loglanmaz.
    ///
    /// <para><c>where T : class</c> şart: "okuyucu null döndüyse şekil beklenmedik"
    /// kuralı değer tiplerinde işlemez (<c>false</c> asla null olmaz). Başarı
    /// bayrağı dönen uçlar için <see cref="SendSuccessAsync"/> var.</para></summary>
    private async Task<GraphResult<T>> SendAsync<T>(
        HttpRequestMessage req, string step, Func<JsonElement, T?> read, CancellationToken ct)
        where T : class
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WhatsApp onboarding ağ hatası ({Step})", step);
            return GraphResult<T>.Failure("network", ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                _log.LogWarning("WhatsApp onboarding hatası ({Step}, {Code}): {Msg}", step, code, msg);
                return GraphResult<T>.Failure(code, msg);
            }

            if (!resp.IsSuccessStatusCode)
                return GraphResult<T>.Failure(((int)resp.StatusCode).ToString(), body);

            var value = read(root);
            return value is null
                ? GraphResult<T>.Failure("unexpected-shape", body)
                : GraphResult<T>.Success(value);
        }
        catch (JsonException)
        {
            return GraphResult<T>.Failure(((int)resp.StatusCode).ToString(), body);
        }
    }
}
```

- [x] **Step 4: Testlerin geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~WhatsAppOnboardingClientTests`
Expected: Passed: 2, Failed: 0.

- [x] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOnboardingClient.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppOnboardingClientTests.cs
git commit -m "feat(whatsapp): Embedded Signup code'unu iş token'ına çevir"
```

---

### Task 4: Kalan üç Graph çağrısı (abone ol, numarayı oku, register)

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOnboardingClient.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppOnboardingClientTests.cs`

- [x] **Step 1: Failing testleri yaz**

`WhatsAppOnboardingClientTests.cs` içindeki son `}` öncesine ekle:

```csharp
    [Fact]
    public async Task Subscribing_the_app_posts_to_the_waba_with_the_business_token()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{ "success": true }"""));

        var result = await Client(handler)
            .SubscribeAppAsync("WABA_9", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/WABA_9/subscribed_apps");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("BIZ_TOKEN");
    }

    [Fact]
    public async Task Reading_the_phone_number_asks_only_for_the_display_fields()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """
            { "display_phone_number": "+90 555 111 22 33", "verified_name": "Emar Global" }
            """));

        var result = await Client(handler)
            .ReadPhoneNumberAsync("PNID_7", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Value!.DisplayPhoneNumber.Should().Be("+90 555 111 22 33");
        result.Value.VerifiedName.Should().Be("Emar Global");
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/PNID_7?fields=display_phone_number,verified_name");
    }

    [Fact]
    public async Task Registering_the_number_sends_the_pin_in_the_body()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{ "success": true }"""));

        var result = await Client(handler)
            .RegisterPhoneNumberAsync("PNID_7", "123456", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/PNID_7/register");
        handler.Bodies[0].Should().Contain("\"messaging_product\":\"whatsapp\"");
        handler.Bodies[0].Should().Contain("\"pin\":\"123456\"");
    }
```

- [x] **Step 2: Testlerin derlenmediğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~WhatsAppOnboardingClientTests`
Expected: CS1061 — `SubscribeAppAsync` / `ReadPhoneNumberAsync` / `RegisterPhoneNumberAsync` yok.

- [x] **Step 3: Arayüze üç metodu ekle**

`WhatsAppOnboardingClient.cs` içinde `IWhatsAppOnboardingClient`'a, `ExchangeCodeAsync` bildiriminin altına:

```csharp
    /// <summary>Uygulamamızı müşterinin WABA'sına abone eder — bu yapılmazsa
    /// o numaraya gelen mesajlar webhook'umuza HİÇ düşmez.</summary>
    Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string businessToken, CancellationToken ct);

    /// <summary>Numaranın görünen hâli (UI için).</summary>
    Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
        string phoneNumberId, string businessToken, CancellationToken ct);

    /// <summary>Numarayı Cloud API'ye kaydeder ve iki adımlı PIN'i belirler.
    /// Numara zaten kayıtlıysa Meta hata döner — çağıran bunu ölümcül saymamalı.</summary>
    Task<GraphResult<bool>> RegisterPhoneNumberAsync(
        string phoneNumberId, string pin, string businessToken, CancellationToken ct);
```

- [x] **Step 4: Gövdeleri yaz**

`WhatsAppOnboardingClient` sınıfında `ExchangeCodeAsync`'in altına:

```csharp
    public async Task<GraphResult<bool>> SubscribeAppAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/{wabaId}/subscribed_apps");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        return await SendSuccessAsync(req, "subscribe-app", ct);
    }

    public async Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
        string phoneNumberId, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"{Root}/{phoneNumberId}?fields=display_phone_number,verified_name");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);

        return await SendAsync(req, "read-phone-number", root =>
        {
            if (!root.TryGetProperty("display_phone_number", out var d)) return null;
            var display = d.GetString();
            if (string.IsNullOrWhiteSpace(display)) return null;
            var name = root.TryGetProperty("verified_name", out var v) ? v.GetString() : null;
            return new WhatsAppPhoneNumberInfo(display, string.IsNullOrWhiteSpace(name) ? null : name);
        }, ct);
    }

    public async Task<GraphResult<bool>> RegisterPhoneNumberAsync(
        string phoneNumberId, string pin, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/{phoneNumberId}/register")
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", pin }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        return await SendSuccessAsync(req, "register-number", ct);
    }

    /// <summary><c>{ "success": true }</c> dönen uçlar için ortak yol. İçeride
    /// <c>string</c> ile çalışıyor çünkü <see cref="SendAsync"/> "okuyucu null
    /// döndü = beklenmedik şekil" kuralına dayanıyor ve <c>bool</c> null olamaz.</summary>
    private async Task<GraphResult<bool>> SendSuccessAsync(
        HttpRequestMessage req, string step, CancellationToken ct)
    {
        var result = await SendAsync(req, step, root =>
            root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
                ? "ok" : null, ct);

        return result.Ok
            ? GraphResult<bool>.Success(true)
            : GraphResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
    }
```

- [x] **Step 5: Testlerin geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~WhatsAppOnboardingClientTests`
Expected: Passed: 5, Failed: 0.

- [x] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppOnboardingClient.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppOnboardingClientTests.cs
git commit -m "feat(whatsapp): WABA aboneliği, numara okuma ve register çağrıları"
```

---

### Task 5: Paylaşılan `UpsertAsync` — iki bağlama yolu tek kural

Bugün "aynı Phone Number ID iki lisansa bağlanamaz" kuralı yalnız admin controller'ında ([AdminWhatsAppAccountsController.cs:85-92](../../../OrderDeck.LicenseServer/Controllers/Licenses/AdminWhatsAppAccountsController.cs)). Panel ucu bunu kopyalarsa iki tanım zamanla ayrışır ve webhook yönlendirmesi belirsizleşir.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppAccountService.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppAccountUpsertTests.cs` (create)

- [x] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppAccountUpsertTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Hesap bağlama kuralı TEK yerde: elle bağlayan admin ucu ile Embedded Signup
/// ucu aynı gövdeyi çağırır. İki kopya olsaydı biri "PhoneNumberId başkasında"
/// kontrolünü kaybettiğinde webhook'lar sessizce yanlış tenant'a giderdi.
/// </summary>
public sealed class WhatsAppAccountUpsertTests
{
    private static WhatsAppAccountService Service(out LicenseDbContext db)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase("wa-upsert-" + Guid.NewGuid().ToString("N"))
            .Options;
        db = new LicenseDbContext(options);
        return new WhatsAppAccountService(
            db, DataProtectionProvider.Create("tests"), Options.Create(new WhatsAppOptions()));
    }

    private static WhatsAppAccountUpsert Input(string pnid) =>
        new("WABA_1", pnid, "+90 555 111 22 33", "TOKEN", "Emar", "123456");

    [Fact]
    public async Task Connecting_twice_updates_the_same_row_instead_of_adding_one()
    {
        var svc = Service(out var db);
        using var _ = db;
        var licenseId = Guid.NewGuid();

        (await svc.UpsertAsync(licenseId, Input("PNID_1"), CancellationToken.None)).Ok
            .Should().BeTrue();
        (await svc.UpsertAsync(licenseId, Input("PNID_1"), CancellationToken.None)).Ok
            .Should().BeTrue();

        db.WhatsAppAccounts.Count(a => a.LicenseId == licenseId).Should().Be(1);
    }

    [Fact]
    public async Task A_number_already_bound_to_another_license_is_refused()
    {
        var svc = Service(out var db);
        using var _ = db;

        await svc.UpsertAsync(Guid.NewGuid(), Input("PNID_SHARED"), CancellationToken.None);
        var second = await svc.UpsertAsync(Guid.NewGuid(), Input("PNID_SHARED"), CancellationToken.None);

        // Kabul edilseydi gelen webhook'un hangi lisansa ait olduğu belirsiz kalırdı.
        second.Ok.Should().BeFalse();
        second.Conflict.Should().BeTrue();
    }

    [Fact]
    public async Task The_token_and_the_pin_are_never_stored_in_clear_text()
    {
        var svc = Service(out var db);
        using var _ = db;
        var licenseId = Guid.NewGuid();

        await svc.UpsertAsync(licenseId, Input("PNID_2"), CancellationToken.None);

        var row = db.WhatsAppAccounts.Single(a => a.LicenseId == licenseId);
        row.AccessTokenProtected.Should().NotContain("TOKEN");
        row.TwoStepPinProtected.Should().NotBeNull().And.NotContain("123456");
        svc.TryUnprotectToken(row.AccessTokenProtected).Should().Be("TOKEN");
    }
}
```

- [x] **Step 2: Testin derlenmediğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~WhatsAppAccountUpsertTests`
Expected: CS0246 — `WhatsAppAccountUpsert` yok.

- [x] **Step 3: Servise upsert'i ekle**

`WhatsAppAccountService.cs` içinde `namespace` satırının altına, sınıfın dışına:

```csharp
/// <summary>Bir hesabı bağlamak için gereken her şey. <paramref name="TwoStepPin"/>
/// yalnız Embedded Signup'ta dolu — elle bağlamada PIN'i biz belirlemiyoruz.</summary>
public sealed record WhatsAppAccountUpsert(
    string WabaId,
    string PhoneNumberId,
    string DisplayPhoneNumber,
    string AccessToken,
    string? VerifiedName,
    string? TwoStepPin);

/// <summary><see cref="WhatsAppAccountService.UpsertAsync"/> sonucu.
/// <paramref name="Conflict"/> = numara başka lisansta (çağıran 409 döner).</summary>
public sealed record WhatsAppAccountUpsertResult(bool Ok, bool Conflict, WhatsAppAccount? Account)
{
    public static WhatsAppAccountUpsertResult Success(WhatsAppAccount a) => new(true, false, a);
    public static readonly WhatsAppAccountUpsertResult Taken = new(false, true, null);
}
```

Ve `WhatsAppAccountService` sınıfının içine, `ResolveSendContextAsync`'in altına:

```csharp
    /// <summary>
    /// Lisansın WhatsApp hesabını oluşturur ya da günceller. Elle bağlayan admin
    /// ucu ile Embedded Signup ucunun ORTAK gövdesi — "aynı Phone Number ID iki
    /// lisansa bağlanamaz" kuralı burada tek kopya duruyor.
    /// </summary>
    public async Task<WhatsAppAccountUpsertResult> UpsertAsync(
        Guid licenseId, WhatsAppAccountUpsert input, CancellationToken ct)
    {
        var phoneNumberId = input.PhoneNumberId.Trim();

        var owner = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.PhoneNumberId == phoneNumberId, ct);
        if (owner is not null && owner.LicenseId != licenseId)
            return WhatsAppAccountUpsertResult.Taken;

        var account = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
        var now = DateTimeOffset.UtcNow;

        if (account is null)
        {
            account = new WhatsAppAccount { Id = Guid.NewGuid(), LicenseId = licenseId, ConnectedAt = now };
            _db.WhatsAppAccounts.Add(account);
        }

        account.WabaId = input.WabaId.Trim();
        account.PhoneNumberId = phoneNumberId;
        account.DisplayPhoneNumber = input.DisplayPhoneNumber;
        account.VerifiedName = string.IsNullOrWhiteSpace(input.VerifiedName) ? null : input.VerifiedName.Trim();
        account.AccessTokenProtected = ProtectToken(input.AccessToken.Trim());
        // PIN yalnız yeni geldiyse yazılır: elle bağlama (PIN'siz) bir Embedded
        // Signup'ın bıraktığı PIN'i silmemeli, yoksa numara yeniden register
        // edilemez hâle gelir.
        if (!string.IsNullOrWhiteSpace(input.TwoStepPin))
            account.TwoStepPinProtected = ProtectToken(input.TwoStepPin);
        account.Status = "active";
        account.LastError = null;
        account.DisconnectedAt = null;

        await _db.SaveChangesAsync(ct);
        return WhatsAppAccountUpsertResult.Success(account);
    }
```

- [x] **Step 4: Testlerin geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~WhatsAppAccountUpsertTests`
Expected: Passed: 3, Failed: 0.

- [x] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppAccountService.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppAccountUpsertTests.cs
git commit -m "refactor(whatsapp): hesap bağlama kuralını tek gövdede topla"
```

---

### Task 6: Admin ucunu ortak gövdeye devret

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/AdminWhatsAppAccountsController.cs:81-117`

- [x] **Step 1: Gövdeyi değiştir**

`Connect` metodunda `var phoneNumberId = req.PhoneNumberId.Trim();` satırından metodun `return Ok(...)` satırına kadar olan bloğu şununla değiştir:

```csharp
        var result = await _accounts.UpsertAsync(
            licenseId,
            new WhatsAppAccountUpsert(
                req.WabaId, req.PhoneNumberId, display, req.AccessToken, req.VerifiedName, null),
            ct);

        if (result.Conflict)
        {
            return Problem(
                title: "phone-number-id-taken", statusCode: 409,
                detail: "Bu Phone Number ID başka bir lisansa bağlı.");
        }

        var account = result.Account!;

        _log.LogInformation(
            "WhatsApp hesabı bağlandı: lisans {LicenseId}, phone_number_id {Pnid}",
            licenseId, account.PhoneNumberId);

        return Ok(ToResponse(account, req.AccessToken));
```

- [x] **Step 2: Artık kullanılmayan `using` ve alanları temizle**

`_db` hâlâ `Licenses.AnyAsync` ve `Get` için gerekli — bırak. Başka bir şey silme.

- [x] **Step 3: Mevcut admin testlerinin hâlâ yeşil olduğunu gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~WhatsAppAccount`
Expected: Failed: 0. Kırmızı varsa refactor davranışı değiştirmiş demektir — geri al, farkı bul.

- [x] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/AdminWhatsAppAccountsController.cs
git commit -m "refactor(whatsapp): admin bağlama ucu ortak upsert'i kullansın"
```

---

### Task 7: Panel ucu — Embedded Signup'ı tamamla

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppAccountController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppAccountControllerTests.cs`

**Yetki notu:** `StockStaffScopeFilter` varsayılan-kapalı; `[AllowStockStaff]` koymadığımız için stok elemanı bu uca zaten erişemez. Ek kod gerekmiyor.

- [x] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppAccountControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Embedded Signup'ın uçtan uca panel yolu. Graph sahtelenmiş — burada
/// doğrulanan şey Meta'nın davranışı değil, BİZİM sıralamamız: token takası
/// tutmadan satır açılmamalı, abonelik atlanmamalı, token tarayıcıya dönmemeli.
/// </summary>
public sealed class PanelWhatsAppAccountControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelWhatsAppAccountControllerTests(ApiFactory factory) => _factory = factory;

    /// <summary>Her adımı ayrı ayrı başarılı/başarısız kılabilen sahte Graph.</summary>
    private sealed class FakeOnboardingClient : IWhatsAppOnboardingClient
    {
        public GraphResult<string> Exchange = GraphResult<string>.Success("BIZ_TOKEN");
        public GraphResult<bool> Subscribe = GraphResult<bool>.Success(true);
        public GraphResult<WhatsAppPhoneNumberInfo> Phone =
            GraphResult<WhatsAppPhoneNumberInfo>.Success(
                new WhatsAppPhoneNumberInfo("+90 555 111 22 33", "Emar Global"));
        public GraphResult<bool> Register = GraphResult<bool>.Success(true);

        public string? SeenCode;
        public string? SeenWabaId;
        public string? SeenPin;

        public Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken ct)
        {
            SeenCode = code;
            return Task.FromResult(Exchange);
        }

        public Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string token, CancellationToken ct)
        {
            SeenWabaId = wabaId;
            return Task.FromResult(Subscribe);
        }

        public Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
            string phoneNumberId, string token, CancellationToken ct) => Task.FromResult(Phone);

        public Task<GraphResult<bool>> RegisterPhoneNumberAsync(
            string phoneNumberId, string pin, string token, CancellationToken ct)
        {
            SeenPin = pin;
            return Task.FromResult(Register);
        }
    }

    private sealed record Seed(HttpClient Client, Guid LicenseId, FakeOnboardingClient Graph);

    private async Task<Seed> SeedAsync()
    {
        var graph = new FakeOnboardingClient();
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
                s.AddSingleton<IWhatsAppOnboardingClient>(graph)));

        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PWA-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        return new Seed(client, license.Id, graph);
    }

    private static object Body => new { code = "CODE_1", wabaId = "WABA_1", phoneNumberId = "PNID_1" };

    [Fact]
    public async Task A_completed_signup_connects_the_account_without_revealing_the_token()
    {
        var seed = await SeedAsync();

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().NotContain("BIZ_TOKEN");
        json.Should().Contain("+90 555 111 22 33");

        seed.Graph.SeenCode.Should().Be("CODE_1");
        seed.Graph.SeenWabaId.Should().Be("WABA_1");
        seed.Graph.SeenPin.Should().HaveLength(6).And.MatchRegex("^[0-9]{6}$");
    }

    [Fact]
    public async Task A_failed_code_exchange_leaves_no_account_behind()
    {
        var seed = await SeedAsync();
        seed.Graph.Exchange = GraphResult<string>.Failure("100", "Invalid verification code format.");

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        // Yarım satır bırakmak en kötüsü olurdu: panel "bağlı" gösterir,
        // gönderim sessizce başarısız olur.
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await TitleAsync(resp)).Should().Be("whatsapp-code-exchange-failed");

        var status = await seed.Client.GetAsync("/api/panel/whatsapp/account");
        status.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_failed_subscription_is_fatal_because_webhooks_would_never_arrive()
    {
        var seed = await SeedAsync();
        seed.Graph.Subscribe = GraphResult<bool>.Failure("200", "Permissions error");

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await TitleAsync(resp)).Should().Be("whatsapp-subscribe-failed");
    }

    [Fact]
    public async Task A_failed_registration_still_connects_but_records_the_error()
    {
        var seed = await SeedAsync();
        seed.Graph.Register = GraphResult<bool>.Failure("133005", "Two step verification PIN mismatch.");

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        // Numara zaten kayıtlıysa register hata verir ama hesap ÇALIŞIR —
        // bunu ölümcül saymak sorunsuz yayıncıyı bağlanamaz hâle getirirdi.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().Contain("133005");
    }

    [Fact]
    public async Task Without_an_active_license_the_answer_is_a_titled_400()
    {
        var graph = new FakeOnboardingClient();
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IWhatsAppOnboardingClient>(graph)));
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("no-active-license");
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }
}
```

- [x] **Step 2: Testin derlenmediğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelWhatsAppAccountControllerTests`
Expected: derleme hatası ya da 404 — controller yok.

- [x] **Step 3: Controller'ı yaz**

`OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppAccountController.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının kendi WhatsApp numarasını panelden bağlaması (Embedded Signup).
///
/// <para><b>Sıra önemli:</b> code→token, sonra WABA aboneliği, sonra numara
/// bilgisi, en son register. Abonelik olmadan o numaraya gelen mesajlar
/// webhook'umuza HİÇ düşmez — o yüzden ölümcül. Register ise ölümcül değil:
/// numara zaten kayıtlıysa Meta hata döner ama hesap çalışır.</para>
///
/// <para><b>Kod saklanmaz:</b> Embedded Signup'ın <c>code</c>'u 30 saniye
/// yaşıyor ve tek kullanımlık; loglanmaz, DB'ye yazılmaz.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp/account")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppAccountController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly IWhatsAppOnboardingClient _graph;
    private readonly ILogger<PanelWhatsAppAccountController> _log;

    public PanelWhatsAppAccountController(
        LicenseDbContext db,
        WhatsAppAccountService accounts,
        IWhatsAppOnboardingClient graph,
        ILogger<PanelWhatsAppAccountController> log)
    {
        _db = db;
        _accounts = accounts;
        _graph = graph;
        _log = log;
    }

    public sealed record EmbeddedSignupRequest(string Code, string WabaId, string PhoneNumberId);

    public sealed record AccountView(
        string WabaId,
        string PhoneNumberId,
        string DisplayPhoneNumber,
        string? VerifiedName,
        string Status,
        string? LastError,
        DateTimeOffset ConnectedAt);

    [HttpPost("embedded-signup")]
    public async Task<IActionResult> Complete(
        [FromBody] EmbeddedSignupRequest req, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        if (string.IsNullOrWhiteSpace(req.Code) ||
            string.IsNullOrWhiteSpace(req.WabaId) ||
            string.IsNullOrWhiteSpace(req.PhoneNumberId))
        {
            return Problem(
                title: "invalid-embedded-signup-payload", statusCode: 400,
                detail: "code, wabaId ve phoneNumberId zorunlu.");
        }

        var exchange = await _graph.ExchangeCodeAsync(req.Code.Trim(), ct);
        if (!exchange.Ok)
        {
            return Problem(
                title: "whatsapp-code-exchange-failed", statusCode: 502,
                detail: Detail(exchange.ErrorCode, exchange.ErrorMessage));
        }

        var token = exchange.Value!;

        var subscribe = await _graph.SubscribeAppAsync(req.WabaId.Trim(), token, ct);
        if (!subscribe.Ok)
        {
            return Problem(
                title: "whatsapp-subscribe-failed", statusCode: 502,
                detail: Detail(subscribe.ErrorCode, subscribe.ErrorMessage));
        }

        var phone = await _graph.ReadPhoneNumberAsync(req.PhoneNumberId.Trim(), token, ct);
        if (!phone.Ok)
        {
            return Problem(
                title: "whatsapp-phone-read-failed", statusCode: 502,
                detail: Detail(phone.ErrorCode, phone.ErrorMessage));
        }

        var pin = NewPin();
        var register = await _graph.RegisterPhoneNumberAsync(req.PhoneNumberId.Trim(), pin, token, ct);

        var result = await _accounts.UpsertAsync(
            licenseId.Value,
            new WhatsAppAccountUpsert(
                req.WabaId, req.PhoneNumberId, phone.Value!.DisplayPhoneNumber,
                token, phone.Value.VerifiedName, pin),
            ct);

        if (result.Conflict)
        {
            return Problem(
                title: "phone-number-id-taken", statusCode: 409,
                detail: "Bu numara başka bir hesaba bağlı. Destekle iletişime geç.");
        }

        var account = result.Account!;

        if (!register.Ok)
        {
            // Hesap çalışıyor olabilir (numara zaten kayıtlı) ama gönderim
            // "not registered" verirse panelin sebebi gösterebilmesi lazım.
            account.LastError = $"register: {register.ErrorCode} {register.ErrorMessage}".Trim();
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "Embedded Signup tamamlandı: lisans {LicenseId}, phone_number_id {Pnid}",
            licenseId, account.PhoneNumberId);

        return Ok(ToView(account));
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var account = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId.Value, ct);
        return account is null ? NotFound() : Ok(ToView(account));
    }

    private static AccountView ToView(WhatsAppAccount a) => new(
        a.WabaId, a.PhoneNumberId, a.DisplayPhoneNumber, a.VerifiedName,
        a.Status, a.LastError, a.ConnectedAt);

    private static string Detail(string? code, string? message) =>
        string.IsNullOrWhiteSpace(message) ? code ?? "bilinmeyen hata" : $"{code}: {message}";

    /// <summary>Numaranın iki adımlı PIN'i. Tahmin edilebilir olmamalı — şifreli
    /// saklanıyor ve yayıncı adına numarayı kilitleyen değer bu.</summary>
    private static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
```

- [x] **Step 4: DI kaydını ekle**

`OrderDeck.LicenseServer/Program.cs` içinde `builder.Services.AddScoped<...WhatsAppAccountService>();` satırının hemen üstüne:

```csharp
        // Embedded Signup Graph istemcisi — gönderenden AYRI kayıtlı: sağlayıcı
        // "log" olsa da (dev) onboarding uçları derlenebilir/test edilebilir olmalı.
        var waOnboardTimeout = builder.Configuration.GetValue("OrderDeck:WhatsApp:TimeoutSeconds", 15);
        builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.WhatsApp.IWhatsAppOnboardingClient,
                OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppOnboardingClient>(
                c => c.Timeout = TimeSpan.FromSeconds(waOnboardTimeout <= 0 ? 15 : waOnboardTimeout));
```

- [x] **Step 5: Testlerin geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelWhatsAppAccountControllerTests`
Expected: Passed: 5, Failed: 0.

- [x] **Step 6: Tüm sunucu takımı yeşil**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: Failed: 0.

- [x] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppAccountController.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppAccountControllerTests.cs
git commit -m "feat(whatsapp): panelden Embedded Signup ile hesap bağlama"
```

---

### Task 8: Panelin ihtiyacı olan yapılandırmayı yayınla

Panel JS SDK'yı açmak için `config_id` ve app id'yi bilmek zorunda. Bunları panele gömmek yerine sunucudan okutuyoruz — Meta tarafında değiştiğinde panel yeniden derlenmesin.

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppAccountController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppAccountControllerTests.cs`

- [x] **Step 1: Failing test yaz**

`PanelWhatsAppAccountControllerTests.cs` içindeki son `}` öncesine:

```csharp
    [Fact]
    public async Task The_panel_can_read_the_signup_configuration_but_never_the_app_secret()
    {
        var seed = await SeedAsync();

        var resp = await seed.Client.GetAsync("/api/panel/whatsapp/account/signup-config");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("appId", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("configId", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("graphApiVersion", out _).Should().BeTrue();
        // App Secret sunucuda kalır; panele sızarsa herkes tenant token'ı üretebilir.
        doc.RootElement.TryGetProperty("appSecret", out _).Should().BeFalse();
    }
```

- [x] **Step 2: Testin kırmızı olduğunu gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~The_panel_can_read_the_signup_configuration`
Expected: FAIL — 404 NotFound.

- [x] **Step 3: Ucu ekle**

`PanelWhatsAppAccountController` içinde `IWhatsAppOnboardingClient _graph;` alanının altına yeni alan ve ctor parametresi ekle:

```csharp
    private readonly WhatsAppOptions _opt;
```

Ctor imzasına `Microsoft.Extensions.Options.IOptions<WhatsAppOptions> opt` parametresini ekle ve gövdesine `_opt = opt.Value;` satırını koy.

Sonra `Get` metodunun altına:

```csharp
    public sealed record SignupConfig(string AppId, string ConfigId, string GraphApiVersion);

    /// <summary>Panelin FB JS SDK'yı açmak için ihtiyaç duyduğu genel değerler.
    /// App Secret BURAYA GİRMEZ — o değerle tenant token'ı üretilebiliyor.</summary>
    [HttpGet("signup-config")]
    public IActionResult GetSignupConfig() =>
        Ok(new SignupConfig(_opt.AppId, _opt.EmbeddedSignupConfigId, _opt.GraphApiVersion));
```

- [x] **Step 4: Testin geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelWhatsAppAccountControllerTests`
Expected: Passed: 6, Failed: 0.

- [x] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppAccountController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppAccountControllerTests.cs
git commit -m "feat(whatsapp): panel için Embedded Signup yapılandırma ucu"
```

---

### Task 9: Dağıtım notu + tam takım doğrulaması

**Files:**
- Modify: `docs/whatsapp-cloud-api-integration-plan.md`

- [x] **Step 1: Yeni ortam değişkenlerini belgele**

`docs/whatsapp-cloud-api-integration-plan.md` dosyasının sonuna:

```markdown
## Embedded Signup — gereken ortam değişkenleri (2026-08-19)

VPS `.env` (prod) ve `docker-compose.yml` ortamına eklenecek:

| Anahtar | Değer / nereden alınır |
|---|---|
| `OrderDeck__WhatsApp__AppId` | `1539000484386031` — "OrderDeck WP" app'i (Facebook chat app'i `3939617702835404` DEĞİL) |
| `OrderDeck__WhatsApp__EmbeddedSignupConfigId` | App Dashboard > Facebook Login for Business > Configurations. 2026-08-19'da **iki** config var: `2063978347839185` ve `1064272643022213`, ikisi de "Tech Provider Embedded Signup config" adında — hangisinin kullanılacağı Meta konsolundan teyit edilmeli |

`AppSecret` zaten mevcut ve webhook imzası için kullanılıyor; Embedded Signup
token takası da onu kullanır — yeni bir sır eklenmiyor.

**Meta konsolu — 2026-08-19'da doğrulanan durum:**
- ✅ App Mode = **Published/Live**, Required actions boş
- ✅ Become Tech Provider = **"2 of 2 steps complete"**
- ✅ App Review 2026-08-12 **onaylandı**: `whatsapp_business_messaging`,
  `whatsapp_business_management`, `public_profile` — üçü de Approved
- ❌ Facebook Login for Business > Ayarlar > **Valid OAuth Redirect URIs BOŞ** —
  doldurulmalı (panel callback adresi)
- ❌ **Allowed Domains for the JavaScript SDK** yalnız `https://orderdeckapp.com/`
  içeriyor; panel `panel.orderdeckapp.com`'da → o alan adı eklenmeli, yoksa
  `FB.login` penceresi açılmaz

**Gerçek yayıncı olmadan test:** Use cases > Become a Partner > Become Tech Provider
sayfasındaki **"Claim a sandbox account"** ile Meta'nın verdiği sandbox WABA
üzerinden Embedded Signup uçtan denenebilir.
```

- [x] **Step 2: İki takımı da çalıştır**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: Failed: 0.

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Expected: Failed: 0. (Bu plan WPF'e dokunmuyor; kırmızı çıkarsa ilgisiz bir sapma var.)

- [x] **Step 3: Sır taraması — repo PUBLIC**

Run: `git diff master --stat && git grep -nE "EAA[A-Za-z0-9]{20,}|client_secret=[A-Za-z0-9]{16,}" -- . ':!docs/superpowers/plans'`
Expected: hiçbir eşleşme yok. Eşleşme varsa commit ETME, önce temizle.

- [x] **Step 4: Commit**

```bash
git add docs/whatsapp-cloud-api-integration-plan.md
git commit -m "docs(whatsapp): Embedded Signup ortam değişkenlerini yaz"
```

---

## Bu planın kapsamadıkları (bilerek)

- **Panel arayüzü** — `OrderDeck-Mobile` reposunda ayrı plan. Bu plan onun ihtiyaç duyduğu iki ucu (`signup-config`, `embedded-signup`) hazır bırakıyor.
- **Hesabı koparma (disconnect)** — bugün admin ucu bile yapmıyor; ihtiyaç doğduğunda eklenir (YAGNI).
- **Kredi hattı paylaşımı** — yayıncılar kendi mesajlarını kendileri ödüyor; paylaşımı biz yapsaydık faturayı Emar öderdi. Ürün kararı, kod kararı değil.
- **Token yenileme/iptal izleme** — iş token'ları süresiz ama yayıncı erişimi iptal edebilir; o zaman gönderim hata verir ve `LastError` dolar. Otomatik yeniden bağlanma daveti ayrı iş.
