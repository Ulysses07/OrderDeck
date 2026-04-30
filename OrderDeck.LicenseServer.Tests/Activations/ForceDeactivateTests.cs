using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Activations;

public class ForceDeactivateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ForceDeactivateTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_force_deactivates_active_activation()
    {
        // Setup: admin issues license, customer activates
        var (adminToken, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var email = $"fd-{Guid.NewGuid():N}@x.com";
        await adminClient.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "FD", initialPassword = "pw12345678", autoConfirm = true
        });
        var issueResp = await adminClient.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var custClient = _factory.CreateClient();
        var loginResp = await custClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "pw12345678"
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        custClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        var actResp = await custClient.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = issued!.licenseKey, hardwareFingerprint = "fp-1", machineName = "PC"
        });
        var actBody = await actResp.Content.ReadFromJsonAsync<ActBody>();

        // Admin force-deactivates
        var resp = await adminClient.DeleteAsync($"/api/v1/admin/activations/{actBody!.activationId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var act = await db.Activations.FirstAsync(a => a.Id == actBody.activationId);
        act.DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Force_deactivate_unknown_id_returns_404()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.DeleteAsync($"/api/v1/admin/activations/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record IssueBody(string licenseKey);
    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record ActBody(Guid activationId);
}
