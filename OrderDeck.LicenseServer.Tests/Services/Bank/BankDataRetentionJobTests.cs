using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class BankDataRetentionJobTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-ret-{Guid.NewGuid():N}").Options);

    private static BankTransaction Row(int ageDays) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), ObifinId = Random.Shared.NextInt64(1, 1_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow.AddDays(-ageDays), FetchedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
        Description = "aciklama", CounterpartyName = "ad", RawJson = "{}",
    };

    [Fact]
    public async Task Ham_json_90_gun_aciklama_180_gun_sonra_bosaltilir_tutar_ve_hash_kalir()
    {
        using var db = NewDb();
        var fresh = Row(10); var mid = Row(100); var old = Row(200);
        db.BankTransactions.AddRange(fresh, mid, old);
        await db.SaveChangesAsync();
        var job = new BankDataRetentionJob(db, Options.Create(new BankOptions { HashKey = new string('k', 32) }), NullLogger<BankDataRetentionJob>.Instance);

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.FindAsync(fresh.Id))!.RawJson.Should().NotBeNull();
        var m = (await db.BankTransactions.FindAsync(mid.Id))!;
        m.RawJson.Should().BeNull(); m.Description.Should().Be("aciklama");
        var o = (await db.BankTransactions.FindAsync(old.Id))!;
        o.RawJson.Should().BeNull(); o.Description.Should().BeNull(); o.CounterpartyName.Should().BeNull();
        o.DescriptionPurgedAt.Should().NotBeNull(); o.Amount.Should().Be(10m);
    }
}
