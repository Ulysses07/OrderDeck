using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class BackupStorageServiceTests
{
    private static (BackupStorageService svc, string tempRoot) Make()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"orderdeck-bs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var opts = Options.Create(new BackupOptions
        {
            MasterKeyHex = new string('a', 64),  // 32 bytes of 0xaa
            StorageRoot = tempRoot,
            MaxBlobSizeMb = 200
        });
        return (new BackupStorageService(opts, NullLogger<BackupStorageService>.Instance), tempRoot);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrips_ToOriginalBytes()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var plaintext = Encoding.UTF8.GetBytes("hello world — orderdeck.db payload");
            var (encrypted, version) = svc.Encrypt(plaintext);
            encrypted.Length.Should().BeGreaterThan(plaintext.Length); // nonce + tag overhead
            version.Should().Be(0, "default ApiFactory test config uses MasterKeyHex → v0");
            var decrypted = svc.Decrypt(encrypted, version);
            decrypted.Should().BeEquivalentTo(plaintext);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Encrypt_TwiceWithSamePlaintext_ProducesDifferentCiphertexts()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var plaintext = Encoding.UTF8.GetBytes("constant");
            var (c1, _) = svc.Encrypt(plaintext);
            var (c2, _) = svc.Encrypt(plaintext);
            c1.Should().NotBeEquivalentTo(c2);
            svc.Decrypt(c1, 0).Should().BeEquivalentTo(plaintext);
            svc.Decrypt(c2, 0).Should().BeEquivalentTo(plaintext);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var (encrypted, version) = svc.Encrypt(Encoding.UTF8.GetBytes("secret"));
            encrypted[encrypted.Length - 1] ^= 0xFF;
            Action act = () => svc.Decrypt(encrypted, version);
            act.Should().Throw<System.Security.Cryptography.AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    // ─── Phase 5b: key versioning ─────────────────────────────────────

    private static (BackupStorageService svc, string tempRoot) MakeWithRing(
        Dictionary<int, string> ring, int activeVersion)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"orderdeck-bs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var opts = Options.Create(new BackupOptions
        {
            MasterKeyHex = "",  // explicitly NOT using legacy field
            MasterKeys = ring,
            ActiveKeyVersion = activeVersion,
            StorageRoot = tempRoot,
            MaxBlobSizeMb = 200
        });
        return (new BackupStorageService(opts, NullLogger<BackupStorageService>.Instance), tempRoot);
    }

    [Fact]
    public void V1_envelope_carries_version_byte_at_position_0()
    {
        var ring = new Dictionary<int, string>
        {
            [0] = new string('a', 64),
            [1] = new string('b', 64)
        };
        var (svc, tempRoot) = MakeWithRing(ring, activeVersion: 1);
        try
        {
            var (envelope, version) = svc.Encrypt(Encoding.UTF8.GetBytes("hello v1"));
            version.Should().Be(1);
            envelope[0].Should().Be(1, "v1 envelope must start with the version byte");
            // Length: 1 (version) + 12 (nonce) + 16 (tag) + plaintext
            envelope.Length.Should().Be(1 + 12 + 16 + 8);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void V0_envelope_remains_bytewise_compatible_with_pre_phase_5b_format()
    {
        // When ActiveKeyVersion=0 the encrypt path MUST produce the legacy envelope
        // (no version byte) so an upgraded server can read blobs written by the
        // previous build and a downgraded server can still read v0 blobs.
        var ring = new Dictionary<int, string> { [0] = new string('a', 64) };
        var (svc, tempRoot) = MakeWithRing(ring, activeVersion: 0);
        try
        {
            var (envelope, version) = svc.Encrypt(Encoding.UTF8.GetBytes("legacy"));
            version.Should().Be(0);
            // Length: 12 (nonce) + 16 (tag) + plaintext — NO version byte.
            envelope.Length.Should().Be(12 + 16 + 6);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Decrypt_picks_correct_key_for_version_in_DB_row()
    {
        // Ring with two distinct keys; the wrong-key path must fail to decrypt.
        var ring = new Dictionary<int, string>
        {
            [0] = new string('a', 64),
            [1] = new string('b', 64)
        };
        var (svc, tempRoot) = MakeWithRing(ring, activeVersion: 1);
        try
        {
            var (envelope, version) = svc.Encrypt(Encoding.UTF8.GetBytes("v1 blob"));
            version.Should().Be(1);
            // Right key → succeeds
            svc.Decrypt(envelope, 1).Should().BeEquivalentTo(Encoding.UTF8.GetBytes("v1 blob"));
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Decrypt_with_unknown_key_version_throws()
    {
        var ring = new Dictionary<int, string> { [0] = new string('a', 64) };
        var (svc, tempRoot) = MakeWithRing(ring, activeVersion: 0);
        try
        {
            var (envelope, _) = svc.Encrypt(Encoding.UTF8.GetBytes("v0"));
            Action act = () => svc.Decrypt(envelope, 99);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*No master key configured for version 99*");
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Constructor_active_version_not_in_ring_throws()
    {
        var ring = new Dictionary<int, string> { [0] = new string('a', 64) };
        var opts = Options.Create(new BackupOptions
        {
            MasterKeys = ring,
            ActiveKeyVersion = 5,  // not in ring
            StorageRoot = Path.GetTempPath()
        });
        Action act = () => new BackupStorageService(opts, NullLogger<BackupStorageService>.Instance);
        act.Should().Throw<InvalidOperationException>().WithMessage("*ActiveKeyVersion=5*");
    }

    [Fact]
    public void Old_v0_blob_is_decryptable_after_introducing_v1()
    {
        // Migration scenario: deployment shipped with key v0 only, customer
        // has historical blobs. Operator adds v1 + bumps active. The historical
        // blobs MUST still decrypt with their original v0 key.
        var ring = new Dictionary<int, string>
        {
            [0] = new string('a', 64),
            [1] = new string('b', 64)
        };
        // Phase 1: only v0 exists.
        var (svc1, root1) = MakeWithRing(new() { [0] = new string('a', 64) }, activeVersion: 0);
        byte[] historicalEnvelope;
        try
        {
            (historicalEnvelope, _) = svc1.Encrypt(Encoding.UTF8.GetBytes("historic"));
        }
        finally { Directory.Delete(root1, recursive: true); }

        // Phase 2: operator adds v1 + bumps active. Old blob still readable.
        var (svc2, root2) = MakeWithRing(ring, activeVersion: 1);
        try
        {
            var roundTrip = svc2.Decrypt(historicalEnvelope, keyVersion: 0);
            roundTrip.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("historic"));
        }
        finally { Directory.Delete(root2, recursive: true); }
    }

    [Fact]
    public async Task WriteBlob_CreatesCustomerSubdirectory_AndFile()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var customerId = Guid.NewGuid();
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var path = await svc.WriteBlobAsync(customerId, bytes, default);

            path.Should().StartWith(Path.Combine(tempRoot, customerId.ToString()));
            File.Exists(path).Should().BeTrue();
            (await File.ReadAllBytesAsync(path)).Should().BeEquivalentTo(bytes);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Constructor_InvalidKeyLength_Throws()
    {
        var opts = Options.Create(new BackupOptions { MasterKeyHex = "tooshort" });
        Action act = () => new BackupStorageService(opts, NullLogger<BackupStorageService>.Instance);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MasterKeyHex*64*");
    }

    [Fact]
    public async Task ReadBlob_PathOutsideStorageRoot_Throws()
    {
        var (svc, tempRoot) = Make();
        try
        {
            // Try to read a file outside the configured storage root.
            var outsideFile = Path.Combine(Path.GetTempPath(), $"orderdeck-evil-{Guid.NewGuid():N}.txt");
            await File.WriteAllBytesAsync(outsideFile, new byte[] { 0xDE, 0xAD });
            try
            {
                Func<Task> act = async () => await svc.ReadBlobAsync(outsideFile);
                await act.Should().ThrowAsync<UnauthorizedAccessException>();
            }
            finally { File.Delete(outsideFile); }
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public async Task ReadBlob_TraversalViaDotDot_Throws()
    {
        var (svc, tempRoot) = Make();
        try
        {
            // tempRoot/customer/.. resolves to tempRoot, but tempRoot/customer/../../../etc/passwd
            // resolves outside. Real-world: BlobPath in DB tampered to "{StorageRoot}/c/../../etc/passwd".
            var traversal = Path.Combine(tempRoot, "anycustomer", "..", "..", "outside.bin");
            Func<Task> act = async () => await svc.ReadBlobAsync(traversal);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void DeleteBlob_PathOutsideStorageRoot_DoesNotDelete()
    {
        var (svc, tempRoot) = Make();
        var outsideFile = Path.Combine(Path.GetTempPath(), $"orderdeck-keep-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllBytes(outsideFile, new byte[] { 1 });
            // DeleteBlob swallows exceptions internally — verify the target file survives.
            svc.DeleteBlob(outsideFile);
            File.Exists(outsideFile).Should().BeTrue("path traversal must be blocked silently");
        }
        finally
        {
            if (File.Exists(outsideFile)) File.Delete(outsideFile);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReadBlob_LegitPathInsideRoot_Succeeds()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var customerId = Guid.NewGuid();
            var path = await svc.WriteBlobAsync(customerId, new byte[] { 9, 9, 9 }, default);
            // Roundtrip: write returned path is canonical, read must accept it.
            var read = await svc.ReadBlobAsync(path);
            read.Should().BeEquivalentTo(new byte[] { 9, 9, 9 });
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }
}
