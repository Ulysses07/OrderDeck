using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.Licensing.Backup;
using Xunit;

namespace OrderDeck.Tests.Services;

public class RestoreServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _tempDir;

    public RestoreServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orderdeck-rs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "orderdeck.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Gerçek, açılabilir bir SQLite veritabanının baytları. Geri yükleme artık
    /// quick_check'ten geçmeyen içeriği reddettiği için testlerin de gerçek bir
    /// veritabanı üretmesi gerekiyor; "NEW-DB" gibi düz metinler doğru olarak
    /// reddedilir.
    /// </summary>
    private byte[] BuildRealDatabase(string marker)
    {
        var path = Path.Combine(_tempDir, $"seed-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE marker (value TEXT NOT NULL);" +
                $"INSERT INTO marker (value) VALUES ('{marker}');";
            cmd.ExecuteNonQuery();
        }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static string ReadMarker(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM marker LIMIT 1;";
        return (string)cmd.ExecuteScalar()!;
    }

    private static byte[] BuildZip(byte[] dbContent)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("orderdeck.db");
            using var s = entry.Open();
            s.Write(dbContent, 0, dbContent.Length);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task RestoreAsync_DownloadsAndExtracts_CreatesPreRestoreBak()
    {
        File.WriteAllBytes(_dbPath, BuildRealDatabase("OLD"));

        var zip = BuildZip(BuildRealDatabase("NEW-FROM-CLOUD"));

        var fake = new FakeBackupClientWithDownload(zip);
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeTrue();
        ReadMarker(_dbPath).Should().Be("NEW-FROM-CLOUD");
        File.Exists(_dbPath + ".pre-restore.bak").Should().BeTrue();
        ReadMarker(_dbPath + ".pre-restore.bak").Should().Be("OLD");
    }

    /// <summary>
    /// Y-08'in özü: zip açılıyor, dosya var, boyutu da makul — ama SQLite
    /// değil. Eskiden bu "Geri yükleme tamamlandı" diyip aktif veritabanının
    /// üzerine yazıyordu; bozukluk ancak bir sonraki açılışta görülüyordu.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_ZipContainsNonSqliteFile_FailsWithoutTouchingDb()
    {
        var existing = BuildRealDatabase("OLD");
        File.WriteAllBytes(_dbPath, existing);

        var zip = BuildZip(Encoding.UTF8.GetBytes(new string('x', 4096)));
        var sut = new RestoreService(
            _dbPath, new FakeBackupClientWithDownload(zip), NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("geçerli bir veritabanı değil");
        File.ReadAllBytes(_dbPath).Should().BeEquivalentTo(existing);
        ReadMarker(_dbPath).Should().Be("OLD");
    }

    /// <summary>
    /// Başlığı doğru ama sayfaları bozuk dosya: başlık denetimini geçer,
    /// quick_check'e takılır.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_CorruptedSqlitePages_FailsWithoutTouchingDb()
    {
        var existing = BuildRealDatabase("OLD");
        File.WriteAllBytes(_dbPath, existing);

        // Başlığı koru, gövdeyi çöple doldur.
        var corrupt = BuildRealDatabase("BROKEN");
        for (var i = 100; i < corrupt.Length; i++) corrupt[i] = 0xEE;

        var sut = new RestoreService(
            _dbPath, new FakeBackupClientWithDownload(BuildZip(corrupt)),
            NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeFalse();
        ReadMarker(_dbPath).Should().Be("OLD");
    }

    /// <summary>Doğrulama başarısız olunca yarım açılmış dosya diskte kalmamalı.</summary>
    [Fact]
    public async Task RestoreAsync_FailedValidation_RemovesTempExtract()
    {
        File.WriteAllBytes(_dbPath, BuildRealDatabase("OLD"));

        var zip = BuildZip(Encoding.UTF8.GetBytes("kesinlikle sqlite değil"));
        var sut = new RestoreService(
            _dbPath, new FakeBackupClientWithDownload(zip), NullLogger<RestoreService>.Instance);

        await sut.RestoreAsync(Guid.NewGuid());

        File.Exists(_dbPath + ".restoring").Should().BeFalse();
    }

    /// <summary>
    /// Ana dosyanın üzerine dışarıdan bir veritabanı yazıldığında eski
    /// <c>-wal</c>/<c>-shm</c> yan dosyaları kalırsa SQLite onları yeni dosyaya
    /// aitmiş gibi uygulamaya çalışır: geri yükleme "başarılı" döner, uygulama
    /// bozuk veya karışmış veriyle açılır.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_deletes_stale_wal_sidecars()
    {
        File.WriteAllBytes(_dbPath, BuildRealDatabase("OLD"));
        File.WriteAllBytes(_dbPath + "-wal", Encoding.UTF8.GetBytes("eski wal"));
        File.WriteAllBytes(_dbPath + "-shm", Encoding.UTF8.GetBytes("eski shm"));

        var fake = new FakeBackupClientWithDownload(BuildZip(BuildRealDatabase("NEW")));
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        (await sut.RestoreAsync(Guid.NewGuid())).Success.Should().BeTrue();

        File.Exists(_dbPath + "-wal").Should().BeFalse();
        File.Exists(_dbPath + "-shm").Should().BeFalse();
    }

    [Fact]
    public async Task RestoreAsync_DownloadFails_LeavesOriginalDbUntouched()
    {
        var existingDb = BuildRealDatabase("OLD");
        File.WriteAllBytes(_dbPath, existingDb);
        var fake = new FakeBackupClientWithDownload(failOnDownload: true);
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeFalse();
        File.ReadAllBytes(_dbPath).Should().BeEquivalentTo(existingDb);
    }

    [Fact]
    public async Task RestoreAsync_InvalidZipMissingDbEntry_ReturnsFailure_NoOverwrite()
    {
        var existingDb = BuildRealDatabase("OLD");
        File.WriteAllBytes(_dbPath, existingDb);

        var badZip = new byte[] { 1, 2, 3 };
        var fake = new FakeBackupClientWithDownload(badZip);
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeFalse();
        File.ReadAllBytes(_dbPath).Should().BeEquivalentTo(existingDb);
    }

    [Fact]
    public async Task ListAvailableAsync_ReturnsClientResults()
    {
        var fake = new FakeBackupClientWithDownload(new byte[0]);
        fake.ListResults = new List<BackupMetadata>
        {
            new(Guid.NewGuid(), 100, DateTimeOffset.UtcNow, true, "M1"),
            new(Guid.NewGuid(), 200, DateTimeOffset.UtcNow.AddDays(-1), false, "M2")
        };
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var list = await sut.ListAvailableAsync();
        list.Should().HaveCount(2);
        list[0].MachineName.Should().Be("M1");
    }
}

internal sealed class FakeBackupClientWithDownload : IBackupClient
{
    private readonly byte[]? _downloadBytes;
    private readonly bool _failOnDownload;
    public List<BackupMetadata> ListResults { get; set; } = new();

    public FakeBackupClientWithDownload(byte[] downloadBytes) { _downloadBytes = downloadBytes; _failOnDownload = false; }
    public FakeBackupClientWithDownload(bool failOnDownload) { _downloadBytes = null; _failOnDownload = failOnDownload; }

    public Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default) =>
        Task.FromResult(new BackupMetadata(Guid.NewGuid(), zipPayload.Length, DateTimeOffset.UtcNow, false, machineName));

    public Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupMetadata>>(ListResults);

    public Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default)
    {
        if (_failOnDownload) throw new InvalidOperationException("simulated download fail");
        return Task.FromResult(_downloadBytes!);
    }

    public Task DeleteAsync(Guid backupId, CancellationToken ct = default) => Task.CompletedTask;
}
