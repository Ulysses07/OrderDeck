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

/// <summary>Denetim budaması gerçek SQL Server'da: InMemory ExecuteDelete bilmez, orada yükle+sil dalı koşar. İmha olayı
/// muafiyeti (<see cref="AuditEvents.DestructionEvents"/>) üretimdeki ExecuteDelete dalında, dizi <c>Contains</c>'in SQL
/// çevirisiyle sınanır (KVKK Ö-13).</summary>
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
            AdminId = Guid.Empty, AdminUsername = "test", TargetType = AuditTargets.Shopper,
        };
        var kept = new[] { Entry(AuditEvents.ShopperPurged, 30), Entry(AuditEvents.ShopperPurged, 30), Entry("test.normal", 1) };
        var pruned = new[]
        {
            Entry(AuditEvents.ShopperPurged, 37), Entry(AuditEvents.ShopperPurged, 37), Entry(AuditEvents.ShopperPurged, 37),
            Entry(AuditEvents.CustomerPurged, 30), Entry("test.normal", 30), Entry("test.normal", 30), Entry("test.normal", 30),
        };
        db.AuditLogs.AddRange(kept.Concat(pruned));
        await db.SaveChangesAsync();

        var olderBefore = await db.AuditLogs.CountAsync(a => a.OccurredAt < DateTimeOffset.UtcNow.AddMonths(-24));

        // Parti boyutu 2: iki koşulun ikisi de birden çok parti döner.
        await new AuditRetentionJobs(db, Options.Create(new AuditRetentionOptions { BatchSize = 2 }),
            scope.ServiceProvider.GetRequiredService<BankJobAlerts>(), NullLogger<AuditRetentionJobs>.Instance)
            .PruneAsync(CancellationToken.None);

        var seeded = kept.Concat(pruned).Select(a => a.Id).ToList();
        (await db.AuditLogs.AsNoTracking().Where(a => seeded.Contains(a.Id)).Select(a => a.Id).ToListAsync())
            .Should().BeEquivalentTo(kept.Select(a => a.Id));
        // ExecuteDelete dalında da her parti imha kaydını aynı işlemde yazar (KVKK Ö-13); koşu kaydı sonda.
        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Where(r => r.Target == "AuditLogs").Should().OnlyContain(r => r.RowCount > 0 && r.RowCount <= 2);
        records.Where(r => r.Target == "AuditLogs").Sum(r => r.RowCount)
            .Should().Be(olderBefore - 2, "budanan her satır kayıtta; 30 aylık iki shopper.purged kalır");
        records.Should().ContainSingle(r => r.Action == DataDestruction.Actions.RunCompleted && r.Target == AuditRetentionJobs.JobId);
    }
}
