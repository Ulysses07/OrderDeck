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

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class SmsBalanceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SmsBalanceTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> CustomerWithLicenseAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-SMS-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        licenseId = license.Id;
        return (client, customerId, licenseId);
    }

    private sealed record TopupResponse(int CreditsRemaining, DateTimeOffset UpdatedAt);
    private sealed record CustomerBalanceResponse(int CreditsRemaining, DateTimeOffset UpdatedAt);
    private sealed record TxItem(int Amount, string Kind, string? Reason, DateTimeOffset CreatedAt);
    private sealed record AdminBalanceResponse(
        int CreditsRemaining, DateTimeOffset UpdatedAt, List<TxItem> RecentTransactions);

    [Fact]
    public async Task Admin_topup_increases_balance_and_records_transaction()
    {
        var (_, _, licenseId) = await CustomerWithLicenseAsync();
        var admin = await AdminClientAsync();

        var r1 = await admin.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/sms/topup", new { credits = 500, reason = "ilk paket" });
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r1.Content.ReadFromJsonAsync<TopupResponse>())!.CreditsRemaining.Should().Be(500);

        // İkinci yükleme birikmeli
        var r2 = await admin.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/sms/topup", new { credits = 250, reason = (string?)null });
        (await r2.Content.ReadFromJsonAsync<TopupResponse>())!.CreditsRemaining.Should().Be(750);

        // Ledger invariant: SUM(Amount) == balance
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sum = await db.LicenseSmsTransactions.Where(t => t.LicenseId == licenseId).SumAsync(t => t.Amount);
        sum.Should().Be(750);
        var bal = await db.LicenseSmsBalances.FirstAsync(b => b.LicenseId == licenseId);
        bal.CreditsRemaining.Should().Be(750);
    }

    [Fact]
    public async Task Admin_topup_invalid_credits_returns_400()
    {
        var (_, _, licenseId) = await CustomerWithLicenseAsync();
        var admin = await AdminClientAsync();
        var resp = await admin.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/sms/topup", new { credits = 0, reason = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_topup_unknown_license_returns_404()
    {
        var admin = await AdminClientAsync();
        var resp = await admin.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{Guid.NewGuid()}/sms/topup", new { credits = 100, reason = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_balance_lists_recent_transactions()
    {
        var (_, _, licenseId) = await CustomerWithLicenseAsync();
        var admin = await AdminClientAsync();
        await admin.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/sms/topup", new { credits = 300, reason = "paket" });

        var resp = await admin.GetFromJsonAsync<AdminBalanceResponse>(
            $"/api/v1/admin/licenses/{licenseId}/sms/balance");
        resp!.CreditsRemaining.Should().Be(300);
        resp.RecentTransactions.Should().ContainSingle()
            .Which.Should().Match<TxItem>(t => t.Amount == 300 && t.Kind == "purchase");
    }

    [Fact]
    public async Task Customer_reads_own_balance()
    {
        var (client, _, licenseId) = await CustomerWithLicenseAsync();
        var admin = await AdminClientAsync();
        await admin.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/sms/topup", new { credits = 120, reason = (string?)null });

        var resp = await client.GetFromJsonAsync<CustomerBalanceResponse>(
            $"/api/v1/licenses/{licenseId}/sms/balance");
        resp!.CreditsRemaining.Should().Be(120);
    }

    [Fact]
    public async Task Customer_reads_zero_when_no_topup()
    {
        var (client, _, licenseId) = await CustomerWithLicenseAsync();
        var resp = await client.GetFromJsonAsync<CustomerBalanceResponse>(
            $"/api/v1/licenses/{licenseId}/sms/balance");
        resp!.CreditsRemaining.Should().Be(0);
    }

    [Fact]
    public async Task Customer_cannot_read_other_license_balance()
    {
        var (clientA, _, _) = await CustomerWithLicenseAsync();
        var (_, _, licenseB) = await CustomerWithLicenseAsync();

        var resp = await clientA.GetAsync($"/api/v1/licenses/{licenseB}/sms/balance");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Customer_cannot_topup_via_admin_endpoint()
    {
        var (client, _, licenseId) = await CustomerWithLicenseAsync();
        // Müşteri JWT'siyle admin endpoint → 401/403
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/sms/topup", new { credits = 100, reason = "x" });
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
