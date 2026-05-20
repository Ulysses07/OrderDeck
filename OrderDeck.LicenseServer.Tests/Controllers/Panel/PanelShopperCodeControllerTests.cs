using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelShopperCodeControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelShopperCodeControllerTests(ApiFactory f) => _factory = f;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed record ShopperCodeResponse(
        string? Code,
        DateTimeOffset? UpdatedAt,
        DateTimeOffset? CanChangeAt,
        Guid LicenseId);

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SeedAsync(
        string? shopperCode = null,
        DateTimeOffset? shopperCodeUpdatedAt = null,
        DateTimeOffset? issuedAt = null)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = await AddLicenseAsync(customerId, shopperCode, shopperCodeUpdatedAt, issuedAt);
        return (client, customerId, licenseId);
    }

    private async Task<Guid> AddLicenseAsync(
        Guid customerId,
        string? shopperCode = null,
        DateTimeOffset? shopperCodeUpdatedAt = null,
        DateTimeOffset? issuedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-SC-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = issuedAt ?? DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            ShopperCode = shopperCode,
            ShopperCodeUpdatedAt = shopperCodeUpdatedAt
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    // -------------------------------------------------------------------------
    // GET tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_null_code_when_first_time()
    {
        // Fresh license — no shopper code set yet
        var (client, _, licenseId) = await SeedAsync();

        var resp = await client.GetAsync("/api/panel/shopper-code");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ShopperCodeResponse>();
        body!.Code.Should().BeNull();
        body.CanChangeAt.Should().BeNull();
        body.LicenseId.Should().Be(licenseId);
    }

    [Fact]
    public async Task Get_returns_existing_code_with_canChangeAt()
    {
        // License has code set 3 days ago
        var updatedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var (client, _, licenseId) = await SeedAsync("myshop", updatedAt);

        var resp = await client.GetAsync("/api/panel/shopper-code");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ShopperCodeResponse>();
        body!.Code.Should().Be("myshop");
        body.UpdatedAt.Should().BeCloseTo(updatedAt, TimeSpan.FromSeconds(1));
        body.CanChangeAt.Should().BeCloseTo(updatedAt.AddDays(7), TimeSpan.FromSeconds(1));
        body.LicenseId.Should().Be(licenseId);
    }

    [Fact]
    public async Task Get_returns_404_when_no_license()
    {
        // Customer authenticated but has no License row
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await client.GetAsync("/api/panel/shopper-code");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("no-license");
    }

    // -------------------------------------------------------------------------
    // PUT tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Put_first_time_sets_code()
    {
        var (client, customerId, licenseId) = await SeedAsync();

        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "freshcode" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ShopperCodeResponse>();
        body!.Code.Should().Be("freshcode");
        body.LicenseId.Should().Be(licenseId);

        // Verify persisted in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = await db.Licenses.FindAsync(licenseId);
        license!.ShopperCode.Should().Be("freshcode");
        license.ShopperCodeUpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Put_invalid_format_returns_400_format()
    {
        var (client, _, _) = await SeedAsync();

        // Contains hyphen — fails AlphaNumLower regex
        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "ROYAL-1" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("format");
    }

    [Fact]
    public async Task Put_reserved_word_returns_400_reserved()
    {
        var (client, _, _) = await SeedAsync();

        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "admin" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("reserved");
    }

    [Fact]
    public async Task Put_cooldown_returns_400_cooldown()
    {
        // Code set 3 days ago — still within 7-day cooldown
        var updatedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var (client, _, _) = await SeedAsync("existingcode", updatedAt);

        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "newcode" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("cooldown");
    }

    [Fact]
    public async Task Put_after_cooldown_succeeds()
    {
        // Code set 8 days ago — cooldown has passed
        var updatedAt = DateTimeOffset.UtcNow.AddDays(-8);
        var (client, _, licenseId) = await SeedAsync("oldcode", updatedAt);

        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "newcode" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ShopperCodeResponse>();
        body!.Code.Should().Be("newcode");
        body.LicenseId.Should().Be(licenseId);
    }

    [Fact]
    public async Task Put_globally_taken_returns_400_taken()
    {
        // Seed a different customer's license with the target code already set
        var (otherClient, otherCustomerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        await AddLicenseAsync(otherCustomerId, "takencode");

        // Now our customer tries to claim that same code
        var (client, _, _) = await SeedAsync();

        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "takencode" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("taken");
    }

    [Fact]
    public async Task Put_same_code_to_same_license_succeeds()
    {
        // "Re-PUT" own current code after cooldown (same-license exclusion in uniqueness check)
        var updatedAt = DateTimeOffset.UtcNow.AddDays(-8);
        var (client, _, licenseId) = await SeedAsync("mycode", updatedAt);

        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "mycode" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ShopperCodeResponse>();
        body!.Code.Should().Be("mycode");
        body.LicenseId.Should().Be(licenseId);
    }

    [Fact]
    public async Task Put_picks_most_recent_license_when_multiple()
    {
        // Customer has two licenses; code must land on the most-recently issued one
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var olderLicenseId = await AddLicenseAsync(customerId, issuedAt: DateTimeOffset.UtcNow.AddDays(-10));
        var newerLicenseId = await AddLicenseAsync(customerId, issuedAt: DateTimeOffset.UtcNow.AddDays(-1));

        var resp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "multitest" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ShopperCodeResponse>();
        body!.LicenseId.Should().Be(newerLicenseId);

        // Older license should remain untouched
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var older = await db.Licenses.FindAsync(olderLicenseId);
        older!.ShopperCode.Should().BeNull();
    }

    [Fact]
    public async Task No_auth_returns_401()
    {
        var client = _factory.CreateClient();

        var getResp = await client.GetAsync("/api/panel/shopper-code");
        getResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var putResp = await client.PutAsJsonAsync("/api/panel/shopper-code", new { code = "test" });
        putResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
