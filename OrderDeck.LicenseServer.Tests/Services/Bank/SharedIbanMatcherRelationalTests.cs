using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
/// Kişi düzeyi IBAN okuyucusu (S2b Görev 5) GERÇEK SQL Server'da. (1) S3b şemasında (dar tekil düşmüş, <see cref="SharedIbanSchema"/>)
/// iki kişiye bağlı özet her koşuda aynı sonucu verir: öneri yok, adaylar son doğrulamaya göre. Sıralama bellekte yapılır, SQL'in doğal
/// okuma sırasına bırakılmaz: test en yeni doğrulamayı doğal okumada sonda gelen bağa yazar. (2) S2b şemasında dar tekil durur: aynı
/// özete ikinci müşteri yazılamaz, prod'da küme en çok bir kişidir. Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SharedIbanMatcherRelationalTests : IAsyncLifetime
{
    private const string NarrowIbanIndex = "IX_CustomerIbanMemories_LicenseId_IbanHash";
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public SharedIbanMatcherRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Iki_kisiye_bagli_ozet_her_kosuda_ayni_sirayla_okunur()
    {
        await SharedIbanSchema.ApplyAsync(_connStr);
        Seeded s;
        await using (var seed = NewDb()) s = await SeedAsync(seed);
        Guid[] expected;
        await using (var db = NewDb())
        {
            db.CustomerIbanMemories.Add(Bond(s.LicenseId, s.Second, s.Hash));
            await db.SaveChangesAsync();
            // Eşleştiricinin okumasıyla aynı biçimli, sırasız okuma. En yeni doğrulama doğal sırada sonda gelen bağa yazılır:
            // sıralamayan bir okuyucu adayları ters sırada döndürürdü.
            var natural = await db.CustomerIbanMemories.AsNoTracking()
                .Where(b => b.LicenseId == s.LicenseId && b.IbanHash == s.Hash)
                .Select(b => new { b.Id, b.WpfCustomerId, Confirmed = b.LastConfirmedAt ?? b.CreatedAt })
                .ToListAsync();
            natural.Should().HaveCount(2, "ön koşul: dar tekil düşmüş şemada iki kişiye bağlı özet");
            var now = DateTimeOffset.UtcNow;
            await db.CustomerIbanMemories.Where(b => b.Id == natural[0].Id)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.LastConfirmedAt, now.AddDays(-10)));
            await db.CustomerIbanMemories.Where(b => b.Id == natural[1].Id)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.LastConfirmedAt, now.AddDays(-2)));
            expected = [natural[1].WpfCustomerId, natural[0].WpfCustomerId];
        }

        for (var run = 1; run <= 2; run++)
        {
            await using var db = NewDb();
            var matcher = new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance);
            var tx = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == s.TxId);

            var m = await matcher.MatchAsync(tx, CancellationToken.None);

            (m.Status, m.ProposedWpfCustomerId, m.Evidence, m.MatcherVersion).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null,
                "iban-memory-shared:2", (int?)PaymentMatcher.PersonLevelVersion), $"koşu {run}: hangi kişi olduğu tahmin edilmez");
            (await matcher.CandidatePersonsAsync(tx, CancellationToken.None)).Should().Equal(expected, $"koşu {run}");
        }
    }

    [Fact]
    public async Task Dar_tekil_dururken_ikinci_bag_2601()
    {
        // (koruma) S2b şeması: dar tekil (LicenseId, IbanHash) S3b'ye kadar durur; aynı özete ikinci müşterinin bağı yazılamaz.
        Seeded s;
        await using (var seed = NewDb()) s = await SeedAsync(seed);

        await using var rival = NewDb();
        rival.CustomerIbanMemories.Add(Bond(s.LicenseId, s.Second, s.Hash));
        var thrown = await Record.ExceptionAsync(() => rival.SaveChangesAsync());

        var error = (thrown as DbUpdateException)?.InnerException as SqlException;
        error.Should().NotBeNull("veritabanı reddetmeli, dönen: {0}", thrown);
        error!.Number.Should().Be(2601);
        error.Message.Should().Contain($"'{NarrowIbanIndex}'", "reddeden geniş tekil değil dar tekildir");
    }

    private sealed record Seeded(Guid LicenseId, Guid First, Guid Second, string Hash, Guid TxId);

    private LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr).Options);

    /// <summary>Lisans, iki müşteri, birincinin bağı ve aynı özetli, açıklamasında kullanıcı adı olmayan gelen havale (satırı yok).
    /// Kimlik alanları ve özet üretilir.</summary>
    private static async Task<Seeded> SeedAsync(LicenseDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"si-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"si-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var first = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube",
            Username = "ornek_musteri34", UpdatedAt = now };
        var second = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube",
            Username = "mehmet_k", UpdatedAt = now };
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 450m, Currency = "TL", OccurredAt = now.AddHours(-1),
            Description = "EFT GELEN", TransactionCode = "FT", FetchedAt = now.AddHours(-1),
            CounterpartyIbanHash = hash, CounterpartyIbanHashValid = true,
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        db.WpfCustomerProjections.AddRange(first, second);
        db.CustomerIbanMemories.Add(Bond(license.Id, first.Id, hash));
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return new Seeded(license.Id, first.Id, second.Id, hash, tx.Id);
    }

    private static CustomerIbanMemory Bond(Guid licenseId, Guid customerId, string hash) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = customerId, IbanHash = hash, IbanMasked = "TR..",
        LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow, LastConfirmedAt = DateTimeOffset.UtcNow,
    };
}
