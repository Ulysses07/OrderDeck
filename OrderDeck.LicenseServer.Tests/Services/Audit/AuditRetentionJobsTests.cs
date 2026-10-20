using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
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

    private static AuditRetentionJobs Make(LicenseDbContext db, int retentionMonths = 24, int batchSize = 1000,
        TestEmailSender? email = null)
        => new(db, Options.Create(new AuditRetentionOptions
        {
            RetentionMonths = retentionMonths,
            BatchSize = batchSize
        }), new BankJobAlerts(email ?? new TestEmailSender(), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BankJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
            }).Build(), NullLogger<BankJobAlerts>.Instance), NullLogger<AuditRetentionJobs>.Instance);

    /// <summary>Paylaşılan fabrika veritabanı değil, kendi InMemory veritabanı: imha kayıtları tam sayılır.</summary>
    private static LicenseDbContext OwnDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"audit-ret-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    /// <summary>Kurulan her kaydı düşürür (yalnız <see cref="Armed"/> iken): DB kesintisi benzetimi.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Armed ? throw new DbUpdateException("DB kesintisi benzetimi") : ValueTask.FromResult(result);
    }

    private static AuditLogEntry Entry(string type, int monthsAgo) => new()
    {
        Id = Guid.NewGuid(), EventType = type, OccurredAt = DateTimeOffset.UtcNow.AddMonths(-monthsAgo).AddDays(-1),
        AdminId = Guid.Empty, AdminUsername = "test",
    };

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

    [Fact]
    public async Task Budama_her_partide_imha_kaydi_ve_sonda_kosu_kaydi_yazar()
    {
        // KVKK Ö-13 / iç imha prosedürü §4.2: denetim kaydı budaması da kişisel veri siler (customer.purged'ın e-postası dahil);
        // her parti kalıcı imha kaydı bırakır, her başarılı koşu bir koşu kaydı ("son başarılı çalışma" tablodan okunur).
        using var db = OwnDb();
        db.AuditLogs.AddRange(Enumerable.Range(0, 5).Select(_ => Entry("test.normal", 30)));
        db.AuditLogs.AddRange(Entry(AuditEvents.ShopperPurged, 37), Entry(AuditEvents.ShopperPurged, 37),
            Entry(AuditEvents.ShopperPurged, 30), Entry("test.normal", 1));
        await db.SaveChangesAsync();

        await Make(db, batchSize: 3).PruneAsync(CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().OnlyContain(r => r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor
            && r.LicenseId == null);
        records.Where(r => r.Target == "AuditLogs").Should().OnlyContain(r => r.Action == DataDestruction.Actions.Deleted);
        records.Where(r => r.Target == "AuditLogs").Select(r => (r.Criterion, r.RowCount)).Should().BeEquivalentTo(new[]
        {
            ("OccurredAt+24ay", 3), ("OccurredAt+24ay", 2), ("OccurredAt+36ay;imha olayı", 2),
        });
        records.Should().ContainSingle(r => r.Action == DataDestruction.Actions.RunCompleted)
            .Which.Should().Match<DataDestructionRecord>(r => r.Target == AuditRetentionJobs.JobId && r.RowCount == 0);
        (await db.AuditLogs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Silinecek_satir_yoksa_yalniz_kosu_kaydi_yazar()
    {
        using var db = OwnDb();
        db.AuditLogs.Add(Entry("test.normal", 1));
        await db.SaveChangesAsync();

        await Make(db).PruneAsync(CancellationToken.None);

        (await db.DataDestructionRecords.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<DataDestructionRecord>(r => r.Action == DataDestruction.Actions.RunCompleted
                && r.Target == AuditRetentionJobs.JobId);
    }

    [Fact]
    public async Task Budama_duserse_yoneticiye_e_posta_gider_hata_yukari_cikar_kosu_kaydi_yazilmaz()
    {
        var failing = new FailingSaveInterceptor(); var email = new TestEmailSender();
        using var db = OwnDb(failing);
        db.AuditLogs.Add(Entry("test.normal", 30));
        await db.SaveChangesAsync();
        failing.Armed = true;

        var act = () => Make(db, email: email).PruneAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be(AuditRetentionJobs.FailureSubject);
        sent.PlainBody.Should().Contain(nameof(DbUpdateException));
        failing.Armed = false;
        (await db.DataDestructionRecords.AsNoTracking().CountAsync()).Should().Be(0);
    }
}
