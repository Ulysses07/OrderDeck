using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using Xunit;

namespace OrderDeck.Tests.Services;

public class RestoreRecoveryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public RestoreRecoveryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rs-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "orderdeck.db");
    }

    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    [Fact]
    public async Task StartAsync_BakFileWithValidMainDb_DeletesBak()
    {
        File.WriteAllBytes(_dbPath, new byte[2048]);
        var bakPath = _dbPath + RestoreService.PreRestoreBakSuffix;
        File.WriteAllBytes(bakPath, new byte[1000]);

        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        await sut.StartAsync(default);

        File.Exists(bakPath).Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_BakFileWithMissingMainDb_LeavesBak()
    {
        var bakPath = _dbPath + RestoreService.PreRestoreBakSuffix;
        File.WriteAllBytes(bakPath, new byte[1000]);

        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        await sut.StartAsync(default);

        File.Exists(bakPath).Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_NoBakFile_NoOp()
    {
        File.WriteAllBytes(_dbPath, new byte[2048]);
        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        Func<Task> act = () => sut.StartAsync(default);
        await act.Should().NotThrowAsync();
    }
}
