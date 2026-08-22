using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Aynı anda gövdesi belleğe alınan yedek yüklemelerinin sayısını sınırlar.
///
/// <para><b>Neden oran sınırı yetmiyor:</b> <c>backup-upload</c> politikası
/// müşteri kimliğine bölünmüş 6/saat. Bu, tek müşterinin ne sıklıkla yüklediğini
/// sınırlar; aynı anda kaç müşterinin yüklediğini değil. Blob başına tavan
/// (<see cref="BackupOptions.MaxBlobSizeMb"/>) tek başına sunucu belleğine
/// tavan koymuyor — eşzamanlılıkla çarpılması gerekiyor, o çarpanı burası
/// tutuyor.</para>
///
/// <para><b>Neden kuyruk değil zaman aşımı:</b> sınırsız beklemek, bellek
/// baskısını açık bağlantı baskısına çevirmekten ibaret olurdu. Sıra
/// <see cref="BackupOptions.UploadQueueWaitSeconds"/> içinde açılmazsa istek
/// 503 + Retry-After ile geri çevriliyor; yedek yükleme kullanıcıyı bekleten
/// etkileşimli bir iş değil, yeniden denenebilir arka plan işi.</para>
///
/// <para>Singleton olarak kaydedilmeli — süreç başına tek sayaç olmasının
/// bütün amacı bu.</para>
/// </summary>
public sealed class BackupUploadThrottle : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _queueWait;

    public BackupUploadThrottle(IOptions<BackupOptions> opt)
    {
        // 0 veya negatif yapılandırma yüklemeyi tamamen kilitlerdi; sessizce
        // kilitlemektense en az bir sıraya çekiliyor.
        var permits = Math.Max(1, opt.Value.MaxConcurrentUploads);
        _gate = new SemaphoreSlim(permits, permits);
        _queueWait = TimeSpan.FromSeconds(Math.Max(0, opt.Value.UploadQueueWaitSeconds));
    }

    /// <summary>Sıra alındıysa true. true dönerse <see cref="Exit"/> ÇAĞRILMALI.</summary>
    public Task<bool> TryEnterAsync(CancellationToken ct) => _gate.WaitAsync(_queueWait, ct);

    public void Exit() => _gate.Release();

    public void Dispose() => _gate.Dispose();
}
