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
            var encrypted = svc.Encrypt(plaintext);
            encrypted.Length.Should().BeGreaterThan(plaintext.Length); // nonce + tag overhead
            var decrypted = svc.Decrypt(encrypted);
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
            var c1 = svc.Encrypt(plaintext);
            var c2 = svc.Encrypt(plaintext);
            c1.Should().NotBeEquivalentTo(c2);
            svc.Decrypt(c1).Should().BeEquivalentTo(plaintext);
            svc.Decrypt(c2).Should().BeEquivalentTo(plaintext);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var encrypted = svc.Encrypt(Encoding.UTF8.GetBytes("secret"));
            encrypted[encrypted.Length - 1] ^= 0xFF;
            Action act = () => svc.Decrypt(encrypted);
            act.Should().Throw<System.Security.Cryptography.AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
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
