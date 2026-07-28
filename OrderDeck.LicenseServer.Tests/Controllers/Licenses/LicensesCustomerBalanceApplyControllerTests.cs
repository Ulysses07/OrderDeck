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

    // ── Idempotency ─────────────────────────────────────────────────────────
    // WPF'in HttpClient dayanıklılık katmanı 5xx/ağ hatasında POST'u yeniden
    // deniyor. Koruma olmadan operatörün tek tıkı müşterinin bakiyesini iki kez
    // düşürür ve ledger'a iki düşüm satırı yazar — sessiz para kaybı.

    [Fact]
    public async Task Apply_same_key_twice_deducts_once()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var key = Guid.NewGuid();
        var body = new { WpfCustomerId = wpfCustomerId, Amount = 200m, ProductTotal = 2100m, IdempotencyKey = key };

        var first = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply", body);
        var second = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply", body);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var b1 = await first.Content.ReadFromJsonAsync<ApplyResponse>();
        var b2 = await second.Content.ReadFromJsonAsync<ApplyResponse>();
        b1!.AppliedAmount.Should().Be(200m);
        // Tekrar isteği İLK sonucun aynısını oynatmalı; "0 düştü" demek de
        // yanlış olurdu — WPF bu tutarı mesaja yazıyor.
        b2!.AppliedAmount.Should().Be(200m);
        b2.TransactionId.Should().Be(b1.TransactionId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.CustomerBalances
            .Single(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Balance.Should().Be(300m);
        db.CustomerBalanceTransactions
            .Count(t => t.LicenseId == licenseId && t.Kind == "purchase-deduction")
            .Should().Be(1);
    }

    [Fact]
    public async Task Apply_uses_idempotency_key_as_transaction_id()
    {
        // Anahtarın ledger satırının PK'sı OLMASI tasarımın kendisi: ayrı bir
        // rezervasyon tablosuna gerek bırakmayan şey bu. Kayarsa idempotency
        // sessizce kapanır, o yüzden ayrıca sabitleniyor.
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);
        var key = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 50m, ProductTotal = 500m, IdempotencyKey = key });

        var body = await resp.Content.ReadFromJsonAsync<ApplyResponse>();
        body!.TransactionId.Should().Be(key);
    }

    [Fact]
    public async Task Apply_without_key_keeps_old_behaviour()
    {
        // Alanı hiç göndermemek eski davranış: her istek yeni düşüm. Eski WPF
        // sürümleri bu yolda kalıyor, sessizce reddedilmemeli.
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var body = new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m };

        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/customer-balance/apply", body);
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/customer-balance/apply", body);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.CustomerBalances
            .Single(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Balance.Should().Be(300m);
    }

    [Fact]
    public async Task Apply_empty_key_returns_400()
    {
        // Boş Guid "anahtar yok" DEĞİL: bozuk anahtar üreten bir istemcide
        // idempotency sessizce kapanır ve çift düşüm serbest kalır.
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 50m, ProductTotal = 500m, IdempotencyKey = Guid.Empty });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Apply_key_belonging_to_another_row_returns_404()
    {
        // Anahtar başka bir kaydın kimliğiyse çağıran onu ne görmeli ne de
        // üzerine yazabilmeli. Ayrıca ele alınmasa PK ihlali 500'e dönerdi.
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);

        Guid foreignTxId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            foreignTxId = db.CustomerBalanceTransactions
                .First(t => t.LicenseId == licenseId).Id;   // kurulumdaki refund-full satırı
        }

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 50m, ProductTotal = 500m, IdempotencyKey = foreignTxId });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
