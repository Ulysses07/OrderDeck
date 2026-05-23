using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperBalanceControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperBalanceControllerTests(ApiFactory f) => _factory = f;

    private sealed record AuthResponse(
        string AccessToken, DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken, DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId, object[] Broadcasters);

    private sealed record RegisterRequest(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username,
        string? Email = null, string? Tc = null);

    private sealed record BalanceDto(decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind,
        decimal? OriginalAmount, decimal? ShippingDeducted, string? Reason,
        DateTimeOffset CreatedAt);
    private sealed record BalanceDetailsResponse(BalanceDto Balance, TransactionDto[] Transactions);

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<(Guid licenseId, string code, Guid customerId)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"bal-{Guid.NewGuid():N}@x.test",
            Name = "BalBC-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        var code = "bal-" + Guid.NewGuid().ToString("N")[..6];
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId, CustomerId = customer.Id, SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code, ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, code, customer.Id);
    }

    private async Task<HttpClient> RegisterShopperAsync(string code)
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(code, "Balance User", UniquePhone(),
                "Pass1234!", "Addr", "youtube", "baluser"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }

    private async Task SeedBalanceAsync(Guid licenseId, Guid wpfCustomerId,
        decimal amount, Guid createdByCustomerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.CustomerBalances.Add(new CustomerBalance
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = wpfCustomerId,
            Balance = amount, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = wpfCustomerId,
            Amount = amount, Kind = "refund-full",
            OriginalAmount = amount,
            CreatedByCustomerId = createdByCustomerId, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_linked_shopper_returns_balance()
    {
        var (licenseId, code, customerId) = await SeedLicenseAsync();
        var client = await RegisterShopperAsync(code);

        // Register otomatik link + WpfCustomerProjection oluşturuyor; wpfCustomerId'i çek.
        Guid wpfCustomerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var link = db.ShopperBroadcasterLinks
                .First(l => l.LicenseId == licenseId && l.WpfCustomerId != null);
            wpfCustomerId = link.WpfCustomerId!.Value;
        }
        await SeedBalanceAsync(licenseId, wpfCustomerId, 250m, customerId);

        var resp = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/v1/shopper/broadcasters/{licenseId}/balance");
        resp.Should().NotBeNull();
        resp!.Balance.Balance.Should().Be(250m);
        resp.Transactions.Should().HaveCount(1);
        resp.Transactions[0].Kind.Should().Be("refund-full");
    }

    [Fact]
    public async Task Get_no_balance_returns_zero()
    {
        var (licenseId, code, _) = await SeedLicenseAsync();
        var client = await RegisterShopperAsync(code);

        var resp = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/v1/shopper/broadcasters/{licenseId}/balance");
        resp!.Balance.Balance.Should().Be(0m);
        resp.Transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_unlinked_license_returns_404()
    {
        var (_, codeA, _) = await SeedLicenseAsync();
        var (licenseB, _, _) = await SeedLicenseAsync();
        var clientA = await RegisterShopperAsync(codeA);

        var resp = await clientA.GetAsync(
            $"/api/v1/shopper/broadcasters/{licenseB}/balance");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_no_auth_returns_401()
    {
        var (licenseId, _, _) = await SeedLicenseAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync(
            $"/api/v1/shopper/broadcasters/{licenseId}/balance");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
