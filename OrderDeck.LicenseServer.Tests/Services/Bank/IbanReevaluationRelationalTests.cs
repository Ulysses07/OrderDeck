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
/// IBAN hafızası değişince aynı IBAN'ın kararsız satırlarının yeniden değerlendirilmesi — GERÇEK SQL Server'da. (1) Elle eşleme
/// IBAN öğrenince eşleştirici eski satırı okuduktan sonra, kaydetmeden önce başka bir admin aynı satıra elle karar verirse karar
/// ezilmez: <see cref="PaymentMatch.UpdatedAt"/> jetonu UPDATE'in WHERE'ine girer, InMemory bu yarışı sınayamaz. Eşleştirici taze
/// satırla bir kez yeniden hesaplar, insan kararını görür ve dokunmadan döner. (2) Telafi taramasının 2. aşamasının seçim sorgusu
/// (iç içe ilişkili alt sorgular) SQL'e çevrilir ve geriye dönük doldurmayı yapar. Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class IbanReevaluationRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public IbanReevaluationRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Yeniden_degerlendirme_sirasinda_ayni_satira_verilen_elle_karar_korunur()
    {
        Seeded s;
        await using (var seed = NewDb(interceptor: null)) s = await SeedAsync(seed, connected: false);
        var older = s.Older;
        var row = await MatchWithoutMemoryAsync(older);
        row.Status.Should().Be(PaymentMatchStatus.NoProposal, "ön koşul");
        var target = await AddIncomingAsync(s.LicenseId, s.Hash, DateTimeOffset.UtcNow.AddHours(-2));
        var decision = new ConcurrentDecision(older.Id, () => DecideAsync(s.LicenseId, older.Id, s.OtherCustomerId));
        await using var db = NewDb(decision);

        var result = await Recon(db).ManualMatchAsync(s.LicenseId, target.Id, s.CustomerId, CancellationToken.None);

        decision.Fired.Should().BeTrue("ön koşul: karar, yeniden değerlendirmenin okuması ile kaydı arasına girdi");
        result.Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Learned), "insan kararlı satır sayılmaz, hata da yok");
        await using var verify = NewDb(interceptor: null);
        var decided = await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == older.Id);
        (decided.ActualWpfCustomerId, decided.Status).Should().Be(((Guid?)s.OtherCustomerId, PaymentMatchStatus.ManualOnly));
        decided.DecidedAt.Should().NotBeNull();
        (await verify.CustomerIbanMemories.AsNoTracking().SingleAsync()).WpfCustomerId.Should().Be(s.CustomerId,
            "çelişen karar bağı ezmez");
        db.ChangeTracker.Entries<PaymentMatch>().Should().NotContain(e => e.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task Tarama_ikinci_asamasi_SQL_Serverda_cevrilir_bagdan_once_hesaplanani_ve_silinmis_bagdan_geleni_duzeltir()
    {
        Seeded s;
        await using (var seed = NewDb(interceptor: null)) s = await SeedAsync(seed, connected: true);
        var now = DateTimeOffset.UtcNow;
        var bondAt = now.AddHours(-3);
        var orphanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var source = await AddIncomingAsync(s.LicenseId, s.Hash, now.AddDays(-1));
        var orphan = await AddIncomingAsync(s.LicenseId, orphanHash, now.AddDays(-3));
        await using (var db = NewDb(interceptor: null))
        {
            db.PaymentMatches.AddRange(
                new PaymentMatch { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = source.Id, Status = PaymentMatchStatus.ManualOnly,
                    ActualWpfCustomerId = s.CustomerId, DecidedAt = bondAt, IbanConfirmedAt = bondAt, Evidence = "no-signal",
                    CreatedAt = bondAt, UpdatedAt = bondAt },
                // Bağdan önce hesaplanmış, öneri yok; özeti kesmeden önce çekilmiş (HashValid=false) eski havale.
                new PaymentMatch { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = s.Older.Id, Status = PaymentMatchStatus.NoProposal,
                    Evidence = "no-signal", CreatedAt = now.AddDays(-4), UpdatedAt = now.AddDays(-4) },
                // Kaldırmanın anlık geri alması koşamadı: bağı artık olmayan IBAN önerisi.
                new PaymentMatch { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = orphan.Id, Status = PaymentMatchStatus.Proposed,
                    ProposedWpfCustomerId = s.CustomerId, Layer = PaymentMatchLayer.IbanMemory, Confidence = PaymentMatcher.IbanConfidence,
                    Evidence = "iban-memory", CreatedAt = now.AddDays(-1), UpdatedAt = now.AddDays(-1) });
            db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = s.CustomerId,
                IbanHash = s.Hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = source.Id,
                CreatedAt = bondAt, LastConfirmedAt = bondAt });
            var older = await db.BankTransactions.SingleAsync(t => t.Id == s.Older.Id);
            older.CounterpartyIbanHashValid = false;
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb(interceptor: null))
            (await new BankMatchSweepJob(db, Recon(db), NullLogger<BankMatchSweepJob>.Instance).RunAsync(CancellationToken.None))
                .Should().Be(0, "1. aşama: satırı olmayan hareket yok");

        await using var verify = NewDb(interceptor: null);
        var stale = await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == s.Older.Id);
        (stale.Status, stale.Layer, stale.ProposedWpfCustomerId, stale.Evidence).Should().Be((PaymentMatchStatus.Proposed,
            PaymentMatchLayer.IbanMemory, (Guid?)s.CustomerId, "iban-memory" + PaymentMatcher.ReevaluationEvidenceSuffix));
        var fixedOrphan = await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == orphan.Id);
        (fixedOrphan.Status, fixedOrphan.ProposedWpfCustomerId).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null));
        (await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == source.Id)).UpdatedAt
            .Should().Be(bondAt, "insan kararına dokunulmaz");
    }

    private sealed record Seeded(Guid LicenseId, Guid CustomerId, Guid OtherCustomerId, string Hash, BankTransaction Older);

    /// <summary>Araya giren admin kararı: ayrı bir bağlamda, havaleyi verilen müşteriye elle eşler.</summary>
    private async Task DecideAsync(Guid licenseId, Guid transactionId, Guid customerId)
    {
        await using var other = NewDb(interceptor: null);
        await Recon(other).ManualMatchAsync(licenseId, transactionId, customerId, CancellationToken.None);
    }

    private async Task<PaymentMatch> MatchWithoutMemoryAsync(BankTransaction tx)
    {
        await using var db = NewDb(interceptor: null);
        return await new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance)
            .MatchAsync(tx, CancellationToken.None);
    }

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }

    private static PaymentMatchReconciler Recon(LicenseDbContext db)
        => new(db, new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance),
            Options.Create(new BankOptions()), NullLogger<PaymentMatchReconciler>.Instance);

    /// <summary>Lisans (<paramref name="connected"/> ise eşleştirmeye açık Obifin bağlantısıyla; korunan alanlar boş), iki müşteri
    /// ve IBAN özetli eski bir gelen havale (satırı yok). Kimlik alanları ve özet üretilir.</summary>
    private static async Task<Seeded> SeedAsync(LicenseDbContext db, bool connected)
    {
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"rv-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"rv-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube",
            Username = "ornek_musteri34", UpdatedAt = now };
        var otherWpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube",
            Username = "mehmet_k", UpdatedAt = now };
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var older = Incoming(license.Id, hash, now.AddDays(-4));
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        if (connected)
            db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = license.Id, BaseUrl = "https://example.invalid",
                UserCode = $"u-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, CreatedAt = now, UpdatedAt = now });
        db.WpfCustomerProjections.AddRange(wpf, otherWpf);
        db.BankTransactions.Add(older);
        await db.SaveChangesAsync();
        return new Seeded(license.Id, wpf.Id, otherWpf.Id, hash, older);
    }

    private async Task<BankTransaction> AddIncomingAsync(Guid licenseId, string hash, DateTimeOffset at)
    {
        await using var db = NewDb(interceptor: null);
        var tx = Incoming(licenseId, hash, at);
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return tx;
    }

    private static BankTransaction Incoming(Guid licenseId, string hash, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 450m, Currency = "TL", OccurredAt = at,
        Description = "EFT GELEN", TransactionCode = "FT", FetchedAt = at,
        // KVKK Ö-7 kesmesinden sonraki çekim: özet geçerli IBAN'dan.
        CounterpartyIbanHash = hash, CounterpartyIbanHashValid = true,
    };

    /// <summary>Verilen hareketin kararsız satırının yeniden hesap kaydı (satır kayıttan önce de sonra da kararsız) gönderilmeden
    /// hemen önce, BİR kez, araya giren kararı koşturur ve bitmesini bekler. Kayıt sonra eski jetonla gider.</summary>
    private sealed class ConcurrentDecision(Guid transactionId, Func<Task> decide) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified
                    && e.Entity.BankTransactionId == transactionId && e.Entity.DecidedAt is null
                    && e.OriginalValues.GetValue<DateTimeOffset?>(nameof(PaymentMatch.DecidedAt)) is null))
            {
                Fired = true;
                await decide();
            }
            return result;
        }
    }
}
