using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperMePatchTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperMePatchTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username,
        string? Email = null,
        string? Tc = null);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        object[] Broadcasters);

    private sealed record NotificationPrefsDto(bool Broadcast, bool Orders, bool Payments);

    private sealed record BroadcasterSummaryDto(Guid LicenseId, string DisplayName, string Platform, string Username);

    private sealed record MeResponse(
        Guid Id,
        string FullName,
        string Phone,
        string Address,
        string? Email,
        string? Tc,
        NotificationPrefsDto NotificationPrefs,
        BroadcasterSummaryDto[] Broadcasters);

    private sealed record PatchMeRequest(
        string? FullName = null,
        string? Address = null,
        string? Email = null,
        string? Tc = null,
        NotificationPrefsDto? NotificationPrefs = null);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private static string UniqueCode() =>
        ("mepatch" + Guid.NewGuid().ToString("N"))[..16];

    private async Task<(string accessToken, Guid shopperId)> RegisterShopperAsync(HttpClient client)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "MePatch-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = UniqueCode();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var phone = UniquePhone();
        var req = new RegisterRequest(code, "Patch User", phone, "Pass1234!", "Ankara", "youtube", "patchuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    // ── T13.1: Partial update FullName only → 200, only FullName changed ──────

    [Fact]
    public async Task PatchMe_fullname_only_updates_only_fullname()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(FullName: "Updated Name"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body!.FullName.Should().Be("Updated Name");
        body.Address.Should().Be("Ankara", "address should not have changed");
    }

    // ── T13.2: Update notification prefs only → 200, prefs changed ───────────

    [Fact]
    public async Task PatchMe_notification_prefs_only_updates_prefs()
    {
        var client = _factory.CreateClient();
        var (token, _) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(NotificationPrefs: new NotificationPrefsDto(false, true, false)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body!.NotificationPrefs.Broadcast.Should().BeFalse();
        body.NotificationPrefs.Orders.Should().BeTrue();
        body.NotificationPrefs.Payments.Should().BeFalse();
        body.FullName.Should().Be("Patch User", "name should not have changed");
    }

    // ── T13.3: Invalid email format → 400, no change ─────────────────────────

    [Fact]
    public async Task PatchMe_invalid_email_returns_400()
    {
        var client = _factory.CreateClient();
        var (token, shopperId) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(Email: "not-an-email"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Verify email was NOT changed in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.FindAsync(shopperId);
        shopper!.Email.Should().BeNull("email should not have been set");
    }

    // ── T13.4: Invalid TC checksum → 400, no change ───────────────────────────

    [Fact]
    public async Task PatchMe_invalid_tc_returns_400()
    {
        var client = _factory.CreateClient();
        var (token, shopperId) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // "12345678901" — wrong checksum
        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(Tc: "12345678901"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.FindAsync(shopperId);
        shopper!.Tc.Should().BeNull("TC should not have been updated with invalid checksum");
    }

    // ── T13.5: Valid TC with correct checksum → 200 ───────────────────────────

    [Fact]
    public async Task PatchMe_valid_tc_returns_200()
    {
        var client = _factory.CreateClient();
        var (token, shopperId) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // "10000000146" — known valid TCKN for tests
        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(Tc: "10000000146"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body!.Tc.Should().Be("10000000146");
    }

    // ── T13.6: No auth → 401 ──────────────────────────────────────────────────

    [Fact]
    public async Task PatchMe_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(FullName: "Name"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
