using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Licenses;

public class AdminLicensesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminLicensesTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> CreateCustomerAsync(HttpClient client, string email)
    {
        await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Test", initialPassword = "pw12345678", autoConfirm = true
        });
        return email;
    }

    [Fact]
    public async Task Issue_creates_license_for_existing_customer()
    {
        var client = await AdminClientAsync();
        var email = $"l-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);

        var resp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<IssueBody>();
        body!.licenseKey.Should().StartWith("LDK-");
    }

    [Fact]
    public async Task Issue_with_unknown_customer_returns_400()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = "nope@x.com", skuCode = "STD"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_returns_license_with_customer_and_empty_activations()
    {
        var client = await AdminClientAsync();
        var email = $"g-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);
        var issueResp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var resp = await client.GetAsync($"/api/v1/admin/licenses/{issued!.licenseKey}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoke_marks_license_revoked()
    {
        var client = await AdminClientAsync();
        var email = $"r-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);
        var issueResp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var resp = await client.PostAsJsonAsync($"/api/v1/admin/licenses/{issued!.licenseKey}/revoke", new
        {
            reason = "Test revoke"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Extend_adds_days_to_expiry()
    {
        var client = await AdminClientAsync();
        var email = $"e-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);
        var issueResp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var resp = await client.PostAsJsonAsync($"/api/v1/admin/licenses/{issued!.licenseKey}/extend", new
        {
            additionalDays = 90
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_returns_issued_licenses()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/admin/licenses");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record IssueBody(string licenseKey, DateTimeOffset expiresAt);
}
