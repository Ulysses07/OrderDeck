using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// Onayın yazıldığı her uç İYS kaydını da üretmeli. Bu testler boru hattının
/// <b>girişini</b> korur: kaynak uçlardan biri toplayıcıyı çağırmayı bırakırsa
/// o onay hiç İYS'ye gitmez ve kimse fark etmez — 2026-09-17'de tam olarak
/// bu oldu (form onayları hiçbir yerde okunmuyordu).
/// </summary>
public sealed class IysConsentWiringTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IysConsentWiringTests(ApiFactory factory) => _factory = factory;

    private static string NewPhone() => $"+90555{Random.Shared.Next(1000000, 9999999)}";

    [Fact]
    public async Task Form_onayi_IysConsent_satiri_uretir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var phone = NewPhone();

        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Slug = $"iys-{Guid.NewGuid():N}",
            WhatsAppPhone = "+905550000000",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.IntakeFormConfigs.Add(config);
        await db.SaveChangesAsync();

        await svc.SaveSubmissionAsync(
            config.Id,
            youTubeUsername: null, instagramUsername: null,
            facebookUsername: null, tikTokUsername: null,
            legacyUsername: "u", fullName: "Ad Soyad", address: "Adres", phone: phone,
            email: null, tckn: null, whatsAppConsent: false, smsConsent: true,
            ipAddress: "203.0.113.9", userAgent: "wiring-test");

        var row = await db.IysConsents.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Recipient == phone);
        row.Should().NotBeNull();
        row!.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending);

        var ev = await db.IysConsentEvents.AsNoTracking()
            .SingleAsync(e => e.Recipient == phone);
        ev.SourceTable.Should().Be("IntakeFormSubmission");
        ev.ProofIp.Should().Be("203.0.113.9", "6563 ispat yükü olay tablosunda yaşar");
    }

    [Fact]
    public async Task Form_isaretsiz_kutusu_ret_yazmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var phone = NewPhone();

        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Slug = $"iys-{Guid.NewGuid():N}",
            WhatsAppPhone = "+905550000000",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.IntakeFormConfigs.Add(config);
        await db.SaveChangesAsync();

        await svc.SaveSubmissionAsync(
            config.Id,
            youTubeUsername: null, instagramUsername: null,
            facebookUsername: null, tikTokUsername: null,
            legacyUsername: "u", fullName: "Ad Soyad", address: "Adres", phone: phone,
            email: null, tckn: null, whatsAppConsent: false, smsConsent: false,
            ipAddress: "203.0.113.9", userAgent: "wiring-test");

        (await db.IysConsents.AnyAsync(c => c.Recipient == phone)).Should().BeFalse(
            "6563: sessizlik ret değildir — işaretsiz kutu geri çekme sayılmaz");
    }

    [Fact]
    public async Task Kayit_onayi_IysConsent_satiri_uretir()
    {
        var phone = NewPhone();
        await RegisterAsync(phone, smsConsent: true);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.IysConsents.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Recipient == phone);
        row.Should().NotBeNull();
        row!.Status.Should().Be(IysConsentStatus.Onay);

        var ev = await db.IysConsentEvents.AsNoTracking()
            .SingleAsync(e => e.Recipient == phone);
        ev.SourceTable.Should().Be("Shopper");
    }

    [Fact]
    public async Task Kayit_isaretsiz_kutusu_ret_yazmaz()
    {
        var phone = NewPhone();
        await RegisterAsync(phone, smsConsent: false);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.IysConsents.AnyAsync(c => c.Recipient == phone)).Should().BeFalse(
            "kayıt ekranında kutuyu işaretlememek geri çekme değildir");
    }

    [Fact]
    public async Task Profilden_acik_ret_Ret_yazar()
    {
        var phone = NewPhone();
        var client = await RegisterAsync(phone, smsConsent: true);

        var resp = await client.PatchAsJsonAsync(
            "/api/v1/shopper/me", new { smsConsent = false });
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.IysConsents.AsNoTracking().SingleAsync(c => c.Recipient == phone);
        row.Status.Should().Be(IysConsentStatus.Ret,
            "profilde kutuyu kaldırmak kişinin kendi açık eylemi — bu ret");
    }

    [Fact]
    public async Task Profilden_acik_onay_Onay_yazar()
    {
        var phone = NewPhone();
        var client = await RegisterAsync(phone, smsConsent: false);

        var resp = await client.PatchAsJsonAsync(
            "/api/v1/shopper/me", new { smsConsent = true });
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.IysConsents.AsNoTracking().SingleAsync(c => c.Recipient == phone);
        row.Status.Should().Be(IysConsentStatus.Onay);
        row.ConsentDate.Should().NotBeNull(
            "ispat tarihi ile durum damgası aynı ana bakmalı");
    }

    // ── Kayıt yardımcısı (ShopperMePatchTests'ten kopya; paylaşılan yardımcı
    //    sınıf oluşturmak bu görevin kapsamı dışında) ───────────────────────────

    private sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username,
        string? Email = null,
        string? Tc = null,
        bool SmsConsent = false);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        object[] Broadcasters);

    /// <summary>Yeni yayıncı kodu + lisans kurar, o kodla shopper kaydı açar ve
    /// jetonu iliştirilmiş istemciyi döndürür.</summary>
    private async Task<HttpClient> RegisterAsync(string phone, bool smsConsent)
    {
        var client = _factory.CreateClient();
        var code = ("iyswire" + Guid.NewGuid().ToString("N"))[..16];
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"cust-{Guid.NewGuid():N}@x.test",
                Name = "IysWiring-" + Guid.NewGuid().ToString("N")[..6],
                PasswordHash = $"ph-{Guid.NewGuid():N}",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Customers.Add(customer);
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                SkuCode = "STD",
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
                LicenseKey = $"key-{Guid.NewGuid():N}",
                ShopperCode = code,
                ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var req = new RegisterRequest(
            code, "Wiring User", phone, $"Pw-{Guid.NewGuid():N}",
            "Ankara", "youtube", $"u{Guid.NewGuid():N}"[..12], SmsConsent: smsConsent);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }
}
