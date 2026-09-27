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
/// Aynı dekontun iki bağdaştırma koşusu (ör. Hangfire'ın çift teslimi) DB'yi farklı anlarda okuyup dekontu iki ayrı
/// harekete bağlamaya çalışır — GERÇEK SQL Server'da. InMemory filtreli tekil indeksi uygulamaz; burada
/// <c>PaymentMatches.PaymentId</c> indeksi ikinci bağı reddeder. Kaybeden bağdaştırıcı denemesinin izini atıp bir kez yeniden
/// dener, dekontu bağlı bulur ve sessizce çıkar: dekont tek harekete bağlanır, iki çağıran da hatasız biter. (Açık gap'i iki
/// geç hareketin aynı anda çözmesi artık bu indekse ulaşmaz: gap ancak hareket dekontun TEK adayıysa çözülür.)
/// Testcontainers kullanır; CI ubuntu işinde koşar.
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
    public async Task Ayni_dekontun_iki_bagdastirmasi_farkli_hareket_secerse_dekont_tek_harekete_baglanir()
    {
        Seeded s;
        await using (var seed = NewDb(interceptor: null)) s = await SeedAsync(seed);

        var rendezvous = new Rendezvous();
        await using var db1 = NewDb(new LinkBarrier(rendezvous));
        await using var db2 = NewDb(new LinkBarrier(rendezvous));
        var log = new RetryCounter();

        // İlk koşu yalnız ilk hareketi görür (tek aday) ve bağı kaydetmeden önce bekler.
        var early = Recon(db1, log).ReconcileApprovalAsync(s.Payment, CancellationToken.None);
        await rendezvous.FirstArrived.WaitAsync(TimeSpan.FromSeconds(30));
        // Arada gönderen adını taşıyan hareket gelir; ikinci koşu iki aday görür, adla onu seçer.
        await using (var seed = NewDb(interceptor: null)) await AddNamedAsync(seed, s.Payment);
        var late = Recon(db2, log).ReconcileApprovalAsync(s.Payment, CancellationToken.None);
        await Task.WhenAll(early, late);

        log.Retries.Should().Be(1, "kaybeden indekse takıldı ve bir kez yeniden denedi");
        await using var verify = NewDb(interceptor: null);
        var linked = await verify.PaymentMatches.AsNoTracking().Where(m => m.PaymentId == s.Payment.Id).ToListAsync();
        linked.Should().ContainSingle("bir dekont iki harekete bağlanamaz");
        (await verify.PaymentMatchGaps.AsNoTracking().CountAsync()).Should().Be(0, "kaybeden dekontu bağlı bulur, gap yazmaz");
        foreach (var db in new[] { db1, db2 })
            db.ChangeTracker.Entries().Where(e => e.Entity is PaymentMatch or PaymentMatchGap or CustomerIbanMemory)
                .Should().NotContain(e => e.State != EntityState.Unchanged, "kaybedenin düşen denemesi izde kalmaz");
    }

    private sealed record Seeded(Payment Payment, BankTransaction First);

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

    /// <summary>Lisans, müşteri + shopper bağı, onaylı dekont ve onunla aynı tutarlı, açıklamasında gönderen adı geçmeyen,
    /// önerisiz bir hareket. Kimlik alanları üretilir.</summary>
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
        var first = Incoming(license.Id, "EFT GELEN");
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
        db.BankTransactions.Add(first);
        await db.SaveChangesAsync();
        await new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance)
            .MatchAsync(first, CancellationToken.None);
        return new Seeded(payment, first);
    }

    /// <summary>Dekontla aynı tutarlı, açıklamasında gönderen adı geçen, sonradan gelmiş hareket ve onun önerisi.</summary>
    private static async Task AddNamedAsync(LicenseDbContext db, Payment payment)
    {
        var named = Incoming(payment.LicenseId, $"HAVALE {payment.PayerName}");
        db.BankTransactions.Add(named);
        await db.SaveChangesAsync();
        await new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance)
            .MatchAsync(named, CancellationToken.None);
    }

    private static BankTransaction Incoming(Guid licenseId, string description) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 450m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow.AddHours(-1), Description = description, TransactionCode = "FT",
        FetchedAt = DateTimeOffset.UtcNow,
    };

    private sealed class Rendezvous
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        /// <summary>İlk taraf bariyere vardı: aday seçimini yapmış, bağı kaydetmek üzere.</summary>
        public Task FirstArrived => _first.Task;

        public async Task ArriveAsync(CancellationToken ct)
        {
            var arrived = Interlocked.Increment(ref _arrived);
            _first.TrySetResult();
            if (arrived >= 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>Dekontu bir harekete bağlayan İLK SaveChanges'i iki taraf da gelene kadar tutar: ikisi de bağsız dekontu
    /// görmüş olur, iki UPDATE filtreli PaymentId indeksinde yarışır. Yeniden deneme beklemez.</summary>
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
