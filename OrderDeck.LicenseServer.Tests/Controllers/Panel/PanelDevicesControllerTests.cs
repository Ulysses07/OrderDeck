using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelDevicesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PanelDevicesControllerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_creates_new_push_device()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "test-device-1",
            platform = "ios",
            pushToken = "fixture-apns-tok"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var device = await db.PushDevices.FirstOrDefaultAsync(d => d.CustomerId == customerId);
        device.Should().NotBeNull();
        device!.DeviceId.Should().Be("test-device-1");
        device.Platform.Should().Be("ios");
        device.PushToken.Should().Be("fixture-apns-tok");
        device.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        device.LastSeenAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Register_upserts_existing_device_by_deviceId()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // First registration
        await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "test-device-2",
            platform = "android",
            pushToken = "fixture-fcm-tok-before"
        });

        // Same deviceId, different token (token rotation scenario)
        var resp = await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "test-device-2",
            platform = "android",
            pushToken = "fixture-fcm-tok-after"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var devices = await db.PushDevices.Where(d => d.CustomerId == customerId).ToListAsync();
        devices.Should().HaveCount(1, "same deviceId should upsert, not insert");
        devices[0].PushToken.Should().Be("fixture-fcm-tok-after");
    }

    [Fact]
    public async Task Register_rejects_invalid_platform()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "test-device-3",
            platform = "windows",  // not allowed
            pushToken = "winrt-token"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_rejects_missing_fields()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "",
            platform = "ios",
            pushToken = ""
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_without_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "test",
            platform = "ios",
            pushToken = "tok"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unregister_removes_device_by_token()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "test-device-4",
            platform = "ios",
            pushToken = "fixture-tok-to-delete"
        });

        var resp = await client.DeleteAsync("/api/panel/devices/fixture-tok-to-delete");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.PushDevices.CountAsync(d => d.CustomerId == customerId);
        count.Should().Be(0);
    }

    [Fact]
    public async Task Unregister_is_idempotent_for_unknown_token()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await client.DeleteAsync("/api/panel/devices/never-existed");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unregister_only_affects_own_devices()
    {
        // Customer A registers a device
        var (clientA, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        await clientA.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "device-a",
            platform = "ios",
            pushToken = "fixture-cross-customer-tok"
        });

        // Customer B tries to unregister A's token — should be no-op
        var (clientB, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var resp = await clientB.DeleteAsync("/api/panel/devices/fixture-cross-customer-tok");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A's device must still exist
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var still = await db.PushDevices.AnyAsync(d => d.PushToken == "fixture-cross-customer-tok");
        still.Should().BeTrue();
    }

    [Fact]
    public async Task List_returns_own_devices_without_token()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "list-d-1",
            platform = "ios",
            pushToken = "fixture-list-tok-1"
        });
        await client.PostAsJsonAsync("/api/panel/devices", new
        {
            deviceId = "list-d-2",
            platform = "android",
            pushToken = "fixture-list-tok-2"
        });

        var resp = await client.GetAsync("/api/panel/devices");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("list-d-1");
        body.Should().Contain("list-d-2");
        // pushToken must NOT leak through the List response
        body.Should().NotContain("fixture-list-tok-1");
        body.Should().NotContain("fixture-list-tok-2");
    }
}
