using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a: AES-256-GCM encrypt/decrypt + filesystem read/write for customer DB backups.
/// Phase 5b: per-blob key versioning so the master key can rotate without
/// breaking historical backups.
///
/// <para><b>İki zarf BİÇİMİ bir arada yaşıyor</b> (biçim ekseni anahtar sürümü
/// ekseninden ayrı — bkz. <see cref="OrderDeck.LicenseServer.Domain.CustomerBackup.EnvelopeFormat"/>):</para>
/// <list type="bullet">
/// <item><b>Biçim 0 — tek atış (eski).</b> v0: <c>[12B nonce][16B tag][ciphertext]</c>,
/// v1+: <c>[1B keyVersion][12B nonce][16B tag][ciphertext]</c>. Yalnız OKUMA için
/// duruyor; .NET'in <c>AesGcm</c> API'si tek atış olduğu için hem düz metni hem
/// şifreli metni TAM BOY bellekte ister, yani 64 MB'lık bir yedek istek başına
/// ~128 MB demekti.</item>
/// <item><b>Biçim 2 — parçalı akış (yeni, tüm yeni yüklemeler).</b> Aşağıdaki
/// STREAM kurgusu; bellekte hiçbir zaman iki parçadan fazlası durmuyor.</item>
/// </list>
///
/// <para><b>Biçim 2 kurgusu (STREAM — age/Tink ile aynı fikir).</b> Parçalı AEAD'in
/// üç bilinen tuzağı var ve üçü de burada kapatılıyor:</para>
/// <list type="number">
/// <item><b>Parça sırasının değiştirilmesi</b> → nonce'un içine parça SAYACI
/// gömülü, yani i. parçanın etiketi ancak i. sırada doğrular.</item>
/// <item><b>Dosyanın sonundan kesilmesi</b> → son parça nonce'unda 1 olan bir
/// SON bayrağı taşıyor. Kesilmiş bir dosyanın son parçası aslında bir ara parça
/// olduğu için bayrak tutmaz ve etiket düşer. Tek tek parçalar geçerli olduğu
/// hâlde bütünün eksik olması bu bayrak olmadan FARK EDİLEMEZ.</item>
/// <item><b>Nonce tekrarı</b> → sayaç aynı blob içinde tekrar etmiyor; bloblar
/// ARASINDA ise anahtar zaten farklı: her blob için 16 baytlık rastgele bir
/// tuzla HKDF-SHA256 ile ana anahtardan ayrı bir blob anahtarı türetiliyor.
/// Rastgele nonce seçilseydi doğum günü sınırına güvenmek zorunda kalırdık.</item>
/// </list>
///
/// <para>Başlık her parçaya <b>AAD</b> olarak bağlanıyor: parça boyutu ya da
/// anahtar sürümü baytı oynatılırsa etiket düşer.</para>
/// </summary>
public sealed class BackupStorageService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>Tek atış (eski) zarf. Yeni yüklemelerde KULLANILMIYOR.</summary>
    public const int FormatSingleShot = 0;

    /// <summary>Parçalı akış zarfı — tüm yeni yüklemeler.</summary>
    public const int FormatChunked = 2;

    /// <summary>
    /// Biçim-2 başlığı: <c>[4B "ODB2"][1B keyVersion][4B chunkSize BE][16B salt]</c>.
    /// Her parçaya AAD olarak bağlanır.
    /// </summary>
    private const int HeaderSize = 4 + 1 + 4 + SaltSize;
    private const int SaltSize = 16;

    private static ReadOnlySpan<byte> Magic => "ODB2"u8;
    private static ReadOnlySpan<byte> HkdfInfo => "orderdeck-backup-v2"u8;

    /// <summary>
    /// Parça boyutu. Tepe bellek ≈ 2 × bu (düz metin + şifreli tampon), blob
    /// boyutundan BAĞIMSIZ. Başlıkta yazılı olduğu için bu sabit ileride
    /// değişse bile eski bloblar okunmaya devam eder.
    /// </summary>
    private const int ChunkSize = 1 << 20;   // 1 MiB

    // Bozuk/kurcalanmış bir başlıktaki parça boyutuna göre tampon ayırmak
    // ("ilk 4 GB'ı ayır" saldırısı) etiket doğrulanmadan ÖNCE olurdu — o yüzden
    // ayırmadan önce aralık kontrolü şart.
    private const int MinReadableChunkSize = 4096;
    private const int MaxReadableChunkSize = 16 << 20;

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
    /// AND the version that was used so the caller persists it alongside the row.
    ///
    /// <para><b>Biçim 0 (tek atış) üretir</b> — düz metnin ve zarfın ikisini de tam
    /// boy bellekte tutar. Yükleme yolu artık <see cref="EncryptToAsync"/>
    /// kullanıyor; bu API yalnız küçük/bilinen boyutlu yükler ve geriye uyum
    /// testleri için duruyor. Yeni bir çağrı ekliyorsan önce neden akıtamadığını
    /// yaz.</para></summary>
    public (byte[] envelope, int keyVersion) Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var version = _activeVersion;
        var key = _keys[version];

        // v0 (legacy) ile v1+ arasındaki TEK fark baştaki sürüm baytı. v0
        // çıktısı Phase-5b öncesiyle baytına kadar aynı kalmalı: mevcut bir
        // kurulum yeni koda geçerken ne blob yeniden yazmak ne de bayrak günü
        // yapmak zorunda kalsın — ActiveKeyVersion >=1 olana dek her yeni blob
        // hâlâ v0.
        var headerSize = version == 0 ? 0 : 1;

        // Doğrudan çıktı tamponuna şifreleniyor. Önceden nonce/ciphertext/tag
        // ayrı ayrı ayrılıp sonra BlockCopy ile buraya taşınıyordu; bu, blob
        // boyutunda GEREKSİZ bir ikinci kopya demekti (64 MB'lık bir yedekte
        // 64 MB fazladan). Çıktı baytları birebir aynı.
        var output = new byte[headerSize + NonceSize + TagSize + plaintext.Length];
        if (headerSize == 1) output[0] = (byte)version;

        var nonce = output.AsSpan(headerSize, NonceSize);
        var tag = output.AsSpan(headerSize + NonceSize, TagSize);
        var ciphertext = output.AsSpan(headerSize + NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

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

    /// <summary>
    /// <paramref name="source"/>'u sonuna kadar okuyup <paramref name="destination"/>'a
    /// <b>biçim 2</b> zarfı yazar. Bellekte hiçbir zaman iki parçadan fazlası durmaz —
    /// blob ne kadar büyürse büyüsün.
    ///
    /// <para>Düz metnin SHA-256'sı ve boyutu burada, TEK GEÇİŞTE hesaplanıyor.
    /// Ayrı bir geçiş yapmak akıtmanın anlamını ortadan kaldırırdı: baytları
    /// ikinci kez okuyabilmek için ya hepsini bellekte ya diskte tutmak gerekirdi.</para>
    ///
    /// <para><paramref name="maxPlaintextBytes"/> aşılırsa
    /// <see cref="BackupTooLargeException"/> fırlatılır — en fazla bir parça
    /// fazladan okunmuş olur, gövdenin tamamı değil.</para>
    /// </summary>
    public async Task<EncryptStreamResult> EncryptToAsync(
        Stream source, Stream destination, long maxPlaintextBytes, CancellationToken ct = default)
    {
        var version = _activeVersion;

        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = checked((byte)version);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), (uint)ChunkSize);
        RandomNumberGenerator.Fill(header.AsSpan(9, SaltSize));

        var blobKey = DeriveBlobKey(_keys[version], header.AsSpan(9, SaltSize));

        await destination.WriteAsync(header, ct).ConfigureAwait(false);

        using var aes = new AesGcm(blobKey, TagSize);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var bufA = new byte[ChunkSize];
        var bufB = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];

        var current = bufA;
        var currentLen = await ReadAtMostAsync(source, current, ct).ConfigureAwait(false);
        long plaintextBytes = 0;
        ulong counter = 0;

        while (true)
        {
            // İleri okuma ŞART: bir parçanın son parça olup olmadığını, nonce'a
            // yazılacak bayrağı belirlediği için ŞİFRELEMEDEN ÖNCE bilmek
            // zorundayız. Kısa okuma zaten dosya sonu demek, o durumda okumuyoruz.
            var next = ReferenceEquals(current, bufA) ? bufB : bufA;
            var nextLen = currentLen == ChunkSize
                ? await ReadAtMostAsync(source, next, ct).ConfigureAwait(false)
                : 0;
            var isLast = nextLen == 0;

            plaintextBytes += currentLen;
            if (plaintextBytes > maxPlaintextBytes) throw new BackupTooLargeException();

            sha.AppendData(current, 0, currentLen);

            WriteNonce(nonce, counter, isLast);
            aes.Encrypt(nonce, current.AsSpan(0, currentLen), cipher.AsSpan(0, currentLen), tag, header);
            await destination.WriteAsync(cipher.AsMemory(0, currentLen), ct).ConfigureAwait(false);
            await destination.WriteAsync(tag, ct).ConfigureAwait(false);

            if (isLast) break;
            current = next;
            currentLen = nextLen;
            counter++;
        }

        var envelopeBytes = HeaderSize + plaintextBytes + (long)(counter + 1) * TagSize;
        return new EncryptStreamResult(
            plaintextBytes,
            envelopeBytes,
            version,
            Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// Zarfı çözüp düz metni <paramref name="destination"/>'a akıtır.
    ///
    /// <para><paramref name="envelopeFormat"/> <b>veritabanı satırından</b> gelmeli
    /// (<c>CustomerBackup.EnvelopeFormat</c>). Diskteki sihirli imza yalnız satırı
    /// olmayan araçlar içindir (<see cref="LooksChunked"/>).</para>
    ///
    /// <para>Biçim 0 için akış YOK: tek atış <c>AesGcm</c> tüm zarfı ve tüm düz
    /// metni ister. Eski bloblar için bilinçli olarak eski davranış korunuyor —
    /// sayıları sabit ve azalıyor, yeni yüklemelerin hiçbiri bu dala girmiyor.</para>
    ///
    /// <para>Biçim 2 kaynağın <b>seek edilebilir</b> olmasını ister: parça sayısı
    /// ve son parçanın boyutu toplam uzunluktan çıkarılıyor. Uzunluğa güvenmek
    /// kesilmeyi gizlemiyor — kesilmiş dosyanın son parçası SON bayrağını taşımadığı
    /// için etiket düşer.</para>
    /// </summary>
    public async Task DecryptToAsync(
        Stream envelopeSource, int envelopeFormat, int keyVersion, Stream destination,
        CancellationToken ct = default)
    {
        if (envelopeFormat != FormatChunked)
        {
            using var buffer = new MemoryStream();
            await envelopeSource.CopyToAsync(buffer, ct).ConfigureAwait(false);
            var plaintext = Decrypt(buffer.ToArray(), keyVersion);
            await destination.WriteAsync(plaintext, ct).ConfigureAwait(false);
            return;
        }

        if (!envelopeSource.CanSeek)
            throw new ArgumentException("Chunked envelope needs a seekable source.", nameof(envelopeSource));

        if (!_keys.TryGetValue(keyVersion, out var master))
            throw new InvalidOperationException(
                $"No master key configured for version {keyVersion}. " +
                $"Add it to Backup:MasterKeys:{keyVersion} or restore from a deployment that has it.");

        var total = envelopeSource.Length - envelopeSource.Position;
        if (total < HeaderSize + TagSize)
            throw new InvalidOperationException("Chunked envelope truncated before the first chunk.");

        var header = new byte[HeaderSize];
        await envelopeSource.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidOperationException("Blob is not a chunked envelope (magic mismatch).");
        if (header[4] != keyVersion)
            throw new InvalidOperationException(
                $"Envelope key-version byte {header[4]} does not match DB row keyVersion {keyVersion}.");

        var declaredChunkSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        if (declaredChunkSize is < MinReadableChunkSize or > MaxReadableChunkSize)
            throw new InvalidOperationException(
                $"Chunked envelope declares an unusable chunk size ({declaredChunkSize}).");
        var chunkSize = (int)declaredChunkSize;

        var body = total - HeaderSize;
        var frame = (long)chunkSize + TagSize;
        var remainder = body % frame;
        if (remainder != 0 && remainder < TagSize)
            throw new InvalidOperationException("Chunked envelope has a trailing fragment shorter than its tag.");

        var chunkCount = remainder == 0 ? body / frame : body / frame + 1;
        var lastPlainLen = remainder == 0 ? chunkSize : (int)(remainder - TagSize);

        var blobKey = DeriveBlobKey(master, header.AsSpan(9, SaltSize));
        using var aes = new AesGcm(blobKey, TagSize);

        var cipher = new byte[chunkSize];
        var plain = new byte[chunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];

        for (long i = 0; i < chunkCount; i++)
        {
            var isLast = i == chunkCount - 1;
            var len = isLast ? lastPlainLen : (int)chunkSize;

            await envelopeSource.ReadExactlyAsync(cipher.AsMemory(0, len), ct).ConfigureAwait(false);
            await envelopeSource.ReadExactlyAsync(tag, ct).ConfigureAwait(false);

            WriteNonce(nonce, (ulong)i, isLast);
            aes.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len), header);

            await destination.WriteAsync(plain.AsMemory(0, len), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Veritabanı satırı OLMAYAN araçlar (restore tatbikatı, <c>RestoreVerify</c>)
    /// için biçim sezgisi. Yetkili kaynak her zaman
    /// <c>CustomerBackup.EnvelopeFormat</c> sütunudur; bu yalnız diskte duran bir
    /// dosyaya elle bakarken kullanılır.
    /// </summary>
    public static bool LooksChunked(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[..4].SequenceEqual(Magic);

    /// <summary>Blob'un yazılacağı tam yolu üretir (dosyayı yaratmaz, dizini yaratır).
    /// Akıtarak yazan çağıran, kendi <see cref="FileStream"/>'ini açabilsin diye ayrı.</summary>
    public string NewBlobPath(Guid customerId)
    {
        var customerDir = Path.Combine(_opt.StorageRoot, customerId.ToString());
        Directory.CreateDirectory(customerDir);
        return Path.Combine(customerDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bin");
    }

    /// <summary>Blob'u okumak için akış açar — yol hapsi <see cref="ReadBlobAsync"/>
    /// ile aynı.</summary>
    public Stream OpenBlobRead(string blobPath)
    {
        EnsurePathInsideStorageRoot(blobPath);
        return new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
    }

    private static byte[] DeriveBlobKey(byte[] masterKey, ReadOnlySpan<byte> salt)
    {
        var blobKey = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, blobKey, salt, HkdfInfo);
        return blobKey;
    }

    /// <summary>
    /// Nonce = <c>[3B sıfır][8B sayaç BE][1B son bayrağı]</c>. Sayaç parça sırasını,
    /// bayrak dosyanın sonunu bağlar; ikisi de doğrulanan veriye nonce üzerinden
    /// girdiği için ayrıca saklanmaları gerekmiyor.
    /// </summary>
    private static void WriteNonce(byte[] nonce, ulong counter, bool isLast)
    {
        Array.Clear(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(3, 8), counter);
        nonce[11] = isLast ? (byte)1 : (byte)0;
    }

    /// <summary><c>Stream.ReadAsync</c> istenenden az dönebilir; tamponu doldurana
    /// ya da dosya sonuna kadar okur. Kısa dönüş burada "dosya bitti" demek olurdu
    /// ve son parça bayrağını yanlış yere koyardı.</summary>
    private static async Task<int> ReadAtMostAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }
        return filled;
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

    /// <summary>
    /// Depolama kökündeki tüm yedek blob'larını, son yazılma zamanlarıyla listeler.
    /// Yetim mutabakatı (<see cref="BackupOrphanCleanupJob"/>) için var: bir dosyanın
    /// veritabanında karşılığı olup olmadığı ancak diski listeleyerek anlaşılabilir —
    /// satırı hiç yazılmamış bir blob'u DB'yi sorgulayarak bulmak tanımı gereği imkânsız.
    ///
    /// <para>Kapsam <b>yapı gereği</b> daraltılmıştır: yalnız adı GUID olarak
    /// ayrıştırılabilen alt klasörlerdeki <c>*.bin</c> dosyaları. Kök bir gün başka bir
    /// şeyle paylaşılan bir dizine ayarlanırsa, silme ölçütünün doğruluğuna değil
    /// dizin yapısına güvenmiş oluruz.</para>
    /// </summary>
    public IReadOnlyList<(string Path, DateTimeOffset LastWriteUtc)> EnumerateBlobs()
    {
        var root = Path.GetFullPath(_opt.StorageRoot);
        if (!Directory.Exists(root)) return Array.Empty<(string, DateTimeOffset)>();

        var result = new List<(string, DateTimeOffset)>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParse(Path.GetFileName(dir), out _)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.bin"))
                result.Add((Path.GetFullPath(file), new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)));
        }
        return result;
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

/// <summary>
/// <see cref="BackupStorageService.EncryptToAsync"/> sonucu. Düz metnin boyutu ve
/// özeti akış sırasında zaten hesaplandığı için birlikte dönüyor — çağıranın
/// baytları ikinci kez görmesi mümkün değil.
/// </summary>
public sealed record EncryptStreamResult(
    long PlaintextBytes,
    long EnvelopeBytes,
    int KeyVersion,
    string PlaintextSha256Hex);

/// <summary>
/// Akıtılan gövde izin verilen tavanı aştı. Ayrı bir tip: çağıran bunu 413'e
/// çevirmek zorunda ve genel bir <see cref="InvalidOperationException"/>'dan
/// ayırt edememek, gerçek bir şifreleme hatasını "çok büyük" diye raporlamak
/// demek olurdu.
/// </summary>
public sealed class BackupTooLargeException : Exception
{
    public BackupTooLargeException() : base("Backup plaintext exceeded the configured limit.") { }
}
