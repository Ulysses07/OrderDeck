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

public class ShopperBroadcastersJoinTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperBroadcastersJoinTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ─────────────────────────────────────────────────────────────────

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
        BroadcasterSummaryDto[] Broadcasters);

    private sealed record BroadcasterSummaryDto(Guid LicenseId, string DisplayName, string Platform, string Username);

    private sealed record JoinRequest(string BroadcasterCode, string Platform, string Username);
    private sealed record JoinResponse(BroadcasterSummaryDto[] Broadcasters);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    /// <summary>
    /// Seeds a broadcaster license and returns (licenseId, shopperCode).
    /// </summary>
    private async Task<(Guid licenseId, string shopperCode, string customerName)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"join-{Guid.NewGuid():N}@x.test",
            Name = "Join-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "join-" + Guid.NewGuid().ToString("N")[..8];
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
        return (licenseId, code, customer.Name);
    }

    /// <summary>
    /// Registers a shopper against broadcaster A. Returns (accessToken, shopperId).
    /// </summary>
    private async Task<(string accessToken, Guid shopperId)> RegisterShopperAsync(
        HttpClient client, string broadcasterCode)
    {
        var phone = UniquePhone();
        var req = new RegisterRequest(broadcasterCode, "Join User", phone, "JoinPass1!", "Ankara", "youtube", "joinuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    // ── T1: Happy path — join broadcaster B after registered with A ──────────

    [Fact]
    public async Task Join_happy_path_returns_200_with_both_broadcasters()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (licenseIdB, codeB, nameB) = await SeedLicenseAsync();

        var (token, shopperId) = await RegisterShopperAsync(client, codeA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "twitch", "myhandle"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JoinResponse>();
        body!.Broadcasters.Should().HaveCount(2);
        body.Broadcasters.Should().Contain(b => b.LicenseId == licenseIdB && b.DisplayName == nameB);
    }

    // ── T2: Unknown broadcaster code → 404 ──────────────────────────────────

    [Fact]
    public async Task Join_unknown_code_returns_404()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, codeA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest("totally-unknown-code-xyz", "youtube", "user"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── T3: Already-linked active code → 409 ────────────────────────────────

    [Fact]
    public async Task Join_already_linked_returns_409()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, codeA);

        // Attempt to join broadcaster A again (already linked via register)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeA, "youtube", "joinuser"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── T4: Re-join after Leave → 200 (new row, 2 rows in DB, 1 active) ──────

    [Fact]
    public async Task Join_rejoin_after_leave_creates_new_row_and_returns_only_active()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (licenseIdB, codeB, _) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, codeA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Join broadcaster B
        var joinResp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "twitch", "rejoinuser"));
        joinResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Leave broadcaster B
        var leaveResp = await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseIdB}");
        leaveResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Re-join broadcaster B
        var rejoinResp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "twitch", "rejoinuser"));
        rejoinResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await rejoinResp.Content.ReadFromJsonAsync<JoinResponse>();
        // Should have 2 active: A + B
        body!.Broadcasters.Should().HaveCount(2);

        // Verify 2 rows in DB for broadcaster B (1 left, 1 active)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var linksForB = await db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseIdB)
            .ToListAsync();
        linksForB.Should().HaveCount(2);
        linksForB.Where(l => l.LeftAt != null).Should().HaveCount(1);
        linksForB.Where(l => l.LeftAt == null).Should().HaveCount(1);
    }

    // ── T5: WpfCustomerProjection match → WpfCustomerId populated ────────────

    [Fact]
    public async Task Join_wpf_match_populates_wpf_customer_id()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (licenseIdB, codeB, _) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, codeA);

        // Seed a WpfCustomerProjection for broadcaster B
        var wpfId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = wpfId,
                LicenseId = licenseIdB,
                Platform = "instagram",
                Username = "wpfmatch",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "instagram", "wpfmatch"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify WpfCustomerId is populated on the new link
        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var link = await db2.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseIdB && l.LeftAt == null)
            .FirstOrDefaultAsync();
        link.Should().NotBeNull();
        link!.WpfCustomerId.Should().Be(wpfId);
    }

    // ── T6: No auth → 401 ────────────────────────────────────────────────────

    [Fact]
    public async Task Join_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var (_, code, _) = await SeedLicenseAsync();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(code, "youtube", "user"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T7: Invalid fields (empty Platform) → 400 ────────────────────────────

    [Fact]
    public async Task Join_empty_platform_returns_400()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (_, codeB, _) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, codeA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "", "user"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── T8: No existing WpfProjection on Join → auto-creates one ────────────

    [Fact]
    public async Task Join_without_existing_projection_auto_creates_projection_and_sets_WpfCustomerId()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (licenseIdB, codeB, _) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, codeA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "instagram", "joinautoproj"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Auto-projection must exist
        var projection = await db.WpfCustomerProjections
            .FirstOrDefaultAsync(p => p.LicenseId == licenseIdB && p.Platform == "instagram" && p.Username == "joinautoproj");
        projection.Should().NotBeNull("auto-projection must be created on join when no prior match");

        // Link must point to the new projection
        var link = await db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseIdB && l.LeftAt == null)
            .FirstOrDefaultAsync();
        link.Should().NotBeNull();
        link!.WpfCustomerId.Should().Be(projection!.Id);
    }

    // ── T9: Existing WpfProjection on Join → no duplicate created ───────────

    [Fact]
    public async Task Join_with_existing_projection_does_not_create_duplicate()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (licenseIdB, codeB, _) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, codeA);

        var wpfId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = wpfId,
                LicenseId = licenseIdB,
                Platform = "twitch",
                Username = "joinexistproj",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "twitch", "joinexistproj"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Should NOT have created a second projection
        var projections = await db2.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseIdB && p.Platform == "twitch" && p.Username == "joinexistproj")
            .ToListAsync();
        projections.Should().HaveCount(1, "no duplicate projection should be created when one already exists");
        projections[0].Id.Should().Be(wpfId);

        var link = await db2.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseIdB && l.LeftAt == null)
            .FirstOrDefaultAsync();
        link!.WpfCustomerId.Should().Be(wpfId);
    }
}
