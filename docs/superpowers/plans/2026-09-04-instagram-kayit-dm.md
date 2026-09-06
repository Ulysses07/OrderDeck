# Instagram "!kayıt" → DM ile kayıt linki — uygulama planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** İzleyici canlı yayında `!kayıt` yazınca Meta webhook'u yorumu düşürür, sunucu private reply ile tokenlı kayıt linki DM'ler; link formda Instagram kimliğini doğrulanmış bağlar.

**Architecture:** WhatsApp webhook deseninin eşi bir Instagram webhook ucu + Hangfire job; yayıncı token'ı mevcut FB OAuth exchange'inde (opt-in bayrakla) sunucuya kalıcılaşır; form linki `ITimeLimitedDataProtector` ile kendinden-doğrulamalı token taşır (DB'siz, restart'a dayanıklı).

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server / testlerde InMemory), Hangfire, Meta Graph API (masaüstü app 3939617702835404), IDataProtection.

**Spec:** `docs/superpowers/specs/2026-09-04-instagram-kayit-dm-design.md`

**Branch:** `feat/intake-instagram-dm` (master'dan). Testler: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`. Commit mesajları Türkçe + `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`. **Test fixture'larında sabit sır YAZMA** — `$"{prefix}-{Guid.NewGuid():N}"` üret (CLAUDE.md kuralı, repo public).

---

### Task 1: `InstagramDmOptions` + Program.cs kaydı

Küresel karanlık-yayın bayrağı. `IntakeLoginOptions` desenine paralel.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Instagram/InstagramDmOptions.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (diğer `AddOptions` bloklarının yanı; `IntakeLogin` veya `WhatsAppOptions` bind'ını bul ve altına ekle)

- [ ] **Step 1: Options sınıfını yaz**

```csharp
namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// "!kayıt → DM" özelliğinin küresel bayrağı. VPS .env:
/// <c>InstagramDm__Enabled</c>, <c>InstagramDm__VerifyToken</c>.
/// Meta App Review (instagram_manage_messages advanced) onaylanana kadar
/// Enabled yazılMAZ — webhook uçları 404 döner (IntakeLogin deseni).
/// İmza doğrulaması masaüstü Meta app'inin secret'ıyla yapılır
/// (OrderDeck__Facebook__AppSecret) — ayrı app YOK, webhook o app'e bağlı.
/// </summary>
public sealed class InstagramDmOptions
{
    public const string SectionName = "InstagramDm";

    public bool Enabled { get; set; }

    /// <summary>Meta webhook abonelik doğrulamasındaki hub.verify_token.
    /// Rastgele üretilir, .env + Meta paneline aynı değer yazılır.</summary>
    public string VerifyToken { get; set; } = "";

    public bool Ready => Enabled && !string.IsNullOrWhiteSpace(VerifyToken);
}
```

- [ ] **Step 2: Program.cs'e bind ekle** (mevcut `IntakeLoginOptions` bind satırını bul, aynı biçimde):

```csharp
builder.Services.Configure<OrderDeck.LicenseServer.Services.Instagram.InstagramDmOptions>(
    builder.Configuration.GetSection(
        OrderDeck.LicenseServer.Services.Instagram.InstagramDmOptions.SectionName));
```

- [ ] **Step 3: Derle** — `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`, hata yok.

- [ ] **Step 4: Commit** — `feat(instagram-dm): InstagramDm seçenekleri (karanlık bayrak)`

---

### Task 2: `InstagramAccount` entity + `IntakeFormConfig.InstagramDmBotEnabled` + migration

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/InstagramAccount.cs`
- Modify: `OrderDeck.LicenseServer/Domain/IntakeFormConfig.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (`WhatsAppAccounts` DbSet + OnModelCreating bloğunu bul, aynısını uygula)
- Create: migration `Data/Migrations/*_AddInstagramAccount.cs` (ef aracı üretir)

- [ ] **Step 1: Entity**

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının (lisansın) "!kayıt → DM" botu için bağlanan Instagram professional
/// hesabı. FB OAuth exchange'i sırasında, müşterinin IntakeFormConfig'inde
/// <c>InstagramDmBotEnabled</c> açıksa oluşur/güncellenir (opt-in — exchange ucu
/// varsayılan davranışında token SAKLAMAZ, bkz. FacebookOAuthController).
///
/// <para><b>Webhook yönlendirme:</b> Meta live_comments olayı entry.id'de IG
/// professional hesap kimliğini taşır; <see cref="IgUserId"/> bu yüzden global
/// unique'tir (WhatsAppAccount.PhoneNumberId deseni).</para>
/// </summary>
public sealed class InstagramAccount
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>IG hesabının bağlı olduğu Facebook Sayfası — private reply
    /// <c>/{PageId}/messages</c> ucuna gider, webhook aboneliği de bu sayfaya yapılır.</summary>
    public string PageId { get; set; } = "";

    /// <summary>instagram_business_account.id — webhook route anahtarı, global unique.</summary>
    public string IgUserId { get; set; } = "";

    /// <summary>Yalnız UI/log için.</summary>
    public string IgUsername { get; set; } = "";

    /// <summary>Page access token — <c>IDataProtector</c> ile şifreli, asla düz metin dönmez.</summary>
    public string PageTokenProtected { get; set; } = "";

    /// <summary>"active" | "revoked" (token geçersizleşti — DM gönderimi hata verdi).</summary>
    public string Status { get; set; } = "active";

    /// <summary>Son gönderim/abonelik hatası — teşhis için.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }
}
```

- [ ] **Step 2: IntakeFormConfig'e bayrak ekle** (sınıf sonuna, `IsActive`'in altına):

```csharp
    /// <summary>"!kayıt → DM" botu (Instagram). Açılınca: FB OAuth exchange'i
    /// bu müşterinin Page token'ını sunucuda saklar ve webhook aboneliği kurar.
    /// Kapalıyken exchange bugünkü gibi yalnız relay eder (token saklanmaz).</summary>
    public bool InstagramDmBotEnabled { get; set; }
```

- [ ] **Step 3: DbContext** — `LicenseDbContext.cs` içinde `WhatsAppAccounts` DbSet'ini ve OnModelCreating'deki `WhatsAppAccount` yapılandırmasını bul; aynı deseni uygula:

```csharp
public DbSet<InstagramAccount> InstagramAccounts => Set<InstagramAccount>();
```

```csharp
modelBuilder.Entity<InstagramAccount>(e =>
{
    e.Property(x => x.PageId).HasMaxLength(64);
    e.Property(x => x.IgUserId).HasMaxLength(64);
    e.Property(x => x.IgUsername).HasMaxLength(128);
    e.Property(x => x.Status).HasMaxLength(16);
    e.Property(x => x.LastError).HasMaxLength(1024);
    e.HasIndex(x => x.IgUserId).IsUnique();
    e.HasIndex(x => x.LicenseId);
    e.HasOne(x => x.License).WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Cascade);
});
```

(WhatsAppAccount'ın gerçek yapılandırması farklı uzunluk/silme davranışı kullanıyorsa ONU taklit et — buradaki değerler değil, depo deseni yetkili.)

- [ ] **Step 4: Migration üret**

Run: `dotnet ef migrations add AddInstagramAccount -p OrderDeck.LicenseServer -o Data/Migrations`
Expected: `Data/Migrations/2026…_AddInstagramAccount.cs` oluşur; içinde `InstagramAccounts` tablosu + `IntakeFormConfigs.InstagramDmBotEnabled` (default false) var. **Var olan tabloya drop/alter sürprizi varsa DUR ve incele.**

- [ ] **Step 5: Testleri koştur** — `dotnet test OrderDeck.LicenseServer.Tests/...csproj`
Expected: PASS (InMemory şemayı modelden kurar, migration'a bakmaz — kırılma modeli bozduğumuz anlamına gelir).

- [ ] **Step 6: Commit** — `feat(instagram-dm): InstagramAccount entity + IntakeFormConfig bot bayrağı`

---

### Task 3: DM link token servisi (`IntakeIgTokenService`)

Kendinden-doğrulamalı, 24 saat ömürlü, URL-güvenli token. DB yok — deploy/restart token'ları öldürmez (IMemoryCache bu yüzden ELENDİ: DM'deki link deploy'da ölürdü).

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Instagram/IntakeIgTokenService.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Instagram/IntakeIgTokenServiceTests.cs`

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using OrderDeck.LicenseServer.Services.Instagram;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Instagram;

public sealed class IntakeIgTokenServiceTests
{
    private static IntakeIgTokenService NewService()
        => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Uretilen_token_geri_okunur()
    {
        var svc = NewService();
        var token = svc.Create("royalmezat", "musa.sevinc");

        token.Should().NotContain("royalmezat", "payload şifreli olmalı, düz metin sızmamalı");
        Uri.EscapeDataString(token).Should().Be(token, "token URL-güvenli olmalı — kaçış gerektirmemeli");

        var payload = svc.TryRead(token);
        payload.Should().Be(("royalmezat", "musa.sevinc"));
    }

    [Fact]
    public void Bozuk_token_null_doner()
    {
        NewService().TryRead("bozuk-token").Should().BeNull();
    }

    [Fact]
    public void Baska_anahtarin_tokeni_null_doner()
    {
        var token = NewService().Create("royalmezat", "musa");
        NewService().TryRead(token).Should().BeNull("EphemeralDataProtectionProvider her seferinde ayrı anahtar üretir");
    }
}
```

- [ ] **Step 2: Koştur, FAIL gör** — `dotnet test ...csproj --filter IntakeIgTokenServiceTests` → derleme hatası (tip yok).

- [ ] **Step 3: Implementasyon**

```csharp
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// "!kayıt → DM" linkindeki <c>?ig=</c> token'ı. Kendinden-doğrulamalı:
/// ITimeLimitedDataProtector (24 sa) — DB kaydı yok, deploy/restart'ta
/// yaşamaya devam eder. Tek-kullanımlık DEĞİL, bilinçli: token yalnız
/// izleyicinin kendi DM'inde; tekrar açması kendi kimliğini tekrar bağlar,
/// zarar yüzeyi yok. Payload'da PII yok (slug + IG kullanıcı adı).
/// </summary>
public sealed class IntakeIgTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly ITimeLimitedDataProtector _protector;

    public IntakeIgTokenService(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("IntakeForm.InstagramDmLink.v1")
            .ToTimeLimitedDataProtector();

    public string Create(string slug, string igUsername)
        => _protector.Protect($"{slug}\n{igUsername}", Lifetime);

    /// <summary>Geçersiz/süresi dolmuş token'da null — form bağlantısız açılır,
    /// hata ekranı YOK (spec §4).</summary>
    public (string Slug, string IgUsername)? TryRead(string token)
    {
        try
        {
            var parts = _protector.Unprotect(token).Split('\n');
            return parts.Length == 2 ? (parts[0], parts[1]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Program.cs'e kaydet** (Task 1'deki bind'ın yanına):

```csharp
builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Instagram.IntakeIgTokenService>();
```

- [ ] **Step 5: Koştur, PASS gör** — `--filter IntakeIgTokenServiceTests` → 3 PASS.

- [ ] **Step 6: Commit** — `feat(instagram-dm): 24 saatlik kendinden-doğrulamalı DM link token'ı`

---

### Task 4: Private reply istemcisi (`InstagramPrivateReplyClient`)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Instagram/InstagramPrivateReplyClient.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Instagram/InstagramPrivateReplyClientTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (typed HttpClient kaydı — `FacebookOAuthExchanger` kaydının deseniyle)

- [ ] **Step 1: Failing test** — `FacebookNameClientTests`'teki StubHandler deseni birebir (o dosyadan kopyala):

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.Instagram;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Instagram;

public sealed class InstagramPrivateReplyClientTests
{
    private static readonly string Tok = $"pagetok-{Guid.NewGuid():N}";

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

    private static (InstagramPrivateReplyClient Client, StubHandler Handler, HttpClient Http) NewClient()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"recipient_id":"1","message_id":"m1"}""",
                    Encoding.UTF8, "application/json")
            }
        };
        var http = new HttpClient(handler);
        var client = new InstagramPrivateReplyClient(
            http, Options.Create(new FacebookOptions()),
            NullLogger<InstagramPrivateReplyClient>.Instance);
        return (client, handler, http);
    }

    [Fact]
    public async Task Basarili_gonderimde_true_doner_ve_yorum_kimligine_gider()
    {
        var (client, handler, http) = NewClient();
        using (http)
        {
            var ok = await client.SendAsync(
                pageId: "page-1", commentId: "cmt-42", text: "Kayıt için: https://x",
                pageToken: Tok, CancellationToken.None);

            ok.Should().BeTrue();
            var req = handler.Requests.Single();
            req.Uri.AbsolutePath.Should().EndWith("/page-1/messages");
            req.Uri.ToString().Should().NotContain(Tok, "token URI'ye sızmamalı");
            req.AuthHeader.Should().Be($"Bearer {Tok}");
            req.Body.Should().Contain("cmt-42").And.Contain("comment_id");
        }
    }

    [Fact]
    public async Task Graph_hatasinda_false_doner_firlatmaz()
    {
        var (client, handler, http) = NewClient();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"window closed","code":10}}""",
                Encoding.UTF8, "application/json")
        };
        using (http)
        {
            var ok = await client.SendAsync("p", "c", "t", Tok, CancellationToken.None);
            ok.Should().BeFalse();
        }
    }
}
```

- [ ] **Step 2: Koştur, FAIL gör** (derleme hatası).

- [ ] **Step 3: Implementasyon**

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// Canlı yayın yorumuna private reply DM'i. Meta kuralları: pencere = yayın
/// süresi, yorum başına 1 reply — düşerse (pencere kapandı vb.) sessizce false,
/// Hangfire retry ANLAMSIZ: aynı yoruma ikinci deneme zaten reddedilir.
/// </summary>
public sealed class InstagramPrivateReplyClient
{
    private readonly HttpClient _http;
    private readonly FacebookOptions _fb;
    private readonly ILogger<InstagramPrivateReplyClient> _log;

    public InstagramPrivateReplyClient(
        HttpClient http, IOptions<FacebookOptions> fb, ILogger<InstagramPrivateReplyClient> log)
    {
        _http = http;
        _fb = fb.Value;
        _log = log;
    }

    public async Task<bool> SendAsync(
        string pageId, string commentId, string text, string pageToken, CancellationToken ct)
    {
        var url = $"{_fb.GraphBaseUrl.TrimEnd('/')}/{_fb.GraphApiVersion}/{pageId}/messages";
        var payload = JsonSerializer.Serialize(new
        {
            recipient = new { comment_id = commentId },
            message = new { text }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        // Token başlıkta — URI'de olsaydı log'lara sızardı (FacebookNameClient kuralı).
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageToken);

        using var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return true;

        var body = await res.Content.ReadAsStringAsync(ct);
        _log.LogWarning("Instagram private reply düştü — comment={CommentId}, status={Status}, body={Body}",
            commentId, (int)res.StatusCode, body.Length > 512 ? body[..512] : body);
        return false;
    }
}
```

- [ ] **Step 4: Program.cs kaydı** (FacebookOAuthExchanger'ın `AddHttpClient` bloğunu bul, aynı biçimde):

```csharp
builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.Instagram.InstagramPrivateReplyClient>();
```

(Mevcut kayıtta timeout/policy varsa aynısını uygula.)

- [ ] **Step 5: Koştur, PASS gör.**

- [ ] **Step 6: Commit** — `feat(instagram-dm): private reply Graph istemcisi`

---

### Task 5: `InstagramAccountService` — bağlama (token saklama + webhook aboneliği)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Instagram/InstagramAccountService.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Instagram/InstagramAccountServiceTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs`

- [ ] **Step 1: Failing testler** — InMemory `LicenseDbContext` + StubHandler. Test kurulumunda müşteri+lisans+IntakeFormConfig tohumla (mevcut testlerde `LicenseDbContext` nasıl kuruluyorsa o yardımcıyı kullan; yoksa `DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())`). Senaryolar:

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.Instagram;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Instagram;

public sealed class InstagramAccountServiceTests
{
    private static readonly string UserTok = $"usertok-{Guid.NewGuid():N}";
    private static readonly string PageTok = $"pagetok-{Guid.NewGuid():N}";

    // StubHandler: Task 4'tekiyle aynı sınıf (kopyala — test projeleri arasında
    // paylaşım kurmaya değmez, üç kullanım).

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body));
            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid CustomerId, Guid LicenseId)> SeedAsync(
        LicenseDbContext db, bool botEnabled)
    {
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"c-{Guid.NewGuid():N}@x.tr", Name = "T" };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id,
            LicenseKey = $"lic-{Guid.NewGuid():N}",
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1)
        };
        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, Slug = $"s{Guid.NewGuid():N}"[..10],
            InstagramDmBotEnabled = botEnabled, IsActive = true
        };
        db.AddRange(customer, license, config);
        await db.SaveChangesAsync();
        return (customer.Id, license.Id);
    }
    // NOT: Customer/License zorunlu alanları modelde farklıysa derleyici/DB söyler —
    // mevcut testlerdeki tohumlama yardımcısı varsa ONU kullan.

    private static InstagramAccountService NewService(LicenseDbContext db, StubHandler handler)
        => new(db, new HttpClient(handler), Options.Create(new FacebookOptions()),
            new EphemeralDataProtectionProvider(), NullLogger<InstagramAccountService>.Instance);

    [Fact]
    public async Task Bayrak_kapaliysa_hicbir_graph_cagrisi_yapilmaz()
    {
        using var db = NewDb();
        var (customerId, _) = await SeedAsync(db, botEnabled: false);
        var handler = new StubHandler();

        await NewService(db, handler).TryConnectAsync(customerId, UserTok, CancellationToken.None);

        handler.Requests.Should().BeEmpty();
        db.InstagramAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Bayrak_acikken_hesap_kaydedilir_ve_abonelik_yapilir()
    {
        using var db = NewDb();
        var (customerId, licenseId) = await SeedAsync(db, botEnabled: true);
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("/me/accounts")
                ? Json($$"""
                    {"data":[{"id":"page-9","access_token":"{{PageTok}}",
                     "instagram_business_account":{"id":"ig-77","username":"royal.mezat"}}]}
                    """)
                : Json("""{"success":true}""")
        };

        await NewService(db, handler).TryConnectAsync(customerId, UserTok, CancellationToken.None);

        var acc = db.InstagramAccounts.Single();
        acc.LicenseId.Should().Be(licenseId);
        acc.PageId.Should().Be("page-9");
        acc.IgUserId.Should().Be("ig-77");
        acc.IgUsername.Should().Be("royal.mezat");
        acc.PageTokenProtected.Should().NotBeEmpty().And.NotBe(PageTok, "şifreli saklanmalı");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Uri.AbsolutePath.Should().EndWith("/page-9/subscribed_apps");
        handler.Requests[1].Body.Should().Contain("live_comments");
    }

    [Fact]
    public async Task Ig_hesabi_olmayan_sayfa_atlanir_kayit_olusmaz()
    {
        using var db = NewDb();
        var (customerId, _) = await SeedAsync(db, botEnabled: true);
        var handler = new StubHandler
        {
            Respond = _ => Json("""{"data":[{"id":"page-1","access_token":"t"}]}""")
        };

        await NewService(db, handler).TryConnectAsync(customerId, UserTok, CancellationToken.None);

        db.InstagramAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Ayni_ig_hesabi_ikinci_baglamada_guncellenir_cogalmaz()
    {
        using var db = NewDb();
        var (customerId, _) = await SeedAsync(db, botEnabled: true);
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("/me/accounts")
                ? Json($$"""
                    {"data":[{"id":"page-9","access_token":"{{PageTok}}",
                     "instagram_business_account":{"id":"ig-77","username":"royal.mezat"}}]}
                    """)
                : Json("""{"success":true}""")
        };
        var svc = NewService(db, handler);

        await svc.TryConnectAsync(customerId, UserTok, CancellationToken.None);
        await svc.TryConnectAsync(customerId, UserTok, CancellationToken.None);

        db.InstagramAccounts.Should().HaveCount(1);
    }
}
```

- [ ] **Step 2: Koştur, FAIL gör.**

- [ ] **Step 3: Implementasyon**

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Facebook;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// FB OAuth exchange'inden çağrılır (FacebookOAuthController). Müşterinin
/// IntakeFormConfig.InstagramDmBotEnabled bayrağı AÇIKSA: uzun ömürlü kullanıcı
/// token'ından sayfaları çeker, IG professional hesabı bağlı ilk sayfayı seçer,
/// Page token'ı şifreli saklar ve sayfayı live_comments webhook'una abone eder.
/// Bayrak kapalıysa HİÇBİR ŞEY yapmaz — exchange'in "token saklamaz" sözü
/// varsayılan davranış olarak korunur.
///
/// <para>Hata exchange'i DÜŞÜRMEZ: masaüstü Facebook bağlantısı bot yüzünden
/// kırılamaz. Çağıran try/catch'ler, biz de kendi içimizde loglarız.</para>
/// </summary>
public sealed class InstagramAccountService
{
    public const string ProtectorPurpose = "InstagramAccount.PageToken.v1";

    private readonly LicenseDbContext _db;
    private readonly HttpClient _http;
    private readonly FacebookOptions _fb;
    private readonly IDataProtector _protector;
    private readonly ILogger<InstagramAccountService> _log;

    public InstagramAccountService(
        LicenseDbContext db, HttpClient http, IOptions<FacebookOptions> fb,
        IDataProtectionProvider protection, ILogger<InstagramAccountService> log)
    {
        _db = db;
        _http = http;
        _fb = fb.Value;
        _protector = protection.CreateProtector(ProtectorPurpose);
        _log = log;
    }

    public string? UnprotectPageToken(InstagramAccount acc)
    {
        try { return _protector.Unprotect(acc.PageTokenProtected); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Instagram Page token çözülemedi — hesap={IgUserId}", acc.IgUserId);
            return null;
        }
    }

    public async Task TryConnectAsync(Guid customerId, string longLivedUserToken, CancellationToken ct)
    {
        var config = await _db.Set<IntakeFormConfig>()
            .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.InstagramDmBotEnabled, ct);
        if (config is null) return; // opt-in yok — varsayılan: sakla-MA

        var now = DateTimeOffset.UtcNow;
        var license = await _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderByDescending(l => l.IssuedAt)
            .FirstOrDefaultAsync(ct);
        if (license is null)
        {
            _log.LogWarning("IG DM botu açık ama aktif lisans yok — customer={CustomerId}", customerId);
            return;
        }

        var root = $"{_fb.GraphBaseUrl.TrimEnd('/')}/{_fb.GraphApiVersion}";

        using var pagesReq = new HttpRequestMessage(HttpMethod.Get,
            $"{root}/me/accounts?fields=id,access_token,instagram_business_account%7Bid,username%7D");
        pagesReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", longLivedUserToken);
        using var pagesRes = await _http.SendAsync(pagesReq, ct);
        if (!pagesRes.IsSuccessStatusCode)
        {
            _log.LogWarning("IG bağlama: /me/accounts düştü — status={Status}", (int)pagesRes.StatusCode);
            return;
        }

        using var doc = JsonDocument.Parse(await pagesRes.Content.ReadAsStringAsync(ct));
        string? pageId = null, pageToken = null, igUserId = null, igUsername = null;
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var page in data.EnumerateArray())
            {
                if (!page.TryGetProperty("instagram_business_account", out var ig)) continue;
                pageId = page.GetProperty("id").GetString();
                pageToken = page.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                igUserId = ig.GetProperty("id").GetString();
                igUsername = ig.TryGetProperty("username", out var u) ? u.GetString() : "";
                break; // IG'li ilk sayfa — birden çoksa loglayıp ilkini alıyoruz
            }
        }
        if (pageId is null || pageToken is null || igUserId is null)
        {
            _log.LogInformation("IG DM botu: IG professional hesabı bağlı sayfa yok — customer={CustomerId}",
                customerId);
            return;
        }

        var acc = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.IgUserId == igUserId, ct)
            ?? _db.InstagramAccounts.Add(new InstagramAccount { Id = Guid.NewGuid(), IgUserId = igUserId }).Entity;
        acc.LicenseId = license.Id;
        acc.PageId = pageId;
        acc.IgUsername = igUsername ?? "";
        acc.PageTokenProtected = _protector.Protect(pageToken);
        acc.Status = "active";
        acc.LastError = null;
        acc.ConnectedAt = now;
        await _db.SaveChangesAsync(ct);

        // Webhook aboneliği — idempotent, her bağlamada tekrar çağrılabilir.
        using var subReq = new HttpRequestMessage(HttpMethod.Post, $"{root}/{pageId}/subscribed_apps")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["subscribed_fields"] = "live_comments"
            })
        };
        subReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageToken);
        using var subRes = await _http.SendAsync(subReq, ct);
        if (!subRes.IsSuccessStatusCode)
        {
            acc.LastError = $"subscribed_apps {(int)subRes.StatusCode}";
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("IG webhook aboneliği düştü — page={PageId}, status={Status}",
                pageId, (int)subRes.StatusCode);
        }

        _log.LogInformation("IG DM botu bağlandı — ig={IgUsername} ({IgUserId}), license={LicenseId}",
            igUsername, igUserId, license.Id);
    }
}
```

- [ ] **Step 4: Program.cs kaydı**

```csharp
builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.Instagram.InstagramAccountService>();
```

- [ ] **Step 5: Koştur, PASS gör** — `--filter InstagramAccountServiceTests` → 4 PASS.

- [ ] **Step 6: Commit** — `feat(instagram-dm): yayıncı IG hesabı bağlama servisi (opt-in token saklama + webhook aboneliği)`

---

### Task 6: Exchange kancası (`FacebookOAuthController`)

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Facebook/FacebookOAuthController.cs`
- Test: mevcut exchange testlerinin dosyasını bul (`Grep "facebook-exchange" OrderDeck.LicenseServer.Tests`) ve senaryo ekle

- [ ] **Step 1: Controller'ı değiştir** — ctor'a `InstagramAccountService igAccounts` ve `ILogger<FacebookOAuthController> log` ekle; `Exchange`'te başarılı sonuçtan SONRA, `return Ok(...)`'tan önce:

```csharp
        // IG "!kayıt → DM" botu (opt-in): bayrağı açık müşteride Page token'ı
        // sunucuya kalıcılaşır. Bayrak kapalıysa TryConnectAsync hiçbir şey
        // yapmaz — bu ucun "token saklamaz" sözü varsayılan olarak sürer.
        // Hata exchange'i DÜŞÜRMEZ: masaüstü FB bağlantısı bottan bağımsız.
        // Hangfire'a kuyruklanMAZ — token job argümanı olarak Hangfire
        // deposuna düz metin yazılırdı.
        try
        {
            await _igAccounts.TryConnectAsync(
                GetCustomerId(), result.Value!.AccessToken, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IG DM botu bağlama denemesi düştü (exchange etkilenmedi).");
        }
```

ve sınıfa (MeController'daki desenle):

```csharp
    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
```

Sınıf başındaki "Yetki: lisans kimliğine bağlı değil" yorumunu güncelle: takas hâlâ tenant verisi okumaz; kimlik yalnız opt-in bot bağlaması için kullanılır.

- [ ] **Step 2: Test ekle** — mevcut exchange testinin fixture'ına InMemory DB + bayraklı config tohumla; başarılı exchange sonrası `db.InstagramAccounts`'ta kayıt oluştuğunu, bayrak kapalıyken oluşmadığını doğrula. (Mevcut test ApiFactory üzerinden gidiyorsa ApiFactory'nin DB'sine tohumla; unit ise ctor'a stub servis ver.)

- [ ] **Step 3: Tüm sunucu testlerini koştur** — PASS. Exchange'in mevcut testleri kırılmamalı.

- [ ] **Step 4: Commit** — `feat(instagram-dm): FB exchange'e opt-in IG bağlama kancası`

---

### Task 7: Webhook ucu (`InstagramWebhookController`)

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/InstagramWebhookController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/InstagramWebhookControllerTests.cs` — WhatsApp webhook testlerinin dosyasını bul (`Grep "whatsapp/webhook" OrderDeck.LicenseServer.Tests`) ve desenini birebir izle (ApiFactory mi unit mi — oradaki neyse).

- [ ] **Step 1: Failing testler** (senaryolar; kurulum biçimini WhatsApp webhook testinden kopyala):
  1. `Bayrak_kapaliyken_get_ve_post_404` — `InstagramDm__Enabled` yok → her iki metod 404.
  2. `Verify_dogru_tokenla_challenge_doner` — GET `?hub.mode=subscribe&hub.verify_token=<doğru>&hub.challenge=abc` → 200, gövde `abc`.
  3. `Verify_yanlis_token_403`.
  4. `Imzasiz_post_403` — gövde var, `X-Hub-Signature-256` yok/yanlış → 403, job kuyruklanMAZ.
  5. `Imzali_post_200_ve_job_kuyruklanir` — HMAC-SHA256(AppSecret, body) imzalı POST → 200 (job doğrulaması WhatsApp testinde nasılsa öyle).

- [ ] **Step 2: Koştur, FAIL gör.**

- [ ] **Step 3: Implementasyon** — `WhatsAppWebhookController`'ın kopyası, farklar: route `api/v1/instagram/webhook`; options `InstagramDmOptions` (VerifyToken) + `FacebookOptions` (AppSecret — masaüstü app'i, webhook o app'e bağlı); **her iki metodun başında** `if (!_opt.Ready) return NotFound();` (karanlık yayın, spec §5); imza `WhatsAppSignatureValidator.IsValid(signature, rawBody, _fb.AppSecret)` (sınıf zaten genel amaçlı; adı yanıltmasın); kuyruk `_jobs.Enqueue<InstagramLiveCommentJob>(j => j.ProcessAsync(rawBody, CancellationToken.None));`. MaxBodyBytes, hızlı-200, FixedTimeEquals aynen.

- [ ] **Step 4: Koştur, PASS gör.**

- [ ] **Step 5: Commit** — `feat(instagram-dm): live_comments webhook ucu (karanlık bayraklı)`

---

### Task 8: `InstagramLiveCommentJob` — tetik, hız sınırı, DM

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Instagram/InstagramLiveCommentJob.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Instagram/InstagramLiveCommentJobTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (`builder.Services.AddScoped<...InstagramLiveCommentJob>();` — `WhatsAppInboundJob` nasıl kayıtlıysa öyle)

- [ ] **Step 1: Failing testler** — InMemory DB'ye `InstagramAccount` (+ lisans + slug'lı aktif `IntakeFormConfig`) tohumla; `InstagramPrivateReplyClient` yerine Task 4'teki StubHandler'lı gerçek istemciyi ver (ayrı interface çıkarma — YAGNI; stub handler yeterli). Payload sabiti:

```csharp
    private static string Payload(string igUserId, string commentId, string fromId,
        string fromUsername, string text) => $$"""
        {"object":"instagram","entry":[{"id":"{{igUserId}}","time":1725400000,
         "changes":[{"field":"live_comments","value":{"id":"{{commentId}}",
          "from":{"id":"{{fromId}}","username":"{{fromUsername}}"},
          "text":"{{text}}","media":{"id":"m1","media_product_type":"LIVE"}}}]}]}
        """;
```

Senaryolar:
  1. `Kayit_yazinca_dm_gider_ve_linkte_gecerli_token_var` — text `!kayıt` → private reply isteği `/{PageId}/messages`e gitti; gövdedeki link `https://orderdeckapp.com/musteri-kayit/{slug}?ig=` ile başlıyor; linkten çekilen token `IntakeIgTokenService.TryRead` ile açılıyor ve `(slug, fromUsername)` veriyor.
  2. `Buyuk_harf_ve_noktasiz_i_varyantlari_tetikler` — `!KAYIT`, `!Kayit`, `!kayit` üçü de DM üretir.
  3. `Alakasiz_yorum_dm_uretmez` — `merhaba 105 yazdım` → Graph çağrısı yok.
  4. `Bilinmeyen_ig_hesabi_sessizce_atlanir` — DB'de olmayan `entry.id` → çağrı yok, exception yok.
  5. `Ayni_izleyiciye_bir_saat_icinde_ikinci_dm_gitmez` — aynı `from.id` iki yorum → tek Graph çağrısı.
  6. `Pasif_form_dm_uretmez` — `IntakeFormConfig.IsActive=false` → çağrı yok.

- [ ] **Step 2: Koştur, FAIL gör.**

- [ ] **Step 3: Implementasyon**

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// Webhook'tan kuyruklanan live_comments işleyicisi. "!kayıt" tetiğinde
/// tokenlı kayıt linkini private reply DM'iyle gönderir.
///
/// <para><b>Retry etme:</b> private reply penceresi yayın süresiyle sınırlı ve
/// yorum başına 1 hak var — düşen gönderim loglanır, yeniden denenmez.
/// Bu yüzden metot exception fırlatmaz (Hangfire retry'ı tetiklemesin).</para>
/// </summary>
public sealed class InstagramLiveCommentJob
{
    private static readonly TimeSpan DmCooldown = TimeSpan.FromHours(1);

    private readonly LicenseDbContext _db;
    private readonly InstagramAccountService _accounts;
    private readonly InstagramPrivateReplyClient _reply;
    private readonly IntakeIgTokenService _tokens;
    private readonly IMemoryCache _cache;
    private readonly ILogger<InstagramLiveCommentJob> _log;

    public InstagramLiveCommentJob(
        LicenseDbContext db, InstagramAccountService accounts,
        InstagramPrivateReplyClient reply, IntakeIgTokenService tokens,
        IMemoryCache cache, ILogger<InstagramLiveCommentJob> log)
    {
        _db = db; _accounts = accounts; _reply = reply;
        _tokens = tokens; _cache = cache; _log = log;
    }

    /// <summary>Yorum tetik mi? Normalize: trim + invariant küçük harf +
    /// noktasız ı→i (Türkçe klavye/otomatik düzeltme her iki biçimi üretir).</summary>
    public static bool IsTrigger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var norm = text.Trim().ToLowerInvariant().Replace('ı', 'i');
        return norm == "!kayit";
    }

    public async Task ProcessAsync(string rawBody, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawBody); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Instagram webhook gövdesi ayrıştırılamadı.");
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entry", out var entries)) return;
            foreach (var entry in entries.EnumerateArray())
            {
                var igUserId = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (igUserId is null || !entry.TryGetProperty("changes", out var changes)) continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (change.TryGetProperty("field", out var f) && f.GetString() == "live_comments"
                        && change.TryGetProperty("value", out var value))
                        await HandleCommentAsync(igUserId, value, ct);
                }
            }
        }
    }

    private async Task HandleCommentAsync(string igUserId, JsonElement value, CancellationToken ct)
    {
        var text = value.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (!IsTrigger(text)) return;

        var commentId = value.TryGetProperty("id", out var c) ? c.GetString() : null;
        string? fromId = null, fromUsername = null;
        if (value.TryGetProperty("from", out var from))
        {
            fromId = from.TryGetProperty("id", out var fi) ? fi.GetString() : null;
            fromUsername = from.TryGetProperty("username", out var fu) ? fu.GetString() : null;
        }
        if (commentId is null || fromId is null || string.IsNullOrWhiteSpace(fromUsername)) return;

        var acc = await _db.InstagramAccounts
            .FirstOrDefaultAsync(a => a.IgUserId == igUserId && a.Status == "active", ct);
        if (acc is null) return; // tanımadığımız hesap — bot kapatılmış olabilir

        var config = await _db.Set<IntakeFormConfig>()
            .Where(cfg => cfg.IsActive && cfg.InstagramDmBotEnabled)
            .Join(_db.Licenses.Where(l => l.Id == acc.LicenseId),
                cfg => cfg.CustomerId, l => l.CustomerId, (cfg, _) => cfg)
            .FirstOrDefaultAsync(ct);
        if (config is null) return;

        // Hız sınırı: aynı izleyiciye saatte 1 DM (spec §3). Süreç içi cache
        // yeter — pencere kısa, restart'ta sıfırlanması kabul edilebilir.
        var cooldownKey = $"igdm:{igUserId}:{fromId}";
        if (_cache.TryGetValue(cooldownKey, out _)) return;
        _cache.Set(cooldownKey, true, DmCooldown);

        var token = _tokens.Create(config.Slug, fromUsername);
        var link = $"https://orderdeckapp.com/musteri-kayit/{Uri.EscapeDataString(config.Slug)}?ig={token}";
        var pageToken = _accounts.UnprotectPageToken(acc);
        if (pageToken is null) return;

        var ok = await _reply.SendAsync(acc.PageId, commentId,
            $"Merhaba! Kayıt formun hazır, Instagram hesabın otomatik bağlanacak: {link}", pageToken,
            ct);
        if (!ok)
        {
            acc.LastError = $"private reply düştü ({DateTimeOffset.UtcNow:O})";
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            _log.LogInformation("IG kayıt DM'i gönderildi — slug={Slug}, viewer={Viewer}",
                config.Slug, fromUsername);
        }
    }
}
```

Not: link taban adresi sabit `https://orderdeckapp.com` — `IntakeLoginOptions.RedirectUri`'nin de sabitlendiği gerekçeyle (tek prod alan adı). Yapılandırılabilirlik YAGNI.

- [ ] **Step 4: Koştur, PASS gör** — 6 senaryo.

- [ ] **Step 5: Commit** — `feat(instagram-dm): !kayıt tetiği → private reply DM işi`

---

### Task 9: Form tarafı — `?ig=TOKEN` → bağlı Instagram çipi

**Files:**
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml`
- Test: intake form testlerinin dosyasını bul (`Grep "baglanti=ok" OrderDeck.LicenseServer.Tests` veya `IntakeForm` testleri) ve oraya ekle

- [ ] **Step 1: Failing testler** (mevcut form testlerinin kurulum biçimiyle):
  1. `Gecerli_ig_tokeni_kimligi_baglar` — GET `/musteri-kayit/{slug}?ig={geçerli}` → yanıtta çip/`@username` görünür; ardından POST'ta `InstagramUsername` doğrulanmış yazılır.
  2. `Yanlis_sluga_ait_token_yok_sayilir` — başka slug'ın token'ı → form normal açılır, çip yok, hata yok.
  3. `Bozuk_token_yok_sayilir` — `?ig=curcuna` → form normal açılır (spec §4: hata ekranı yok).

- [ ] **Step 2: PageModel değişiklikleri** (`IntakeForm.cshtml.cs`):

ctor'a `IntakeIgTokenService igTokens` ve `IWebHostEnvironment env` ekle; alanlar `_igTokens`, `_env`. Property ekle (LinkedFacebook'un altına):

```csharp
    public IntakeLinkedIdentity? LinkedInstagram { get; private set; }
```

`OnGetAsync` başına, `LoadLinkedIdentities()`'ten ÖNCE:

```csharp
        // "!kayıt → DM" linki: token'daki kimlik OAuth kimliğiyle aynı depoya
        // (IntakeLinkStore) yazılır — çip, submit ve unlink akışları tek yoldan
        // işler. Geçersiz/bayat token SESSİZCE yok sayılır: izleyici formu yine
        // doldurabilir, elle alan zaten duruyor.
        var igToken = Request.Query["ig"].ToString();
        if (!string.IsNullOrEmpty(igToken) && igToken.Length <= 512
            && _igTokens.TryRead(igToken) is { } ig
            && string.Equals(ig.Slug, Slug, StringComparison.OrdinalIgnoreCase))
        {
            var nonce = Request.Cookies[IntakeLinkController.CookieName];
            if (string.IsNullOrEmpty(nonce) || nonce.Length > 128)
                nonce = IntakeLinkStore.RandomToken();
            // Çerez seçenekleri IntakeLinkController.Start ile bire bir.
            Response.Cookies.Append(IntakeLinkController.CookieName, nonce, new CookieOptions
            {
                HttpOnly = true,
                Secure = _env.IsProduction(),
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromHours(1)
            });
            _linkStore.SaveIdentity(nonce, "instagram",
                new IntakeLinkedIdentity(ig.IgUsername, ig.IgUsername, null));
            LinkedInstagram = _linkStore.GetIdentity(nonce, "instagram");
            (LinkBanner, LinkBannerIsError) =
                ("Instagram hesabın bağlandı. Kalan bilgileri doldurup formu gönder.", false);
        }
```

`LoadLinkedIdentities()`'e ekle:

```csharp
        LinkedInstagram ??= _linkStore.GetIdentity(nonce, "instagram");
```

`OnPostSubmitAsync`'te `ig` çözümlemesini bağlı kimliğe bağla (mevcut satırı değiştir):

```csharp
        var (ig, _) = LinkedInstagram is null
            ? Resolve("Input.InstagramUsername", HandleValidator.Instagram, Input.InstagramUsername)
            : (null, null);
```

LinkedFacebook bloğunun (satır ~239) hemen altına:

```csharp
        // Instagram: DM token'ından gelen kullanıcı adı. Elle girişle aynı
        // normalize/doğrulama kapısından geçer (LinkedYouTube handle deseni);
        // geçemezse sessizce boş kalır — kendi verimiz müşteriyi kilitlemesin.
        if (LinkedInstagram is not null)
        {
            var linkedIg = HandleValidator.Normalize(LinkedInstagram.Handle);
            if (HandleValidator.Validate(HandleValidator.Instagram, linkedIg) is null)
                ig = linkedIg;
        }
```

"En az bir platform" kontrolüne `LinkedInstagram is null &&` ekle. Submit sonrası temizlik bloğuna (`RemoveIdentity` satırlarının yanına) `_linkStore.RemoveIdentity(linkNonce, "instagram");` ekle. `OnPostUnlinkAsync`'teki platform kümesine `or "instagram"` ekle.

- [ ] **Step 3: Razor** (`IntakeForm.cshtml`) — `LinkedFacebook` çipinin çizildiği bloğu bul (`Grep LinkedFacebook`), Instagram alanının yanına aynı yapıda `Model.LinkedInstagram` bloğu ekle: bağlıyken elle giriş kutusu yerine `@("@" + Model.LinkedInstagram.Handle)` çipi + "Bağlantıyı kaldır" unlink butonu (`asp-page-handler="Unlink"`, `platform=instagram`); değilken mevcut kutu aynen. Var olan sınıf/markup'ı KOPYALA — yeni stil icat etme. Dikkat: `.cshtml` tag helper özniteliğinde `@` gerekiyorsa `&#64;` kullan (bkz. `reference_razor_att_escape_bug` — 2026-09-03'te prod formu kilitledi).

- [ ] **Step 4: Koştur, PASS gör** — yeni 3 test + mevcut form testleri.

- [ ] **Step 5: Commit** — `feat(instagram-dm): formda ?ig= token'ı ile bağlı Instagram çipi`

---

### Task 10: Admin toggle — `InstagramDmBotEnabled`

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs` (IntakeFormConfig alanlarının okunduğu/yazıldığı uçlar — `Grep IntakeFormConfig` o dosyada)
- Test: aynı controller'ın mevcut test dosyasına senaryo ekle

- [ ] **Step 1:** Config GET/PUT DTO'larına `bool InstagramDmBotEnabled` alanı ekle, entity'ye eşle (mevcut `IsActive`/`CustomTitle` alanları hangi uçtan nasıl akıyorsa birebir aynı yol).
- [ ] **Step 2:** Test: PUT ile `true` yaz → GET'te `true` döner ve DB'de yazılıdır.
- [ ] **Step 3:** Koştur, PASS gör. Commit — `feat(instagram-dm): admin panelden bot bayrağı`

---

### Task 11: Deploy + dokümantasyon

**Files:**
- Modify: `deploy/docker-compose.yml` — `IntakeLogin__*` bayraklarının geçirildiği yere ekle (**`:-false` varsayılanıyla** — #363 dersi: varsayılansız bayrak compose'u kırar):
  `InstagramDm__Enabled: ${InstagramDm__Enabled:-false}` ve `InstagramDm__VerifyToken: ${InstagramDm__VerifyToken:-}`
- Modify: `docs/kayit-formu-giris.md` — yeni bölüm: "Instagram '!kayıt' → DM" — env değişkenleri, Meta paneli el adımları:
  1. Masaüstü app (3939617702835404) → Facebook Login for Business → login config'e `instagram_manage_messages` + `pages_manage_metadata` scope'ları.
  2. App → Webhooks → Instagram nesnesi → callback `https://license.orderdeckapp.com/api/v1/instagram/webhook`, verify token = `.env`'deki `InstagramDm__VerifyToken`, `live_comments` alanına abone. (Doğrulama isteğinin çalışması için önce `Enabled=true` + restart gerekir — sıra: env → restart → Meta panelinde doğrula.)
  3. App Review: `instagram_manage_messages` advanced access başvurusu (ekran kaydı ister). Onaya kadar yalnız app'te rolü olan hesaplar test edebilir.
  4. Yayın sonrası doğrulama listesi: admin bayrağı aç → WPF'ten FB'ye yeniden bağlan → `InstagramAccounts` satırını DB'den kontrol et → canlı yayında `!kayıt` yaz → DM linki → formda çip → kayıtta `InstagramUsername`.
- [ ] Commit — `docs(instagram-dm): kurulum ve Meta panel adımları`

---

### Task 12: Kapanış

- [ ] **Step 1:** Tüm sunucu testleri: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj` → PASS (Docker açık olmalı — Testcontainers).
- [ ] **Step 2:** WPF tarafı etkilenmedi ama derlemeyi doğrula: `dotnet build OrderDeck.App/OrderDeck.App.csproj`.
- [ ] **Step 3:** PR aç: `feat(intake): Instagram '!kayıt' yorumuna DM ile kayıt linki` — açıklamada spec linki + "karanlık yayın: InstagramDm__Enabled yazılmadan davranış değişmez". **Merge kullanıcıya ait.**

---

## Kenar notları (uygulayan için)

- **Neden interface yok:** `InstagramPrivateReplyClient`/`InstagramAccountService` somut sınıf — testler StubHandler'lı gerçek `HttpClient` kullanıyor (FacebookNameClient emsali). Soyutlama YAGNI.
- **Neden Hangfire'da token yok:** job argümanları Hangfire deposuna düz metin serileşir; token yalnız DB'de şifreli ve bellekte yaşar.
- **Private reply retry YOK:** pencere yayın süresi + yorum başına 1 hak; retry Meta'dan hata almaya mahkûm. Düşen gönderim `LastError`'a yazılır.
- **`WhatsAppSignatureValidator` yeniden kullanılıyor** — Meta imza şeması ürün bağımsız aynı. Adı genelleştirmek (rename) İSTENMEDİ, dokunma.
