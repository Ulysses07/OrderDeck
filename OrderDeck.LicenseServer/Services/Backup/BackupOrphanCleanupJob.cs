using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Hangfire günlük işi: depolama kökünde kalmış <b>yetim</b> yedek blob'larını süpürür.
///
/// <para>Neden gerekli — dosya sistemi ile SQL Server aynı işleme giremez. İkisinden
/// biri her zaman diğeri olmadan başarılı olabilir, dolayısıyla soru "nasıl atomik
/// yaparız" değil, <i>hangi tarafın patlamasına izin veririz</i>. İki bozuk son durum
/// var:</para>
/// <list type="bullet">
/// <item><b>Yetim blob</b> (dosya var, satır yok): disk yer kaplar, kimse görmez,
/// sonradan toplanabilir.</item>
/// <item><b>Hayalet satır</b> (satır var, dosya yok): müşteri yedeğini listede görür,
/// geri yüklemeye kalktığı an — yani tam ihtiyaç duyduğu anda — yoktur.</item>
/// </list>
/// <para>Yetim ucuz olan. Bu yüzden tüm yollar veritabanı satırını kaynak kabul eder:
/// <b>en son yaratılır, en önce silinir</b>. Bedeli, geriye yetim dosya kalması; bu iş
/// de onları toplar. Sıralamayı düzeltmek tek başına yetmezdi — süreç blob yazımıyla
/// satır kaydı arasında ölürse hiçbir <c>catch</c> çalışmaz, yetimi yalnız disk ile
/// veritabanını karşılaştıran bir mutabakat bulabilir.</para>
///
/// <para><b>YIKICI iş.</b> Yanlış kurgulanırsa müşterinin tek yedeğini siler. Bu yüzden
/// silme ölçütü iki koşulun BİRLİKTE sağlanmasıdır: dosyanın veritabanında karşılığı
/// yok <i>ve</i> dosya ödemsiz süreden eski.</para>
/// </summary>
public sealed class BackupOrphanCleanupJob
{
    /// <summary>
    /// Ödemsiz süre — zorunlu emniyet mandalı. Yükleme akışında blob diske
    /// yazıldıktan sonra satır kaydedilene kadar geçen kısa aralıkta dosya
    /// veritabanında GÖRÜNMEZ. Süre olmasaydı iş, o an devam eden bir yüklemenin
    /// blob'unu silip tam da engellemeye çalıştığı şeyi — hayalet satırı — kendi
    /// eliyle üretirdi. 24 saat, en yavaş yükleme için bile fazlasıyla yeter.
    /// </summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly ILogger<BackupOrphanCleanupJob> _log;

    public BackupOrphanCleanupJob(
        LicenseDbContext db, BackupStorageService storage,
        ILogger<BackupOrphanCleanupJob> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var files = _storage.EnumerateBlobs();
        if (files.Count == 0) return;

        // Referans kümesi silmeden ÖNCE okunur. Veritabanı erişilemezse bu çağrı
        // patlar ve iş tek bir dosyaya dokunmadan düşer — sessizce "hiçbir satır
        // yok, hepsi yetim" sonucuna varıp her şeyi silmesindense.
        var referenced = (await _db.CustomerBackups
                .Select(b => b.BlobPath)
                .ToListAsync(ct))
            .Select(Path.GetFullPath)
            .ToHashSet(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        var cutoff = DateTimeOffset.UtcNow - GracePeriod;
        var deleted = 0;

        foreach (var (path, lastWrite) in files)
        {
            // İki koşul BİRLİKTE: DB'de yok VE ödemsiz süreden eski.
            if (referenced.Contains(path)) continue;
            if (lastWrite >= cutoff) continue;

            _storage.DeleteBlob(path);
            deleted++;
        }

        if (deleted == 0)
            _log.LogDebug("Yedek mutabakatı: yetim blob bulunmadı");
        else
            _log.LogInformation("Yedek mutabakatı: {Count} yetim blob silindi", deleted);
    }
}
