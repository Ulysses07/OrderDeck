using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a admin viewer: opens a CustomerBackup blob into a disposable BackupSession.
/// 1. Loads encrypted blob from disk
/// 2. Decrypts via BackupStorageService
/// 3. Extracts ZIP to /tmp/{guid}/orderdeck.db
/// 4. Opens read-only SqliteConnection
/// 5. Returns BackupSession (caller `using`)
/// </summary>
public sealed class BackupViewerService
{
    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly ILogger<BackupViewerService> _log;

    public BackupViewerService(LicenseDbContext db, BackupStorageService storage, ILogger<BackupViewerService> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task<BackupSession> OpenAsync(Guid backupId, CancellationToken ct = default)
    {
        var b = await _db.CustomerBackups.FirstOrDefaultAsync(x => x.Id == backupId, ct)
            ?? throw new InvalidOperationException($"Backup {backupId} not found");

        var encrypted = await _storage.ReadBlobAsync(b.BlobPath, ct);
        var zipBytes = _storage.Decrypt(encrypted);

        var tempDir = Path.Combine(Path.GetTempPath(), $"orderdeck-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var dbEntry = archive.GetEntry("orderdeck.db")
                    ?? throw new InvalidOperationException("Backup zip missing orderdeck.db entry");
                var dbPath = Path.Combine(tempDir, "orderdeck.db");
                using var dest = File.Create(dbPath);
                using var src = dbEntry.Open();
                await src.CopyToAsync(dest, ct);
            }

            var dbFile = Path.Combine(tempDir, "orderdeck.db");
            var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            return new BackupSession(tempDir, conn, _log);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            throw;
        }
    }
}
