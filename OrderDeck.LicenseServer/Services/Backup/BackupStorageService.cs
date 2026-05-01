using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a: AES-256-GCM encrypt/decrypt + filesystem read/write for customer DB backups.
/// Format: [12B nonce][16B auth tag][ciphertext bytes].
/// Master key is 32 bytes (64 hex chars), supplied via Backup:MasterKeyHex config.
/// </summary>
public sealed class BackupStorageService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;
    private readonly BackupOptions _opt;
    private readonly ILogger<BackupStorageService> _log;

    public BackupStorageService(IOptions<BackupOptions> opt, ILogger<BackupStorageService> log)
    {
        _opt = opt.Value;
        _log = log;
        if (string.IsNullOrWhiteSpace(_opt.MasterKeyHex) || _opt.MasterKeyHex.Length != 64)
            throw new InvalidOperationException(
                "Backup:MasterKeyHex must be exactly 64 hex chars (32 bytes). Set via env var BACKUP_MASTER_KEY.");
        _key = Convert.FromHexString(_opt.MasterKeyHex);
        Directory.CreateDirectory(_opt.StorageRoot);
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return output;
    }

    public byte[] Decrypt(byte[] envelope)
    {
        if (envelope.Length < NonceSize + TagSize)
            throw new ArgumentException("Envelope too short to contain nonce + tag.", nameof(envelope));

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[envelope.Length - NonceSize - TagSize];
        Buffer.BlockCopy(envelope, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(envelope, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public async Task<string> WriteBlobAsync(Guid customerId, byte[] bytes, CancellationToken ct = default)
    {
        var customerDir = Path.Combine(_opt.StorageRoot, customerId.ToString());
        Directory.CreateDirectory(customerDir);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bin";
        var fullPath = Path.Combine(customerDir, fileName);
        await File.WriteAllBytesAsync(fullPath, bytes, ct);
        return fullPath;
    }

    public async Task<byte[]> ReadBlobAsync(string blobPath, CancellationToken ct = default)
    {
        EnsurePathInsideStorageRoot(blobPath);
        return await File.ReadAllBytesAsync(blobPath, ct);
    }

    public void DeleteBlob(string blobPath)
    {
        try
        {
            EnsurePathInsideStorageRoot(blobPath);
            if (File.Exists(blobPath)) File.Delete(blobPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete backup blob {Path}", blobPath);
        }
    }

    /// <summary>
    /// Defense-in-depth against path traversal: BlobPath comes from the database, but if
    /// a row is ever tampered with (DB injection, restore from compromised dump, etc.) we
    /// must not let an attacker-controlled path escape the configured storage root.
    /// Throws UnauthorizedAccessException on any escape attempt.
    /// </summary>
    private void EnsurePathInsideStorageRoot(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
            throw new ArgumentException("BlobPath must not be empty.", nameof(blobPath));

        var rootFull = Path.GetFullPath(_opt.StorageRoot);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        var blobFull = Path.GetFullPath(blobPath);
        if (!blobFull.StartsWith(rootFull, StringComparison.Ordinal))
        {
            _log.LogError("Blob path traversal attempt blocked: {Blob} not under {Root}", blobFull, rootFull);
            throw new UnauthorizedAccessException("Blob path is outside the configured storage root.");
        }
    }
}
