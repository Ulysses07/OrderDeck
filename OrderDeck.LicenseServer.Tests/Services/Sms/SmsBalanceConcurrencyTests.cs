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
