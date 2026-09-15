using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

/// <summary>
/// R10-S03'ün GERÇEK SQL kanıtı: InMemory eşi
/// (PasswordResetCodeIssueRaceTests) jeton semantiğini ve "yalnız biri kod
/// alır"ı kanıtlıyor ama InMemory transaksiyonsuz olduğu için kaybedenin
/// satırının GERİ ALINDIĞINI kanıtlayamıyor. Burada SQL Server'ın kendisi
/// kaybedenin batch'ini (kod INSERT'i dahil) tek transaction'da geri alır:
/// DB'de tek satır kalır. Testcontainers kullandığı için canlı yayın
/// sırasında ÇALIŞTIRILMAZ; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PasswordResetCodeIssueConcurrencyTests : IAsyncLifetime
{
    private static readonly PasswordHasher Hasher = new();

    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public PasswordResetCodeIssueConcurrencyTests(SqlServerContainerFixture sql)
        => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Eszamanli_iki_uretim_sql_uzerinde_tek_satir_birakir()
    {
        var rendezvous = new IssueRendezvous();

        await using var seedDb = NewDb(interceptor: null);
        var shopperId = await SeedShopperAsync(seedDb);

        await using var db1 = NewDb(new ResetCodeSaveBarrier(rendezvous));
        await using var db2 = NewDb(new ResetCodeSaveBarrier(rendezvous));
        var svc1 = NewService(db1);
        var svc2 = NewService(db2);

        // Controller'lar gibi: her istek shopper'ı KENDİ context'inden izlenir yükler.
        var shopper1 = await db1.Shoppers.SingleAsync(s => s.Id == shopperId);
        var shopper2 = await db2.Shoppers.SingleAsync(s => s.Id == shopperId);

        var results = await Task.WhenAll(
            svc1.IssueWithHandleAsync(shopper1, "1.2.3.4"),
            svc2.IssueWithHandleAsync(shopper2, "5.6.7.8"));

        results.Count(r => r is not null).Should().Be(1,
            "aynı shopper için eşzamanlı iki üretimden yalnız biri kod almalı");

        // Gerçek SQL'de SaveChanges atomik: kaybedenin kod satırı da geri
        // alınır — InMemory'nin kanıtlayamadığı yarısı bu.
        var winner = results.Single(r => r is not null)!;
        await using var verifyDb = NewDb(interceptor: null);
        var rows = await verifyDb.ShopperPasswordResetCodes.ToListAsync();
        rows.Should().ContainSingle().Which.Id.Should().Be(winner.Id);
    }

    // ── Helpers (InMemory eşiyle aynı desen) ─────────────────────────────────

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(_connStr);
        if (interceptor is not null)
            opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }

    private static PasswordResetCodeService NewService(LicenseDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new PasswordResetCodeService(db, Hasher, config,
            NullLogger<PasswordResetCodeService>.Instance);
    }

    private static async Task<Guid> SeedShopperAsync(LicenseDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "S",
            Phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999),
            PasswordHash = Hasher.Hash("Password1!"),
            Address = "A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();
        return shopper.Id;
    }

    private sealed class IssueRendezvous
    {
        private readonly TaskCompletionSource<bool> _both =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) >= 2)
                _both.TrySetResult(true);
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>
    /// Yeni ShopperPasswordResetCode ekleyen SaveChanges'ı bariyerde tutar:
    /// iki üretim de kontrol sorgularını satırlar (ve kilitler) yokken geçmiş
    /// olur, transaction'lar yazma anında yarışır. Kaybedenin UPDATE'i
    /// kazananın commit'ini bekler, 0 satır etkiler → jeton fırlatır.
    /// </summary>
    private sealed class ResetCodeSaveBarrier : SaveChangesInterceptor
    {
        private readonly IssueRendezvous _rendezvous;
        public ResetCodeSaveBarrier(IssueRendezvous rendezvous)
            => _rendezvous = rendezvous;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null
                && eventData.Context.ChangeTracker
                    .Entries<ShopperPasswordResetCode>()
                    .Any(e => e.State == EntityState.Added))
            {
                await _rendezvous.ArriveAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
