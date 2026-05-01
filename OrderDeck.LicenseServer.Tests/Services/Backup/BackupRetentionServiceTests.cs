using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class BackupRetentionServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BackupRetentionServiceTests(ApiFactory factory) => _factory = factory;

    private static async Task<(LicenseDbContext db, BackupRetentionService svc, BackupStorageService storage, Guid customerId)> SetupAsync(ApiFactory factory)
    {
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Reset CustomerBackups for isolation
        db.CustomerBackups.RemoveRange(db.CustomerBackups);
        await db.SaveChangesAsync();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"retention-{Guid.NewGuid():N}@test.com",
            Name = "T",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();
        var svc = scope.ServiceProvider.GetRequiredService<BackupRetentionService>();
        return (db, svc, storage, customer.Id);
    }

    private static async Task<CustomerBackup> InsertBackupAsync(
        LicenseDbContext db, BackupStorageService storage, Guid customerId, DateTimeOffset createdAt)
    {
        var bytes = await Task.FromResult(new byte[] { 1, 2, 3 });
        var path = await storage.WriteBlobAsync(customerId, storage.Encrypt(bytes));
        var b = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            BlobPath = path,
            SizeBytes = new FileInfo(path).Length,
            ChecksumSha256 = new string('0', 64),
            CreatedAt = createdAt,
            IsMonthlyMilestone = false
        };
        db.CustomerBackups.Add(b);
        await db.SaveChangesAsync();
        return b;
    }

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
        milestones.Count.Should().Be(8, because: "all 8 first-of-month backups preserved");
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
}
