using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperBroadcastersCodeLookupTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperBroadcastersCodeLookupTests(ApiFactory factory) => _factory = factory;

    private sealed record LookupResponse(Guid LicenseId, string DisplayName);

    private async Task<(Guid licenseId, string customerName)> SeedLicenseAsync(string shopperCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"u-{Guid.NewGuid():N}@x",
            Name = "Örnek Mağaza",
            PasswordHash = "h",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        var sku = await db.Skus.FirstOrDefaultAsync();
        if (sku is null)
        {
            sku = new Sku { Code = "TEST-SKU", DisplayName = "Test", DefaultDurationDays = 365, DefaultActivationSlots = 1 };
            db.Skus.Add(sku);
        }
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = sku.Code,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = $"key-{Guid.NewGuid():N}",
            ShopperCode = shopperCode,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, customer.Name);
    }

    [Fact]
    public async Task Lookup_returns_200_with_license_id_and_display_name()
    {
        var uniqueCode = "lookup200-" + Guid.NewGuid().ToString("N")[..8];
        var (licenseId, name) = await SeedLicenseAsync(uniqueCode);
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/code-lookup?code={uniqueCode}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LookupResponse>();
        body!.LicenseId.Should().Be(licenseId);
        body.DisplayName.Should().Be(name);
    }

    [Fact]
    public async Task Lookup_is_case_insensitive()
    {
        var uniqueCode = "ornekmagaza" + Guid.NewGuid().ToString("N")[..6];
        await SeedLicenseAsync(uniqueCode);
        var resp = await _factory.CreateClient()
            .GetAsync($"/api/v1/shopper/broadcasters/code-lookup?code={uniqueCode.ToUpperInvariant()}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lookup_returns_404_for_unknown_code()
    {
        var resp = await _factory.CreateClient()
            .GetAsync("/api/v1/shopper/broadcasters/code-lookup?code=nosuch");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lookup_returns_400_for_empty_code()
    {
        var resp = await _factory.CreateClient()
            .GetAsync("/api/v1/shopper/broadcasters/code-lookup?code=");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── KVKK K-3: yayıncının veri sorumlusu bilgisi (kayıt ekranı) ───────────

    /// <summary>Shopper'ın okuduğu ad (camelCase <c>dataController</c>; OrderDeck-Shopper
    /// <c>apps/shopper/src/privacy/dataController.ts</c>).</summary>
    private sealed record ControllerDto(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress);
    private sealed record LookupWithController(Guid LicenseId, string DisplayName, ControllerDto? DataController);

    [Fact]
    public async Task Lookup_yayincinin_veri_sorumlusu_bilgisini_doner_girilmediyse_null()
    {
        var withCode = "dc-" + Guid.NewGuid().ToString("N")[..8];
        var withoutCode = "nodc-" + Guid.NewGuid().ToString("N")[..8];
        var (licenseId, _) = await SeedLicenseAsync(withCode);
        await SeedLicenseAsync(withoutCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customerId = await db.Licenses.Where(l => l.Id == licenseId).Select(l => l.CustomerId).SingleAsync();
            db.DataControllerProfiles.Add(new DataControllerProfile { CustomerId = customerId, LegalName = "Örnek Ticaret",
                PostalAddress = "Adres", KvkkEmail = "kvkk@example.test", UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel" });
            await db.SaveChangesAsync();
        }
        var client = _factory.CreateClient();

        var with = await client.GetFromJsonAsync<LookupWithController>($"/api/v1/shopper/broadcasters/code-lookup?code={withCode}");
        var without = await client.GetFromJsonAsync<LookupWithController>($"/api/v1/shopper/broadcasters/code-lookup?code={withoutCode}");

        with!.DataController.Should().Be(new ControllerDto("Örnek Ticaret", "Adres", "kvkk@example.test", null));
        without!.DataController.Should().BeNull();
        var raw = await client.GetStringAsync($"/api/v1/shopper/broadcasters/code-lookup?code={withoutCode}");
        raw.Should().Contain("\"dataController\":null", "alan her zaman var; Shopper null'ı 'bilgi girilmedi' okur");
    }
}
