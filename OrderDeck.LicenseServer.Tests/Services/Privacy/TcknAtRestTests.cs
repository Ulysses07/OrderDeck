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
/// Daralt adımını (2. sürüm/PR-0b) uçtan uca doğrular: Shopper.TcProtected ve
/// IntakeFormSubmission.TcknProtected artık ŞİFRELİ yazılıyor (register, PATCH
/// /me, IntakeFormService.SaveSubmissionAsync — üç yazma yeri de). Okuma hâlâ
/// Unprotect'ten geçtiği için düz metni VEYA şifreli metni ikisini de doğru
/// çözüyor — bu, <see cref="TcknBackfillJob"/> satırları şifreleyene kadar
/// sahada kalacak eski düz metin satırlar için gerekli. Birim seviyesindeki
/// şifreleme/çözme davranışı <see cref="TcknProtectorTests"/>'te; burada asıl
/// sorular (a) gerçek yazma yolları artık şifreli mi, (b) eski düz metin
/// satır hâlâ okunabiliyor mu.
/// </summary>
public sealed class TcknAtRestTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public TcknAtRestTests(ApiFactory factory) => _factory = factory;

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
    // (doğrudan seed — register artık zaten şifreli yazıyor, bkz. test 2;
    // burada asıl kanıtlanan GET'in okuma tarafının bağımsız doğru çalışması.)

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
                TcProtected = protector.Protect(plain), // gerçek yazma biçimi (2. sürüm)
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

    // ── 2. Tc ile kayıt → DB'de şifreli, API'de düz metin (2. sürüm/PR-0b:
    // yazma artık Protect'ten geçiyor) ───────────────────────────────────────

    [Fact]
    public async Task Register_with_tc_stores_ciphertext_and_returns_plaintext_over_api()
    {
        var client = _factory.CreateClient();
        var plain = TestTckn.NewValid();
        var (token, shopperId) = await RegisterShopperAsync(client, plain);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
            var shopper = await db.Shoppers.FirstAsync(s => s.Id == shopperId);
            shopper.TcProtected.Should().StartWith("CfDJ8");
            shopper.TcProtected.Should().NotBe(plain);
            shopper.TcProtected.Should().NotContain(plain);
            protector.Unprotect(shopper.TcProtected).Should().Be(plain);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/shopper/me");
        me!.Tc.Should().Be(plain);
    }

    // ── 3. PATCH ile geçerli Tc → DB'de şifreli, yanıtta düz metin ───────────

    [Fact]
    public async Task PatchMe_with_tc_stores_ciphertext_and_returns_plaintext()
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
        var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
        var shopper = await db.Shoppers.FirstAsync(s => s.Id == shopperId);
        shopper.TcProtected.Should().StartWith("CfDJ8");
        shopper.TcProtected.Should().NotBe(plain);
        protector.Unprotect(shopper.TcProtected).Should().Be(plain);
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
                TcProtected = ciphertext, // gerçek yazma biçimi (2. sürüm)
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

    // ── 9. Form gönderimleri: biri önceden şifrelenmiş bir satırı taklit etmek
    // için doğrudan seed edilmiş (örn. backfill sonrası ya da daha önceki bir
    // şifreli yazım), öteki GERÇEK IntakeFormService yoluyla (2. sürüm/PR-0b:
    // artık ŞİFRELİ) — GET'te ikisi de düz metin döner. Servis satırı bilerek
    // DB'den doğrudan doğrulanıyor: "IntakeFormService artık şifreli yazar"
    // iddiasını üretim koduna bağlar, varsayıma değil ────────────────────────

    [Fact]
    public async Task Intake_form_returns_both_seeded_and_service_written_ciphertext_as_plaintext()
    {
        var (authedClient, customerId) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        var putResp = await authedClient.PutAsJsonAsync("/api/v1/me/intake-form",
            new { slug, whatsAppPhone = TestPhone.NewE164() });
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var seededPlain = TestTckn.NewValid();
        var servicePlain = TestTckn.NewValid();
        Guid seededId;
        Guid serviceId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
            var cfg = await db.IntakeFormConfigs.FirstAsync(c => c.CustomerId == customerId);

            // Önceden şifrelenmiş bir satırı taklit eder — doğrudan seed.
            seededId = Guid.NewGuid();
            db.IntakeFormSubmissions.Add(new IntakeFormSubmission
            {
                Id = seededId,
                IntakeFormConfigId = cfg.Id,
                Username = "sifreliuser" + Guid.NewGuid().ToString("N")[..6],
                FullName = "Encrypted Form User",
                Address = "Form Adres",
                TcknProtected = protector.Protect(seededPlain),
                // Kararlılık ufkunun gerisi — GET bu satırı atlamasın (bkz.
                // ReverseSyncCursor / aynı desen IntakeFormControllerTests'te de var).
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();

            // IntakeFormService'in GERÇEK yazma yolu — 2. sürümde (PR-0b)
            // artık Protect'ten geçiyor.
            var service = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
            var serviceSub = await service.SaveSubmissionAsync(
                cfg.Id,
                youTubeUsername: null, instagramUsername: null,
                facebookUsername: null, tikTokUsername: null,
                legacyUsername: "sifreliuser2" + Guid.NewGuid().ToString("N")[..6],
                fullName: "Freshly Encrypted Form User", address: "Form Adres",
                phone: null, email: null, tckn: servicePlain,
                whatsAppConsent: false, smsConsent: false,
                ipAddress: null, userAgent: null);
            serviceId = serviceSub.Id;

            // Kararlılık ufkunun gerisine çek — GET bu satırı atlamasın.
            var row = await db.IntakeFormSubmissions.FirstAsync(s => s.Id == serviceId);
            row.SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            // "IntakeFormService artık şifreli yazar" iddiasını DB'de
            // doğrudan pinliyor.
            row.TcknProtected.Should().StartWith("CfDJ8");
            row.TcknProtected.Should().NotBe(servicePlain);
            protector.Unprotect(row.TcknProtected).Should().Be(servicePlain);
        }

        var getResp = await authedClient.GetAsync("/api/v1/me/form-submissions");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await getResp.Content.ReadFromJsonAsync<List<SubmissionBody>>();
        rows!.Single(r => r.id == seededId).tckn.Should().Be(seededPlain);
        rows!.Single(r => r.id == serviceId).tckn.Should().Be(servicePlain);
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
