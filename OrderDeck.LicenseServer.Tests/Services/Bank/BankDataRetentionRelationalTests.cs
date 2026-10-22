using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>BankDataRetentionJob gerçek SQL Server'da (göçlerle kurulmuş şema, FK'ler, CHECK kısıtları, eşzamanlılık jetonu):
/// saklama tablosu uygulanır, her imha partisi kaydını aynı işlemde yazar. Bu iş her gece prod verisi siler; alt sorgular
/// ("eşleşmeyen" = bağlı eşleşmesi olmayan; gerekçe metni için hareket üzerinde EXISTS), <c>COALESCE(LastConfirmedAt,
/// CreatedAt)</c>, çekim alt sınırı ve jetonlu eşleşme silmesi InMemory'de SQL'e çevrilmez.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankDataRetentionRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public BankDataRetentionRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Saklama_tablosu_gercek_SQL_Serverda_uygulanir_ve_imha_kaydi_yazar()
    {
        var now = DateTimeOffset.UtcNow;
        var lic = Guid.NewGuid();
        long obifinId = 1;
        BankTransaction Tx(int ageDays, int? fetchedAgeDays = null) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = obifinId++, ObifinAccountId = 6, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now.AddDays(-ageDays),
            FetchedAt = now.AddDays(-(fetchedAgeDays ?? ageDays)), Description = "aciklama", CounterpartyName = "ad", RawJson = "{}",
        };
        PaymentMatch Match(BankTransaction tx, Guid? actual) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, ActualWpfCustomerId = actual, Evidence = "kanıt",
            DecidedAt = actual is null ? null : now, Layer = PaymentMatchLayer.NameAmount,
            Status = actual is null ? PaymentMatchStatus.Proposed : PaymentMatchStatus.ManualOnly,
            CreatedAt = tx.FetchedAt, UpdatedAt = tx.FetchedAt,
        };
        CustomerIbanMemory Memory(DateTimeOffset createdAt, DateTimeOffset? lastConfirmedAt) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(), IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = createdAt,
            LastConfirmedAt = lastConfirmedAt,
        };
        var expired = Tx(181); var unmatched = Tx(31); var linked = Tx(31); var young = Tx(10);
        var lateFetched = Tx(200, fetchedAgeDays: 1);
        // İşlem tarihi 40, çekim 10 gün önce: alt sınır geçti, çekimden 30 gün dolmadı. Süre çekim anına dönerse SQL'de de düşer.
        var unmatchedFetchedLater = Tx(40, fetchedAgeDays: 10); var linkedFetchedLater = Tx(40, fetchedAgeDays: 10);
        var expiredMatch = Match(expired, Guid.NewGuid()); var unmatchedMatch = Match(unmatched, null);
        unmatchedMatch.CreatedAt = now; // geç oluşturulmuş satır: gerekçe metni hareketin tarihine bağlı
        var linkedMatch = Match(linked, Guid.NewGuid());
        var unmatchedFetchedLaterMatch = Match(unmatchedFetchedLater, null);
        var linkedFetchedLaterMatch = Match(linkedFetchedLater, Guid.NewGuid());
        var staleConfirmed = Memory(now.AddMonths(-20), now.AddMonths(-12).AddDays(-1));
        var staleNeverConfirmed = Memory(now.AddMonths(-12).AddDays(-1), null);
        var fresh = Memory(now.AddMonths(-20), now.AddMonths(-1));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = new Customer { Id = Guid.NewGuid(), Email = $"br-{Guid.NewGuid():N}@example.test", Name = "Saklama",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
            db.Customers.Add(customer);
            db.Licenses.Add(new License { Id = lic, CustomerId = customer.Id, LicenseKey = "br-" + Guid.NewGuid().ToString("N")[..12],
                SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1) });
            db.BankTransactions.AddRange(expired, unmatched, linked, young, lateFetched, unmatchedFetchedLater, linkedFetchedLater);
            db.PaymentMatches.AddRange(expiredMatch, unmatchedMatch, linkedMatch, unmatchedFetchedLaterMatch, linkedFetchedLaterMatch);
            db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(),
                CreatedAt = now.AddDays(-181) });
            db.CustomerIbanMemories.AddRange(staleConfirmed, staleNeverConfirmed, fresh);
            await db.SaveChangesAsync();
        }

        BankRetentionResult r;
        using (var scope = _factory.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<BankDataRetentionJob>().RunAsync(CancellationToken.None);

        (r.TransactionsDeleted, r.MatchesDeleted, r.GapsDeleted, r.IbanMemoriesDeleted).Should().Be((1, 1, 1, 2));
        r.UnmatchedDescriptionsPurged.Should().Be(2, "öneri eşleşme değildir; bağlı olanın açıklaması kalır");
        (r.RawPurged, r.EvidencePurged).Should().Be((4, 2));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.BankTransactions.AnyAsync(t => t.Id == expired.Id)).Should().BeFalse();
            (await db.PaymentMatches.AnyAsync(m => m.Id == expiredMatch.Id)).Should().BeFalse();
            var u = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == unmatched.Id);
            (u.Description, u.CounterpartyName, u.RawJson).Should().Be(((string?)null, (string?)null, (string?)null));
            (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == unmatchedMatch.Id)).Evidence
                .Should().BeNull("gerekçe metni hareketin açıklamasıyla gider");
            var l = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == linked.Id);
            l.Description.Should().Be("aciklama");
            l.RawJson.Should().BeNull("ham kayıt 30 günde gider");
            (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == linkedMatch.Id)).Evidence.Should().Be("kanıt");
            (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == young.Id)).RawJson.Should().Be("{}");
            var late = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == lateFetched.Id);
            (late.RawJson, late.Description).Should().Be(("{}", "aciklama"), "çekimden 7 gün geçmeden dokunulmaz");
            var uf = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == unmatchedFetchedLater.Id);
            (uf.RawJson, uf.Description, uf.CounterpartyName).Should().Be(((string?)null, (string?)null, (string?)null),
                "süre çekim anından değil işlem tarihinden");
            uf.DescriptionPurgedAt.Should().NotBeNull();
            (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == unmatchedFetchedLaterMatch.Id)).Evidence.Should().BeNull();
            var lf = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == linkedFetchedLater.Id);
            (lf.RawJson, lf.Description, lf.DescriptionPurgedAt).Should().Be(((string?)null, "aciklama", (DateTimeOffset?)null));
            (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == linkedFetchedLaterMatch.Id)).Evidence.Should().Be("kanıt");
            (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync()).Should().BeEquivalentTo(new[] { fresh.Id });
            var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
            records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.Retention);
            records.Where(x => x.Action != DataDestruction.Actions.RunCompleted).Should().OnlyContain(x => x.LicenseId == lic);
            records.Where(x => x.Action != DataDestruction.Actions.RunCompleted).Select(x => (x.Target, x.RowCount))
                .Should().BeEquivalentTo(new[]
                {
                    ("BankTransactions", 1), ("PaymentMatches", 1), ("PaymentMatchGaps", 1), ("BankTransactions.RawJson", 4),
                    ("BankTransactions.Description+CounterpartyName", 2), ("PaymentMatches.Evidence", 2), ("CustomerIbanMemories", 2),
                });
            records.Should().ContainSingle(x => x.Action == DataDestruction.Actions.RunCompleted && x.Target == BankDataRetentionJob.JobId);
        }
    }
}
