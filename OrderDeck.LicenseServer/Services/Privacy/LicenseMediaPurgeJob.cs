using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// Yayıncı silmesinin (KVKK) lisans medyası görevlerini (<see cref="LicenseMediaPurgeTask"/>) tamamlar: geç yazım penceresinden
/// sonraki doğrulama turu, silinemeyen nesnenin ya da listelenemeyen deponun yeniden denemesi ve istekteki ilk tur hiç
/// koşamadan (iptal, yeniden başlatma, veritabanı hatası) kalan görevin bütün işi. Tur kuralları <see cref="LicenseMediaPurge"/>'da.
/// Saatte bir; açık görev yoksa tek indeksli sorgu.
/// </summary>
public sealed class LicenseMediaPurgeJob
{
    public const string JobId = "license-media-purge";

    /// <summary>Bir koşuda ele alınan azami görev (lisans); her biri dört öneki toplu siler, iş Hangfire'ı tıkamasın.</summary>
    private const int BatchSize = 50;

    private readonly LicenseDbContext _db;
    private readonly LicenseMediaPurge _purge;
    private readonly ILogger<LicenseMediaPurgeJob> _log;
    private readonly TimeProvider _clock;

    public LicenseMediaPurgeJob(LicenseDbContext db, LicenseMediaPurge purge, ILogger<LicenseMediaPurgeJob> log,
        TimeProvider? clock = null)
    {
        _db = db;
        _purge = purge;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Silinen nesne sayısını döndürür. Üst üste binen iki koşu aynı görevleri ele alıp ikisi de imha kaydı yazardı
    /// (görev satırında eşzamanlılık jetonu yok): ikinci koşu birincinin bitmesini bekler.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var due = await _db.LicenseMediaPurgeTasks
            .Where(t => t.CompletedAt == null && t.NextAttemptAt <= now)
            .OrderBy(t => t.NextAttemptAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0) return 0;

        var result = await _purge.RunAsync(due, DataDestruction.SystemActor, ct);
        _log.LogInformation(
            "[LicenseMediaPurge] {Tasks} görev: {Deleted} nesne silindi, {Failed} silinemedi, {Unlisted} lisans listelenemedi, " +
            "{Completed} görev kapandı",
            due.Count, result.Deleted, result.Failed, result.Unlisted, due.Count(t => t.CompletedAt is not null));
        return result.Deleted;
    }
}
