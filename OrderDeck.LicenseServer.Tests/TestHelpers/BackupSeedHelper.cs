using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public static class BackupSeedHelper
{
    /// <summary>
    /// Seeds a Customer + a CustomerBackup row with an encrypted blob containing a
    /// minimal valid orderdeck.db zip (1 customer "alice" + 1 session "Yayın #1" + 2 labels of 75 TL each = 150 TL).
    /// Returns (customerId, backupId).
    /// </summary>
    public static async Task<(Guid customerId, Guid backupId)> SeedSampleBackupAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"viewer-seed-{Guid.NewGuid():N}@test.com",
            Name = "T",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
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
        return (customer.Id, backup.Id);
    }

    private static byte[] BuildSampleDbZip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sample-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
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
        // Critical for Windows: clear pool so file lock is released before File.OpenRead/Delete
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
}
