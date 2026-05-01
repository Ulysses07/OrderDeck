using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.Services;

public class BackupServiceTests : IDisposable
{
    private readonly string _tempDb;

    public BackupServiceTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"orderdeck-bs-test-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(_tempDb, Encoding.UTF8.GetBytes("fake sqlite content for test purposes"));
    }

    public void Dispose()
    {
        if (File.Exists(_tempDb)) File.Delete(_tempDb);
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
