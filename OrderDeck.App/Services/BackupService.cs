using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Services;

public sealed record BackupResult(bool Success, string? Error, BackupMetadata? Metadata);

/// <summary>
/// Phase 5a: zip orderdeck.db, SHA256 it, upload via IBackupClient. Fire-and-forget for
/// stream-end trigger; awaitable RunBackupNowAsync for explicit calls (none in v1).
/// Single-flight via SemaphoreSlim(1).
/// </summary>
public sealed class BackupService
{
    private readonly string _databaseFile;
    private readonly IBackupClient _client;
    private readonly ILogger<BackupService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(string databaseFile, IBackupClient client, ILogger<BackupService> log)
    {
        _databaseFile = databaseFile;
        _client = client;
        _log = log;
    }

    /// <summary>Fire-and-forget. Returns immediately. Errors only logged. Single-flight.</summary>
    public void QueueBackup(string reason)
    {
        if (!_gate.Wait(0))
        {
            _log.LogInformation("Backup already in progress; skipping ({Reason})", reason);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await RunCoreAsync(default);
                if (!result.Success)
                    _log.LogWarning("Background backup failed ({Reason}): {Error}", reason, result.Error);
                else
                    _log.LogInformation("Background backup OK ({Reason}): {Bytes} bytes", reason, result.Metadata?.SizeBytes);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Background backup unhandled exception ({Reason})", reason);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    public async Task<BackupResult> RunBackupNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await RunCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<BackupResult> RunCoreAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_databaseFile))
                return new BackupResult(false, $"Database file not found: {_databaseFile}", null);

            var tempCopy = Path.Combine(Path.GetTempPath(), $"orderdeck-bup-{Guid.NewGuid():N}.db");
            try
            {
                File.Copy(_databaseFile, tempCopy, overwrite: true);

                byte[] zipBytes;
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var entry = archive.CreateEntry("orderdeck.db", CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await using var src = File.OpenRead(tempCopy);
                        await src.CopyToAsync(entryStream, ct);
                    }
                    zipBytes = ms.ToArray();
                }

                var sha = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
                var meta = await _client.UploadAsync(zipBytes, sha, Environment.MachineName, ct);

                return new BackupResult(true, null, meta);
            }
            finally
            {
                try { if (File.Exists(tempCopy)) File.Delete(tempCopy); }
                catch { /* swallow */ }
            }
        }
        catch (Exception ex)
        {
            return new BackupResult(false, ex.Message, null);
        }
    }
}
