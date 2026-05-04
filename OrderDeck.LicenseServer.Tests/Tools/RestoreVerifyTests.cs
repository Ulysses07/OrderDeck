using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// Round-trip drill: hand-craft a fake backup blob the same way the running
/// server would (env-driven key + AES-GCM envelope + zip with a SQLite db
/// inside), invoke <see cref="RestoreVerify.RunAsync"/>, and assert exit
/// code + structured-output expectations.
///
/// We don't mock <see cref="global::OrderDeck.LicenseServer.Services.Backup.BackupStorageService"/> — the
/// whole point of the drill is to prove the same code path the production
/// host runs. We DO override env vars locally so the test is hermetic.
/// </summary>
public class RestoreVerifyTests : IDisposable
{
    private readonly string _workdir;
    private readonly string _testKey =
        // 32 bytes = 64 hex. Deterministic so a failure is reproducible.
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public RestoreVerifyTests()
    {
        _workdir = Path.Combine(Path.GetTempPath(),
            "orderdeck-restore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workdir);

        // The production host reads Backup__* env vars; mirror that here so
        // BackupStorageService finds the same key at version 0 in the drill
        // and in this test.
        Environment.SetEnvironmentVariable("Backup__MasterKeyHex", _testKey);
        Environment.SetEnvironmentVariable("Backup__ActiveKeyVersion", "0");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workdir)) Directory.Delete(_workdir, recursive: true); }
        catch { /* best effort */ }
        Environment.SetEnvironmentVariable("Backup__MasterKeyHex", null);
        Environment.SetEnvironmentVariable("Backup__ActiveKeyVersion", null);
    }

    [Fact]
    public async Task Run_against_round_tripped_blob_returns_zero()
    {
        var blobPath = await CreateFakeBackupBlobAsync(includeDb: true);
        var drillWorkdir = Path.Combine(_workdir, "drill");

        var rc = await RestoreVerify.RunAsync(new[]
        {
            "restore-verify", blobPath, "0", $"--workdir={drillWorkdir}"
        });

        rc.Should().Be(0, "the synthetic blob is well-formed and the key matches");
        // Workdir is wiped by the drill on success.
        Directory.Exists(drillWorkdir).Should().BeFalse();
    }

    [Fact]
    public async Task Run_with_tampered_blob_returns_nonzero()
    {
        var blobPath = await CreateFakeBackupBlobAsync(includeDb: true);
        // Flip a byte in the ciphertext region — auth tag verification fails.
        var bytes = await File.ReadAllBytesAsync(blobPath);
        bytes[bytes.Length - 1] ^= 0xFF;
        await File.WriteAllBytesAsync(blobPath, bytes);

        var rc = await RestoreVerify.RunAsync(new[]
        {
            "restore-verify", blobPath, "0", $"--workdir={Path.Combine(_workdir, "drill")}"
        });

        rc.Should().NotBe(0, "auth tag mismatch on a tampered blob must fail the drill");
    }

    [Fact]
    public async Task Run_with_missing_blob_returns_nonzero()
    {
        var rc = await RestoreVerify.RunAsync(new[]
        {
            "restore-verify", Path.Combine(_workdir, "does-not-exist.bin"),
            "0", $"--workdir={Path.Combine(_workdir, "drill")}"
        });

        rc.Should().NotBe(0);
    }

    [Fact]
    public async Task Run_with_too_few_args_prints_usage_and_returns_nonzero()
    {
        var rc = await RestoreVerify.RunAsync(new[] { "restore-verify" });
        rc.Should().NotBe(0);
    }

    /// <summary>
    /// Builds a synthetic encrypted backup that mirrors the production
    /// pipeline: a tiny SQLite db gets zipped, then encrypted with the
    /// same <see cref="BackupStorageService"/> the drill will use.
    /// </summary>
    private async Task<string> CreateFakeBackupBlobAsync(bool includeDb)
    {
        var blobsDir = Path.Combine(_workdir, "blobs");
        Directory.CreateDirectory(blobsDir);

        // 1. Tiny SQLite db with one table + one row, so the integrity check
        //    has something to look at.
        var dbPath = Path.Combine(_workdir, "fixture.db");
        if (includeDb)
        {
            // Pooling=false so we don't keep an OS handle on dbPath after
            // the using block — otherwise the subsequent CreateEntryFromFile
            // call below races against SQLite's pooled connection holding
            // a write lock on Windows (the "another process" IOException).
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Pooling=false"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE Customer (Id INTEGER PRIMARY KEY, Email TEXT)";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO Customer (Id, Email) VALUES (1, 'test@example.com')";
                    cmd.ExecuteNonQuery();
                }
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // 2. Zip it.
        var zipPath = Path.Combine(_workdir, "fixture.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            if (includeDb) zip.CreateEntryFromFile(dbPath, "fixture.db");
        }
        var plaintext = await File.ReadAllBytesAsync(zipPath);

        // 3. Encrypt via the same service the drill uses.
        var opts = new global::OrderDeck.LicenseServer.Services.Backup.BackupOptions
        {
            MasterKeyHex = _testKey,
            ActiveKeyVersion = 0,
            StorageRoot = blobsDir,
        };
        var svc = new global::OrderDeck.LicenseServer.Services.Backup.BackupStorageService(
            Microsoft.Extensions.Options.Options.Create(opts),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<global::OrderDeck.LicenseServer.Services.Backup.BackupStorageService>.Instance);
        var (envelope, _) = svc.Encrypt(plaintext);

        var blobPath = Path.Combine(blobsDir, "fixture.bin");
        await File.WriteAllBytesAsync(blobPath, envelope);
        return blobPath;
    }
}
