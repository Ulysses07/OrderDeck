using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Audit;

/// <summary>Denetim budaması gerçek SQL Server'da: InMemory ExecuteDelete/ExecuteUpdate bilmez, orada yükle+sil dalı koşar.
/// İmha olayı muafiyeti (<see cref="AuditEvents.DestructionEvents"/>) üretimdeki ExecuteDelete dalında, dizi <c>Contains</c>'in
/// SQL çevirisiyle (KVKK Ö-13); muaf satırın 24 ayda maskelenmesi ExecuteUpdate dalında, Guid→metin çevirisiyle (Ö-14) sınanır.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class AuditRetentionRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public AuditRetentionRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

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
    public async Task Imha_olayi_36_ay_normal_olay_ve_customer_purged_24_ay_kalir_parti_parti()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        AuditLogEntry Entry(string type, int monthsAgo) => new()
        {
            Id = Guid.NewGuid(), EventType = type, OccurredAt = DateTimeOffset.UtcNow.AddMonths(-monthsAgo).AddDays(-1),
            AdminId = Guid.NewGuid(), AdminUsername = $"yonetici-{Guid.NewGuid():N}", IpAddress = "192.0.2.20",
            TargetType = AuditTargets.Shopper,
        };
        var masked = new[] { Entry(AuditEvents.ShopperPurged, 30), Entry(AuditEvents.ShopperPurged, 30) };
        var recentPurge = Entry(AuditEvents.ShopperPurged, 12);
        var kept = masked.Append(recentPurge).Append(Entry("test.normal", 1)).ToArray();
        var pruned = new[]
        {
            Entry(AuditEvents.ShopperPurged, 37), Entry(AuditEvents.ShopperPurged, 37), Entry(AuditEvents.ShopperPurged, 37),
            Entry(AuditEvents.CustomerPurged, 30), Entry("test.normal", 30), Entry("test.normal", 30), Entry("test.normal", 30),
        };
        db.AuditLogs.AddRange(kept.Concat(pruned));
        await db.SaveChangesAsync();

        var olderBefore = await db.AuditLogs.CountAsync(a => a.OccurredAt < DateTimeOffset.UtcNow.AddMonths(-24));

        // Parti boyutu 2: iki koşulun ikisi de birden çok parti döner. İkinci koşu: maskelenen satır yeniden seçilmez.
        Task Prune() => new AuditRetentionJobs(db, Options.Create(new AuditRetentionOptions { BatchSize = 2 }),
            scope.ServiceProvider.GetRequiredService<BankJobAlerts>(), NullLogger<AuditRetentionJobs>.Instance)
            .PruneAsync(CancellationToken.None);
        await Prune();
        await Prune();

        var seeded = kept.Concat(pruned).Select(a => a.Id).ToList();
        var after = await db.AuditLogs.AsNoTracking().Where(a => seeded.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
        after.Keys.Should().BeEquivalentTo(kept.Select(a => a.Id));
        // ExecuteDelete dalında da her parti imha kaydını aynı işlemde yazar (KVKK Ö-13); koşu kaydı sonda.
        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Where(r => r.Target == "AuditLogs").Should().OnlyContain(r => r.RowCount > 0 && r.RowCount <= 2);
        records.Where(r => r.Target == "AuditLogs").Sum(r => r.RowCount)
            .Should().Be(olderBefore - 2, "budanan her satır kayıtta; 30 aylık iki shopper.purged kalır");
        records.Count(r => r.Action == DataDestruction.Actions.RunCompleted && r.Target == AuditRetentionJobs.JobId).Should().Be(2);
        // KVKK Ö-14: imha olayının 24 aydan eski satırında yöneticinin adı kimliğine çevrilir (.NET biçimi, küçük harf), IP
        // boşalır — ExecuteUpdate dalı ve Guid→metin çevirisi gerçek SQL Server'da. 24 aydan yeni satıra dokunulmaz.
        foreach (var a in masked)
            after[a.Id].Should().Match<AuditLogEntry>(x => x.AdminUsername == a.AdminId.ToString() && x.IpAddress == null);
        after[recentPurge.Id].Should().Match<AuditLogEntry>(x => x.AdminUsername == recentPurge.AdminUsername
            && x.IpAddress == recentPurge.IpAddress);
        records.Where(r => r.Target == AuditRetentionJobs.MaskedTarget).Should().OnlyContain(r => r.Action == DataDestruction.Actions.Masked)
            .And.Subject.Sum(r => r.RowCount).Should().Be(2, "iki koşuda toplam iki satır maskelenir");
    }
}
