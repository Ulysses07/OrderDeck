using System.Buffers.Binary;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

/// <summary>
/// Biçim 2 (parçalı akış / STREAM) zarfı.
///
/// <para>Bu testlerin çoğu "çözülüyor mu" değil <b>"bozulmuş bir zarf REDDEDİLİYOR
/// mu"</b> sorusunu soruyor. Parçalı AEAD'de asıl tehlike bu: her parça tek tek
/// geçerli olduğu hâlde bütün yanlış olabilir (sıra değişmiş, son parça
/// kesilmiş). Yalnız mutlu yolu test etmek, bu tasarımın var oluş sebebini
/// test etmemek demek.</para>
/// </summary>
public class BackupChunkedEnvelopeTests
{
    private const int ChunkSize = 1 << 20;   // BackupStorageService.ChunkSize ile aynı
    private const int HeaderSize = 4 + 1 + 4 + 16;
    private const int TagSize = 16;

    private static (BackupStorageService svc, string root) Make(
        int activeVersion = 0, Dictionary<int, string>? ring = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"orderdeck-chunk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var opts = Options.Create(new BackupOptions
        {
            MasterKeyHex = ring is null ? new string('a', 64) : "",
            MasterKeys = ring ?? new Dictionary<int, string>(),
            ActiveKeyVersion = activeVersion,
            StorageRoot = root,
            MaxBlobSizeMb = 200
        });
        return (new BackupStorageService(opts, NullLogger<BackupStorageService>.Instance), root);
    }

    private static byte[] Random(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static async Task<(byte[] envelope, EncryptStreamResult result)> EncryptAsync(
        BackupStorageService svc, byte[] plaintext, long max = long.MaxValue)
    {
        using var src = new MemoryStream(plaintext);
        using var dst = new MemoryStream();
        var result = await svc.EncryptToAsync(src, dst, max);
        return (dst.ToArray(), result);
    }

    private static async Task<byte[]> DecryptAsync(
        BackupStorageService svc, byte[] envelope, int keyVersion = 0)
    {
        using var src = new MemoryStream(envelope);
        using var dst = new MemoryStream();
        await svc.DecryptToAsync(src, BackupStorageService.FormatChunked, keyVersion, dst);
        return dst.ToArray();
    }

    // ─── Mutlu yol: parça sınırlarının HER İKİ yanı ───────────────────

    [Theory]
    [InlineData(0)]                    // boş gövde — son parça sıfır uzunlukta
    [InlineData(1)]
    [InlineData(ChunkSize - 1)]
    [InlineData(ChunkSize)]            // tam bir parça; ileri okuma 0 dönmeli
    [InlineData(ChunkSize + 1)]
    [InlineData(2 * ChunkSize)]
    [InlineData(2 * ChunkSize + 12345)]
    public async Task RoundTrips_across_chunk_boundaries(int size)
    {
        var (svc, root) = Make();
        try
        {
            var plaintext = Random(size);
            var (envelope, result) = await EncryptAsync(svc, plaintext);

            result.PlaintextBytes.Should().Be(size);
            result.EnvelopeBytes.Should().Be(envelope.Length,
                "bildirilen zarf boyutu diske yazılanla birebir aynı olmalı — " +
                "kota ve SizeBytes bu sayıya güveniyor");
            result.PlaintextSha256Hex.Should().Be(
                Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant(),
                "özet tek geçişte hesaplanıyor; ikinci bir geçiş akıtmanın anlamını yok ederdi");

            (await DecryptAsync(svc, envelope)).Should().BeEquivalentTo(plaintext);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Header_is_self_describing_on_disk()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(10));

            envelope.AsSpan(0, 4).ToArray().Should().BeEquivalentTo("ODB2"u8.ToArray());
            envelope[4].Should().Be(0, "aktif anahtar sürümü başlıkta");
            BinaryPrimitives.ReadUInt32BigEndian(envelope.AsSpan(5, 4)).Should().Be(ChunkSize);
            BackupStorageService.LooksChunked(envelope.AsSpan(0, 4)).Should().BeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LooksChunked_is_false_for_a_legacy_single_shot_envelope()
    {
        var (svc, root) = Make();
        try
        {
            var (legacy, _) = svc.Encrypt(Random(64));
            BackupStorageService.LooksChunked(legacy.AsSpan(0, 4)).Should().BeFalse(
                "biçim 0 zarfı rastgele nonce ile başlar, sihirli imzası yok");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Same_plaintext_twice_yields_a_different_blob_key()
    {
        var (svc, root) = Make();
        try
        {
            var plaintext = Random(4096);
            var (e1, _) = await EncryptAsync(svc, plaintext);
            var (e2, _) = await EncryptAsync(svc, plaintext);

            e1.AsSpan(9, 16).ToArray().Should().NotBeEquivalentTo(e2.AsSpan(9, 16).ToArray(),
                "her blob kendi HKDF tuzunu taşımalı — sayaç bazlı nonce'un blob'lar " +
                "arası tekrar etmemesinin TEK garantisi bu");
            e1.Should().NotBeEquivalentTo(e2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─── Tuzak 1: parça sırasının değiştirilmesi ──────────────────────

    [Fact]
    public async Task Swapping_two_chunks_is_rejected()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(2 * ChunkSize + 1));
            var frame = ChunkSize + TagSize;

            // 0. ve 1. çerçeveyi yer değiştir. İkisi de kendi içinde geçerli;
            // onları yerlerine bağlayan tek şey nonce'a gömülü sayaç.
            var swapped = (byte[])envelope.Clone();
            Buffer.BlockCopy(envelope, HeaderSize + frame, swapped, HeaderSize, frame);
            Buffer.BlockCopy(envelope, HeaderSize, swapped, HeaderSize + frame, frame);

            var act = async () => await DecryptAsync(svc, swapped);
            await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─── Tuzak 2: dosyanın sonundan kesilmesi ─────────────────────────

    [Fact]
    public async Task Chopping_the_final_chunk_off_is_rejected()
    {
        var (svc, root) = Make();
        try
        {
            // 3 parça: [1 MiB][1 MiB][1 bayt]. Son çerçeveyi TAMAMEN atınca
            // geriye kendi içinde kusursuz iki çerçeve kalıyor. Son parça
            // bayrağı olmasaydı bu kesik dosya SESSİZCE çözülürdü — müşteri
            // eksik bir yedeği sağlam sanırdı.
            var (envelope, _) = await EncryptAsync(svc, Random(2 * ChunkSize + 1));
            var truncated = envelope[..(HeaderSize + 2 * (ChunkSize + TagSize))];

            var act = async () => await DecryptAsync(svc, truncated);
            await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Chopping_bytes_out_of_the_middle_of_the_last_chunk_is_rejected()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(ChunkSize + 5000));
            var truncated = envelope[..(envelope.Length - 100)];

            var act = async () => await DecryptAsync(svc, truncated);
            await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_trailing_fragment_shorter_than_a_tag_is_rejected_before_any_crypto()
    {
        var (svc, root) = Make();
        try
        {
            // Tam bir çerçeve + etiketten kısa bir kuyruk. Bu dal, hiçbir
            // parçayı çözmeye kalkışmadan aritmetikten reddetmeli.
            var (envelope, _) = await EncryptAsync(svc, Random(ChunkSize + 100));
            var truncated = envelope[..(HeaderSize + (ChunkSize + TagSize) + 8)];

            var act = async () => await DecryptAsync(svc, truncated);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*trailing fragment*");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task An_envelope_shorter_than_header_plus_tag_is_rejected()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(100));
            var act = async () => await DecryptAsync(svc, envelope[..HeaderSize]);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*truncated before the first chunk*");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Flipping_a_single_ciphertext_byte_is_rejected()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(4096));
            envelope[HeaderSize + 10] ^= 0xFF;

            var act = async () => await DecryptAsync(svc, envelope);
            await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─── Başlık kurcalama ─────────────────────────────────────────────

    [Fact]
    public async Task Tampered_chunk_size_in_the_header_is_rejected()
    {
        var (svc, root) = Make();
        try
        {
            // Aralık içi ama YANLIŞ bir parça boyutu: çerçeveleme aritmetiği
            // tutar, etiket tutmaz — başlık her parçaya AAD olarak bağlı.
            var (envelope, _) = await EncryptAsync(svc, Random(2 * ChunkSize));
            BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(5, 4), 4096);

            var act = async () => await DecryptAsync(svc, envelope);
            await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(4095u)]           // MinReadableChunkSize altı
    [InlineData(uint.MaxValue)]   // "önce 4 GB ayır" saldırısı
    public async Task Out_of_range_chunk_size_is_rejected_before_a_buffer_is_allocated(uint declared)
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(4096));
            BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(5, 4), declared);

            var act = async () => await DecryptAsync(svc, envelope);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*unusable chunk size*");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Magic_mismatch_is_rejected()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(64));
            envelope[0] = (byte)'X';

            var act = async () => await DecryptAsync(svc, envelope);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*magic mismatch*");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Key_version_byte_must_agree_with_the_database_row()
    {
        var ring = new Dictionary<int, string> { [0] = new string('a', 64), [1] = new string('b', 64) };
        var (svc, root) = Make(activeVersion: 1, ring: ring);
        try
        {
            var (envelope, result) = await EncryptAsync(svc, Random(64));
            result.KeyVersion.Should().Be(1);
            envelope[4].Should().Be(1);

            (await DecryptAsync(svc, envelope, keyVersion: 1)).Should().HaveCount(64);

            // Satır 0 diyor, blob 1 diyor → DB ile disk anlaşmazlığı.
            var act = async () => await DecryptAsync(svc, envelope, keyVersion: 0);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not match DB row keyVersion*");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unknown_key_version_throws_before_touching_the_blob()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(64));
            var act = async () => await DecryptAsync(svc, envelope, keyVersion: 99);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*No master key configured for version 99*");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─── Boyut tavanı ─────────────────────────────────────────────────

    [Fact]
    public async Task Exceeding_the_plaintext_cap_throws_BackupTooLarge()
    {
        var (svc, root) = Make();
        try
        {
            using var src = new MemoryStream(Random(5000));
            using var dst = new MemoryStream();
            var act = async () => await svc.EncryptToAsync(src, dst, maxPlaintextBytes: 1000);
            await act.Should().ThrowAsync<BackupTooLargeException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_body_exactly_at_the_cap_is_accepted()
    {
        var (svc, root) = Make();
        try
        {
            var (_, result) = await EncryptAsync(svc, Random(1000), max: 1000);
            result.PlaintextBytes.Should().Be(1000);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─── Geriye uyum ve akış kaynağı ──────────────────────────────────

    [Fact]
    public async Task DecryptToAsync_still_reads_a_format0_legacy_envelope()
    {
        var (svc, root) = Make();
        try
        {
            var plaintext = Random(3000);
            var (legacy, keyVersion) = svc.Encrypt(plaintext);

            using var src = new MemoryStream(legacy);
            using var dst = new MemoryStream();
            await svc.DecryptToAsync(src, BackupStorageService.FormatSingleShot, keyVersion, dst);

            dst.ToArray().Should().BeEquivalentTo(plaintext,
                "sahadaki her mevcut blob biçim 0 — okunamaması müşterinin tek " +
                "felaket kurtarma kopyasını kaybetmesi demek");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_non_seekable_chunked_source_is_rejected_explicitly()
    {
        var (svc, root) = Make();
        try
        {
            var (envelope, _) = await EncryptAsync(svc, Random(64));
            using var src = new NonSeekableStream(envelope);
            using var dst = new MemoryStream();

            var act = async () => await svc.DecryptToAsync(
                src, BackupStorageService.FormatChunked, 0, dst);
            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*seekable*");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_dripping_source_stream_still_produces_full_chunks()
    {
        // Stream.ReadAsync istenenden az dönebilir. Kısa dönüşü "dosya bitti"
        // sanmak son parça bayrağını yanlış yere koyar ve zarfı kendi
        // çözücümüzün bile reddedeceği hâle getirir.
        var (svc, root) = Make();
        try
        {
            var plaintext = Random(ChunkSize + 777);
            using var src = new DrippingStream(plaintext, maxPerRead: 4096);
            using var dst = new MemoryStream();
            var result = await svc.EncryptToAsync(src, dst, long.MaxValue);

            result.PlaintextBytes.Should().Be(plaintext.Length);
            (await DecryptAsync(svc, dst.ToArray())).Should().BeEquivalentTo(plaintext);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NewBlobPath_creates_the_directory_but_not_the_file()
    {
        var (svc, root) = Make();
        try
        {
            var customerId = Guid.NewGuid();
            var path = svc.NewBlobPath(customerId);

            Directory.Exists(Path.Combine(root, customerId.ToString())).Should().BeTrue();
            File.Exists(path).Should().BeFalse(
                "çağıran FileMode.CreateNew ile açıyor; dosya önceden varsa bu çakışırdı");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OpenBlobRead_blocks_paths_outside_the_storage_root()
    {
        var (svc, root) = Make();
        var outside = Path.Combine(Path.GetTempPath(), $"orderdeck-evil-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(outside, new byte[] { 1, 2, 3 });
            var act = () => svc.OpenBlobRead(outside);
            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    /// <summary>Her okumada en fazla <paramref name="maxPerRead"/> bayt veren
    /// akış — soket/kestrel gövdesinin gerçek davranışı.</summary>
    private sealed class DrippingStream(byte[] data, int maxPerRead) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, maxPerRead));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxPerRead)], cancellationToken);
    }
}
