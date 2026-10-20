using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Spec §5/§7: ham JSON 30 gün (KVKK karar 5), açıklama/gönderen adı 180 gün; tutar, tarih, hash'ler kalır.
/// Faz 1'de kanıt metni (PaymentMatch.Evidence) de 180 günde boşaltılır. Her boşaltma partisi kişisel veri içermeyen kalıcı
/// imha kaydını (<see cref="DataDestruction"/>) satırın lisansı başına, aynı işlemde yazar; iş düşerse yöneticiye e-posta gider
/// (KVKK Ö-13).</summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankDataRetentionJob
{
    /// <summary>Tek SaveChanges'te en fazla bu kadar satır; partiler arasında izleyici boşaltılır. İlk koşu (ya da uzun bir
    /// kesintiden sonraki) on binlerce satırı — ham JSON dahil — tek listede belleğe almasın, tek dev işlem açmasın.</summary>
    public const int BatchSize = 500;

    public const string FailureSubject = "[OrderDeck] Banka veri saklama işi düştü";

    private readonly LicenseDbContext _db;
    private readonly BankOptions _opt;
    private readonly BankJobAlerts _alerts;
    private readonly ILogger<BankDataRetentionJob> _log;

    public BankDataRetentionJob(LicenseDbContext db, IOptions<BankOptions> opt, BankJobAlerts alerts,
        ILogger<BankDataRetentionJob> log)
    { _db = db; _opt = opt.Value; _alerts = alerts; _log = log; }

    /// <summary>Düşerse yöneticiye e-posta (KVKK Ö-13) ve istisna Hangfire'a çıkar (koşu Failed).</summary>
    public async Task<(int RawPurged, int DescriptionPurged)> RunAsync(CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Banka veri saklama işi düştü");
            await _alerts.SendAsync(FailureSubject,
                $"bank-data-retention düştü: {ex.GetType().Name}. Hangfire panosuna bakın; iş ertesi gün yeniden koşar, " +
                "o güne kadar saklama süreleri aşılmış olur.", CancellationToken.None);
            throw;
        }
    }

    private async Task<(int RawPurged, int DescriptionPurged)> RunCoreAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rawCutoff = now.AddDays(-_opt.RawJsonRetentionDays);
        var descCutoff = now.AddDays(-_opt.DescriptionRetentionDays);

        // InMemory sağlayıcı ExecuteUpdate desteklemez; izlenen parti güncellemesi iki sağlayıcıda da aynı çalışır.
        var raw = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.RawJson != null && t.FetchedAt < rawCutoff).OrderBy(t => t.Id), t => t.LicenseId,
            t => t.RawJson = null, "BankTransactions.RawJson", $"FetchedAt+{_opt.RawJsonRetentionDays}g", ct);
        var desc = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.DescriptionPurgedAt == null && t.FetchedAt < descCutoff).OrderBy(t => t.Id),
            t => t.LicenseId, t => { t.Description = null; t.CounterpartyName = null; t.DescriptionPurgedAt = now; },
            "BankTransactions.Description+CounterpartyName", $"FetchedAt+{_opt.DescriptionRetentionDays}g", ct);
        // UpdatedAt eşzamanlılık jetonudur, her yazan ilerletir: arada okuyup yazan bir bağdaştırma çakışmayı görsün.
        var evidence = await PurgeInBatchesAsync(
            _db.PaymentMatches.Where(m => m.Evidence != null && m.CreatedAt < descCutoff).OrderBy(m => m.Id), m => m.LicenseId,
            m => { m.Evidence = null; m.UpdatedAt = now; }, "PaymentMatches.Evidence",
            $"CreatedAt+{_opt.DescriptionRetentionDays}g", ct);
        if (raw + desc + evidence > 0)
            _log.LogInformation("Banka veri saklama: {Raw} ham JSON, {Desc} açıklama, {Evidence} kanıt metni boşaltıldı",
                raw, desc, evidence);
        return (raw, desc);
    }

    /// <summary><paramref name="expired"/>'i <see cref="BatchSize"/>'lık partilerle boşaltır; her parti imha kaydını satırın
    /// lisansı başına (<paramref name="licenseOf"/>; yayıncı kendi verisinin imha kanıtını isteyebilir) AYNI SaveChanges'te yazar.
    /// Sorgu, boşaltılan satırı artık seçmeyecek biçimde yazılmalı (boşaltılan alan filtrenin içinde): her parti baştan
    /// sorgulanır, Skip gerekmez.</summary>
    private async Task<int> PurgeInBatchesAsync<T>(IQueryable<T> expired, Func<T, Guid> licenseOf, Action<T> purge,
        string target, string criterion, CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await expired.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var row in batch) purge(row);
            foreach (var license in batch.GroupBy(licenseOf))
                DataDestruction.Record(_db, license.Key, target, DataDestruction.Actions.Nulled, license.Count(), criterion,
                    DataDestruction.Triggers.Retention, DataDestruction.SystemActor);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }
}
