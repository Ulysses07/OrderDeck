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

namespace OrderDeck.LicenseServer.Tests.Auth;

/// <summary>
/// PR-5 Faz 2 (2026-05-14): operator login + tenant resolve helper tests.
///
/// Doğrulama:
///  - POST /api/v1/auth/operator-login → valid creds, JWT döner
///  - JWT'deki tcid claim'i License sahibi Customer.Id'sine işaret eder
///  - Operator token'la tenant-scoped panel endpoint'leri çalışır
///    (owner'ın gördüğü payment/order/shipment'ları görür)
///  - PanelOperatorsController POST/DELETE owner-only (operator 403)
///  - Revoked license → 403
///  - Wrong password → 401
/// </summary>
public class OperatorLoginTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public OperatorLoginTests(ApiFactory factory) => _factory = factory;

    private sealed record OperatorLoginResp(
        string Token, DateTimeOffset ExpiresAt,
        Guid OperatorId, Guid TenantCustomerId,
        string Email, string Name, string Role);

    private sealed record OperatorDto(
        Guid Id, Guid LicenseId, string Email, string Name, string Role,
        DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? RevokedAt);

    private static string FreshPassword() => "pwd-" + Guid.NewGuid().ToString("N");

    /// <summary>Owner customer + license seed + 1 invited operator.</summary>
    private async Task<(HttpClient ownerClient, Guid ownerCustomerId, Guid licenseId,
        string operatorEmail, string operatorPassword)> SeedOwnerAndOperatorAsync()
    {
        var (ownerClient, ownerCustomerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-OPLOGIN-" + Guid.NewGuid().ToString("N"),
                CustomerId = ownerCustomerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }

        var opEmail = $"op-{Guid.NewGuid():N}@example.com";
        var opPassword = FreshPassword();
        var invite = await ownerClient.PostAsJsonAsync("/api/panel/operators", new
        {
            email = opEmail, name = "Staff", password = opPassword
        });
        invite.EnsureSuccessStatusCode();

        return (ownerClient, ownerCustomerId, licenseId, opEmail, opPassword);
    }

    [Fact]
    public async Task OperatorLogin_returns_jwt_with_tenant_customer_id()
    {
        var (_, ownerCustomerId, _, email, password) = await SeedOwnerAndOperatorAsync();

        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login", new { email, password });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<OperatorLoginResp>();
        body.Should().NotBeNull();
        body!.TenantCustomerId.Should().Be(ownerCustomerId);
        body.Role.Should().Be("staff");
        body.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OperatorLogin_wrong_password_401()
    {
        var (_, _, _, email, _) = await SeedOwnerAndOperatorAsync();
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login",
            new { email, password = "wrong" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OperatorLogin_unknown_email_401()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login",
            new { email = "ghost@example.com", password = "anything" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OperatorLogin_revoked_license_403()
    {
        var (_, _, licenseId, email, password) = await SeedOwnerAndOperatorAsync();

        // Revoke license
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var lic = await db.Licenses.FirstAsync(l => l.Id == licenseId);
            lic.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login",
            new { email, password });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Operator_token_can_access_tenant_scoped_endpoints()
    {
        // Owner seeds an operator + creates a Payment via sync endpoint.
        var (ownerClient, _, licenseId, email, password) = await SeedOwnerAndOperatorAsync();

        // Owner pushes a payment so tenant has data.
        var paymentId = Guid.NewGuid();
        var pushResp = await ownerClient.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = paymentId, payerName = "Operator Visible", amount = 99m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "OP-VISIBLE",
                          pdfHash = (string?)null }
                }
            });
        pushResp.EnsureSuccessStatusCode();

        // Operator logs in, hits PanelPaymentsController.List
        var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login",
            new { email, password });
        var login = await loginResp.Content.ReadFromJsonAsync<OperatorLoginResp>();

        var opClient = _factory.CreateClient();
        opClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        var paymentsResp = await opClient.GetAsync("/api/panel/payments?status=pending");
        paymentsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await paymentsResp.Content.ReadAsStringAsync();
        body.Should().Contain("OP-VISIBLE",
            "operator inherits the owner's tenant data via tcid claim");
    }

    [Fact]
    public async Task Operator_cannot_invite_another_operator()
    {
        var (_, _, _, email, password) = await SeedOwnerAndOperatorAsync();
        var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login",
            new { email, password });
        var login = await loginResp.Content.ReadFromJsonAsync<OperatorLoginResp>();

        var opClient = _factory.CreateClient();
        opClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        var inviteAsOp = await opClient.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "newbie@example.com", name = "Newbie",
            password = FreshPassword()
        });
        inviteAsOp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Operator_cannot_delete_operator()
    {
        var (ownerClient, _, _, email, password) = await SeedOwnerAndOperatorAsync();

        // Owner creates a second operator that the first will try to delete.
        var victimEmail = $"victim-{Guid.NewGuid():N}@example.com";
        var createResp = await ownerClient.PostAsJsonAsync("/api/panel/operators", new
        {
            email = victimEmail, name = "Victim", password = FreshPassword()
        });
        var victim = await createResp.Content.ReadFromJsonAsync<OperatorDto>();

        // First operator logs in, tries to delete the second
        var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login",
            new { email, password });
        var login = await loginResp.Content.ReadFromJsonAsync<OperatorLoginResp>();

        var opClient = _factory.CreateClient();
        opClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        var del = await opClient.DeleteAsync($"/api/panel/operators/{victim!.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Operator_can_list_team_members()
    {
        var (_, _, _, email, password) = await SeedOwnerAndOperatorAsync();

        var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync("/api/v1/auth/operator-login",
            new { email, password });
        var login = await loginResp.Content.ReadFromJsonAsync<OperatorLoginResp>();

        var opClient = _factory.CreateClient();
        opClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        var listResp = await opClient.GetAsync("/api/panel/operators");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await listResp.Content.ReadFromJsonAsync<List<OperatorDto>>();
        rows!.Should().ContainSingle(r => r.Email == email);
    }
}
