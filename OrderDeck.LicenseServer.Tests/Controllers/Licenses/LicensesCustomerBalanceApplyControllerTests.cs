using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesCustomerBalanceApplyControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public LicensesCustomerBalanceApplyControllerTests(ApiFactory f) => _factory = f;

    private sealed record PreviewResponse(Guid WpfCustomerId, decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record ApplyResponse(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    private async Task<(HttpClient client, Guid licenseId, Guid wpfCustomerId)> SetupWithBalanceAsync(decimal initialBalance)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId, LicenseKey = "LDK-APPLY-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId, SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        var wpfCustomerId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId, LicenseId = licenseId,
            Platform = "youtube", Username = "u", UpdatedAt = DateTimeOffset.UtcNow,
        });
        if (initialBalance > 0)
        {
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = wpfCustomerId,
                Balance = initialBalance, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = wpfCustomerId,
                Amount = initialBalance, Kind = "refund-full",
                OriginalAmount = initialBalance,
                CreatedByCustomerId = customerId, CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (client, licenseId, wpfCustomerId);
    }

    [Fact]
    public async Task Preview_returns_current_balance()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);

        var resp = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        resp.Should().NotBeNull();
        resp!.Balance.Should().Be(500m);
    }

    [Fact]
    public async Task Preview_no_balance_returns_zero()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(0m);

        var resp = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        resp!.Balance.Should().Be(0m);
    }

    [Fact]
    public async Task Apply_full_balance_when_enough()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ApplyResponse>();
        body!.AppliedAmount.Should().Be(100m);
        body.RemainingBalance.Should().Be(0m);
    }

    [Fact]
    public async Task Apply_caps_at_balance_when_requested_more()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 500m, ProductTotal = 2100m });
        var body = await resp.Content.ReadFromJsonAsync<ApplyResponse>();
        body!.AppliedAmount.Should().Be(100m);
        body.RemainingBalance.Should().Be(0m);
    }

    [Fact]
    public async Task Apply_caps_at_product_total_when_balance_exceeds()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 500m, ProductTotal = 100m });
        var body = await resp.Content.ReadFromJsonAsync<ApplyResponse>();
        body!.AppliedAmount.Should().Be(100m);
        body.RemainingBalance.Should().Be(400m);
    }

    [Fact]
    public async Task Apply_no_balance_returns_409()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(0m);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Apply_invalid_amount_returns_400()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 0m, ProductTotal = 100m });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
