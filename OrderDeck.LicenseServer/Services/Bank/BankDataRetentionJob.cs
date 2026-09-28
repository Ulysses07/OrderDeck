using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Spec §5/§7: ham JSON 90 gün, açıklama/gönderen adı 180 gün; tutar, tarih, hash'ler kalır.
/// Faz 1'de kanıt metni (PaymentMatch.Evidence) de 180 günde boşaltılır.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankDataRetentionJob
{
    /// <summary>Tek SaveChanges'te en fazla bu kadar satır; partiler arasında izleyici boşaltılır. İlk koşu (ya da uzun bir
    /// kesintiden sonraki) on binlerce satırı — ham JSON dahil — tek listede belleğe almasın, tek dev işlem açmasın.</summary>
    public const int BatchSize = 500;

    private readonly LicenseDbContext _db;
    private readonly BankOptions _opt;
    private readonly ILogger<BankDataRetentionJob> _log;

    public BankDataRetentionJob(LicenseDbContext db, IOptions<BankOptions> opt, ILogger<BankDataRetentionJob> log)
    { _db = db; _opt = opt.Value; _log = log; }

    public async Task<(int RawPurged, int DescriptionPurged)> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rawCutoff = now.AddDays(-_opt.RawJsonRetentionDays);
        var descCutoff = now.AddDays(-_opt.DescriptionRetentionDays);

        // InMemory sağlayıcı ExecuteUpdate desteklemez; izlenen parti güncellemesi iki sağlayıcıda da aynı çalışır.
        var raw = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.RawJson != null && t.FetchedAt < rawCutoff).OrderBy(t => t.Id),
            t => t.RawJson = null, ct);
        var desc = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.DescriptionPurgedAt == null && t.FetchedAt < descCutoff).OrderBy(t => t.Id),
            t => { t.Description = null; t.CounterpartyName = null; t.DescriptionPurgedAt = now; }, ct);
        // UpdatedAt eşzamanlılık jetonudur, her yazan ilerletir: arada okuyup yazan bir bağdaştırma çakışmayı görsün.
        var evidence = await PurgeInBatchesAsync(
            _db.PaymentMatches.Where(m => m.Evidence != null && m.CreatedAt < descCutoff).OrderBy(m => m.Id),
            m => { m.Evidence = null; m.UpdatedAt = now; }, ct);
        if (raw + desc + evidence > 0)
            _log.LogInformation("Banka veri saklama: {Raw} ham JSON, {Desc} açıklama, {Evidence} kanıt metni boşaltıldı",
                raw, desc, evidence);
        return (raw, desc);
    }

    /// <summary><paramref name="expired"/>'i <see cref="BatchSize"/>'lık partilerle boşaltır. Sorgu, boşaltılan satırı
    /// artık seçmeyecek biçimde yazılmalı (boşaltılan alan filtrenin içinde): her parti baştan sorgulanır, Skip gerekmez.</summary>
    private async Task<int> PurgeInBatchesAsync<T>(IQueryable<T> expired, Action<T> purge, CancellationToken ct)
        where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await expired.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var row in batch) purge(row);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }
}
