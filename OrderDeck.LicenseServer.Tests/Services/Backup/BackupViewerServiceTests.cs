using System.IO.Compression;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class BackupViewerServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BackupViewerServiceTests(ApiFactory factory) => _factory = factory;

    /// <summary>Builds a minimal valid orderdeck.db zip with one Customer + one Session + one Label.</summary>
    private static byte[] BuildSampleDbZip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sample-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        using (var conn = new SqliteConnection(connStr))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE Customer (
                    Id TEXT PRIMARY KEY, Platform TEXT, Username TEXT,
                    DisplayName TEXT, AvatarUrl TEXT,
                    FirstSeenAt INTEGER, LastSeenAt INTEGER,
                    IsBlacklisted INTEGER, BlacklistReason TEXT, Notes TEXT,
                    TotalLabelsPrinted INTEGER, TotalAmount NUMERIC,
                    BlacklistedAt INTEGER, Address TEXT, Phone TEXT
                );
                CREATE TABLE StreamSession (
                    Id TEXT PRIMARY KEY, Title TEXT, StartedAt INTEGER,
                    EndedAt INTEGER, Platforms TEXT, Notes TEXT
                );
                CREATE TABLE Label (
                    Id TEXT PRIMARY KEY, SessionId TEXT, CustomerId TEXT,
                    Platform TEXT, Username TEXT, MessageText TEXT, Code TEXT,
                    Price NUMERIC, AddedAt INTEGER, PrintedAt INTEGER
                );
                CREATE TABLE Giveaway (
                    Id TEXT PRIMARY KEY, Keyword TEXT, StartedAt INTEGER, EndedAt INTEGER
                );
                CREATE TABLE GiveawayParticipant (
                    Id TEXT PRIMARY KEY, GiveawayId TEXT, IsWinner INTEGER
                );

                INSERT INTO Customer VALUES
                    ('c1','twitch','alice','Alice',NULL,1000,2000,0,NULL,NULL,3,150.0,NULL,NULL,'+905551111111');
                INSERT INTO StreamSession VALUES
                    ('s1','Yayın #1',1500,1900,'[]',NULL);
                INSERT INTO Label VALUES
                    ('l1','s1','c1','twitch','alice','Apple',NULL,75.0,1600,1700);
                INSERT INTO Label VALUES
                    ('l2','s1','c1','twitch','alice','Pear',NULL,75.0,1650,1750);
            ";
            cmd.ExecuteNonQuery();
        }
        // Clear the SQLite connection pool so the .db file handle is released on Windows
        // before we try to read it back via File.OpenRead and delete it below.
        SqliteConnection.ClearAllPools();

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("orderdeck.db");
            using var entryStream = entry.Open();
            using var src = File.OpenRead(dbPath);
            src.CopyTo(entryStream);
        }
        File.Delete(dbPath);
        return ms.ToArray();
    }

    private async Task<Guid> SeedBackupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"viewer-{Guid.NewGuid():N}@test.com",
            Name = "T", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var zipBytes = BuildSampleDbZip();
        var encrypted = storage.Encrypt(zipBytes);
        var path = await storage.WriteBlobAsync(customer.Id, encrypted);

        var backup = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BlobPath = path,
            SizeBytes = encrypted.Length,
            ChecksumSha256 = new string('a', 64),
            CreatedAt = DateTimeOffset.UtcNow,
            IsMonthlyMilestone = false
        };
        db.CustomerBackups.Add(backup);
        await db.SaveChangesAsync();
        return backup.Id;
    }

    [Fact]
    public async Task OpenAsync_AndGetSummary_ReturnsCorrectAggregates()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        await using var session = await viewer.OpenAsync(backupId);
        var summary = await session.GetSummaryAsync();

        summary.TotalSessions.Should().Be(1);
        summary.TotalLabels.Should().Be(2);
        summary.TotalUniqueCustomers.Should().Be(1);
        summary.TotalRevenue.Should().Be(150m);
        summary.TopCustomer!.Username.Should().Be("alice");
        summary.TopCustomer.Total.Should().Be(150m);
    }

    [Fact]
    public async Task OpenAsync_GetCustomers_PaginatedReturnsRows()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        await using var session = await viewer.OpenAsync(backupId);
        var page = await session.GetCustomersAsync(page: 1, search: null);

        page.Rows.Should().HaveCount(1);
        page.Rows[0].Username.Should().Be("alice");
        page.Rows[0].Phone.Should().Be("+905551111111");
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_GetSessions_IncludesAggregatedTotal()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        await using var session = await viewer.OpenAsync(backupId);
        var page = await session.GetSessionsAsync(page: 1);

        page.Rows.Should().HaveCount(1);
        page.Rows[0].Title.Should().Be("Yayın #1");
        page.Rows[0].LabelCount.Should().Be(2);
        page.Rows[0].TotalAmount.Should().Be(150m);
    }

    [Fact]
    public async Task BackupSession_DisposeAsync_RemovesTempDir()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        var session = await viewer.OpenAsync(backupId);
        var snapshotBefore = Directory.GetDirectories(Path.GetTempPath(), "orderdeck-view-*").Length;
        await session.DisposeAsync();
        var snapshotAfter = Directory.GetDirectories(Path.GetTempPath(), "orderdeck-view-*").Length;

        snapshotAfter.Should().BeLessThan(snapshotBefore);
    }
}
