using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.Services;

public class BackupServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tempDb;

    /// <summary>
    /// Gerçek bir SQLite dosyası şart: yedekleme artık çevrimiçi yedekleme
    /// API'sini kullanıyor. Uydurma baytlarla test etmek, kopyalanan şeyin
    /// açılabilir bir veritabanı olduğunu hiç sınamıyordu.
    /// </summary>
    public BackupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"orderdeck-bs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _tempDb = Path.Combine(_dir, "orderdeck.db");

        using var conn = new SqliteConnectionFactory(_tempDb).Open();
        conn.Execute("CREATE TABLE Siparis (Kod TEXT PRIMARY KEY);");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] ExtractDb(byte[] zipPayload)
    {
        using var ms = new MemoryStream(zipPayload);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        using var entry = archive.GetEntry("orderdeck.db")!.Open();
        using var outp = new MemoryStream();
        entry.CopyTo(outp);
        return outp.ToArray();
    }

    [Fact]
    public async Task RunBackupNowAsync_ZipsDbAndUploadsWithCorrectSha()
    {
        var fake = new FakeBackupClient();
        var sut = new BackupService(_tempDb, fake, NullLogger<BackupService>.Instance);

        var result = await sut.RunBackupNowAsync();

        result.Success.Should().BeTrue();
        fake.Uploads.Should().HaveCount(1);

        var upload = fake.Uploads[0];
        // Verify it's a valid zip containing orderdeck.db
        using var ms = new MemoryStream(upload.payload);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.GetEntry("orderdeck.db").Should().NotBeNull();

        // SHA matches actual zip bytes
        var expected = Convert.ToHexString(SHA256.HashData(upload.payload)).ToLowerInvariant();
        upload.sha.Should().Be(expected);
    }

    /// <summary>
    /// WAL kipinde işlenmiş bir kayıt bir süre <c>-wal</c> yan dosyasında durur.
    /// Ana dosyayı düz kopyalayan bir yedek geçerli görünür, açılır, sadece o
    /// kaydı içermez — sipariş sessizce buharlaşır. Yedeğin kaydı içermesi şart.
    /// </summary>
    [Fact]
    public async Task RunBackupNowAsync_includes_records_still_sitting_in_the_WAL()
    {
        using (var conn = new SqliteConnectionFactory(_tempDb).Open())
            conn.Execute("INSERT INTO Siparis (Kod) VALUES ('A17');");

        new FileInfo(_tempDb + "-wal").Length.Should().BeGreaterThan(0,
            because: "testin bir şey kanıtlaması için kayıt gerçekten WAL'da olmalı");

        var fake = new FakeBackupClient();
        var sut = new BackupService(_tempDb, fake, NullLogger<BackupService>.Instance);

        (await sut.RunBackupNowAsync()).Success.Should().BeTrue();

        var restored = Path.Combine(_dir, "geri-yuklenen.db");
        File.WriteAllBytes(restored, ExtractDb(fake.Uploads[0].payload));

        using var check = new SqliteConnectionFactory(restored).Open();
        check.Query<string>("SELECT Kod FROM Siparis").Should().ContainSingle()
            .Which.Should().Be("A17");
    }

    [Fact]
    public async Task RunBackupNowAsync_NoDbFile_ReturnsFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.db");
        var fake = new FakeBackupClient();
        var sut = new BackupService(missing, fake, NullLogger<BackupService>.Instance);

        var result = await sut.RunBackupNowAsync();

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        fake.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task RunBackupNowAsync_UploadException_ReturnsFailureWithoutThrowing()
    {
        var fake = new FakeBackupClient { UploadException = new InvalidOperationException("simulated network") };
        var sut = new BackupService(_tempDb, fake, NullLogger<BackupService>.Instance);

        var result = await sut.RunBackupNowAsync();

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("simulated network");
    }

    [Fact]
    public async Task QueueBackup_ConcurrentCalls_OnlyOneActiveUploadAtATime()
    {
        var fake = new FakeBackupClient();
        var startedSignals = new SemaphoreSlim(0);
        var releaseSignals = new SemaphoreSlim(0);
        fake.UploadResponseFactory = (_, _, m) =>
        {
            startedSignals.Release();
            releaseSignals.Wait();
            return new OrderDeck.Licensing.Backup.BackupMetadata(
                Guid.NewGuid(), 1, DateTimeOffset.UtcNow, false, m);
        };

        var sut = new BackupService(_tempDb, fake, NullLogger<BackupService>.Instance);
        sut.QueueBackup("test1");
        sut.QueueBackup("test2");  // should be skipped (single-flight)

        await startedSignals.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSignals.Release();
        await Task.Delay(200);

        fake.Uploads.Count.Should().Be(1, because: "second QueueBackup detected active and skipped");
    }
}
