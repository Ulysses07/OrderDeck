using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Audit;

public class AuditRetentionJobsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AuditRetentionJobsTests(ApiFactory factory) => _factory = factory;

    private async Task<string> SeedAsync(int oldCount, int newCount)
    {
        // Unique tag per test so the shared in-memory DB doesn't bleed seeds
        // across cases (xUnit IClassFixture ⇒ same DB across [Fact] runs).
        var tag = "test-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var oldCutoff = DateTimeOffset.UtcNow.AddYears(-3);  // 3 years old → past 24-month default
        var freshCutoff = DateTimeOffset.UtcNow.AddDays(-7); // last week → keep

        for (var i = 0; i < oldCount; i++)
        {
            db.AuditLogs.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                EventType = $"{tag}-old",
                OccurredAt = oldCutoff.AddSeconds(-i),
                AdminId = Guid.Empty,
                AdminUsername = "test"
            });
        }
        for (var i = 0; i < newCount; i++)
        {
            db.AuditLogs.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                EventType = $"{tag}-new",
                OccurredAt = freshCutoff.AddSeconds(-i),
                AdminId = Guid.Empty,
                AdminUsername = "test"
            });
        }
        await db.SaveChangesAsync();
        return tag;
    }

    private AuditRetentionJobs Make(LicenseDbContext db, int retentionMonths = 24, int batchSize = 1000)
        => new(db, Options.Create(new AuditRetentionOptions
        {
            RetentionMonths = retentionMonths,
            BatchSize = batchSize
        }), NullLogger<AuditRetentionJobs>.Instance);

    [Fact]
    public async Task Prune_deletes_rows_older_than_retention_keeps_recent()
    {
        var tag = await SeedAsync(oldCount: 5, newCount: 3);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var initial = await db.AuditLogs.CountAsync(a => a.EventType.StartsWith(tag));
        initial.Should().Be(8);

        await Make(db).PruneAsync(default);

        (await db.AuditLogs.CountAsync(a => a.EventType == $"{tag}-old")).Should().Be(0);
        (await db.AuditLogs.CountAsync(a => a.EventType == $"{tag}-new")).Should().Be(3);
    }

    [Fact]
    public async Task Prune_with_retention_zero_is_a_noop()
    {
        var tag = await SeedAsync(oldCount: 5, newCount: 0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var before = await db.AuditLogs.CountAsync(a => a.EventType.StartsWith(tag));

        await Make(db, retentionMonths: 0).PruneAsync(default);

        var after = await db.AuditLogs.CountAsync(a => a.EventType.StartsWith(tag));
        after.Should().Be(before);  // nothing pruned when retention is disabled
    }

    [Fact]
    public async Task Prune_handles_batches_larger_than_batchSize()
    {
        // 7 old rows + batchSize=3 → forces 3 iterations (3, 3, 1).
        var tag = await SeedAsync(oldCount: 7, newCount: 0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await Make(db, batchSize: 3).PruneAsync(default);

        var remaining = await db.AuditLogs.CountAsync(a => a.EventType == $"{tag}-old");
        remaining.Should().Be(0);
    }

    [Fact]
    public async Task Kisisel_veri_icermeyen_imha_olaylari_36_ayda_customer_purged_24_ayda_budanir()
    {
        // Silme Yönetmeliği md. 7/3: imha kayıtları en az üç yıl. Normal olay 24 ayda gider. customer.purged ayrıntısında
        // silinen yayıncının e-postası var: muafiyet onu 36 ay tutmasın (kalıcı kaydı DataDestructionRecords'ta).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        AuditLogEntry Entry(string type, int monthsAgo) => new()
        {
            Id = Guid.NewGuid(), EventType = type, OccurredAt = DateTimeOffset.UtcNow.AddMonths(-monthsAgo).AddDays(-1),
            AdminId = Guid.Empty, AdminUsername = "test",
        };
        var purge30 = Entry(AuditEvents.ShopperPurged, 30);
        var purge37 = Entry(AuditEvents.ShopperPurged, 37);
        var customerPurge30 = Entry(AuditEvents.CustomerPurged, 30);
        var normal30 = Entry("test-" + Guid.NewGuid().ToString("N")[..8], 30);
        db.AuditLogs.AddRange(purge30, purge37, customerPurge30, normal30);
        await db.SaveChangesAsync();

        await Make(db).PruneAsync(CancellationToken.None);

        var ids = new[] { purge30.Id, purge37.Id, customerPurge30.Id, normal30.Id };
        (await db.AuditLogs.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { purge30.Id });
        AuditEvents.DestructionEvents.Should().NotContain(AuditEvents.CustomerPurged);
    }
}
