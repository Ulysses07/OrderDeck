using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelCustomerBalanceControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelCustomerBalanceControllerTests(ApiFactory f) => _factory = f;

    private sealed record BalanceDto(Guid WpfCustomerId, Guid LicenseId, decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind,
        decimal? OriginalAmount, decimal? ShippingDeducted, string? Reason,
        Guid? ReversesTransactionId, DateTimeOffset CreatedAt);
    private sealed record BalanceDetailsResponse(BalanceDto Balance, TransactionDto[] Transactions);

    private async Task<(HttpClient client, Guid customerId, Guid licenseId, Guid wpfCustomerId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = "LDK-BAL-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        var wpfCustomerId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "u" + wpfCustomerId.ToString("N")[..6],
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (client, customerId, licenseId, wpfCustomerId);
    }

    [Fact]
    public async Task Get_no_balance_returns_zero_with_empty_transactions()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        var resp = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance");
        resp.Should().NotBeNull();
        resp!.Balance.Balance.Should().Be(0m);
        resp.Transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task RefundFull_adds_full_amount_and_logs_transaction()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-full",
            new { Amount = 2100m, Reason = "Hatalı ürün" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var details = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance");
        details!.Balance.Balance.Should().Be(2100m);
        details.Transactions.Should().HaveCount(1);
        details.Transactions[0].Kind.Should().Be("refund-full");
        details.Transactions[0].Amount.Should().Be(2100m);
        details.Transactions[0].OriginalAmount.Should().Be(2100m);
        details.Transactions[0].Reason.Should().Be("Hatalı ürün");
    }

    [Fact]
    public async Task RefundNet_subtracts_shipping_and_logs_both_values()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-net",
            new { OriginalAmount = 2100m, ShippingDeducted = 100m, Reason = "Beden uymadı" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var details = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance");
        details!.Balance.Balance.Should().Be(2000m);
        details.Transactions[0].Amount.Should().Be(2000m);
        details.Transactions[0].OriginalAmount.Should().Be(2100m);
        details.Transactions[0].ShippingDeducted.Should().Be(100m);
    }

    [Fact]
    public async Task RefundFull_invalid_amount_returns_400()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-full",
            new { Amount = 0m, Reason = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefundNet_shipping_gte_amount_returns_400()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-net",
            new { OriginalAmount = 100m, ShippingDeducted = 100m, Reason = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ManualAdjustment_positive_adds_to_balance()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/manual-adjustment",
            new { Amount = 50m, Reason = "Hediye" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var details = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance");
        details!.Balance.Balance.Should().Be(50m);
    }

    [Fact]
    public async Task ManualAdjustment_negative_exceeding_balance_returns_409()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        // Balance is 0, try to subtract 10.
        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/manual-adjustment",
            new { Amount = -10m, Reason = "Düzeltme" });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ManualAdjustment_empty_reason_returns_400()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/manual-adjustment",
            new { Amount = 10m, Reason = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reverse_undoes_previous_transaction()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();

        // First add 2100 refund.
        await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-full",
            new { Amount = 2100m, Reason = "Hata" });
        var afterRefund = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance");
        var refundTxId = afterRefund!.Transactions[0].Id;

        // Reverse it.
        var resp = await client.PostAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/transactions/{refundTxId}/reverse",
            content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance");
        after!.Balance.Balance.Should().Be(0m);
        after.Transactions.Should().HaveCount(2);
        after.Transactions[0].Kind.Should().Be("reversal");
        after.Transactions[0].ReversesTransactionId.Should().Be(refundTxId);
    }

    [Fact]
    public async Task Reverse_already_reversed_returns_409()
    {
        var (client, _, _, wpfCustomerId) = await SetupAsync();
        await client.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-full",
            new { Amount = 100m, Reason = "x" });
        var details = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance");
        var txId = details!.Transactions[0].Id;

        await client.PostAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/transactions/{txId}/reverse",
            content: null);
        var resp = await client.PostAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/transactions/{txId}/reverse",
            content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cross_tenant_isolation_returns_404()
    {
        // Yayıncı A bir customer projection oluşturur; B onun balance'ına dokunamaz.
        var (_, _, _, wpfCustomerIdA) = await SetupAsync();
        var (clientB, _, _, _) = await SetupAsync();

        var resp = await clientB.GetAsync(
            $"/api/panel/customers/{wpfCustomerIdA}/balance");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── A5b: rotadaki kopya Id'si asıl kayda çözülür ────────────────────────
    // Panel eski bir bağlantı/sekme ya da eski sürümün Id'siyle kopyaya gelebilir.
    // Çözülmeden işlense kopyaya ikinci, bölünmüş bir bakiye açılırdı.

    private async Task<Guid> SeedAliasAsync(Guid licenseId, Guid canonicalId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var aliasId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = aliasId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "k" + aliasId.ToString("N")[..6],
            MergedIntoId = canonicalId,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return aliasId;
    }

    [Fact]
    public async Task Get_kopya_Id_ile_asil_kaydin_bakiyesini_doner()
    {
        var (client, _, licenseId, canonicalId) = await SetupAsync();
        (await client.PostAsJsonAsync(
            $"/api/panel/customers/{canonicalId}/balance/refund-full",
            new { Amount = 300m, Reason = "Hata" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var aliasId = await SeedAliasAsync(licenseId, canonicalId);

        var resp = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{aliasId}/balance");

        resp!.Balance.Balance.Should().Be(300m);
        resp.Transactions.Should().ContainSingle().Which.Kind.Should().Be("refund-full");
    }

    [Fact]
    public async Task ManualAdjustment_kopya_Id_ile_asil_kayda_yazar_kopyaya_satir_acmaz()
    {
        var (client, _, licenseId, canonicalId) = await SetupAsync();
        var aliasId = await SeedAliasAsync(licenseId, canonicalId);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/customers/{aliasId}/balance/manual-adjustment",
            new { Amount = 50m, Reason = "Elle düzeltme" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.CustomerBalances.SingleAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId))
            .Balance.Should().Be(50m);
        (await db.CustomerBalances.AnyAsync(b => b.WpfCustomerId == aliasId))
            .Should().BeFalse("kopyaya asla bakiye satırı açılmaz");
        (await db.CustomerBalanceTransactions.AnyAsync(t => t.WpfCustomerId == aliasId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Reverse_kopya_Id_ile_asil_kaydin_hareketini_geri_alir()
    {
        var (client, _, licenseId, canonicalId) = await SetupAsync();
        await client.PostAsJsonAsync(
            $"/api/panel/customers/{canonicalId}/balance/refund-full",
            new { Amount = 200m, Reason = "Hata" });
        var details = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{canonicalId}/balance");
        var txId = details!.Transactions[0].Id;
        var aliasId = await SeedAliasAsync(licenseId, canonicalId);

        var resp = await client.PostAsync(
            $"/api/panel/customers/{aliasId}/balance/transactions/{txId}/reverse", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await client.GetFromJsonAsync<BalanceDetailsResponse>(
            $"/api/panel/customers/{canonicalId}/balance");
        after!.Balance.Balance.Should().Be(0m);
        after.Transactions[0].ReversesTransactionId.Should().Be(txId);
    }

    [Fact]
    public async Task Kopya_Id_kiraci_yalitimini_delmez()
    {
        var (_, _, licenseIdA, canonicalA) = await SetupAsync();
        var aliasA = await SeedAliasAsync(licenseIdA, canonicalA);
        var (clientB, _, _, _) = await SetupAsync();

        var resp = await clientB.GetAsync($"/api/panel/customers/{aliasA}/balance");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
