namespace OrderDeck.Licensing.Backup;

public interface IBackupClient
{
    Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default);
    Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default);
    Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default);
    Task DeleteAsync(Guid backupId, CancellationToken ct = default);
}
