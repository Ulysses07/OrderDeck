using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Bir saklama koşusunun sayıları.</summary>
public sealed record BankRetentionResult(int RawPurged, int UnmatchedDescriptionsPurged, int EvidencePurged,
    int TransactionsDeleted, int MatchesDeleted, int GapsDeleted, int IbanMemoriesDeleted)
{
    public int Total => RawPurged + UnmatchedDescriptionsPurged + EvidencePurged + TransactionsDeleted + MatchesDeleted
        + GapsDeleted + IbanMemoriesDeleted;
}

/// <summary>
/// Banka verisinin saklama tablosu (KVKK K-6, Ö-14, K-4 öneri 2; not karar 1, 3, 5; süreler <see cref="BankOptions"/>). Süreler
/// hareketin İŞLEM tarihinden (<c>OccurredAt</c>) işler, çekim anından değil: ilk çekimdeki eski havale de işlem tarihinden
/// sayılır. Geç muhasebeleşen hareket çekildikten en az <see cref="BankOptions.MinDaysAfterFetch"/> gün kalır (eşleştirme ve
/// dekont bağdaştırması yetişsin), yani her süre fiilen <c>max(işlem tarihi + süre, çekim + 7 gün)</c>.
/// <list type="bullet">
/// <item>Hareketin tamamı ve eşleştirme satırı: <see cref="BankOptions.TransactionRetentionDays"/>. Eşleştirme satırı sayılara
/// indirgenmeden silinir (karar 3; ölçüm ekranı son 30 güne bakar). Gap satırı oluşturmasından aynı süre.</item>
/// <item>Ham kayıt: <see cref="BankOptions.RawJsonRetentionDays"/>.</item>
/// <item>Eşleşmeyen havalenin açıklaması ve gönderen adı: <see cref="BankOptions.UnmatchedDescriptionRetentionDays"/>.
/// "Eşleşen" = eşleştirme satırında gerçek müşteri YA DA dekont bağı var (insan kararı); öneri eşleşme değildir. Eşleşenin
/// açıklaması hareketle birlikte gider.</item>
/// <item>Eşleştirmenin gerekçe metni: bağlı hareketin açıklamasıyla aynı anda (satırın oluşturulma anı süre değildir: tarama ya
/// da elle eşleme satırı geç oluşturduysa metin daha geç gitmez).</item>
/// <item>IBAN–müşteri bağı: <see cref="CustomerIbanMemory.LastConfirmedAt"/>'ten (yoksa oluşturmadan)
/// <see cref="BankOptions.IbanMemoryRetentionMonths"/> ay.</item>
/// </list>
/// Önce süresi dolan hareketler silinir: sonraki boşaltmalar onlar için boşuna koşmaz. Her imha partisi kişisel veri içermeyen
/// kalıcı kaydını (<see cref="DataDestruction"/>) satırın lisansı başına, AYNI SaveChanges'te yazar (yayıncı kendi verisinin imha
/// kanıtını isteyebilir); başarılı koşu sonda bir koşu kaydı (<see cref="DataDestruction.RecordRun"/>) bırakır. İş düşerse
/// yöneticiye e-posta gider (<see cref="AdminJobAlerts"/>) ve istisna Hangfire'a çıkar. Partiler <see cref="BatchSize"/>'lık;
/// InMemory ExecuteUpdate/Delete desteklemediği için izlenen parti kalıbı (iki sağlayıcıda da aynı çalışır; gerçek SQL Server
/// çevirisi <c>BankDataRetentionRelationalTests</c>'te). <c>PaymentMatch.UpdatedAt</c> eşzamanlılık jetonudur: okunduktan sonra
/// yazılmış eşleşmenin silinmesi ya da boşaltılması düşer, iş uyarır ve ertesi gün yeniden dener.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankDataRetentionJob
{
    /// <summary>Tek SaveChanges'te en fazla bu kadar satır; partiler arasında izleyici boşaltılır. İlk koşu (ya da uzun bir
    /// kesintiden sonraki) on binlerce satırı — ham JSON dahil — tek listede belleğe almasın, tek dev işlem açmasın.</summary>
    public const int BatchSize = 500;

    public const string JobId = "bank-data-retention";
    public const string FailureSubject = "[OrderDeck] Banka veri saklama işi düştü";

    private readonly LicenseDbContext _db;
    private readonly BankOptions _opt;
    private readonly AdminJobAlerts _alerts;
    private readonly ILogger<BankDataRetentionJob> _log;

    public BankDataRetentionJob(LicenseDbContext db, IOptions<BankOptions> opt, AdminJobAlerts alerts,
        ILogger<BankDataRetentionJob> log)
    { _db = db; _opt = opt.Value; _alerts = alerts; _log = log; }

    /// <summary>Düşerse yöneticiye e-posta (KVKK Ö-13) ve istisna Hangfire'a çıkar (koşu Failed).</summary>
    public async Task<BankRetentionResult> RunAsync(CancellationToken ct = default)
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

    private async Task<BankRetentionResult> RunCoreAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var fetchFloor = now.AddDays(-_opt.MinDaysAfterFetch);
        var transactionCutoff = now.AddDays(-_opt.TransactionRetentionDays);
        var rawCutoff = now.AddDays(-_opt.RawJsonRetentionDays);
        var unmatchedCutoff = now.AddDays(-_opt.UnmatchedDescriptionRetentionDays);
        var memoryCutoff = now.AddMonths(-_opt.IbanMemoryRetentionMonths);
        var floor = $"FetchedAt+{_opt.MinDaysAfterFetch}g";

        // 1. Süresi dolan hareketlerin tamamı, eşleştirme satırlarıyla; gap satırları.
        var (transactions, matches) = await DeleteExpiredTransactionsAsync(transactionCutoff, fetchFloor,
            $"OccurredAt+{_opt.TransactionRetentionDays}g, {floor}", ct);
        var gaps = await DeleteInBatchesAsync(
            _db.PaymentMatchGaps.Where(g => g.CreatedAt < transactionCutoff).OrderBy(g => g.Id), g => g.LicenseId,
            "PaymentMatchGaps", $"CreatedAt+{_opt.TransactionRetentionDays}g", ct);

        // 2. Ham kayıt.
        var raw = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.RawJson != null && t.OccurredAt < rawCutoff && t.FetchedAt < fetchFloor)
                .OrderBy(t => t.Id), t => t.LicenseId,
            t => t.RawJson = null, "BankTransactions.RawJson", $"OccurredAt+{_opt.RawJsonRetentionDays}g, {floor}", ct);

        // 3. Eşleşmeyen havalenin açıklaması ve gönderen adı. Eşleşen = gerçek müşteri ya da dekont bağı (insan kararı).
        var linked = _db.PaymentMatches.Where(m => m.ActualWpfCustomerId != null || m.PaymentId != null)
            .Select(m => m.BankTransactionId);
        var unmatched = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.DescriptionPurgedAt == null && t.OccurredAt < unmatchedCutoff
                    && t.FetchedAt < fetchFloor && !linked.Contains(t.Id))
                .OrderBy(t => t.Id), t => t.LicenseId,
            t => { t.Description = null; t.CounterpartyName = null; t.DescriptionPurgedAt = now; },
            "BankTransactions.Description+CounterpartyName",
            $"OccurredAt+{_opt.UnmatchedDescriptionRetentionDays}g, {floor}, eşleşmeyen", ct);

        // 4. Gerekçe metni bağlı hareketin açıklamasıyla gider (bu koşuda ya da önce boşaltılmış). UpdatedAt eşzamanlılık
        //    jetonudur, her yazan ilerletir: arada okuyup yazan bir bağdaştırma çakışmayı görsün.
        var evidence = await PurgeInBatchesAsync(
            _db.PaymentMatches.Where(m => m.Evidence != null
                    && _db.BankTransactions.Any(t => t.Id == m.BankTransactionId && t.DescriptionPurgedAt != null))
                .OrderBy(m => m.Id), m => m.LicenseId,
            m => { m.Evidence = null; m.UpdatedAt = now; }, "PaymentMatches.Evidence", "hareketin açıklamasıyla", ct);

        // 5. IBAN–müşteri bağı: son doğrulanmış havaleden (P1 öncesi satırda öğrenmeden).
        var memories = await DeleteInBatchesAsync(
            _db.CustomerIbanMemories.Where(m => (m.LastConfirmedAt ?? m.CreatedAt) < memoryCutoff).OrderBy(m => m.Id),
            m => m.LicenseId, "CustomerIbanMemories", $"LastConfirmedAt+{_opt.IbanMemoryRetentionMonths}ay", ct);

        var result = new BankRetentionResult(raw, unmatched, evidence, transactions, matches, gaps, memories);
        if (result.Total > 0) _log.LogInformation("Banka veri saklama: {Result}", result);
        // Başarılı koşunun kaydı, silecek satır olmasa da (iç imha prosedürü §4.2-3: son başarılı çalışma buradan okunur).
        DataDestruction.RecordRun(_db, JobId, $"işlem tarihi+{_opt.TransactionRetentionDays}g / ham +{_opt.RawJsonRetentionDays}g / " +
            $"eşleşmeyen +{_opt.UnmatchedDescriptionRetentionDays}g / IBAN bağı +{_opt.IbanMemoryRetentionMonths}ay / {floor}");
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return result;
    }

    /// <summary>Süresi dolan hareketleri eşleştirme satırlarıyla birlikte siler (eşleşme FK kaskadına bırakılmaz: jetonu
    /// sınansın, sayısı kayda geçsin); her parti imha kaydını satırın lisansı başına yazar.</summary>
    private async Task<(int Transactions, int Matches)> DeleteExpiredTransactionsAsync(DateTimeOffset cutoff,
        DateTimeOffset fetchFloor, string criterion, CancellationToken ct)
    {
        int transactions = 0, matches = 0;
        while (true)
        {
            var batch = await _db.BankTransactions.Where(t => t.OccurredAt < cutoff && t.FetchedAt < fetchFloor)
                .OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            var ids = batch.Select(t => t.Id).ToList();
            var rows = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
            _db.PaymentMatches.RemoveRange(rows);
            _db.BankTransactions.RemoveRange(batch);
            RecordPerLicense(batch.Select(t => t.LicenseId), "BankTransactions", DataDestruction.Actions.Deleted, criterion);
            RecordPerLicense(rows.Select(m => m.LicenseId), "PaymentMatches", DataDestruction.Actions.Deleted, criterion);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            transactions += batch.Count;
            matches += rows.Count;
            if (batch.Count < BatchSize) break;
        }
        return (transactions, matches);
    }

    /// <summary>Partinin imha kaydı satırın lisansı başına (yayıncı kendi verisinin imha kanıtını isteyebilir: VİS denetim ve
    /// bilgi verme). Satır sayısı silinen/boşaltılan kümeden. İzleyiciye ekler; partinin SaveChanges'i yazar.</summary>
    private void RecordPerLicense(IEnumerable<Guid> rowLicenses, string target, string action, string criterion)
    {
        foreach (var license in rowLicenses.GroupBy(l => l))
            DataDestruction.Record(_db, license.Key, target, action, license.Count(), criterion,
                DataDestruction.Triggers.Retention, DataDestruction.SystemActor);
    }

    /// <summary>Sıralı sorgunun seçtiği satırları <see cref="BatchSize"/>'lık partilerle siler; silinen satır sorgudan düşer,
    /// her parti baştan sorgulanır.</summary>
    private async Task<int> DeleteInBatchesAsync<T>(IQueryable<T> expired, Func<T, Guid> licenseOf, string target,
        string criterion, CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await expired.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            _db.RemoveRange(batch);
            RecordPerLicense(batch.Select(licenseOf), target, DataDestruction.Actions.Deleted, criterion);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }

    /// <summary><paramref name="expired"/>'i <see cref="BatchSize"/>'lık partilerle boşaltır. Sorgu, boşaltılan satırı artık
    /// seçmeyecek biçimde yazılmalı (boşaltılan alan filtrenin içinde): her parti baştan sorgulanır, Skip gerekmez.</summary>
    private async Task<int> PurgeInBatchesAsync<T>(IQueryable<T> expired, Func<T, Guid> licenseOf, Action<T> purge,
        string target, string criterion, CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await expired.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var row in batch) purge(row);
            RecordPerLicense(batch.Select(licenseOf), target, DataDestruction.Actions.Nulled, criterion);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }
}
