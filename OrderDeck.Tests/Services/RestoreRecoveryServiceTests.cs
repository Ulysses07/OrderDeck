using FluentAssertions;
using Microsoft.Data.Sqlite;
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

    /// <summary>Yerinde gerçek, açılabilir bir veritabanı oluşturur.</summary>
    private void CreateRealDatabase(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE marker (value TEXT);";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task StartAsync_BakFileWithValidMainDb_DeletesBak()
    {
        CreateRealDatabase(_dbPath);
        var bakPath = _dbPath + RestoreService.PreRestoreBakSuffix;
        File.WriteAllBytes(bakPath, new byte[1000]);

        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        await sut.StartAsync(default);

        File.Exists(bakPath).Should().BeFalse();
    }

    /// <summary>
    /// Y-08'in üçüncü maddesi: eski ölçüt "1 KB'den büyük" idi, yani yarım
    /// yazılmış büyük bir dosya son kurtarma kopyasını sildiriyordu. Artık
    /// veritabanı açılıp doğrulanamıyorsa yedek KORUNUR.
    /// </summary>
    [Fact]
    public async Task StartAsync_MainDbLargeButNotSqlite_KeepsBak()
    {
        File.WriteAllBytes(_dbPath, new byte[64 * 1024]);
        var bakPath = _dbPath + RestoreService.PreRestoreBakSuffix;
        File.WriteAllBytes(bakPath, new byte[1000]);

        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        await sut.StartAsync(default);

        File.Exists(bakPath).Should().BeTrue("doğrulanmamış veritabanı son kurtarma kopyasını sildirmemeli");
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
        CreateRealDatabase(_dbPath);
        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        Func<Task> act = () => sut.StartAsync(default);
        await act.Should().NotThrowAsync();
    }
}
