using System.IO.Compression;
using System.Text;
using FluentAssertions;
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
        var existingDb = Encoding.UTF8.GetBytes("OLD-DB");
        File.WriteAllBytes(_dbPath, existingDb);

        var newDbContent = Encoding.UTF8.GetBytes("NEW-DB-FROM-CLOUD");
        var zip = BuildZip(newDbContent);

        var fake = new FakeBackupClientWithDownload(zip);
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeTrue();
        File.ReadAllBytes(_dbPath).Should().BeEquivalentTo(newDbContent);
        File.Exists(_dbPath + ".pre-restore.bak").Should().BeTrue();
        File.ReadAllBytes(_dbPath + ".pre-restore.bak").Should().BeEquivalentTo(existingDb);
    }

    [Fact]
    public async Task RestoreAsync_DownloadFails_LeavesOriginalDbUntouched()
    {
        var existingDb = Encoding.UTF8.GetBytes("OLD-DB");
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
        var existingDb = Encoding.UTF8.GetBytes("OLD-DB");
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
