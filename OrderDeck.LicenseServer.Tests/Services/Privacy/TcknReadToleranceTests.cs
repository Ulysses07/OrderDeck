using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>
/// Genişlet adımının (1. sürüm/PR-0a) okuma-toleransını uçtan uca doğrular:
/// Shopper.TcProtected ve IntakeFormSubmission.TcknProtected bu sürümde hâlâ
/// düz metin YAZILIYOR, ama her okuma Unprotect'ten geçtiği için düz metni
/// VEYA şifreli metni (PR-0b'nin — 2. sürümün — yazacağı biçim) ikisini de
/// doğru çözüyor. Birim seviyesindeki şifreleme/çözme davranışı
/// <see cref="TcknProtectorTests"/>'te; burada asıl soru "bu sürüm, henüz
/// kendisi hiç yazmadığı bir biçimi okuyabiliyor mu".
/// </summary>
public sealed class TcknReadToleranceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public TcknReadToleranceTests(ApiFactory factory) => _factory = factory;

    // ── DTO'lar (Shopper auth/me) — alan adları PascalCase, System.Text.Json'ın
    // Web varsayılanı (camelCase + case-insensitive) karşıya/karşıdan eşliyor. ──

    private sealed record RegisterRequest(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username,
        string? Email = null, string? Tc = null, bool SmsConsent = false);

    private sealed record AuthResponse(
        string AccessToken, DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken, DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId, object[] Broadcasters);

    private sealed record NotificationPrefsDto(bool Broadcast, bool Orders, bool Payments);

    private sealed record MeResponse(
        Guid Id, string FullName, string Phone, string Address,
        string? Email, string? Tc, NotificationPrefsDto NotificationPrefs,
        bool SmsConsent, object[] Broadcasters);

    private sealed record PatchMeRequest(string? FullName = null, string? Tc = null);

    private sealed record ProblemBody(string? Title, int? Status);

    // ── DTO'lar (form gönderimi) ─────────────────────────────────────────────

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record SubmissionBody(Guid id, string username, string? tckn, DateTimeOffset submittedAt);

    // ── Shopper yardımcıları ─────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private static string UniqueCode() =>
        ("tckn" + Guid.NewGuid().ToString("N"))[..16];

    private async Task<string> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "Tckn-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = UniqueCode();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return code;
    }

    private async Task<(string accessToken, Guid shopperId)> RegisterShopperAsync(
        HttpClient client, string? tc)
    {
        var code = await SeedLicenseAsync();
        var phone = UniquePhone();
        var girisDegeri = $"pw-{Guid.NewGuid():N}";
        var req = new RegisterRequest(
            code, "Tckn Test", phone, girisDegeri, "Ankara", "youtube",
            "tcknuser" + Guid.NewGuid().ToString("N")[..6], Tc: tc);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    // ── 1. Şifreli yazılmış bir Shopper satırı → GET /me düz metin döner ─────
    // (PR-0b'nin yazacağı biçimi taklit eder — bu testin asıl kanıtladığı şey:
    // bu sürüm henüz kendisi hiç yazmadığı bir biçimi de okuyabiliyor.)

    [Fact]
    public async Task Shopper_row_with_ciphertext_is_still_readable_via_get_me()
    {
        var plain = TestTckn.NewValid();
        var phone = UniquePhone();
        Guid shopperId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
            shopperId = Guid.NewGuid();
            db.Shoppers.Add(new Shopper
            {
                Id = shopperId,
                FullName = "Encrypted Shopper",
                Phone = phone,
                PasswordHash = $"h-{Guid.NewGuid():N}",
                Address = "Adres",
                TcProtected = protector.Protect(plain), // PR-0b'nin yazacağı biçim
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            (token, _) = jwt.IssueShopperToken(shopperId, phone, 0);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/shopper/me");
        me!.Tc.Should().Be(plain);
    }

    // ── 2. Tc ile kayıt → DB'de de API'de de düz metin (bu sürüm yazmayı
    // henüz şifrelemiyor; PR-0b bu testi tersine çevirecek) ──────────────────

    [Fact]
    public async Task Register_with_tc_stores_plaintext_and_returns_plaintext_over_api()
    {
        var client = _factory.CreateClient();
        var plain = TestTckn.NewValid();
        var (token, shopperId) = await RegisterShopperAsync(client, plain);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = await db.Shoppers.FirstAsync(s => s.Id == shopperId);
            shopper.TcProtected.Should().Be(plain);
            shopper.TcProtected.Should().NotStartWith("CfDJ8");
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/shopper/me");
        me!.Tc.Should().Be(plain);
    }

    // ── 3. PATCH ile geçerli Tc → DB'de de yanıtta da düz metin ──────────────

    [Fact]
    public async Task PatchMe_with_tc_stores_plaintext_and_returns_plaintext()
    {
        var client = _factory.CreateClient();
        var (token, shopperId) = await RegisterShopperAsync(client, null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var plain = TestTckn.NewValid();
        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me", new PatchMeRequest(Tc: plain));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body!.Tc.Should().Be(plain);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.FirstAsync(s => s.Id == shopperId);
        shopper.TcProtected.Should().Be(plain);
        shopper.TcProtected.Should().NotStartWith("CfDJ8");
    }

    // ── 4. PATCH TC'siz (başka bir alan değişir) → var olan şifreli TC'ye
    // dokunmaz: ne DB'de ne yanıtta değişir (PATCH'in Tc'yi "yeniden yazmadığı"
    // davranışı — Unprotect yalnız okur, req.Tc is not null koruması yazar) ──

    [Fact]
    public async Task PatchMe_without_tc_leaves_existing_ciphertext_untouched()
    {
        var plain = TestTckn.NewValid();
        var phone = UniquePhone();
        Guid shopperId;
        string ciphertext;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
            shopperId = Guid.NewGuid();
            ciphertext = protector.Protect(plain)!;
            db.Shoppers.Add(new Shopper
            {
                Id = shopperId,
                FullName = "Original Name",
                Phone = phone,
                PasswordHash = $"h-{Guid.NewGuid():N}",
                Address = "Adres",
                TcProtected = ciphertext, // PR-0b'nin yazacağı biçim
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            (token, _) = jwt.IssueShopperToken(shopperId, phone, 0);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Tc alanı OLMADAN PATCH — TC'ye hiç dokunmamalı.
        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me", new PatchMeRequest(FullName: "Updated Name"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body!.Tc.Should().Be(plain);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await verifyDb.Shoppers.FirstAsync(s => s.Id == shopperId);
        shopper.TcProtected.Should().Be(ciphertext);
    }

    // ── 5. Kırpma sonrası 11 haneyi aşan Tc → 400 invalid-tc, shopper oluşmaz ─

    [Fact]
    public async Task Register_with_overlong_tc_returns_400_and_creates_no_shopper()
    {
        var client = _factory.CreateClient();
        var code = await SeedLicenseAsync();
        var phone = UniquePhone();
        var overlong = TestTckn.NewValid() + "1"; // 12 karakter
        var girisDegeri = $"pw-{Guid.NewGuid():N}";
        var req = new RegisterRequest(
            code, "Tckn Test", phone, girisDegeri, "Ankara", "youtube",
            "tcknuser" + Guid.NewGuid().ToString("N")[..6], Tc: overlong);

        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemBody>();
        problem!.Title.Should().Be("invalid-tc");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Shoppers.AnyAsync(s => s.Phone == phone)).Should().BeFalse();
    }

    // ── 6. CfDJ8 önekiyle başlayan Tc ile kayıt → 400 invalid-tc, shopper
    // oluşmaz (çözme kâhini koruması — bkz. TcknProtector sınıf dokümanı;
    // gerçek kullanıcılar etkilenmez, mobil uygulama rakam dışını ayıklar) ───

    [Fact]
    public async Task Register_with_data_protection_prefix_returns_400_and_creates_no_shopper()
    {
        var client = _factory.CreateClient();
        var code = await SeedLicenseAsync();
        var phone = UniquePhone();
        // 11 karakter — gerçek bir şifreli metin gibi GÖRÜNÜYOR ama rastgele üretildi.
        var suspicious = "CfDJ8" + Random.Shared.Next(100_000, 999_999);
        var girisDegeri = $"pw-{Guid.NewGuid():N}";
        var req = new RegisterRequest(
            code, "Tckn Test", phone, girisDegeri, "Ankara", "youtube",
            "tcknuser" + Guid.NewGuid().ToString("N")[..6], Tc: suspicious);

        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemBody>();
        problem!.Title.Should().Be("invalid-tc");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Shoppers.AnyAsync(s => s.Phone == phone)).Should().BeFalse();
    }

    // ── 7. Yalnız boşluktan oluşan Tc ile kayıt → 201, TcProtected NULL ──────
    // (boş TC artık >9990 TL'de "TC var" sayılmıyor — Normalize boşluğu null'a çevirir)

    [Fact]
    public async Task Register_with_blank_tc_succeeds_and_stores_null()
    {
        var client = _factory.CreateClient();
        var (_, shopperId) = await RegisterShopperAsync(client, "   ");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.FirstAsync(s => s.Id == shopperId);
        shopper.TcProtected.Should().BeNull();
    }

    // ── 8. Eski düz-metin satır → GET /me değiştirmeden döner ────────────────

    [Fact]
    public async Task Legacy_plaintext_tc_is_returned_unchanged_by_get_me()
    {
        var plain = TestTckn.NewValid();
        var phone = UniquePhone();
        Guid shopperId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            shopperId = Guid.NewGuid();
            db.Shoppers.Add(new Shopper
            {
                Id = shopperId,
                FullName = "Legacy Shopper",
                Phone = phone,
                PasswordHash = $"h-{Guid.NewGuid():N}",
                Address = "Adres",
                TcProtected = plain, // göç öncesi satırı taklit eder — düz metin
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            (token, _) = jwt.IssueShopperToken(shopperId, phone, 0);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/shopper/me");
        me!.Tc.Should().Be(plain);
    }

    // ── 9. Form gönderimleri: biri PR-0b'nin biçiminde (şifreli) doğrudan
    // seed edilmiş, öteki GERÇEK IntakeFormService yoluyla (düz metin) —
    // GET'te ikisi de düz metin döner. Düz-metin satır bilerek SERVİS
    // ÜZERİNDEN yazılıyor: "PR-0a hiç şifreli yazmaz" iddiasını üretim
    // koduna bağlar, varsayıma değil ────────────────────────────────────────

    [Fact]
    public async Task Intake_form_returns_both_encrypted_and_plaintext_submissions_as_plaintext()
    {
        var (authedClient, customerId) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        var putResp = await authedClient.PutAsJsonAsync("/api/v1/me/intake-form",
            new { slug, whatsAppPhone = "+905551234567" });
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var encryptedPlain = TestTckn.NewValid();
        var plaintextPlain = TestTckn.NewValid();
        Guid encryptedId;
        Guid plaintextId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
            var cfg = await db.IntakeFormConfigs.FirstAsync(c => c.CustomerId == customerId);

            // PR-0b'nin yazacağı biçimi taklit eder — doğrudan seed (bugün
            // hiçbir üretim kodu şifreli yazmadığı için başka türlü mümkün değil).
            encryptedId = Guid.NewGuid();
            db.IntakeFormSubmissions.Add(new IntakeFormSubmission
            {
                Id = encryptedId,
                IntakeFormConfigId = cfg.Id,
                Username = "sifreliuser" + Guid.NewGuid().ToString("N")[..6],
                FullName = "Encrypted Form User",
                Address = "Form Adres",
                TcknProtected = protector.Protect(encryptedPlain),
                // Kararlılık ufkunun gerisi — GET bu satırı atlamasın (bkz.
                // ReverseSyncCursor / aynı desen IntakeFormControllerTests'te de var).
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();

            // PR-0a'nın GERÇEK yazma yolu — IntakeFormService üzerinden.
            var service = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
            var plaintextSub = await service.SaveSubmissionAsync(
                cfg.Id,
                youTubeUsername: null, instagramUsername: null,
                facebookUsername: null, tikTokUsername: null,
                legacyUsername: "duzmetinuser" + Guid.NewGuid().ToString("N")[..6],
                fullName: "Plaintext Form User", address: "Form Adres",
                phone: null, email: null, tckn: plaintextPlain,
                whatsAppConsent: false, smsConsent: false,
                ipAddress: null, userAgent: null);
            plaintextId = plaintextSub.Id;

            // Kararlılık ufkunun gerisine çek — GET bu satırı atlamasın.
            var row = await db.IntakeFormSubmissions.FirstAsync(s => s.Id == plaintextId);
            row.SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            // "PR-0a hiç şifreli yazmaz" iddiasını DB'de doğrudan pinliyor.
            row.TcknProtected.Should().Be(plaintextPlain);
            row.TcknProtected.Should().NotStartWith("CfDJ8");
        }

        var getResp = await authedClient.GetAsync("/api/v1/me/form-submissions");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await getResp.Content.ReadFromJsonAsync<List<SubmissionBody>>();
        rows!.Single(r => r.id == encryptedId).tckn.Should().Be(encryptedPlain);
        rows!.Single(r => r.id == plaintextId).tckn.Should().Be(plaintextPlain);
    }

    // ── Form gönderimi yardımcısı — IntakeFormControllerTests.CreateAuthedClientAsync ile aynı desen ──

    private async Task<(HttpClient client, Guid customerId)> CreateAuthedClientAsync()
    {
        var client = _factory.CreateClient();
        var email = $"tckn-ifc-{Guid.NewGuid():N}@x";
        var girisDegeri = $"pw-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, name = "TcknIFC", password = girisDegeri });

        Guid tokenId, customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Email == email);
            customerId = customer.Id;
            var tok = await db.EmailConfirmationTokens
                .Where(t => t.CustomerId == customerId).FirstAsync();
            tokenId = tok.Token;

            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-TCKN-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
            await db.SaveChangesAsync();
        }
        await client.GetAsync($"/api/v1/auth/confirm-email/{tokenId}");

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = girisDegeri });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return (client, customerId);
    }
}
