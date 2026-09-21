using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// §5.2b — SMS onayı TOPLAMA NOKTASINDA (kayıt) alınır ve İYS kaydı MARKA
/// başına düşer. Kritik delik: mevcut shopper ikinci yayıncıya kaydolurken
/// (reuse dalı) onay kutusu işaretliyse eski kod hiçbir şey yazmıyordu.
/// </summary>
public sealed class ShopperAuthRegisterConsentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthRegisterConsentTests(ApiFactory factory) => _factory = factory;

    private sealed record RegisterRequest(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username,
        string? Email = null, string? Tc = null, bool SmsConsent = false);

    private static string UniquePhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
    private static string NewPassword() => $"Pw-{Guid.NewGuid():N}";
    private static string UniqueCode() => ("rc" + Guid.NewGuid().ToString("N"))[..16];
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();
    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    private async Task<(Guid LicenseId, string ShopperCode, string BrandCode)>
        SeedBroadcasterAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Yayinci",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        var code = UniqueCode();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-{Guid.NewGuid():N}"[..24],
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });

        var brandCode = NewBrandCode();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return (licenseId, code, brandCode);
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string code, string phone, string password, bool consent)
        => client.PostAsJsonAsync("/api/v1/shopper/auth/register", new RegisterRequest(
            code, "Test Alici", phone, password, "Adres 1", "youtube",
            $"u{Guid.NewGuid():N}"[..12], SmsConsent: consent));

    [Fact]
    public async Task Yeni_shopper_kaydinda_onay_ve_ispat_alanlari_yazilir()
    {
        var (_, code, brand) = await SeedBroadcasterAsync();
        var client = _factory.CreateClient();
        var phone = UniquePhone();

        var resp = await RegisterAsync(client, code, phone, NewPassword(), consent: true);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var consentRow = await db.IysConsents.AsNoTracking()
            .SingleAsync(c => c.BrandCode == brand && c.Recipient == phone);
        consentRow.Status.Should().Be(IysConsentStatus.Onay);

        var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Phone == phone);
        shopper.SmsConsent.Should().BeTrue();
        shopper.SmsConsentSource.Should().Be("register");
    }

    [Fact]
    public async Task Mevcut_shopper_ikinci_yayinciya_kaydolunca_onay_o_markaya_da_yazilir()
    {
        var (_, codeA, _) = await SeedBroadcasterAsync();
        var (_, codeB, brandB) = await SeedBroadcasterAsync();
        var client = _factory.CreateClient();
        var phone = UniquePhone();
        var password = NewPassword();

        (await RegisterAsync(client, codeA, phone, password, consent: false))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        // Reuse dalı: aynı telefon + aynı parola, ikinci yayıncı, onay işaretli.
        (await RegisterAsync(client, codeB, phone, password, consent: true))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var consentRow = await db.IysConsents.AsNoTracking()
            .SingleAsync(c => c.BrandCode == brandB && c.Recipient == phone);
        consentRow.Status.Should().Be(IysConsentStatus.Onay);

        var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Phone == phone);
        shopper.SmsConsent.Should().BeTrue();
        shopper.SmsConsentSource.Should().Be("register");
    }

    [Fact]
    public async Task Onay_kutusu_isaretsizse_yeniden_kayitta_onay_yazilmaz()
    {
        var (_, codeA, brandA) = await SeedBroadcasterAsync();
        var (_, codeB, brandB) = await SeedBroadcasterAsync();
        var client = _factory.CreateClient();
        var phone = UniquePhone();
        var password = NewPassword();

        (await RegisterAsync(client, codeA, phone, password, consent: false))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await RegisterAsync(client, codeB, phone, password, consent: false))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.IysConsents.AsNoTracking()
                .Where(c => (c.BrandCode == brandA || c.BrandCode == brandB)
                            && c.Recipient == phone)
                .AnyAsync())
            .Should().BeFalse("sessizlik ret değildir ama onay hiç verilmedi");
    }
}
