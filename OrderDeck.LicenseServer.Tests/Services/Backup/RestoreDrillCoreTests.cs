using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

/// <summary>
/// Drill-core unit tests. Hand-rolls a fake encrypted blob through the
/// real <see cref="BackupStorageService"/>, then exercises every result
/// path of <see cref="RestoreDrillCore.RunAsync"/>.
///
/// We don't mock the storage service — the whole point of the drill is
/// to exercise the production decrypt path. The HostedService /
/// Hangfire wiring is tested separately in
/// <see cref="BackupRestoreDrillJobTests"/>.
/// </summary>
public class RestoreDrillCoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _testKey =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public RestoreDrillCoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "orderdeck-drill-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private BackupStorageService BuildService()
    {
        var opts = new BackupOptions
        {
            MasterKeyHex = _testKey,
            ActiveKeyVersion = 0,
            StorageRoot = _root,
        };
        return new BackupStorageService(
            Options.Create(opts),
            NullLogger<BackupStorageService>.Instance);
    }

    private async Task<string> CreateBlobAsync(BackupStorageService svc, bool includeDb = true)
    {
        // Build a tiny SQLite db, zip it, encrypt the zip → write blob.
        var dbPath = Path.Combine(_root, "fixture.db");
        if (includeDb)
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Pooling=false"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE T (Id INTEGER PRIMARY KEY, V TEXT); INSERT INTO T VALUES (1,'a')";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        var zipPath = Path.Combine(_root, "fixture.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            if (includeDb) zip.CreateEntryFromFile(dbPath, "fixture.db");
            else zip.CreateEntry("placeholder.txt"); // empty entry, valid zip but no .db
        }
        var plaintext = await File.ReadAllBytesAsync(zipPath);
        var (envelope, _) = svc.Encrypt(plaintext);

        var blobDir = Path.Combine(_root, "blobs");
        Directory.CreateDirectory(blobDir);
        var blobPath = Path.Combine(blobDir, $"fx-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(blobPath, envelope);
        return blobPath;
    }

    [Fact]
    public async Task RunAsync_with_well_formed_blob_passes_all_steps()
    {
        var svc = BuildService();
        var blob = await CreateBlobAsync(svc, includeDb: true);
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);

        var result = await RestoreDrillCore.RunAsync(svc, blob, keyVersion: 0, workdir);

        result.Passed.Should().BeTrue();
        result.Steps.Should().Contain(s => s.Name == "Decrypt" && s.Ok);
        result.Steps.Should().Contain(s => s.Name == "ZIP integrity" && s.Ok);
        result.Steps.Should().Contain(s => s.Name == "SQLite open" && s.Ok);
        result.Steps.Should().Contain(s => s.Name == "SQLite integrity_check" && s.Ok);
    }

    [Fact]
    public async Task RunAsync_with_tampered_blob_fails_at_decrypt()
    {
        var svc = BuildService();
        var blob = await CreateBlobAsync(svc);
        // Flip a byte in the ciphertext region — auth-tag verification fails.
        var bytes = await File.ReadAllBytesAsync(blob);
        bytes[bytes.Length - 1] ^= 0xFF;
        await File.WriteAllBytesAsync(blob, bytes);

        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);
        var result = await RestoreDrillCore.RunAsync(svc, blob, keyVersion: 0, workdir);

        result.Passed.Should().BeFalse();
        result.Steps.Should().Contain(s => s.Name == "Decrypt" && !s.Ok);
        // We bail at the first failure — no SQLite step recorded.
        result.Steps.Should().NotContain(s => s.Name == "SQLite open");
    }

    [Fact]
    public async Task RunAsync_with_blob_missing_db_marks_sqlite_step_unhealthy_but_does_not_fail_overall()
    {
        // Rationale: an older customer might have backups whose archive
        // shape doesn't include a .db (rare but real). Drill should
        // surface the gap without failing the overall run — failing here
        // would page someone for a non-issue.
        var svc = BuildService();
        var blob = await CreateBlobAsync(svc, includeDb: false);
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);

        var result = await RestoreDrillCore.RunAsync(svc, blob, keyVersion: 0, workdir);

        result.Passed.Should().BeTrue("decrypt + zip succeeded; missing db is informational");
        result.Steps.Should().Contain(s => s.Name == "SQLite" && !s.Ok && s.Message.Contains("No .db"));
    }

    [Fact]
    public async Task RunAsync_with_missing_blob_path_fails_at_read()
    {
        var svc = BuildService();
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);

        var result = await RestoreDrillCore.RunAsync(svc,
            blobPath: Path.Combine(_root, "does-not-exist.bin"),
            keyVersion: 0, workdir);

        result.Passed.Should().BeFalse();
        result.Steps.Should().Contain(s => s.Name == "Read blob" && !s.Ok);
    }

    [Fact]
    public void FindLatestBlob_returns_newest_across_subdirs()
    {
        var storageRoot = Path.Combine(_root, "store");
        Directory.CreateDirectory(Path.Combine(storageRoot, "cust-A"));
        Directory.CreateDirectory(Path.Combine(storageRoot, "cust-B"));

        var older = Path.Combine(storageRoot, "cust-A", "old.bin");
        var newer = Path.Combine(storageRoot, "cust-B", "new.bin");
        File.WriteAllBytes(older, new byte[] { 1 });
        File.WriteAllBytes(newer, new byte[] { 2 });
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        RestoreDrillCore.FindLatestBlob(storageRoot).Should().Be(newer);
    }

    [Fact]
    public void FindLatestBlob_returns_null_for_missing_root()
    {
        RestoreDrillCore.FindLatestBlob(
            Path.Combine(_root, "never-created")).Should().BeNull();
    }

    [Fact]
    public void FindLatestBlob_returns_null_for_empty_root()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        RestoreDrillCore.FindLatestBlob(empty).Should().BeNull();
    }

    [Fact]
    public async Task DrillResult_ToReport_emits_pass_marker_for_passing_drill()
    {
        var svc = BuildService();
        var blob = await CreateBlobAsync(svc);
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);

        var result = await RestoreDrillCore.RunAsync(svc, blob, keyVersion: 0, workdir);
        var report = result.ToReport();

        report.Should().Contain("RESTORE DRILL PASSED");
        report.Should().Contain("[OK] Decrypt");
        report.Should().Contain($"Blob: {blob}");
    }
}
