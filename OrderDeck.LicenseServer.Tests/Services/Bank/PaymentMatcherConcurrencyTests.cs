using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>
/// Aynı harekete eşzamanlı iki eşleştirme GERÇEK SQL Server'da. InMemory tekil indeksi uygulamaz; kaybedenin
/// INSERT'i orada hiç patlamaz. Burada hareket başına tek öneri indeksi kaybedeni reddeder: eşleştirici kendi
/// satırını izlemeden çıkarıp kazananın satırını döndürmeli — iki çağıran tek satırda buluşur, kaybedenin
/// kapsamı (çekim işi ya da dekont onay isteği) zehirlenmez. Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PaymentMatcherConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public PaymentMatcherConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ayni_harekete_eszamanli_iki_eslestirme_tek_satirda_bulusur()
    {
        Guid txId;
        await using (var seed = NewDb(interceptor: null)) txId = await SeedAsync(seed);

        var rendezvous = new Rendezvous();
        await using var db1 = NewDb(new MatchInsertBarrier(rendezvous));
        await using var db2 = NewDb(new MatchInsertBarrier(rendezvous));
        var tx1 = await db1.BankTransactions.SingleAsync(t => t.Id == txId);
        var tx2 = await db2.BankTransactions.SingleAsync(t => t.Id == txId);

        var results = await Task.WhenAll(
            Matcher(db1).MatchAsync(tx1, CancellationToken.None),
            Matcher(db2).MatchAsync(tx2, CancellationToken.None));

        results.Select(r => r.Id).Distinct().Should().ContainSingle("kaybeden, kazananın satırını döndürmeli");
        results.Should().OnlyContain(r => r.Status == PaymentMatchStatus.Proposed);
        // Kaybedenin eklemesi izlemede kalmadı: kapsamın sonraki SaveChanges'i yeniden denemez, yazacak şey yok.
        db1.ChangeTracker.Entries<PaymentMatch>().Should().OnlyContain(e => e.State == EntityState.Unchanged);
        db2.ChangeTracker.Entries<PaymentMatch>().Should().OnlyContain(e => e.State == EntityState.Unchanged);
        (await db1.SaveChangesAsync()).Should().Be(0);
        (await db2.SaveChangesAsync()).Should().Be(0);
        await using var verify = NewDb(interceptor: null);
        (await verify.PaymentMatches.CountAsync(m => m.BankTransactionId == txId)).Should().Be(1);
    }

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }

    private static PaymentMatcher Matcher(LicenseDbContext db)
        => new(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance);

    private static async Task<Guid> SeedAsync(LicenseDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"m-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"match-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 500m, Currency = "TL", OccurredAt = now,
            Description = "HAVALE ayse_gul34", TransactionCode = "FT", FetchedAt = now,
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = now,
        });
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return tx.Id;
    }

    private sealed class Rendezvous
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) >= 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>Yeni PaymentMatch ekleyen SaveChanges'ı iki taraf da gelene kadar tutar: ikisi de mevcut satır
    /// sorgusunu satır yokken geçmiş olur, INSERT'ler tekil indekste yarışır.</summary>
    private sealed class MatchInsertBarrier(Rendezvous rendezvous) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Added))
                await rendezvous.ArriveAsync(cancellationToken);
            return result;
        }
    }
}
