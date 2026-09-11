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

    // ── A11: idempotency anahtarı + İÇERİK sözleşmesi ───────────────────────
    // Replay yalnız istek İLK isteğin aynısıysa meşru. Farklı müşteri/toplam
    // ile gelen aynı anahtar bir istemci hatasıdır; ilk sonucu oynatmak yanlış
    // satışa düşüm bağlar. 409 content-conflict + SIFIR yan etki.

    [Fact]
    public async Task Apply_same_key_different_product_total_returns_content_conflict()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var key = Guid.NewGuid();

        var ilk = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });
        ilk.StatusCode.Should().Be(HttpStatusCode.OK);

        var ikinci = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 999m, IdempotencyKey = key });
        ikinci.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ikinci.Content.ReadFromJsonAsync<ProblemDetailsLite>();
        problem!.Title.Should().Be("content-conflict");

        // Yan etki yok: bakiye ilk düşümden sonraki değerde kalmalı.
        var preview = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        preview!.Balance.Should().Be(400m);
    }

    [Fact]
    public async Task Apply_same_key_different_customer_returns_content_conflict()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var key = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });

        var ikinci = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = Guid.NewGuid(), Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });
        ikinci.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ikinci.Content.ReadFromJsonAsync<ProblemDetailsLite>();
        problem!.Title.Should().Be("content-conflict");
    }

    [Fact]
    public async Task Apply_same_key_larger_amount_still_replays()
    {
        // Toleranslı yön: replay Amount >= ilk düşüm olduğu sürece meşru —
        // istemci replay'de Amount=ProductTotal gönderir (sunucu zaten kırpar).
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);
        var key = Guid.NewGuid();

        var ilk = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });
        var ilkBody = await ilk.Content.ReadFromJsonAsync<ApplyResponse>();

        var ikinci = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 2100m, ProductTotal = 2100m, IdempotencyKey = key });
        ikinci.StatusCode.Should().Be(HttpStatusCode.OK);
        var ikinciBody = await ikinci.Content.ReadFromJsonAsync<ApplyResponse>();
        ikinciBody!.AppliedAmount.Should().Be(ilkBody!.AppliedAmount);
    }

    [Fact]
    public async Task Apply_same_key_smaller_amount_than_deducted_returns_content_conflict()
    {
        // A11 — üçüncü koşul: depolanan düşüm (-tx.Amount) yeni isteğin
        // yetkilendirdiği miktardan (req.Amount) büyükse çelişki. İlk çağrı
        // 150m bakiyeden 150m düşürür; replay'de Amount=50m geliyor — oynatmak
        // istemcinin onaylamadığı bir düşümü kabul etmek olur.
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(200m);
        var key = Guid.NewGuid();

        var ilk = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 150m, ProductTotal = 2100m, IdempotencyKey = key });
        ilk.StatusCode.Should().Be(HttpStatusCode.OK);

        // Replay: aynı anahtar + aynı müşteri + aynı ProductTotal, ama Amount
        // gerçekte düşülen tutardan (150m) daha küçük → content-conflict.
        var ikinci = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 50m, ProductTotal = 2100m, IdempotencyKey = key });
        ikinci.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ikinci.Content.ReadFromJsonAsync<ProblemDetailsLite>();
        problem!.Title.Should().Be("content-conflict");

        // Yan etki yok: bakiye ilk düşümden sonraki değerde kalmalı.
        var preview = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        preview!.Balance.Should().Be(50m);
    }

    private sealed record ProblemDetailsLite(string? Title, string? Detail, int? Status);

    // ── Reverse (revizyon akışının sunucu yarısı) ───────────────────────────
    // WPF, aynı yayında toplam değişince eski düşümü geri alıp yeni toplamla
    // taze düşüm yapar (spec K2). Bu uç panel'deki reverse'in WPF-yüzeyi
    // ikizidir: yalnız kendi lisansının purchase-deduction satırını geri
    // alabilir, hakem N01 filtered-unique index'tir.

    private async Task<Guid> ApplyAndGetTransactionIdAsync(
        HttpClient client, Guid licenseId, Guid wpfCustomerId, decimal amount, decimal productTotal)
    {
        var key = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = amount, ProductTotal = productTotal, IdempotencyKey = key });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ApplyResponse>();
        return body!.TransactionId;
    }

    [Fact]
    public async Task Reverse_restores_balance_and_writes_reversal_row()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var txId = await ApplyAndGetTransactionIdAsync(client, licenseId, wpfCustomerId, 100m, 2100m);

        var resp = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{txId}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var preview = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        preview!.Balance.Should().Be(500m); // düşüm geri geldi
    }

    [Fact]
    public async Task Reverse_second_call_returns_already_reversed()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var txId = await ApplyAndGetTransactionIdAsync(client, licenseId, wpfCustomerId, 100m, 2100m);

        await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{txId}/reverse", null);
        var ikinci = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{txId}/reverse", null);

        ikinci.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ikinci.Content.ReadFromJsonAsync<ProblemDetailsLite>();
        problem!.Title.Should().Be("already-reversed");

        var preview = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        preview!.Balance.Should().Be(500m); // ikinci geri alma para EKLEMEDİ
    }

    [Fact]
    public async Task Reverse_unknown_transaction_returns_404()
    {
        var (client, licenseId, _) = await SetupWithBalanceAsync(500m);
        var resp = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{Guid.NewGuid()}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reverse_non_deduction_transaction_returns_404()
    {
        // Seed'deki refund-full satırı bu ucun kapsamı dışında — müşteri yüzeyi
        // yalnız KENDİ purchase-deduction'ını geri alabilir.
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        Guid refundTxId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            refundTxId = db.CustomerBalanceTransactions
                .Where(t => t.LicenseId == licenseId && t.WpfCustomerId == wpfCustomerId
                    && t.Kind == "refund-full")
                .Select(t => t.Id)
                .Single();
        }

        var resp = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{refundTxId}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reverse_foreign_license_returns_404()
    {
        var (clientA, licenseA, wpfCustomerA) = await SetupWithBalanceAsync(500m);
        var txId = await ApplyAndGetTransactionIdAsync(clientA, licenseA, wpfCustomerA, 100m, 2100m);

        var (clientB, licenseB, _) = await SetupWithBalanceAsync(100m);
        var resp = await clientB.PostAsync(
            $"/api/v1/licenses/{licenseB}/customer-balance/transactions/{txId}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
