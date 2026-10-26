using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

/// <summary>Günlük aylık yedek süpürmesinin iş kabuğu: koşu kaydı, kapalıyken hiçbir şey, düşünce yöneticiye e-posta (KVKK Ö-13,
/// iç imha prosedürü §4.2-3). Kuralın kendisi <see cref="BackupRetentionServiceTests"/>'te.</summary>
public sealed class BackupMonthlyRetentionJobTests : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset TestNow = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory;
    public BackupMonthlyRetentionJobTests(ApiFactory factory) => _factory = factory;

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>Kurulan her kaydı düşürür (yalnız <see cref="Armed"/> iken): DB kesintisi benzetimi.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Armed ? throw new DbUpdateException("DB kesintisi benzetimi") : ValueTask.FromResult(result);
    }

    /// <summary>Paylaşılan fabrika veritabanı değil, kendi InMemory veritabanı: imha kayıtları tam sayılır.</summary>
    private static LicenseDbContext OwnDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"backup-ret-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private BackupMonthlyRetentionJob Make(LicenseDbContext db, int months = 12, TestEmailSender? email = null)
    {
        var opt = Options.Create(new BackupOptions { MonthlyRetentionMonths = months });
        var retention = new BackupRetentionService(db, _factory.Services.GetRequiredService<BackupStorageService>(), opt,
            NullLogger<BackupRetentionService>.Instance, new FixedClock(TestNow));
        var alerts = new AdminJobAlerts(email ?? new TestEmailSender(), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
            }).Build(), NullLogger<AdminJobAlerts>.Instance);
        return new BackupMonthlyRetentionJob(db, retention, opt, alerts, NullLogger<BackupMonthlyRetentionJob>.Instance);
    }

    /// <summary>Bir müşteriye iki aylık yedek: süresi dolmuş (silinecek) ve en yeni (kalır).</summary>
    private async Task<(Guid customerId, CustomerBackup expired, CustomerBackup newest)> SeedAsync(LicenseDbContext db)
    {
        var storage = _factory.Services.GetRequiredService<BackupStorageService>();
        var customerId = Guid.NewGuid();
        async Task<CustomerBackup> Add(DateTimeOffset createdAt)
        {
            var (envelope, _) = storage.Encrypt(new byte[] { 1, 2, 3 });
            var path = await storage.WriteBlobAsync(customerId, envelope);
            var b = new CustomerBackup
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BlobPath = path, SizeBytes = new FileInfo(path).Length,
                ChecksumSha256 = new string('0', 64), CreatedAt = createdAt, IsMonthlyMilestone = true,
            };
            db.CustomerBackups.Add(b);
            return b;
        }
        var expired = await Add(TestNow.AddMonths(-14));
        var newest = await Add(TestNow.AddMonths(-1));
        await db.SaveChangesAsync();
        return (customerId, expired, newest);
    }

    [Fact]
    public void Is_DI_kapsamindan_cozulur()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackupMonthlyRetentionJob>().Should().NotBeNull();
    }

    [Fact]
    public async Task Basarili_kosu_suresi_dolani_siler_imha_ve_kosu_kaydi_yazar()
    {
        using var db = OwnDb();
        var (customerId, expired, newest) = await SeedAsync(db);

        (await Make(db).RunAsync(CancellationToken.None)).Should().Be(1);

        db.ChangeTracker.Clear();
        (await db.CustomerBackups.Select(b => b.Id).ToListAsync()).Should().Equal(newest.Id);
        File.Exists(expired.BlobPath).Should().BeFalse();
        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().HaveCount(2);
        records.Should().ContainSingle(r => r.Action == DataDestruction.Actions.Deleted)
            .Which.Should().Match<DataDestructionRecord>(r => r.SubjectId == customerId && r.RowCount == 1
                && r.Target == BackupRetentionService.DestructionTarget);
        records.Should().ContainSingle(r => r.Action == DataDestruction.Actions.RunCompleted)
            .Which.Should().Match<DataDestructionRecord>(r => r.Target == BackupMonthlyRetentionJob.JobId && r.RowCount == 0
                && r.Criterion == "CreatedAt+12ay, aylık yedek" && r.Trigger == DataDestruction.Triggers.Retention);
        records.Should().OnlyContain(r => !r.Criterion.Contains(';'));
    }

    [Fact]
    public async Task Silinecek_yedek_yoksa_yalniz_kosu_kaydi_yazar()
    {
        using var db = OwnDb();

        (await Make(db).RunAsync(CancellationToken.None)).Should().Be(0);

        (await db.DataDestructionRecords.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<DataDestructionRecord>(r => r.Action == DataDestruction.Actions.RunCompleted
                && r.Target == BackupMonthlyRetentionJob.JobId);
    }

    [Fact]
    public async Task Sure_0_ise_hicbir_sey_silmez_kosu_kaydi_da_yazmaz()
    {
        using var db = OwnDb();
        await SeedAsync(db);

        (await Make(db, months: 0).RunAsync(CancellationToken.None)).Should().Be(0);

        (await db.CustomerBackups.CountAsync()).Should().Be(2);
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Duserse_yoneticiye_e_posta_gider_hata_yukari_cikar_kosu_kaydi_yazilmaz()
    {
        var failing = new FailingSaveInterceptor();
        var email = new TestEmailSender();
        using var db = OwnDb(failing);
        var (_, expired, _) = await SeedAsync(db);
        failing.Armed = true;

        var act = () => Make(db, email: email).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be(BackupMonthlyRetentionJob.FailureSubject);
        sent.PlainBody.Should().Contain(BackupMonthlyRetentionJob.JobId).And.Contain(nameof(DbUpdateException));
        failing.Armed = false;
        db.ChangeTracker.Clear();
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0);
        (await db.CustomerBackups.CountAsync()).Should().Be(2, "satır silinemediyse dosyası da durur");
        File.Exists(expired.BlobPath).Should().BeTrue();
    }
}
