using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using DomainShopper = OrderDeck.LicenseServer.Domain.Shopper;
using ShopperSupportRequest = OrderDeck.LicenseServer.Domain.ShopperSupportRequest;
using ShopperRefreshToken = OrderDeck.LicenseServer.Domain.ShopperRefreshToken;
using License = OrderDeck.LicenseServer.Domain.License;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelSupportRequestsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelSupportRequestsControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = "LDK-SR-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return (client, customerId, licenseId);
    }

    private async Task<(Guid shopperId, Guid requestId)> SeedShopperWithRequestAsync(
        Guid licenseId, string kind = "forgot-password", DateTimeOffset? resolvedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopperId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new DomainShopper
        {
            Id = shopperId,
            FullName = "Shopper " + shopperId.ToString("N")[..6],
            Phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999),
            PasswordHash = $"hash-{Guid.NewGuid():N}",
            Address = "Addr",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ShopperSupportRequests.Add(new ShopperSupportRequest
        {
            Id = requestId,
            ShopperId = shopperId,
            LicenseId = licenseId,
            Kind = kind,
            CreatedAt = now,
            ResolvedAt = resolvedAt,
        });
        await db.SaveChangesAsync();
        return (shopperId, requestId);
    }

    private sealed record SupportRequestDto(
        Guid Id, Guid LicenseId, Guid ShopperId,
        string ShopperName, string ShopperPhone,
        string Kind, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);
    private sealed record IssueResp(string? TempPassword, string? Status);

    [Fact]
    public async Task List_returns_pending_only_by_default()
    {
        var (client, _, licenseId) = await SetupAsync();
        await SeedShopperWithRequestAsync(licenseId);
        await SeedShopperWithRequestAsync(licenseId, resolvedAt: DateTimeOffset.UtcNow);

        var resp = await client.GetFromJsonAsync<SupportRequestDto[]>(
            "/api/panel/support-requests");
        resp.Should().NotBeNull();
        resp!.Length.Should().Be(1);
        resp[0].ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task List_includeResolved_true_returns_both()
    {
        var (client, _, licenseId) = await SetupAsync();
        await SeedShopperWithRequestAsync(licenseId);
        await SeedShopperWithRequestAsync(licenseId, resolvedAt: DateTimeOffset.UtcNow);

        var resp = await client.GetFromJsonAsync<SupportRequestDto[]>(
            "/api/panel/support-requests?includeResolved=true");
        resp.Should().NotBeNull();
        resp!.Length.Should().Be(2);
    }

    [Fact]
    public async Task List_cross_tenant_isolated()
    {
        var (_, _, licenseA) = await SetupAsync();
        await SeedShopperWithRequestAsync(licenseA);

        var (clientB, _, _) = await SetupAsync();
        var resp = await clientB.GetFromJsonAsync<SupportRequestDto[]>(
            "/api/panel/support-requests");
        resp.Should().NotBeNull();
        resp!.Length.Should().Be(0);
    }

    [Fact]
    public async Task IssueTempPassword_starts_verified_recovery_without_disclosing_password()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (shopperId, requestId) = await SeedShopperWithRequestAsync(licenseId);

        string originalHash;
        string phone;
        using (var beforeScope = _factory.Services.CreateScope())
        {
            var beforeDb = beforeScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var before = await beforeDb.Shoppers.FindAsync(shopperId);
            originalHash = before!.PasswordHash;
            phone = before.Phone;
        }

        var resp = await client.PostAsync(
            $"/api/panel/support-requests/{requestId}/issue-temp-password", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<IssueResp>();
        body.Should().NotBeNull();
        body!.TempPassword.Should().BeNull();
        body.Status.Should().Be("verification-sent");
        _factory.Sms.Sent.Should().Contain(m => m.Phone == phone);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.FindAsync(shopperId);
        shopper!.PasswordHash.Should().Be(originalHash);
        var req = await db.ShopperSupportRequests.FindAsync(requestId);
        req!.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task IssueTempPassword_already_resolved_returns_409()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (_, requestId) = await SeedShopperWithRequestAsync(
            licenseId, resolvedAt: DateTimeOffset.UtcNow);

        var resp = await client.PostAsync(
            $"/api/panel/support-requests/{requestId}/issue-temp-password", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task IssueTempPassword_other_tenant_returns_404()
    {
        var (_, _, licenseA) = await SetupAsync();
        var (_, requestId) = await SeedShopperWithRequestAsync(licenseA);

        var (clientB, _, _) = await SetupAsync();
        var resp = await clientB.PostAsync(
            $"/api/panel/support-requests/{requestId}/issue-temp-password", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueTempPassword_revokes_refresh_tokens()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (shopperId, requestId) = await SeedShopperWithRequestAsync(licenseId);

        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ShopperRefreshTokens.Add(new ShopperRefreshToken
            {
                Id = Guid.NewGuid(),
                ShopperId = shopperId,
                TokenHash = $"token-hash-{Guid.NewGuid():N}",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/panel/support-requests/{requestId}/issue-temp-password", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var tokens = verifyDb.ShopperRefreshTokens
            .Where(t => t.ShopperId == shopperId)
            .ToList();
        tokens.Should().NotBeEmpty();
        tokens.Should().AllSatisfy(t => t.RevokedAt.Should().NotBeNull());
    }

    [Fact]
    public async Task IssueTempPassword_sms_hatasindan_sonra_retry_edilebilir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (_, requestId) = await SeedShopperWithRequestAsync(licenseId);

        HttpResponseMessage failed;
        _factory.Sms.ThrowOnSend = true;
        try
        {
            failed = await client.PostAsync(
                $"/api/panel/support-requests/{requestId}/issue-temp-password", null);
        }
        finally
        {
            _factory.Sms.ThrowOnSend = false;
        }

        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var retry = await client.PostAsync(
            $"/api/panel/support-requests/{requestId}/issue-temp-password", null);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
