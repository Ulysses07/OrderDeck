using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Shoppers;

/// <summary>
/// KVKK silmesinin eşleşme kanıtı boşaltması ile gölge eşleştirmenin eşzamanlı yazısı — GERÇEK SQL Server'da.
/// <see cref="PaymentMatch.UpdatedAt"/> eşzamanlılık jetonudur: silme satırı okuduktan sonra gölge eşleştirme (çekimin
/// eşleştiricisi, telafi taraması, dekont bağdaştırması, admin kararı) ona yazarsa boşaltmanın UPDATE'i 0 satır etkiler ve
/// bütün silme geri alınır. Silme eşleşmeleri tazeleyip boşaltmayı bir kez yeniden uygular. InMemory'de SaveChanges işlem
/// değildir (çakışmadan önceki yazılar kalır, yeniden deneme onlara takılır); geri almanın kendisi burada gerçek.
/// Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class ShopperPurgeServiceConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public ShopperPurgeServiceConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Eşleşme satırını değiştiren ilk <paramref name="times"/> SaveChanges'ten hemen önce öteki bağlamın yazısını
    /// koşturur: silmenin okuduğu satır kaydetmeden önce değişmiş olur.</summary>
    private sealed class BeforeMatchUpdate(Func<Task> interleave, int times = 1) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired < times && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified))
            {
                Fired++;
                await interleave();
            }
            return result;
        }
    }

    private sealed record Seeded(Guid ShopperId, Guid LicenseId, Guid WpfCustomerId, Guid MatchId, string Marker);

    [Fact]
    public async Task Eszamanli_eslestirme_yazisiyla_carpisan_silme_tazeleyip_kaniti_yine_bosaltir()
    {
        // Silme satırı okuduktan sonra dekont onayı aynı satırı müşteriye bağlar (jeton ilerler). Silme 500 ile geri
        // alınmaz: satır tazelenir, bağdaştırmanın yazdığı karar korunur, kanıt yine boşaltılır, kişisel veri silinir.
        var s = await SeedAsync();
        var paymentId = Guid.NewGuid();
        var link = new BeforeMatchUpdate(() => LinkConcurrentlyAsync(s, paymentId));
        await using var db = NewDb(link);

        var result = await Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        link.Fired.Should().Be(1, "yeniden deneme eşzamanlı yazıyla bir kez daha karşılaşmadı");
        await using var verify = NewDb(interceptor: null);
        var row = await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId);
        row.Evidence.Should().BeNull("tazelenen satırın kanıtı yine boşaltılır");
        row.PaymentId.Should().Be(paymentId, "eşzamanlı yazanın kararı ezilmez");
        row.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().Be("[Silindi]");
        (await verify.ShopperBroadcasterLinks.AsNoTracking().CountAsync(l => l.ShopperId == s.ShopperId)).Should().Be(0);
        (await verify.WpfCustomerProjections.AsNoTracking().SingleAsync(c => c.Id == s.WpfCustomerId)).PurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Yeniden_denemede_ikinci_kez_carpisirsa_istisna_yukari_gider_hicbir_sey_yazilmaz()
    {
        var s = await SeedAsync();
        var link = new BeforeMatchUpdate(() => LinkConcurrentlyAsync(s, Guid.NewGuid()), times: 2);
        await using var db = NewDb(link);

        var act = () => Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        link.Fired.Should().Be(2, "yalnız bir kez yeniden denenir");
        await using var verify = NewDb(interceptor: null);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().NotBe("[Silindi]",
            "silme tek işlemdir, geri alındı");
        (await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId)).Evidence.Should().Be(s.Marker);
    }

    /// <summary>Dekont onayının bağdaştırması gibi: satırı ayrı bir bağlamda müşteriye bağlar, jetonu ilerletir.</summary>
    private async Task LinkConcurrentlyAsync(Seeded s, Guid paymentId)
    {
        await using var other = NewDb(interceptor: null);
        var m = await other.PaymentMatches.SingleAsync(x => x.Id == s.MatchId);
        m.PaymentId = paymentId;
        m.ActualWpfCustomerId = s.WpfCustomerId;
        m.DecidedAt = DateTimeOffset.UtcNow;
        m.Status = PaymentMatchStatus.ConfirmedByHuman;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await other.SaveChangesAsync();
    }

    /// <summary>Shopper, lisans, sahipliği kanıtlanmış WPF müşterisi ve ona kullanıcı adı katmanıyla önerilmiş bir hareket.
    /// Kimlik alanları üretilir.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var db = NewDb(interceptor: null);
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"p-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"purge-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(), FullName = "Ornek Musteri", Phone = $"+9055{Random.Shared.Next(10000000, 99999999)}",
            PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-", CreatedAt = now, UpdatedAt = now,
        };
        var wpf = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "instagram", Username = "ornek_m", UpdatedAt = now,
        };
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 300m, Currency = "TL",
            OccurredAt = now.AddHours(-1), Description = "HAVALE ornek_m", TransactionCode = "FT", FetchedAt = now,
        };
        var marker = "username=" + BankTextNormalizer.UsernameKey("ornek_m");
        var match = new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, BankTransactionId = tx.Id, ProposedWpfCustomerId = wpf.Id,
            Layer = PaymentMatchLayer.UsernameInDescription, Confidence = PaymentMatcher.UsernameConfidence, Evidence = marker,
            Status = PaymentMatchStatus.Proposed, CreatedAt = now, UpdatedAt = now,
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        db.Shoppers.Add(shopper);
        db.WpfCustomerProjections.Add(wpf);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = license.Id, Platform = "instagram", Username = "ornek_m",
            WpfCustomerId = wpf.Id, JoinedAt = now,
        });
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(match);
        await db.SaveChangesAsync();
        return new Seeded(shopper.Id, license.Id, wpf.Id, match.Id, marker);
    }

    private static ShopperPurgeService Service(LicenseDbContext db)
        => new(db, new StubShopperPaymentStorage(), NullLogger<ShopperPurgeService>.Instance);

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }
}
