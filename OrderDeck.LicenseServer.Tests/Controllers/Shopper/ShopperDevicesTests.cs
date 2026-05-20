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

public class ShopperDevicesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperDevicesTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record RegisterShopperRequest(
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

    private sealed record RegisterDeviceRequest(string DeviceId, string Platform, string PushToken);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(Guid licenseId, string code)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"dev-{Guid.NewGuid():N}@x.test",
            Name = "Dev-BC-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "dev-" + Guid.NewGuid().ToString("N")[..8];
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
        return (licenseId, code);
    }

    private async Task<(string token, Guid shopperId)> RegisterShopperAsync(HttpClient client, string code)
    {
        var phone = UniquePhone();
        var req = new RegisterShopperRequest(code, "Device User", phone, "DevPass1!", "Bursa", "youtube", "devuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    // ── T1: Register new device → 204 + row created ───────────────────────────

    [Fact]
    public async Task Register_new_device_returns_204_and_creates_row()
    {
        var client = _factory.CreateClient();
        var (_, code) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var deviceId = "device-" + Guid.NewGuid().ToString("N")[..8];
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest(deviceId, "android", "push-token-abc"));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.ShopperPushDevices
            .FirstOrDefaultAsync(d => d.ShopperId == shopperId && d.DeviceId == deviceId);
        row.Should().NotBeNull();
        row!.Platform.Should().Be("android");
        row.PushToken.Should().Be("push-token-abc");
    }

    // ── T2: Register same device twice → upsert, no duplicate row ────────────

    [Fact]
    public async Task Register_same_device_upserts_no_duplicate()
    {
        var client = _factory.CreateClient();
        var (_, code) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var deviceId = "upsert-" + Guid.NewGuid().ToString("N")[..8];

        // First register
        await client.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest(deviceId, "ios", "token-v1"));

        // Second register with updated token
        var resp2 = await client.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest(deviceId, "ios", "token-v2"));
        resp2.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.ShopperPushDevices
            .Where(d => d.ShopperId == shopperId && d.DeviceId == deviceId)
            .ToListAsync();
        rows.Should().HaveCount(1, "upsert must not create duplicate rows");
        rows[0].PushToken.Should().Be("token-v2");
    }

    // ── T3: Same DeviceId, different shopper → separate rows ─────────────────

    [Fact]
    public async Task Register_same_device_different_shopper_creates_separate_rows()
    {
        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();

        var (_, codeA) = await SeedLicenseAsync();
        var (_, codeB) = await SeedLicenseAsync();
        var (tokenA, shopperIdA) = await RegisterShopperAsync(clientA, codeA);
        var (tokenB, shopperIdB) = await RegisterShopperAsync(clientB, codeB);

        var sharedDeviceId = "shared-" + Guid.NewGuid().ToString("N")[..8];

        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var respA = await clientA.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest(sharedDeviceId, "android", "token-a"));
        respA.StatusCode.Should().Be(HttpStatusCode.NoContent);

        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var respB = await clientB.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest(sharedDeviceId, "android", "token-b"));
        respB.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rowsForDevice = await db.ShopperPushDevices
            .Where(d => d.DeviceId == sharedDeviceId)
            .ToListAsync();
        rowsForDevice.Should().HaveCount(2, "different shoppers with the same deviceId get separate rows");
        rowsForDevice.Should().Contain(r => r.ShopperId == shopperIdA);
        rowsForDevice.Should().Contain(r => r.ShopperId == shopperIdB);
    }

    // ── T4: Invalid platform → 400 ────────────────────────────────────────────

    [Fact]
    public async Task Register_invalid_platform_returns_400()
    {
        var client = _factory.CreateClient();
        var (_, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest("device-xyz", "windows", "some-token"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("title").GetString().Should().Be("invalid-platform");
    }

    // ── T5: Unregister existing device → 204 ─────────────────────────────────

    [Fact]
    public async Task Unregister_existing_returns_204()
    {
        var client = _factory.CreateClient();
        var (_, code) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var deviceId = "del-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest(deviceId, "ios", "some-token"));

        var resp = await client.DeleteAsync($"/api/v1/shopper/devices/{deviceId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify row is gone
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.ShopperPushDevices
            .FirstOrDefaultAsync(d => d.ShopperId == shopperId && d.DeviceId == deviceId);
        row.Should().BeNull();
    }

    // ── T6: Unregister missing device → 404 ──────────────────────────────────

    [Fact]
    public async Task Unregister_missing_returns_404()
    {
        var client = _factory.CreateClient();
        var (_, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.DeleteAsync("/api/v1/shopper/devices/nonexistent-device-id");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── T7: No auth → 401 ────────────────────────────────────────────────────

    [Fact]
    public async Task No_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/shopper/devices",
            new RegisterDeviceRequest("device-xyz", "android", "token"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
