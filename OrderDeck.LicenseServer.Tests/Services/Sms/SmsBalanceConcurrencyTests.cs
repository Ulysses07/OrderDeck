using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// LicenseSmsBalance kayıp-güncelleme koruması (F03, 2026-09-09 denetimi).
///
/// Gerçek SQL Server şart: InMemory'de eşzamanlılık semantiği yok. Token
/// öncesi davranış gerçek SQL Server'da tekrar üretilmişti: paralel topup +
/// kampanya rezervi birbirinin yazımını ezip kredi kaybettirebiliyordu
/// (invariant: CreditsRemaining = SUM(Transactions.Amount)).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SmsBalanceConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public SmsBalanceConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Paralel_topuplar_kredi_kaybettirmez()
    {
        var licenseId = await SeedAsync(initialCredits: 0);

        // Her görev kendi DI scope'u (kendi DbContext'i) ile yazar — HTTP
        // katmanı olmadan servis seviyesinde saf yarış.
        var results = await Task.WhenAll(Enumerable.Range(0, ParallelAttempts)
            .Select(async _ =>
            {
                using var scope = _factory.Services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<LicenseSmsBalanceService>();
                try
                {
                    return await svc.ApplyAndSaveAsync(
                        licenseId, 50, "purchase", reason: null,
                        createdByCustomerId: null, disallowNegative: false, CancellationToken.None);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Retry tükenmesi (3 deneme): istek iz bırakmadan düşer.
                    return null;
                }
            }));

        var succeeded = results.Count(r => r is not null);
        succeeded.Should().BeGreaterThan(0);

        var (credits, ledgerSum, txCount) = await ReadStateAsync(licenseId);
        txCount.Should().Be(succeeded, "kaybeden yazımın transaction'ı da geri alınmalı");
        credits.Should().Be(succeeded * 50,
            "token öncesi davranışta paralel topup'lar birbirini ezip kredi kaybettiriyordu");
        credits.Should().Be(ledgerSum, "invariant: CreditsRemaining = SUM(ledger)");
    }

    [Fact]
    public async Task Esazamanli_iki_rezervden_yalnizca_biri_gecer()
    {
        // Kredi 100 — iki paralel -100 rezerv (disallowNegative). Token öncesi
        // ikisi de 100 görüp geçebiliyordu → kredi -100 (kural delindi).
        var licenseId = await SeedAsync(initialCredits: 100);

        var results = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(async _ =>
            {
                using var scope = _factory.Services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<LicenseSmsBalanceService>();
                return await svc.ApplyAndSaveAsync(
                    licenseId, -100, "send-reserve", reason: null,
                    createdByCustomerId: null, disallowNegative: true, CancellationToken.None);
            }));

        results.Count(r => r is not null).Should().Be(1,
            "kredi 100 iken -100'lük rezervlerden yalnızca biri geçebilir");
        results.Count(r => r is null).Should().Be(1);

        var (credits, ledgerSum, _) = await ReadStateAsync(licenseId);
        credits.Should().Be(0, "kredi asla sıfırın altına düşmemeli");
        // Seed kredisi ledger'sız yazıldığı için toplam yalnız -100 olmalı.
        ledgerSum.Should().Be(-100);
    }

    private async Task<Guid> SeedRefundCampaignAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaignId = Guid.NewGuid();

        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId,
            LicenseId = licenseId,
            MessageBody = "Kampanya",
            Status = "sending",
            ClaimedAt = DateTimeOffset.UnixEpoch,
            SegmentsPerMessage = 1,
            RecipientCount = 2,
            ReservedCredits = 2,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        for (var i = 0; i < 2; i++)
        {
            db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Phone = $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}",
                Status = "failed",
                Error = "provider-rejected",
            });
        }

        await db.SaveChangesAsync();
        return campaignId;
    }

    /// <summary>
    /// Bulgu 2 — bayat bir tamamlanma+iade, kampanya çakışmasında TOPTAN
    /// düşmeli. Bugün `ApplyAndSaveAsync` `ex.Entries`'in tamamını yeniden
    /// yüklüyor: hazırlanmış `completed` + `RefundedCredits` silinip yalnız
    /// bakiye artışı hayatta kalıyor, yani kampanya "hiç tamamlanmamış" ama
    /// krediler İADE EDİLMİŞ oluyor. Sonraki koşu iadeyi bir kez daha yazar.
    /// </summary>
    [Fact]
    public async Task Kampanya_cakismasi_iadeyi_yeniden_uygulamaz()
    {
        var licenseId = await SeedAsync(initialCredits: 100);
        var campaignId = await SeedRefundCampaignAsync(licenseId);

        using (var workerScope = _factory.Services.CreateScope())
        {
            var workerDb = workerScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var balance = workerScope.ServiceProvider
                .GetRequiredService<LicenseSmsBalanceService>();
            var campaign = await workerDb.SmsCampaigns.SingleAsync(c => c.Id == campaignId);

            // İşçi kampanyayı okuduktan SONRA başka biri devam ettiriyor:
            // ClaimedAt değişti, yani elimizdeki tamamlanma artık bayat.
            using (var resumeScope = _factory.Services.CreateScope())
            {
                var resumeDb = resumeScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
                var resumed = await resumeDb.SmsCampaigns.SingleAsync(c => c.Id == campaignId);

                resumed.Status = "pending";
                resumed.ClaimedAt = null;
                await resumeDb.SaveChangesAsync();
            }

            campaign.Status = "completed";
            campaign.CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.ClaimedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.RefundedCredits = 2;

            Func<Task> staleRefund = async () =>
            {
                await balance.ApplyAndSaveAsync(
                    licenseId, 2, "send-refund",
                    reason: $"campaign:{campaignId} failed=2",
                    createdByCustomerId: null,
                    disallowNegative: false,
                    CancellationToken.None);
            };

            await staleRefund.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.AsNoTracking()
                .SingleAsync(c => c.Id == campaignId);

            campaign.Status.Should().Be("pending");
            campaign.ClaimedAt.Should().BeNull();
            campaign.CompletedAt.Should().BeNull();
            campaign.RefundedCredits.Should().Be(0);

            (await db.LicenseSmsBalances
                .Where(b => b.LicenseId == licenseId)
                .Select(b => b.CreditsRemaining)
                .SingleAsync()).Should().Be(100, "düşen tamamlanma kredi yaratmamalı");

            (await db.LicenseSmsTransactions.CountAsync(
                t => t.LicenseId == licenseId && t.Kind == "send-refund"))
                .Should().Be(0, "ledger satırı bakiyeyle birlikte geri alınmalı");
        }

        // Kampanya gerçekten yeniden koşturulduğunda iade BİR KEZ yazılmalı;
        // ikinci koşu hiç alıcı bulamayıp doğrudan tamamlamaya gider ve
        // idempotans farkı sıfır çıkar.
        using (var retryScope = _factory.Services.CreateScope())
        {
            await retryScope.ServiceProvider
                .GetRequiredService<SmsCampaignSendJob>().RunAsync(campaignId);
        }

        using (var duplicateScope = _factory.Services.CreateScope())
        {
            await duplicateScope.ServiceProvider
                .GetRequiredService<SmsCampaignSendJob>().RunAsync(campaignId);
        }

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.AsNoTracking()
                .SingleAsync(c => c.Id == campaignId);

            campaign.Status.Should().Be("completed");
            campaign.RefundedCredits.Should().Be(2);

            (await db.LicenseSmsBalances
                .Where(b => b.LicenseId == licenseId)
                .Select(b => b.CreditsRemaining)
                .SingleAsync()).Should().Be(102);

            var refunds = await db.LicenseSmsTransactions.AsNoTracking()
                .Where(t => t.LicenseId == licenseId && t.Kind == "send-refund")
                .ToListAsync();

            refunds.Should().ContainSingle();
            refunds.Single().Amount.Should().Be(2);
        }
    }

    /// <summary>
    /// Gerileme koruması: MEŞRU bakiye çakışması (paralel bakiye yüklemesi)
    /// hâlâ yeniden denenmeli. Retry'ı tamamen kaldıran "düzeltme" bu testi
    /// kırar — yükleme araya girdiğinde iade 409/500'e dönüşürdü.
    /// </summary>
    [Fact]
    public async Task Yalniz_bakiye_cakismasi_tamamlanma_ve_iadeyi_korur()
    {
        var licenseId = await SeedAsync(initialCredits: 100);
        var campaignId = await SeedRefundCampaignAsync(licenseId);

        using (var workerScope = _factory.Services.CreateScope())
        {
            var db = workerScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var service = workerScope.ServiceProvider
                .GetRequiredService<LicenseSmsBalanceService>();

            var staleBalance = await db.LicenseSmsBalances
                .SingleAsync(b => b.LicenseId == licenseId);
            var campaign = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);

            using (var topupScope = _factory.Services.CreateScope())
            {
                var topupDb = topupScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
                var balance = await topupDb.LicenseSmsBalances
                    .SingleAsync(b => b.LicenseId == licenseId);

                balance.CreditsRemaining += 50;
                // Jeton KESİN ilerlemeli: `UtcNow` seed damgasının gerisinde
                // kalırsa çakışma hiç doğmaz ve test hiçbir şey kanıtlamaz.
                balance.UpdatedAt = staleBalance.UpdatedAt.AddTicks(1);

                topupDb.LicenseSmsTransactions.Add(new LicenseSmsTransaction
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    Amount = 50,
                    Kind = "purchase",
                    CreatedAt = DateTimeOffset.UtcNow,
                });

                await topupDb.SaveChangesAsync();
            }

            campaign.Status = "completed";
            campaign.CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.ClaimedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.RefundedCredits = 2;

            (await service.ApplyAndSaveAsync(
                licenseId, 2, "send-refund",
                reason: $"campaign:{campaignId} failed=2",
                createdByCustomerId: null,
                disallowNegative: false,
                CancellationToken.None)).Should().Be(152);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var completed = await verifyDb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        completed.Status.Should().Be("completed", "bakiye retry'ı kampanyayı geri almamalı");
        completed.RefundedCredits.Should().Be(2);

        var (credits, ledgerSum, txCount) = await ReadStateAsync(licenseId);
        credits.Should().Be(152);
        // `ledgerSum` 52: seed başlangıç bakiyesini ledger satırı YAZMADAN
        // kuruyor, ledger'da yalnız +50 yükleme ve +2 iade var.
        ledgerSum.Should().Be(52);
        txCount.Should().Be(2);
    }

    private async Task<(int Credits, int LedgerSum, int TxCount)> ReadStateAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var credits = await db.LicenseSmsBalances
            .Where(b => b.LicenseId == licenseId)
            .Select(b => b.CreditsRemaining)
            .SingleAsync();
        var ledger = await db.LicenseSmsTransactions
            .Where(t => t.LicenseId == licenseId)
            .Select(t => t.Amount)
            .ToListAsync();
        return (credits, ledger.Sum(), ledger.Count);
    }

    private async Task<Guid> SeedAsync(int initialCredits)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"smsc-{Guid.NewGuid():N}@example.com",
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = "smsc-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        // Bakiye satırını baştan yaz: testler UPDATE yolundaki token'ı hedefliyor
        // (insert yarışı unique index'e bırakıldı — bilinçli kapsam dışı).
        db.LicenseSmsBalances.Add(new LicenseSmsBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CreditsRemaining = initialCredits,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return licenseId;
    }
}
