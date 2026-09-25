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

        // InMemory sağlayıcı ExecuteUpdate desteklemez; satır sayısı küçük (günlük artış), döngü yeterli.
        var raw = await _db.BankTransactions.Where(t => t.RawJson != null && t.FetchedAt < rawCutoff).ToListAsync(ct);
        foreach (var t in raw) t.RawJson = null;
        var desc = await _db.BankTransactions
            .Where(t => t.DescriptionPurgedAt == null && t.FetchedAt < descCutoff).ToListAsync(ct);
        foreach (var t in desc) { t.Description = null; t.CounterpartyName = null; t.DescriptionPurgedAt = now; }
        var evidence = await _db.PaymentMatches.Where(m => m.Evidence != null && m.CreatedAt < descCutoff).ToListAsync(ct);
        foreach (var m in evidence) m.Evidence = null;
        await _db.SaveChangesAsync(ct);
        if (raw.Count + desc.Count > 0)
            _log.LogInformation("Banka veri saklama: {Raw} ham JSON, {Desc} açıklama boşaltıldı", raw.Count, desc.Count);
        return (raw.Count, desc.Count);
    }
}
