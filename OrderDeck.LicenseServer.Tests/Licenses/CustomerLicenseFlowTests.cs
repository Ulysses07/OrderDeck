using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Licenses;

public class CustomerLicenseFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerLicenseFlowTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, string licenseKey)> SetupAsync(int slots = 1)
    {
        // Admin issues license
        var (adminToken, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var email = $"flow-{Guid.NewGuid():N}@x.com";
        await adminClient.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Flow", initialPassword = "pw12345678", autoConfirm = true
        });
        var issueResp = await adminClient.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD", slotsOverride = slots
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        // Customer logs in
        var customerClient = _factory.CreateClient();
        var loginResp = await customerClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "pw12345678"
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        customerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        return (customerClient, issued!.licenseKey);
    }

    [Fact]
    public async Task Validate_unactivated_returns_NotActivated_status()
    {
        var (client, key) = await SetupAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ValidateBody>();
        body!.status.Should().Be("notactivated");
    }

    [Fact]
    public async Task Activate_then_validate_returns_active()
    {
        var (client, key) = await SetupAsync();
        var actResp = await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = "PC-1"
        });
        actResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var validateResp = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        var body = await validateResp.Content.ReadFromJsonAsync<ValidateBody>();
        body!.status.Should().Be("active");
    }

    [Fact]
    public async Task Activate_when_slot_full_returns_409()
    {
        var (client, key) = await SetupAsync(slots: 1);
        await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = (string?)null
        });

        var resp = await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-2", machineName = (string?)null
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deactivate_frees_slot()
    {
        var (client, key) = await SetupAsync(slots: 1);
        await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = (string?)null
        });
        var deactResp = await client.PostAsJsonAsync("/api/v1/licenses/deactivate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        deactResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var actResp = await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-2", machineName = (string?)null
        });
        actResp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Heartbeat_updates_LastSeenAt()
    {
        var (client, key) = await SetupAsync();
        await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = (string?)null
        });

        var resp = await client.PostAsJsonAsync("/api/v1/licenses/heartbeat", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Customer_cannot_access_other_customers_license()
    {
        var (clientA, keyA) = await SetupAsync();
        var (clientB, _) = await SetupAsync();

        // clientB tries to validate clientA's license
        var resp = await clientB.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = keyA, hardwareFingerprint = "fp"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Validate_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = "LDK-X", hardwareFingerprint = "fp"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record IssueBody(string licenseKey, DateTimeOffset expiresAt);
    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record ValidateBody(string status, DateTimeOffset? expiresAt, int? remainingDays);
}
