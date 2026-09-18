using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// Spec §5.2 — profil kutusu tek boolean ama İYS onayı marka başına. Geri çekme
/// bağlı TÜM markalara RET yazar; onay verme profilden HİÇ yazılmaz.
///
/// InMemory burada yeterli: iki satırın markası farklı olduğu için
/// `(BrandCode, ChannelType, RecipientType, Recipient)` tekil indeksi zaten
/// devreye girmiyor — kanıtlanan şey indeks değil, kaç satır yazıldığı.
/// </summary>
public sealed class ShopperMeConsentRevokeTests : IClassFixture<ApiFactory>
{
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    private readonly ApiFactory _factory;
    public ShopperMeConsentRevokeTests(ApiFactory factory) => _factory = factory;

    private sealed record RegisterRequest(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username,
        string? Email = null, string? Tc = null, bool SmsConsent = false);

    private sealed record AuthResponse(
        string AccessToken, DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken, DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId, object[] Broadcasters);

    private sealed record PatchMeRequest(bool? SmsConsent = null);

    private static string UniquePhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    /// <summary>Sabit parola metni yazmıyoruz: depo public ve gizli tarayıcı
    /// fixture ile gerçeği ayıramıyor.</summary>
    private static string NewPassword() => $"Pw-{Guid.NewGuid():N}";

    private static string UniqueCode()
        => ("revoke" + Guid.NewGuid().ToString("N"))[..16];

    /// <summary>Doğrulanmış Netgsm hesabı olan bir yayıncı açar ve
    /// (lisans kimliği, shopper kodu) döner.</summary>
    private async Task<(Guid LicenseId, string ShopperCode)> SeedBroadcasterAsync(string brandCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Yayıncı-" + brandCode,
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = UniqueCode();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });

        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = "8503021111",
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return (licenseId, code);
    }

    private static async Task LinkAsync(LicenseDbContext db, Guid shopperId, Guid licenseId)
    {
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "revokeuser",
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Geri_cekme_bagli_TUM_markalara_RET_yazar()
    {
        var (licenseA, codeA) = await SeedBroadcasterAsync(BrandA);
        var (licenseB, _) = await SeedBroadcasterAsync(BrandB);

        var client = _factory.CreateClient();
        var phone = UniquePhone();

        // A'ya onay VEREREK kaydol: shopper.SmsConsent = true.
        var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(codeA, "Revoke User", phone, NewPassword(),
                "Ankara", "youtube", "revokeuser", SmsConsent: true));
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        // İkinci yayıncıya da bağlı.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            await LinkAsync(db, auth.ShopperId, licenseB);
        }

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(SmsConsent: false));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await checkDb.IysConsents
            .Where(c => c.Recipient == phone)
            .ToListAsync();

        rows.Should().HaveCount(2, "kişi iki yayıncıya bağlı, geri çekme ikisine de gitmeli");
        rows.Should().OnlyContain(c => c.Status == IysConsentStatus.Ret);
        rows.Select(c => c.BrandCode).Should().BeEquivalentTo(new[] { BrandA, BrandB });
    }

    [Fact]
    public async Task Boolean_zaten_false_iken_de_RET_uretilir()
    {
        var (_, codeA) = await SeedBroadcasterAsync(BrandA);

        var client = _factory.CreateClient();
        var phone = UniquePhone();

        // Onay VERMEDEN kaydol → shopper.SmsConsent zaten false.
        var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(codeA, "Revoke User", phone, NewPassword(),
                "Ankara", "youtube", "revokeuser", SmsConsent: false));
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(SmsConsent: false));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await checkDb.IysConsents.SingleOrDefaultAsync(c => c.Recipient == phone);

        row.Should().NotBeNull(
            "kişi başka bir yayıncının formundan onay vermiş olabilir; geri çekme " +
            "boolean'ın DEĞİŞMESİNE bağlanırsa o onay yerinde kalır");
        row!.Status.Should().Be(IysConsentStatus.Ret);
        row.BrandCode.Should().Be(BrandA);
    }

    [Fact]
    public async Task Profilden_onay_ISYS_e_yazilmaz()
    {
        var (_, codeA) = await SeedBroadcasterAsync(BrandA);

        var client = _factory.CreateClient();
        var phone = UniquePhone();

        var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(codeA, "Revoke User", phone, NewPassword(),
                "Ankara", "youtube", "revokeuser", SmsConsent: false));
        var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(SmsConsent: true));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await checkDb.IysConsents.AnyAsync(c => c.Recipient == phone))
            .Should().BeFalse("profildeki tek kutu 'hangi yayıncıya izin veriyorum' " +
                              "sorusunu cevaplayamaz; onay yalnız toplama noktasında alınır");

        var shopper = (await checkDb.Shoppers.FindAsync(auth.ShopperId))!;
        shopper.SmsConsent.Should().BeTrue("yerel bayrak yine de açılır");
        shopper.SmsConsentSource.Should().Be("profile");
    }
}
