namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Off-host backup replication. Implementations forward an encrypted blob
/// (already produced by <see cref="BackupStorageService"/>) to durable storage
/// outside the VPS, so a host loss doesn't take all customer backups with it.
///
/// Disabled-by-default: when <see cref="S3BackupOptions.Enabled"/> is false the
/// no-op implementation is registered. Enable by configuring Backup:S3:* env vars.
/// </summary>
public interface IS3BackupSink
{
    bool IsEnabled { get; }

    /// <summary>Upload the (already-encrypted) blob to remote storage. The
    /// remote object key is derived from the local path so a side-by-side
    /// listing matches the on-disk layout.</summary>
    /// <returns>true if upload succeeded (or sink is disabled — no-op success).</returns>
    Task<bool> UploadAsync(string localPath, Guid customerId, CancellationToken ct = default);

    /// <summary>Mirror a deletion. Best-effort — backup retention runs
    /// independently locally and remotely.</summary>
    Task DeleteAsync(string remoteKey, CancellationToken ct = default);
}

public sealed class NoOpS3BackupSink : IS3BackupSink
{
    public bool IsEnabled => false;
    public Task<bool> UploadAsync(string localPath, Guid customerId, CancellationToken ct = default) => Task.FromResult(true);
    public Task DeleteAsync(string remoteKey, CancellationToken ct = default) => Task.CompletedTask;
}
