using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a: AES-256-GCM encrypt/decrypt + filesystem read/write for customer DB backups.
/// Phase 5b: per-blob key versioning so the master key can rotate without
/// breaking historical backups.
///
/// Two envelope formats coexist:
///   v0 (legacy): [12B nonce][16B auth tag][ciphertext]
///   v1+ (Phase 5b): [1B keyVersion][12B nonce][16B auth tag][ciphertext]
/// The DB column CustomerBackup.KeyVersion (default 0) tells us which to use.
/// </summary>
public sealed class BackupStorageService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly Dictionary<int, byte[]> _keys;
    private readonly int _activeVersion;
    private readonly BackupOptions _opt;
    private readonly ILogger<BackupStorageService> _log;

    public BackupStorageService(IOptions<BackupOptions> opt, ILogger<BackupStorageService> log)
    {
        _opt = opt.Value;
        _log = log;

        _keys = BuildKeyRing(_opt);
        _activeVersion = _opt.ActiveKeyVersion;

        if (_keys.Count == 0)
            throw new InvalidOperationException(
                "No backup master keys configured. Set Backup:MasterKeyHex (legacy) or Backup:MasterKeys:0=...");
        if (!_keys.ContainsKey(_activeVersion))
            throw new InvalidOperationException(
                $"Backup:ActiveKeyVersion={_activeVersion} but no key configured at that index.");

        Directory.CreateDirectory(_opt.StorageRoot);
        _log.LogInformation("[BackupStorage] key ring loaded: versions=[{Versions}], active={Active}",
            string.Join(",", _keys.Keys.OrderBy(v => v)), _activeVersion);
    }

    /// <summary>Active key version applied to new uploads. Persisted to
    /// CustomerBackup.KeyVersion so the matching key is selectable on read.</summary>
    public int ActiveKeyVersion => _activeVersion;

    /// <summary>Encrypts plaintext with the active key. Returns the envelope bytes
    /// AND the version that was used so the caller persists it alongside the row.</summary>
    public (byte[] envelope, int keyVersion) Encrypt(byte[] plaintext)
    {
        var version = _activeVersion;
        var key = _keys[version];

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        if (version == 0)
        {
            // Legacy v0 path: bytewise-identical to pre-Phase-5b output. This lets
            // a deployment opt into the new code without rewriting any blobs and
            // without needing a flag day — until ActiveKeyVersion bumps to >=1,
            // every new blob is still v0.
            var v0 = new byte[NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, v0, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, v0, NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, v0, NonceSize + TagSize, ciphertext.Length);
            return (v0, 0);
        }

        // v1+ envelope: prepend the version byte.
        var output = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        output[0] = (byte)version;
        Buffer.BlockCopy(nonce, 0, output, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, output, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, 1 + NonceSize + TagSize, ciphertext.Length);
        return (output, version);
    }

    /// <summary>Decrypts an envelope using the version stored on the row. v0 has
    /// no version byte on disk; v1+ does. Caller MUST pass the row's KeyVersion.</summary>
    public byte[] Decrypt(byte[] envelope, int keyVersion)
    {
        if (!_keys.TryGetValue(keyVersion, out var key))
        {
            throw new InvalidOperationException(
                $"No master key configured for version {keyVersion}. " +
                $"Add it to Backup:MasterKeys:{keyVersion} or restore from a deployment that has it.");
        }

        int headerSize;
        if (keyVersion == 0)
        {
            // v0: no version byte on disk. Whole envelope is [nonce][tag][ct].
            headerSize = 0;
        }
        else
        {
            // v1+: first byte is the version. Sanity-check it matches what the
            // caller said — a mismatch means the DB row and the blob disagree
            // (DB tampered with, or wrong file resurrected from backup).
            if (envelope.Length < 1 + NonceSize + TagSize)
                throw new ArgumentException("Envelope too short for v1+ header.", nameof(envelope));
            if (envelope[0] != keyVersion)
                throw new InvalidOperationException(
                    $"Envelope key-version byte {envelope[0]} does not match DB row keyVersion {keyVersion}.");
            headerSize = 1;
        }

        if (envelope.Length < headerSize + NonceSize + TagSize)
            throw new ArgumentException("Envelope too short to contain nonce + tag.", nameof(envelope));

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[envelope.Length - headerSize - NonceSize - TagSize];
        Buffer.BlockCopy(envelope, headerSize, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, headerSize + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(envelope, headerSize + NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
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

    /// <summary>Defense-in-depth against path traversal: BlobPath comes from the database, but if
    /// a row is ever tampered with (DB injection, restore from compromised dump, etc.) we
    /// must not let an attacker-controlled path escape the configured storage root.
    /// Throws UnauthorizedAccessException on any escape attempt.</summary>
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

    private static Dictionary<int, byte[]> BuildKeyRing(BackupOptions opt)
    {
        var ring = new Dictionary<int, byte[]>();

        // Legacy single-key config — treated as v0 if no v0 was set explicitly.
        // Allows running new code against pre-Phase-5b .env files without changes.
        if (!string.IsNullOrWhiteSpace(opt.MasterKeyHex))
        {
            ValidateHex(opt.MasterKeyHex, "MasterKeyHex");
            ring[0] = Convert.FromHexString(opt.MasterKeyHex);
        }

        if (opt.MasterKeys is not null)
        {
            foreach (var (version, hex) in opt.MasterKeys)
            {
                if (string.IsNullOrWhiteSpace(hex)) continue;
                ValidateHex(hex, $"MasterKeys[{version}]");
                ring[version] = Convert.FromHexString(hex);  // overrides legacy if both set at v0
            }
        }
        return ring;
    }

    private static void ValidateHex(string hex, string label)
    {
        if (hex.Length != 64)
            throw new InvalidOperationException(
                $"{label} must be exactly 64 hex chars (32 bytes); got {hex.Length}.");
    }
}
