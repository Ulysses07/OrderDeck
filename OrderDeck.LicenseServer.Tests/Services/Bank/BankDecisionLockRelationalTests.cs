using System.Data.Common;
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
/// Karar katmanı kilidi (tasarım §2.4; S2b Görev 6) GERÇEK SQL Server'da. S2b'de prod'da <c>Decision ≠ None</c> satır yoktur
/// (okuyucu önce): satırlar tohumlanır. (1) Kilidin üç sorgu koşulu SQL'e çevrilir ve Awaiting satırı eler: dekont adaylarının
/// <c>taken</c> kümesi (K3), aynı IBAN'ın yeniden değerlendirme seçimi (K4a) ve taramanın 2. aşaması (K5). Sütun dize saklanır;
/// seçilen satırı eşleştirici yine dokunmadan döndüreceği için (K1) seçim, hareketin hiç okunmamasıyla gösterilir. (2) Eşleştiricinin
/// okuduğu satır kaydından önce karar katmanına girerse jeton (UPDATE'in WHERE'indeki <c>UpdatedAt</c>) kaydı reddeder, taze satırda
/// K1 dokunmaz. (3) Bağdaştırıcının seçtiği aday, satırı okunmadan hemen önce karar katmanına girerse bayat seçim (K2) bir kez yeniden
/// denenir ve taze seçimi <c>taken</c> (K3) eler; komut kancası yalnız ilişkiselde var. Araya giren yazı ham SQL'dir
/// (<c>N'Awaiting'</c>): okuyucu sütunun dize biçimini tanımalı. Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankDecisionLockRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public BankDecisionLockRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Decision_karsilastirmasi_SQLe_cevrilir()
    {
        Seeded s;
        await using (var seed = NewDb(interceptor: null)) s = await SeedAsync(seed, connected: true);
        var now = DateTimeOffset.UtcNow;
        var awaiting = Incoming(s.LicenseId, 450m, now.AddDays(-1), "EFT GELEN", s.Hash);
        var target = Incoming(s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", s.Hash);
        var payment = Receipt(s, 450m, awaiting.OccurredAt.AddMinutes(10));
        await using (var db = NewDb(interceptor: null))
        {
            db.BankTransactions.AddRange(awaiting, target);
            // Bağdan önce hesaplanmış: elle eşlemenin öğreteceği bağ sonra kurulur, taramanın (a) koşulu tutar.
            db.PaymentMatches.Add(AwaitingRow(s, awaiting.Id, now.AddDays(-2)));
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }
        var before = await RowAsync(awaiting.Id);
        var reads = new TransactionReads(awaiting.Id);

        // K3: dekontun tek tutar ve zaman adayı Awaiting satırın hareketi.
        await using (var db = NewDb(reads)) await Recon(db).ReconcileApprovalAsync(payment, CancellationToken.None);
        reads.Count.Should().Be(0, "K3: dekontun aday sorgusu karar katmanındaki hareketi döndürmez");
        await using (var verify = NewDb(interceptor: null))
        {
            var gap = (await verify.PaymentMatchGaps.AsNoTracking().ToListAsync()).Should().ContainSingle("K3: dekont gap'e düşer").Subject;
            (gap.PaymentId, gap.Reason, gap.ResolvedAt).Should().Be((payment.Id, PaymentMatchGapReason.NoCandidate, (DateTimeOffset?)null));
        }

        // K4a: elle eşleme aynı özetin bağını öğretir.
        await using (var db = NewDb(reads))
            (await Recon(db).ManualMatchAsync(s.LicenseId, target.Id, s.CustomerId, CancellationToken.None))
                .Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Learned));
        reads.Count.Should().Be(0, "K4a: aynı IBAN'ın yeniden değerlendirme seçimi karar katmanındaki hareketi döndürmez");

        // K5: bağ artık satırın hesabından yeni.
        await using (var db = NewDb(reads))
            (await new BankMatchSweepJob(db, Recon(db), NullLogger<BankMatchSweepJob>.Instance).RunAsync(CancellationToken.None))
                .Should().Be(0, "1. aşama: satırı olmayan hareket yok");
        reads.Count.Should().Be(0, "K5: taramanın 2. aşaması karar katmanındaki hareketi seçmez");

        (await RowAsync(awaiting.Id)).Should().BeEquivalentTo(before, o => o.Excluding(m => m.BankTransaction));
    }

    [Fact]
    public async Task Yeniden_hesap_okuduktan_sonra_karar_katmanina_giren_satira_dokunmaz()
    {
        Seeded s;
        await using (var seed = NewDb(interceptor: null)) s = await SeedAsync(seed, connected: false);
        var computedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var tx = Incoming(s.LicenseId, 450m, DateTimeOffset.UtcNow.AddHours(-3), "HAVALE ornek_musteri34", hash: null);
        await using (var db = NewDb(interceptor: null))
        {
            db.BankTransactions.Add(tx);
            // Satır düzeyi eşleştiricinin eski hesabı (sürüm boş): yeniden hesap kullanıcı adının müşterisini önerir, sürüm 2 yazardı.
            db.PaymentMatches.Add(new PaymentMatch
            {
                Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = tx.Id, Status = PaymentMatchStatus.NoProposal,
                Layer = PaymentMatchLayer.None, Evidence = "no-signal", CreatedAt = computedAt, UpdatedAt = computedAt,
            });
            await db.SaveChangesAsync();
        }
        var enteredAt = DateTimeOffset.UtcNow;
        var decision = new ConcurrentDecision(tx.Id, () => EnterDecisionLayerAsync(tx.Id, enteredAt));
        await using var matcherDb = NewDb(decision);

        var row = await Matcher(matcherDb).MatchAsync(tx, CancellationToken.None);

        decision.Fired.Should().BeTrue("ön koşul: karar katmanına giriş eşleştiricinin okuması ile kaydı arasına girdi");
        await using var verify = NewDb(interceptor: null);
        var stored = await verify.PaymentMatches.AsNoTracking().SingleAsync();
        foreach (var m in new[] { row, stored })
            (m.Decision, m.Status, m.ProposedWpfCustomerId, m.Evidence, m.UpdatedAt, m.MatcherVersion).Should().Be(
                (PaymentMatchDecision.Awaiting, PaymentMatchStatus.NoProposal, (Guid?)null, (string?)"no-signal", enteredAt, (int?)null),
                "jeton kaydı reddetti; taze satır karar katmanında, K1 dokunmaz");
        matcherDb.ChangeTracker.Entries<PaymentMatch>().Should().NotContain(e => e.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task Aday_secildikten_sonra_karar_katmanina_giren_hareket_baglanmaz()
    {
        Seeded s;
        await using (var seed = NewDb(interceptor: null)) s = await SeedAsync(seed, connected: false);
        var computedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var tx = Incoming(s.LicenseId, 450m, DateTimeOffset.UtcNow.AddHours(-3), "HAVALE ornek_musteri34", hash: null);
        var payment = Receipt(s, 450m, tx.OccurredAt.AddMinutes(10));
        await using (var db = NewDb(interceptor: null))
        {
            db.BankTransactions.Add(tx);
            db.PaymentMatches.Add(new PaymentMatch
            {
                Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = tx.Id, Status = PaymentMatchStatus.Proposed,
                ProposedWpfCustomerId = s.CustomerId, Layer = PaymentMatchLayer.UsernameInDescription,
                Confidence = PaymentMatcher.UsernameConfidence, Evidence = "username=ornekmusteri34",
                MatcherVersion = PaymentMatcher.PersonLevelVersion, CreatedAt = computedAt, UpdatedAt = computedAt,
            });
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }
        var enteredAt = DateTimeOffset.UtcNow;
        var hook = new BeforeTransactionRowRead(tx.Id, () => EnterDecisionLayerAsync(tx.Id, enteredAt));
        var log = new LogRecorder<PaymentMatchReconciler>();
        await using var jobDb = NewDb(hook);

        await Recon(jobDb, log).ReconcileApprovalAsync(payment, CancellationToken.None);

        hook.Fired.Should().BeTrue("ön koşul: karar katmanına giriş aday seçiminden sonra, satırın okunmasından önce");
        log.Entries.Where(e => e.Message.Contains("yeniden deneniyor")).Should().ContainSingle()
            .Which.Message.Should().Contain("StaleCandidateException", "K2: bayat seçim bir kez yeniden denendi");
        await using var verify = NewDb(interceptor: null);
        var row = await verify.PaymentMatches.AsNoTracking().SingleAsync();
        (row.PaymentId, row.ActualWpfCustomerId, row.DecidedAt).Should().Be(((Guid?)null, (Guid?)null, (DateTimeOffset?)null),
            "karar katmanındaki havaleye dekont bağlanmaz");
        (row.Decision, row.Status, row.ProposedWpfCustomerId, row.Evidence, row.UpdatedAt, row.MatcherVersion).Should().Be(
            (PaymentMatchDecision.Awaiting, PaymentMatchStatus.Proposed, (Guid?)s.CustomerId, (string?)"username=ornekmusteri34",
                enteredAt, (int?)PaymentMatcher.PersonLevelVersion), "satır araya giren yazının bıraktığı gibi");
        var gap = (await verify.PaymentMatchGaps.AsNoTracking().ToListAsync())
            .Should().ContainSingle("K3: yeniden denemenin taze seçimi hareketi aday saymaz, dekont gap'e düşer").Subject;
        (gap.PaymentId, gap.Reason, gap.ResolvedAt).Should().Be((payment.Id, PaymentMatchGapReason.NoCandidate, (DateTimeOffset?)null));
        jobDb.ChangeTracker.Entries().Where(e => e.Entity is PaymentMatch or PaymentMatchGap or CustomerIbanMemory)
            .Should().NotContain(e => e.State != EntityState.Unchanged, "düşen deneme izde kalmaz");
    }

    private sealed record Seeded(Guid LicenseId, Guid ShopperId, Guid CustomerId, Guid OtherCustomerId, string Hash);

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }

    private static PaymentMatcher Matcher(LicenseDbContext db)
        => new(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance);

    private static PaymentMatchReconciler Recon(LicenseDbContext db, ILogger<PaymentMatchReconciler>? log = null)
        => new(db, Matcher(db), Options.Create(new BankOptions()), log ?? NullLogger<PaymentMatchReconciler>.Instance);

    private async Task<PaymentMatch> RowAsync(Guid transactionId)
    {
        await using var db = NewDb(interceptor: null);
        return await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == transactionId);
    }

    /// <summary>Karar katmanına giriş, ayrı bağlamdan ham SQL'le: dize değer, jeton ilerler.</summary>
    private async Task EnterDecisionLayerAsync(Guid transactionId, DateTimeOffset at)
    {
        await using var other = NewDb(interceptor: null);
        (await other.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [PaymentMatches] SET [Decision] = N'Awaiting', [UpdatedAt] = {at} WHERE [BankTransactionId] = {transactionId}"))
            .Should().Be(1);
    }

    /// <summary>Lisans (<paramref name="connected"/> ise eşleştirmeye açık Obifin bağlantısıyla; korunan alanlar boş), shopper'ı
    /// bağlı müşteri, öneri için ikinci müşteri ve bir IBAN özeti. Kimlik alanları, telefon ve özet üretilir.</summary>
    private static async Task<Seeded> SeedAsync(LicenseDbContext db, bool connected)
    {
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"dl-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"dl-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(), FullName = "Ornek Musteri", Phone = TestPhone.NewE164(), PasswordHash = $"h-{Guid.NewGuid():N}",
            Address = "-", CreatedAt = now, UpdatedAt = now,
        };
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube",
            Username = "ornek_musteri34", FullName = "Ornek Musteri", UpdatedAt = now };
        var other = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube",
            Username = "mehmet_k", UpdatedAt = now };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        db.Shoppers.Add(shopper);
        if (connected)
            db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = license.Id, BaseUrl = "https://example.invalid",
                UserCode = $"u-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, CreatedAt = now, UpdatedAt = now });
        db.WpfCustomerProjections.AddRange(wpf, other);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink { Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = license.Id,
            Platform = "youtube", Username = "ornek_musteri34", WpfCustomerId = wpf.Id, JoinedAt = now });
        await db.SaveChangesAsync();
        return new Seeded(license.Id, shopper.Id, wpf.Id, other.Id, $"{Guid.NewGuid():N}{Guid.NewGuid():N}");
    }

    private static BankTransaction Incoming(Guid licenseId, decimal amount, DateTimeOffset at, string description, string? hash) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = amount, Currency = "TL", OccurredAt = at,
        Description = description, TransactionCode = "FT", FetchedAt = at,
        // KVKK Ö-7 kesmesinden sonraki çekim: özet geçerli IBAN'dan.
        CounterpartyIbanHash = hash, CounterpartyIbanHashValid = hash is not null,
    };

    /// <summary>Shopper'ın onaylı dekontu (gönderen adı açıklamalarda geçmez).</summary>
    private static Payment Receipt(Seeded s, decimal amount, DateTimeOffset paidAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = s.LicenseId, ShopperId = s.ShopperId, PayerName = "ORNEK MUSTERI", Amount = amount,
        PaidAt = paidAt, ReferansNo = $"r-{Guid.NewGuid():N}", Status = PaymentStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Karar katmanındaki satır: Awaiting, insan kararı yok (Proposed, karar anı ve dekont boş), öneri ikinci müşteri.</summary>
    private static PaymentMatch AwaitingRow(Seeded s, Guid transactionId, DateTimeOffset computedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = transactionId, Decision = PaymentMatchDecision.Awaiting,
        Status = PaymentMatchStatus.Proposed, ProposedWpfCustomerId = s.OtherCustomerId, Layer = PaymentMatchLayer.NameAmount,
        Confidence = PaymentMatcher.NameConfidence, Evidence = "name:2", MatcherVersion = PaymentMatcher.PersonLevelVersion,
        CreatedAt = computedAt, UpdatedAt = computedAt,
    };

    /// <summary>Verilen hareketin varlık olarak kaç kez okunduğunu sayar (izlenmeden okuma dahil): bir seçim sorgusu onu
    /// döndürdüyse okunmuştur.</summary>
    private sealed class TransactionReads(Guid transactionId) : IMaterializationInterceptor
    {
        public int Count { get; private set; }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is BankTransaction t && t.Id == transactionId) Count++;
            return entity;
        }
    }

    /// <summary>Verilen hareketin satırını değiştiren İLK kayıt gönderilmeden hemen önce, BİR kez, araya giren yazıyı koşturur;
    /// kayıt sonra eski jetonla gider (<c>IbanReevaluationRelationalTests.ConcurrentDecision</c> deseni).</summary>
    private sealed class ConcurrentDecision(Guid transactionId, Func<Task> interleave) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<PaymentMatch>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.BankTransactionId == transactionId))
            {
                Fired = true;
                await interleave();
            }
            return result;
        }
    }

    /// <summary>Hareketin Id'sini parametre taşıyan İLK <c>PaymentMatches</c> okumasından (bağdaştırıcının satır okuması) hemen
    /// önce, BİR kez, araya giren yazıyı koşturur. Aday seçiminin <c>taken</c> alt sorgusu ve dekontun "zaten bağlı mı" okuması
    /// hareketin Id'sini taşımaz: yazı seçimden sonra, satırın okunmasından önce düşer.</summary>
    private sealed class BeforeTransactionRowRead(Guid transactionId, Func<Task> interleave) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("FROM [PaymentMatches]", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid id && id == transactionId))
            {
                Fired = true;
                await interleave();
            }
            return result;
        }
    }
}
