using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Services;

public sealed record RestoreResult(bool Success, string? Error);

/// <summary>
/// Phase 5a: download cloud backup, hedge with .pre-restore.bak copy of existing db,
/// then extract zip → orderdeck.db. Caller must restart app for new connections.
/// </summary>
public sealed class RestoreService
{
    public const string PreRestoreBakSuffix = ".pre-restore.bak";

    private readonly string _databaseFile;
    private readonly IBackupClient _client;
    private readonly ILogger<RestoreService> _log;

    public RestoreService(string databaseFile, IBackupClient client, ILogger<RestoreService> log)
    {
        _databaseFile = databaseFile;
        _client = client;
        _log = log;
    }

    public Task<IReadOnlyList<BackupMetadata>> ListAvailableAsync(CancellationToken ct = default) =>
        _client.ListAsync(ct);

    public async Task<RestoreResult> RestoreAsync(Guid backupId, CancellationToken ct = default)
    {
        byte[] zipBytes;
        try
        {
            zipBytes = await _client.DownloadAsync(backupId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore download failed for {BackupId}", backupId);
            return new RestoreResult(false, $"İndirme başarısız: {ex.Message}");
        }

        var bakPath = _databaseFile + PreRestoreBakSuffix;
        try
        {
            // Hedge: backup existing db before overwriting
            if (File.Exists(_databaseFile))
                File.Copy(_databaseFile, bakPath, overwrite: true);

            // Extract to temp first, then atomic move-overwrite
            var tempExtract = _databaseFile + ".restoring";
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry("orderdeck.db")
                    ?? throw new InvalidOperationException("Backup zip missing orderdeck.db entry");
                await using var src = entry.Open();
                await using var dst = File.Create(tempExtract);
                await src.CopyToAsync(dst, ct);
            }

            // Replace db
            File.Move(tempExtract, _databaseFile, overwrite: true);
            _log.LogInformation("Restore complete: {BackupId} → {Path}", backupId, _databaseFile);
            return new RestoreResult(true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed mid-way for {BackupId}", backupId);
            // Roll back from .pre-restore.bak if extract corrupted the original
            try
            {
                if (File.Exists(bakPath) && !ZipLooksValid(_databaseFile))
                    File.Copy(bakPath, _databaseFile, overwrite: true);
            }
            catch { /* best effort */ }
            return new RestoreResult(false, $"Geri yükleme hatası: {ex.Message}");
        }
    }

    private static bool ZipLooksValid(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
}
