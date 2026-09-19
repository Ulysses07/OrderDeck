using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Yayıncının kendi Netgsm/İYS kurulum ucu. İki değişmez korunuyor:
/// (1) API şifresi panele ASLA dönmez, (2) uç owner-only — staff operatör
/// yayıncının SMS kimliklerini göremez/değiştiremez.
/// </summary>
public sealed class PanelNetgsmAccountControllerTests : IDisposable
{
    private readonly List<ApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private ApiFactory NewFactory()
    {
        var f = new ApiFactory();
        _factories.Add(f);
        return f;
    }

    private sealed record Seed(HttpClient Client, Guid LicenseId, ApiFactory Factory);

    private static async Task<Seed> SeedTenantAsync(ApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-PNA-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        return new Seed(client, license.Id, factory);
    }

    /// <summary>Hesabı seed eder ve <b>düz metin şifreyi döndürür</b> — şifre
    /// sızıntısı testi yanıtta gerçek sırrı arayabilsin diye.</summary>
    private static async Task<string> SeedAccountAsync(
        ApiFactory factory, Guid licenseId, NetgsmAccountStatus status,
        string? lastError = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var rawPassword = $"pw-{Guid.NewGuid():N}";
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            PasswordProtected = protector.ProtectPassword(rawPassword),
            Header = "ORDERDECK",
            BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
            Status = status,
            LastError = lastError,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return rawPassword;
    }

    [Fact]
    public async Task Hesap_yoksa_bos_gorunum_doner()
    {
        var seed = await SeedTenantAsync(NewFactory());

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "kurulumu olmayan yayıncı 404 değil BOŞ form görmeli");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("none");
        doc.RootElement.GetProperty("passwordSet").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Sifre_panele_DONMEZ()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        var rawPassword = await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Verified);

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");
        var body = await resp.Content.ReadAsStringAsync();

        // 1) Gerçek sır gövdede olmamalı — ham da olsa şifrelenmiş hâliyle de.
        body.Should().NotContain(rawPassword, "düz metin şifre panele dönmez");

        using var doc = JsonDocument.Parse(body);

        // 2) `passwordSet` DIŞINDA şifreye benzeyen alan adı olmamalı.
        //    Ham metinde "assword" aramak işe yaramaz: `passwordSet`'in kendisi
        //    o dizgiyi içerir, test hiçbir doğru DTO ile yeşile dönemezdi.
        var leakyFields = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n.Contains("password", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals("passwordSet", StringComparison.OrdinalIgnoreCase))
            .ToList();
        leakyFields.Should().BeEmpty("yalnız passwordSet bayrağı dönebilir");

        doc.RootElement.GetProperty("passwordSet").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("status").GetString().Should().Be("verified");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Failed_hesapta_sms_kapali_ve_lastError_gorunur()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Failed,
            lastError: "İYS marka kodu bu Netgsm hesabına ait değil (kod 60).");

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("lastError").GetString().Should().Contain("kod 60");
    }

    [Fact]
    public async Task Staff_operator_goremez()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);
        var staff = await PanelOperatorHelper.StaffClientAsync(factory, seed.Client);

        var resp = await staff.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "SMS kimlikleri yayıncının kendi faturalı hesabı — staff görmez");
    }

    [Fact]
    public async Task Aktif_lisansi_olmayan_musteri_400_alir()
    {
        var factory = NewFactory();
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var resp = await client.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
