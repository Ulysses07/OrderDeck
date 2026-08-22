using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Services;

public sealed record RestoreResult(bool Success, string? Error);

/// <summary>
/// Phase 5a: download cloud backup, hedge with .pre-restore.bak copy of existing db,
/// then extract zip → orderdeck.db. Caller must restart app for new connections.
/// </summary>
public sealed class RestoreService
{
    public const string PreRestoreBakSuffix = ".pre-restore.bak";

    /// <summary>
    /// Zip içinden açılacak azami veritabanı boyutu. Yedek kendi sunucumuzdan
    /// kimlik doğrulamalı indiriliyor, yani zip bombası için önce sunucunun
    /// ele geçmesi gerekir — ama açma işlemi diski dolduran tek adım olduğu
    /// için sınır ucuz bir emniyet. 2 GB, en büyük gerçek yayıncı
    /// veritabanının kat kat üstünde.
    /// </summary>
    private const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _databaseFile;
    private readonly IBackupClient _client;
    private readonly ILogger<RestoreService> _log;

    public RestoreService(string databaseFile, IBackupClient client, ILogger<RestoreService> log)
    {
        _databaseFile = databaseFile;
        _client = client;
        _log = log;
    }

    public Task<IReadOnlyList<BackupMetadata>> ListAvailableAsync(CancellationToken ct = default) =>
        _client.ListAsync(ct);

    public async Task<RestoreResult> RestoreAsync(Guid backupId, CancellationToken ct = default)
    {
        byte[] zipBytes;
        try
        {
            zipBytes = await _client.DownloadAsync(backupId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore download failed for {BackupId}", backupId);
            return new RestoreResult(false, $"İndirme başarısız: {ex.Message}");
        }

        var bakPath = _databaseFile + PreRestoreBakSuffix;
        var tempExtract = _databaseFile + ".restoring";
        try
        {
            // Hedge: backup existing db before overwriting
            if (File.Exists(_databaseFile))
                HedgeExistingDatabase(bakPath);

            // Extract to temp first, then atomic move-overwrite
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry("orderdeck.db")
                    ?? throw new InvalidOperationException("Backup zip missing orderdeck.db entry");

                // Merkezî dizindeki boyut yalan söyleyebilir, o yüzden hem
                // beyan edileni hem gerçekten akan baytı sınırlıyoruz.
                if (entry.Length > MaxUncompressedBytes)
                    throw new InvalidOperationException(
                        $"Yedek beyan edilen boyutu aşıyor ({entry.Length} bayt).");

                await using var src = entry.Open();
                await using var dst = File.Create(tempExtract);
                await CopyWithLimitAsync(src, dst, MaxUncompressedBytes, ct);
            }

            // Aktif veritabanının üzerine yazmadan ÖNCE doğrula. Eskiden tek
            // denetim "dosya var ve boyutu > 0" idi ve o da yalnız hata
            // yolunda çalışıyordu: kırpılmış bir indirme ya da SQLite bile
            // olmayan bir içerik "Geri yükleme tamamlandı" mesajıyla aktif
            // veritabanının üzerine yazılıyordu. Bozukluk ancak bir sonraki
            // açılışta görülüyordu — o noktada operatörün elinde ne yedek
            // vardı ne de ne olduğuna dair bir iz.
            if (!SqliteFile.IsIntactDatabase(tempExtract, out var integrityError))
                throw new InvalidOperationException(
                    $"Yedek dosyası geçerli bir veritabanı değil ({integrityError}); " +
                    "mevcut veritabanına dokunulmadı.");

            // Replace db
            File.Move(tempExtract, _databaseFile, overwrite: true);

            // Eski -wal/-shm yan dosyaları ZORUNLU olarak gitmeli: SQLite onları
            // yeni dosyaya aitmiş gibi uygulamaya çalışır ve veritabanını bozar.
            // Burada hata alırsak geri yükleme başarısız sayılmalı — catch bloğu
            // .pre-restore.bak'tan geri dönüyor.
            SqliteFile.DeleteSidecars(_databaseFile);

            _log.LogInformation("Restore complete: {BackupId} → {Path}", backupId, _databaseFile);
            return new RestoreResult(true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed mid-way for {BackupId}", backupId);

            // Yarım açılmış dosya diskte kalmasın; bir sonraki denemede
            // File.Create zaten üzerine yazar ama boşuna yer tutar.
            try { if (File.Exists(tempExtract)) File.Delete(tempExtract); } catch { /* best effort */ }

            // Roll back from .pre-restore.bak if extract corrupted the original
            try
            {
                if (File.Exists(bakPath) && !SqliteFile.IsIntactDatabase(_databaseFile, out _))
                {
                    File.Copy(bakPath, _databaseFile, overwrite: true);
                    SqliteFile.DeleteSidecars(_databaseFile);
                }
            }
            catch { /* best effort */ }
            return new RestoreResult(false, $"Geri yükleme hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>Stream.CopyToAsync</c>'in sınırlı sürümü. Zip'in beyan ettiği boyuta
    /// güvenmeyip gerçekten akan baytı sayar — sıkıştırma bombasında disk
    /// dolmadan durur.
    /// </summary>
    private static async Task CopyWithLimitAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException(
                    $"Yedek açılırken {maxBytes} baytlık sınır aşıldı.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>
    /// Üzerine yazmadan önce mevcut veritabanının kopyasını alır. Öncelik
    /// çevrimiçi yedekleme API'sinde: düz kopya WAL kipinde son işlemleri
    /// kaçırır, yani geri dönülen "hedge" eksik olur.
    ///
    /// Kaynak zaten bozuksa API açamaz ve patlar. Operatör geri yüklemeye
    /// çoğunlukla tam da bu yüzden başvuruyor; bu durumda geri yüklemeyi
    /// iptal etmek yanlış olur — bozuk dosyanın ham kopyasına düşüyoruz ki
    /// elde en azından adli inceleme için bir şey kalsın.
    /// </summary>
    private void HedgeExistingDatabase(string bakPath)
    {
        try
        {
            SqliteFile.Snapshot(_databaseFile, bakPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Mevcut veritabanının tutarlı kopyası alınamadı; ham dosya kopyalanıyor");
            File.Copy(_databaseFile, bakPath, overwrite: true);
        }
    }
}
