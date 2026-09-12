using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.Shared.Backup;
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

    /// <summary>
    /// Gerçek bir yedek arşivi üretir. <paramref name="entryName"/> ve
    /// <paramref name="orderDeckSchema"/> ayrı parametreler çünkü R4-06'nın
    /// ayırdığı iki kusur da bunlar: yanlış ADLA konulmuş dosya ve doğru adla
    /// konulmuş YABANCI şema.
    /// </summary>
    private async Task<string> CreateBlobAsync(
        BackupStorageService svc,
        bool includeDb = true,
        string entryName = BackupArchive.DatabaseEntryName,
        bool orderDeckSchema = true)
    {
        // Build a SQLite db, zip it, encrypt the zip → write blob.
        var dbPath = Path.Combine(_root, "fixture.db");
        if (includeDb)
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Pooling=false"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = orderDeckSchema
                    ? @"CREATE TABLE _meta (Id INTEGER PRIMARY KEY CHECK (Id = 1),
                                            SchemaVersion INTEGER NOT NULL);
                        INSERT INTO _meta (Id, SchemaVersion) VALUES (1, 35);
                        CREATE TABLE Customer (Id TEXT PRIMARY KEY, Username TEXT);
                        CREATE TABLE StreamSession (Id TEXT PRIMARY KEY, Title TEXT);
                        CREATE TABLE Label (Id TEXT PRIMARY KEY, Price NUMERIC);
                        INSERT INTO Customer VALUES ('c1','alice');"
                    : "CREATE TABLE T (Id INTEGER PRIMARY KEY, V TEXT); INSERT INTO T VALUES (1,'a')";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        var zipPath = Path.Combine(_root, "fixture.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            if (includeDb) zip.CreateEntryFromFile(dbPath, entryName);
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
        result.Steps.Should().Contain(s => s.Name == "OrderDeck schema" && s.Ok);
    }

    /// <summary>
    /// R4-06 sınır deneyi #1: sağlam ama BAŞKA ADLA konulmuş veritabanı.
    /// Masaüstü geri yüklemesi arşivin kökündeki <c>orderdeck.db</c> girdisini
    /// arıyor; bulamazsa çöküyor. Drill "ilk *.db"ye baktığı sürece bu yedeği
    /// yeşil raporluyordu — yani gerçekte geri yüklenemeyen bir yedeğe
    /// "tatbikat başarılı" deniyordu.
    /// </summary>
    [Fact]
    public async Task RunAsync_with_db_under_wrong_entry_name_fails()
    {
        var svc = BuildService();
        var blob = await CreateBlobAsync(svc, entryName: "foreign.db");
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);

        var result = await RestoreDrillCore.RunAsync(svc, blob, keyVersion: 0, workdir);

        result.Passed.Should().BeFalse(
            "masaüstü kökteki orderdeck.db girdisini arıyor; başka bir sağlam dosya yetmez");
        result.Steps.Should().Contain(s =>
            s.Name == "SQLite" && !s.Ok && s.Message.Contains("orderdeck.db"));
    }

    /// <summary>
    /// R4-06 sınır deneyi #2: doğru adla konulmuş ama YABANCI şema. Bu, daha
    /// tehlikeli olanı: <c>PRAGMA integrity_check</c> "ok" diyor, masaüstü de
    /// eskiden geri yüklemeyi BAŞARILI sayıp aktif veritabanının (Customer
    /// tablosu dahil) yerine bunu koyuyordu.
    /// </summary>
    [Fact]
    public async Task RunAsync_with_foreign_schema_fails_even_when_sqlite_is_sound()
    {
        var svc = BuildService();
        var blob = await CreateBlobAsync(svc, orderDeckSchema: false);
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);

        var result = await RestoreDrillCore.RunAsync(svc, blob, keyVersion: 0, workdir);

        result.Passed.Should().BeFalse("sağlam SQLite, geçerli OrderDeck yedeği demek değil");
        result.Steps.Should().Contain(s => s.Name == "SQLite integrity_check" && s.Ok);
        result.Steps.Should().Contain(s => s.Name == "OrderDeck schema" && !s.Ok);
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
    public async Task RunAsync_with_blob_missing_db_fails_overall()
    {
        // R3-05: .db içermeyen arşiv masaüstü RestoreService tarafından
        // REDDEDİLİYOR — yani bu yedek gerçekte geri YÜKLENEMEZ. Drill'in
        // amacı tam da bunu yakalamak; "informational" diye yeşil dönmek
        // alarmı susturup sahte güven veriyordu (drill PASSED, restore fail).
        var svc = BuildService();
        var blob = await CreateBlobAsync(svc, includeDb: false);
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);

        var result = await RestoreDrillCore.RunAsync(svc, blob, keyVersion: 0, workdir);

        result.Passed.Should().BeFalse("masaüstü bu arşivi restore edemez; drill de geçmemeli");
        result.Steps.Should().Contain(s =>
            s.Name == "SQLite" && !s.Ok && s.Message.Contains("orderdeck.db"));
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
    public async Task RunAsync_rejects_blob_outside_storage_root()
    {
        var svc = BuildService(); // StorageRoot = _root
        // Kök DIŞINDA, gerçekten var olan bir dosya.
        var outsideDir = Path.Combine(Path.GetTempPath(),
            "orderdeck-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideBlob = Path.Combine(outsideDir, "evil.bin");
        await File.WriteAllBytesAsync(outsideBlob, new byte[] { 1, 2, 3 });
        var workdir = Path.Combine(_root, "drill");
        Directory.CreateDirectory(workdir);
        try
        {
            var result = await RestoreDrillCore.RunAsync(svc, outsideBlob, keyVersion: 0, workdir);

            result.Passed.Should().BeFalse();
            result.Steps.Should().Contain(s => s.Name == "Read blob" && !s.Ok);
            // Kök-dışı yol decrypt'e ULAŞMADAN reddedilmeli.
            result.Steps.Should().NotContain(s => s.Name == "Decrypt");
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { /* best effort */ }
        }
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
