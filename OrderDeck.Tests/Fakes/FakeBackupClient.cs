using OrderDeck.Licensing.Backup;

namespace OrderDeck.Tests.Fakes;

public sealed class FakeBackupClient : IBackupClient
{
    public List<(byte[] payload, string sha, string? machine)> Uploads { get; } = new();
    public Func<byte[], string, string?, BackupMetadata>? UploadResponseFactory { get; set; }
    public Exception? UploadException { get; set; }

    public Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default)
    {
        Uploads.Add((zipPayload, sha256Hex, machineName));
        if (UploadException is not null) throw UploadException;
        var meta = UploadResponseFactory?.Invoke(zipPayload, sha256Hex, machineName)
                ?? new BackupMetadata(Guid.NewGuid(), zipPayload.Length, DateTimeOffset.UtcNow, false, machineName);
        return Task.FromResult(meta);
    }

    public Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupMetadata>>(Array.Empty<BackupMetadata>());

    public Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task DeleteAsync(Guid backupId, CancellationToken ct = default) => Task.CompletedTask;
}
