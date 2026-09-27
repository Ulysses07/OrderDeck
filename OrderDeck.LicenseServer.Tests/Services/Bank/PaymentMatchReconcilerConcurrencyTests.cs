using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
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
/// Tek açık gap'i iki aynı tutarlı hareket aynı anda çözmeye çalışır (sink ile telafi taraması) — GERÇEK SQL Server'da.
/// InMemory filtreli tekil indeksi uygulamaz; burada <c>PaymentMatches.PaymentId</c> indeksi ikinci bağı reddeder.
/// Kaybeden bağdaştırıcı denemesinin izini atıp bir kez yeniden dener, gap'i çözülmüş bulur ve sessizce çıkar: dekont tek
/// harekete bağlanır, iki çağıran da hatasız biter. Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PaymentMatchReconcilerConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public PaymentMatchReconcilerConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tek_acik_gap_i_iki_hareket_ayni_anda_cozerse_dekont_tek_harekete_baglanir()
    {
        Seeded s;
        await using (var seed = NewDb(interceptor: null)) s = await SeedAsync(seed);

        var rendezvous = new Rendezvous();
        await using var db1 = NewDb(new LinkBarrier(rendezvous));
        await using var db2 = NewDb(new LinkBarrier(rendezvous));
        var log = new RetryCounter();

        await Task.WhenAll(
            Recon(db1, log).TryResolveGapAsync(s.First, CancellationToken.None),
            Recon(db2, log).TryResolveGapAsync(s.Second, CancellationToken.None));

        log.Retries.Should().Be(1, "kaybeden indekse takıldı ve bir kez yeniden denedi");
        await using var verify = NewDb(interceptor: null);
        var linked = await verify.PaymentMatches.AsNoTracking().Where(m => m.PaymentId == s.PaymentId).ToListAsync();
        linked.Should().ContainSingle("bir dekont iki harekete bağlanamaz");
        var gap = await verify.PaymentMatchGaps.AsNoTracking().SingleAsync();
        gap.ResolvedBankTransactionId.Should().Be(linked[0].BankTransactionId, "gap'i bağı kazanan hareket çözer");
        foreach (var db in new[] { db1, db2 })
            db.ChangeTracker.Entries().Where(e => e.Entity is PaymentMatch or PaymentMatchGap or CustomerIbanMemory)
                .Should().NotContain(e => e.State != EntityState.Unchanged, "kaybedenin düşen denemesi izde kalmaz");
    }

    private sealed record Seeded(Guid PaymentId, BankTransaction First, BankTransaction Second);

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }

    private static PaymentMatchReconciler Recon(LicenseDbContext db, ILogger<PaymentMatchReconciler> log)
        => new(db, new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance), log);

    /// <summary>Bağdaştırıcının "bir kez yeniden deneniyor" kayıtlarını sayar (iki bağlamdan eşzamanlı yazılır).</summary>
    private sealed class RetryCounter : ILogger<PaymentMatchReconciler>
    {
        private int _retries;
        public int Retries => Volatile.Read(ref _retries);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("yeniden deneniyor")) Interlocked.Increment(ref _retries);
        }
    }

    /// <summary>Lisans, müşteri + shopper bağı, onaylı dekont ve onun açık (NoCandidate) gap'i; sonradan gelmiş, aynı
    /// tutarlı, önerisiz iki hareket. Kimlik alanları üretilir.</summary>
    private static async Task<Seeded> SeedAsync(LicenseDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"m-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"recon-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(), FullName = "Ayse Gul", Phone = $"+9055{Random.Shared.Next(10000000, 99999999)}",
            PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-", CreatedAt = now, UpdatedAt = now,
        };
        var wpf = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = now,
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ShopperId = shopper.Id, PayerName = "AYSE GUL", Amount = 450m,
            PaidAt = now.AddHours(-3), ReferansNo = $"r-{Guid.NewGuid():N}", Status = PaymentStatus.Approved, ApprovedAt = now,
            CreatedAt = now, UpdatedAt = now,
        };
        BankTransaction Incoming() => new()
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 450m, Currency = "TL",
            OccurredAt = now.AddHours(-1), Description = "EFT GELEN", TransactionCode = "FT", FetchedAt = now,
        };
        var first = Incoming(); var second = Incoming();
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        db.Shoppers.Add(shopper);
        db.WpfCustomerProjections.Add(wpf);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = license.Id, Platform = "youtube", Username = "ayse_gul34",
            WpfCustomerId = wpf.Id, JoinedAt = now,
        });
        db.Payments.Add(payment);
        db.PaymentMatchGaps.Add(new PaymentMatchGap
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, PaymentId = payment.Id, Reason = PaymentMatchGapReason.NoCandidate,
            CreatedAt = now,
        });
        db.BankTransactions.AddRange(first, second);
        await db.SaveChangesAsync();
        var matcher = new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance);
        await matcher.MatchAsync(first, CancellationToken.None);
        await matcher.MatchAsync(second, CancellationToken.None);
        return new Seeded(payment.Id, first, second);
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

    /// <summary>Dekontu bir harekete bağlayan İLK SaveChanges'i iki taraf da gelene kadar tutar: ikisi de açık gap'i ve
    /// bağsız dekontu görmüş olur, iki UPDATE filtreli PaymentId indeksinde yarışır. Yeniden deneme beklemez.</summary>
    private sealed class LinkBarrier(Rendezvous rendezvous) : SaveChangesInterceptor
    {
        private bool _passed;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_passed && eventData.Context!.ChangeTracker.Entries<PaymentMatch>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.PaymentId != null))
            {
                _passed = true;
                await rendezvous.ArriveAsync(cancellationToken);
            }
            return result;
        }
    }
}
