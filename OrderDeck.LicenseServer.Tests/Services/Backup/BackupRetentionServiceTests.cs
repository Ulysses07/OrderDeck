using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class BackupRetentionServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BackupRetentionServiceTests(ApiFactory factory) => _factory = factory;

    /// <summary>Testlerin "şimdi"si. Sabit: aylık yedeğin süresi saate bağlı, gerçek saatle sabit tarihli testler zamanla
    /// kendiliğinden kırılırdı.</summary>
    private static readonly DateTimeOffset TestNow = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static async Task<(LicenseDbContext db, BackupRetentionService svc, BackupStorageService storage, Guid customerId)> SetupAsync(
        ApiFactory factory, TimeProvider? clock = null, int monthlyRetentionMonths = 12)
    {
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Reset CustomerBackups for isolation
        db.CustomerBackups.RemoveRange(db.CustomerBackups);
        await db.SaveChangesAsync();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"retention-{Guid.NewGuid():N}@example.test",
            Name = "T",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();
        var svc = new BackupRetentionService(db, storage,
            Options.Create(new BackupOptions { MonthlyRetentionMonths = monthlyRetentionMonths }),
            NullLogger<BackupRetentionService>.Instance, clock ?? new FixedClock(TestNow));
        return (db, svc, storage, customer.Id);
    }

    private static async Task<CustomerBackup> InsertBackupAsync(
        LicenseDbContext db, BackupStorageService storage, Guid customerId, DateTimeOffset createdAt,
        bool isMonthlyMilestone = false)
    {
        var bytes = await Task.FromResult(new byte[] { 1, 2, 3 });
        var (envelope, _) = storage.Encrypt(bytes);
        var path = await storage.WriteBlobAsync(customerId, envelope);
        var b = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            BlobPath = path,
            SizeBytes = new FileInfo(path).Length,
            ChecksumSha256 = new string('0', 64),
            CreatedAt = createdAt,
            IsMonthlyMilestone = isMonthlyMilestone
        };
        db.CustomerBackups.Add(b);
        await db.SaveChangesAsync();
        return b;
    }

    /// <summary>Yedeği ekler ve yükleme sonrası saklama turunu koşar (yükleme ucunun sırası). <paramref name="clock"/> verilirse
    /// saat yüklemenin anına alınır: üretimde <c>CreatedAt</c> sunucunun "şimdi"sidir.</summary>
    private static async Task<CustomerBackup> UploadAsync(
        LicenseDbContext db, BackupRetentionService svc, BackupStorageService storage, Guid customerId, DateTimeOffset createdAt,
        FixedClock? clock = null)
    {
        if (clock is not null) clock.UtcNow = createdAt;
        var b = await InsertBackupAsync(db, storage, customerId, createdAt);
        await svc.EnforceAfterInsertAsync(customerId, b.Id);
        return b;
    }

    private static Task<List<DataDestructionRecord>> RecordsAsync(LicenseDbContext db, Guid customerId)
        => db.DataDestructionRecords.AsNoTracking().Where(r => r.SubjectId == customerId).ToListAsync();

    [Fact]
    public async Task EnforceAfterInsert_FirstOfMonth_MarksAsMilestone()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var b = await InsertBackupAsync(db, storage, customerId, DateTimeOffset.UtcNow);

        await svc.EnforceAfterInsertAsync(customerId, b.Id);

        var refreshed = await db.CustomerBackups.FindAsync(b.Id);
        refreshed!.IsMonthlyMilestone.Should().BeTrue();
    }

    [Fact]
    public async Task EnforceAfterInsert_SecondOfSameMonth_NotMilestone()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var b1 = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 4, 5, 10, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, b1.Id);

        var b2 = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, b2.Id);

        (await db.CustomerBackups.FindAsync(b1.Id))!.IsMonthlyMilestone.Should().BeTrue();
        (await db.CustomerBackups.FindAsync(b2.Id))!.IsMonthlyMilestone.Should().BeFalse();
    }

    [Fact]
    public async Task Retentiondan_once_iki_satir_varsa_ayin_en_eskisini_milestone_yapar()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var oldest = await InsertBackupAsync(db, storage, customerId,
            new DateTimeOffset(2026, 4, 5, 10, 0, 0, TimeSpan.Zero));
        var newest = await InsertBackupAsync(db, storage, customerId,
            new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));

        await Task.WhenAll(
            svc.EnforceAfterInsertAsync(customerId, oldest.Id),
            svc.EnforceAfterInsertAsync(customerId, newest.Id));

        var milestones = await db.CustomerBackups
            .Where(b => b.CustomerId == customerId && b.IsMonthlyMilestone)
            .ToListAsync();
        milestones.Should().ContainSingle().Which.Id.Should().Be(oldest.Id);
    }

    [Fact]
    public async Task Ayni_anda_olusan_yedeklerde_en_kucuk_id_milestone_olur()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var createdAt = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var first = await InsertBackupAsync(db, storage, customerId, createdAt);
        var second = await InsertBackupAsync(db, storage, customerId, createdAt);
        var expectedId = first.Id.CompareTo(second.Id) < 0 ? first.Id : second.Id;

        await svc.EnforceAfterInsertAsync(customerId, second.Id);

        var milestones = await db.CustomerBackups
            .Where(b => b.CustomerId == customerId && b.IsMonthlyMilestone)
            .ToListAsync();
        milestones.Should().ContainSingle().Which.Id.Should().Be(expectedId);
    }

    [Fact]
    public async Task EnforceAfterInsert_SixthNonMilestone_DeletesOldestNonMilestone()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);

        var milestone = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, milestone.Id);

        var marchBackups = new List<CustomerBackup>();
        for (int i = 0; i < 6; i++)
        {
            var date = new DateTimeOffset(2026, 3, 1 + i, 10, 0, 0, TimeSpan.Zero);
            var b = await InsertBackupAsync(db, storage, customerId, date);
            await svc.EnforceAfterInsertAsync(customerId, b.Id);
            marchBackups.Add(b);
        }

        var remaining = await db.CustomerBackups
            .Where(x => x.CustomerId == customerId && !x.IsMonthlyMilestone)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
        remaining.Count.Should().Be(5, because: "non-milestones trimmed to 5 most recent");
    }

    [Fact]
    public async Task EnforceAfterInsert_MonthlyMilestonesPreserved_BeyondFive()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);

        var milestoneIds = new List<Guid>();
        for (int month = 1; month <= 8; month++)
        {
            var b = await InsertBackupAsync(db, storage, customerId,
                new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero));
            await svc.EnforceAfterInsertAsync(customerId, b.Id);
            milestoneIds.Add(b.Id);
        }

        var milestones = await db.CustomerBackups
            .Where(x => x.CustomerId == customerId && x.IsMonthlyMilestone)
            .ToListAsync();
        milestones.Count.Should().Be(8, because: "all 8 first-of-month backups are within 12 months — preserved");
    }

    [Fact]
    public async Task EnforceAfterInsert_DeletedBackup_AlsoRemovesFile()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);

        var milestone = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, milestone.Id);

        var oldest = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, oldest.Id);

        var nonMilestones = new List<CustomerBackup>();
        for (int day = 5; day < 10; day++)
        {
            var b = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 2, day, 10, 0, 0, TimeSpan.Zero));
            await svc.EnforceAfterInsertAsync(customerId, b.Id);
            nonMilestones.Add(b);
        }

        var sixth = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.Zero));
        var deletedPath = nonMilestones[0].BlobPath;
        await svc.EnforceAfterInsertAsync(customerId, sixth.Id);

        File.Exists(deletedPath).Should().BeFalse(because: "oldest non-milestone blob deleted from disk");
        (await db.CustomerBackups.FindAsync(nonMilestones[0].Id)).Should().BeNull(because: "row removed");
    }

    [Fact]
    public async Task EnforceAfterInsert_OnlyOneBackup_NoTrimming()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var b = await InsertBackupAsync(db, storage, customerId, DateTimeOffset.UtcNow);
        await svc.EnforceAfterInsertAsync(customerId, b.Id);

        var count = await db.CustomerBackups.CountAsync(x => x.CustomerId == customerId);
        count.Should().Be(1);
    }

    // ── Aylık yedeğin süresi (karar 7, 23.10.2026: 12 ay) ──────────────────────────────────────────────

    [Fact]
    public void Aylik_yedek_saklama_suresi_varsayilan_12_ay()
        => new BackupOptions().MonthlyRetentionMonths.Should().Be(12);

    [Fact]
    public async Task Aylik_yedek_12_aydan_eskiyse_sonraki_yuklemede_satiri_ve_dosyasi_silinir()
    {
        var clock = new FixedClock(TestNow);
        var (db, svc, storage, customerId) = await SetupAsync(_factory, clock);
        var old = await UploadAsync(db, svc, storage, customerId, TestNow.AddMonths(-13), clock);
        var oldPath = old.BlobPath;

        var fresh = await UploadAsync(db, svc, storage, customerId, TestNow, clock);

        (await db.CustomerBackups.FindAsync(old.Id)).Should().BeNull("alındığı andan 12 aydan eski aylık yedek gider");
        File.Exists(oldPath).Should().BeFalse("satırla birlikte dosyası da silinir");
        (await db.CustomerBackups.FindAsync(fresh.Id))!.IsMonthlyMilestone.Should().BeTrue();
    }

    [Fact]
    public async Task Tam_12_ay_once_alinan_aylik_yedek_kalir_bir_saniye_sonra_silinir()
    {
        var clock = new FixedClock(TestNow);
        var (db, svc, storage, customerId) = await SetupAsync(_factory, clock);
        var monthly = await UploadAsync(db, svc, storage, customerId, TestNow.AddMonths(-12), clock);
        await UploadAsync(db, svc, storage, customerId, TestNow, clock);

        (await db.CustomerBackups.FindAsync(monthly.Id)).Should().NotBeNull("tam 12 ay: süre henüz aşılmadı");

        await UploadAsync(db, svc, storage, customerId, TestNow.AddSeconds(1), clock);

        (await db.CustomerBackups.FindAsync(monthly.Id)).Should().BeNull("12 ay + 1 saniye: süre aşıldı");
    }

    [Fact]
    public async Task En_yeni_yedek_aylik_ve_12_aydan_eskiyse_silinmez()
    {
        // Saat TestNow'da duruyor: müşteri 14 aydır yükleme yapmamış gibi (en yeni yedeği de süresi dolmuş aylık).
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var older = await UploadAsync(db, svc, storage, customerId, TestNow.AddMonths(-15));
        var newest = await UploadAsync(db, svc, storage, customerId, TestNow.AddMonths(-14));

        var kept = await db.CustomerBackups.FindAsync(newest.Id);
        kept.Should().NotBeNull("müşterinin en yeni yedeği süre kuralıyla silinmez: elinde hiç yedek kalmaz");
        kept!.IsMonthlyMilestone.Should().BeTrue();
        File.Exists(newest.BlobPath).Should().BeTrue();
        (await db.CustomerBackups.FindAsync(older.Id)).Should().BeNull("en yeni olmayan, süresi dolan aylık yedek gider");
    }

    [Fact]
    public async Task Son_5_kurali_degismez_aylik_olmayanlar_yasa_bakmadan_kalir_suresi_dolan_aylik_ayrica_gider()
    {
        var clock = new FixedClock(TestNow);
        var (db, svc, storage, customerId) = await SetupAsync(_factory, clock);
        // 14 ay önceki ay: ayın ilki (aylık) + 3 aylık olmayan.
        var oldMonth = TestNow.AddMonths(-14);
        var oldMonthly = await UploadAsync(db, svc, storage, customerId, oldMonth, clock);
        var oldRegulars = new List<CustomerBackup>();
        for (var day = 1; day <= 3; day++)
            oldRegulars.Add(await UploadAsync(db, svc, storage, customerId, oldMonth.AddDays(day), clock));
        // Bu ay: ayın ilki + 3 aylık olmayan. Aylık olmayan toplam 6: en eskisi son-5 kuralıyla gider.
        var thisMonth = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var currentMonthly = await UploadAsync(db, svc, storage, customerId, thisMonth, clock);
        var currentRegulars = new List<CustomerBackup>();
        for (var day = 1; day <= 3; day++)
            currentRegulars.Add(await UploadAsync(db, svc, storage, customerId, thisMonth.AddDays(day), clock));

        var remaining = await db.CustomerBackups.AsNoTracking()
            .Where(b => b.CustomerId == customerId).Select(b => b.Id).ToListAsync();
        remaining.Should().NotContain(oldMonthly.Id, "en yeni olmayan, süresi dolan aylık yedek");
        remaining.Should().BeEquivalentTo(
            new[] { currentMonthly.Id, oldRegulars[1].Id, oldRegulars[2].Id }.Concat(currentRegulars.Select(b => b.Id)),
            "süresi dolan aylık gider; son 5 aylık olmayan (ikisi 12 aydan eski) kalır, 6.'sı son-5 kuralıyla gider");
        (await db.CustomerBackups.AsNoTracking().CountAsync(b => b.CustomerId == customerId && !b.IsMonthlyMilestone))
            .Should().Be(5);

        // İmha kaydı yalnız süresi dolan aylık yedek için: son-5 kırpması bugünkü gibi kayıtsız.
        (await RecordsAsync(db, customerId)).Should().ContainSingle().Which.RowCount.Should().Be(1);
    }

    [Fact]
    public async Task Suresi_dolan_aylik_yedekler_tek_imha_kaydi_birakir()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        await InsertBackupAsync(db, storage, customerId, TestNow.AddMonths(-15), isMonthlyMilestone: true);
        await InsertBackupAsync(db, storage, customerId, TestNow.AddMonths(-14), isMonthlyMilestone: true);

        await UploadAsync(db, svc, storage, customerId, TestNow);

        (await db.CustomerBackups.CountAsync(b => b.CustomerId == customerId)).Should().Be(1);
        var record = (await RecordsAsync(db, customerId)).Should().ContainSingle().Subject;
        record.Target.Should().Be(BackupRetentionService.DestructionTarget);
        record.Action.Should().Be(DataDestruction.Actions.Deleted);
        record.RowCount.Should().Be(2);
        record.Criterion.Should().Be("CreatedAt+12ay, aylık yedek");
        record.Trigger.Should().Be(DataDestruction.Triggers.Retention);
        record.Actor.Should().Be(DataDestruction.SystemActor);
        record.LicenseId.Should().BeNull();
    }

    [Fact]
    public async Task Sure_secenekle_ayarlanir()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory, monthlyRetentionMonths: 6);
        var sevenMonths = await InsertBackupAsync(db, storage, customerId, TestNow.AddMonths(-7), isMonthlyMilestone: true);
        var fiveMonths = await InsertBackupAsync(db, storage, customerId, TestNow.AddMonths(-5), isMonthlyMilestone: true);

        await UploadAsync(db, svc, storage, customerId, TestNow);

        (await db.CustomerBackups.FindAsync(sevenMonths.Id)).Should().BeNull();
        (await db.CustomerBackups.FindAsync(fiveMonths.Id)).Should().NotBeNull();
        (await RecordsAsync(db, customerId)).Should().ContainSingle()
            .Which.Criterion.Should().Be("CreatedAt+6ay, aylık yedek");
    }

    [Fact]
    public async Task Sure_0_ise_aylik_yedek_suresiz_kalir()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory, monthlyRetentionMonths: 0);
        var ancient = await InsertBackupAsync(db, storage, customerId, TestNow.AddMonths(-30), isMonthlyMilestone: true);

        await UploadAsync(db, svc, storage, customerId, TestNow);

        (await db.CustomerBackups.FindAsync(ancient.Id)).Should().NotBeNull();
        (await RecordsAsync(db, customerId)).Should().BeEmpty();
    }
}
