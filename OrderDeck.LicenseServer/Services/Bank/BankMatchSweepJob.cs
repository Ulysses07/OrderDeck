using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Telafi taraması: <see cref="MatchingBankTransactionSink"/>'in kaçırdığı gelen hareketleri eşleştirir. Çekim işi bir
/// hareketi sink'e yalnız bir kez verir (<see cref="IBankTransactionSink"/> sözleşmesi); sink düşerse (DB kesintisi, hata)
/// hareket sonsuza dek önerisiz kalırdı. Son <see cref="LookbackDays"/> gündeki, tutarı sıfır olmayan, hiç
/// <see cref="PaymentMatch"/>'i olmayan GELEN hareketler en eskiden başlanarak koşu başına en çok <see cref="MaxPerRun"/>
/// tane eşleştirilir. Eşleştirici her işlenen harekete bir satır yazar (Proposed ya da dışlanan dahil NoProposal): işlenen
/// hareket bir daha taranmaz.
/// <para>Bir hareketin hatası sıradakileri bekletmez: loglanır (hareket Id'si + istisna; açıklama, ad, tutar değil),
/// sıradakine geçilir, sonda tek <see cref="AggregateException"/> fırlar (Hangfire koşuyu Failed gösterir). Aksi hâlde en
/// eskiden başlayan tarama kalıcı düşen tek bir harekette durup arkasındakileri aç bırakırdı. Yalnız işin kendi iptali
/// döngüyü hemen keser.</para>
/// <para>Çekimle eşzamanlı koşabilir: aynı hareketi sink de eşleştiriyorsa eşleştirici tek satırda buluşturur (hareket başına
/// tek öneri indeksi).</para></summary>
[DisableConcurrentExecution(LockResource, 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankMatchSweepJob
{
    public const string LockResource = "bank-match-sweep";
    public const int LookbackDays = 30;
    public const int MaxPerRun = 500;
    /// <summary>Bu kadar hareketten bir izleyici boşaltılır: öneri satırları koşu boyunca birikmesin.</summary>
    public const int TrackerClearInterval = 50;

    private readonly LicenseDbContext _db;
    private readonly PaymentMatcher _matcher;
    private readonly ILogger<BankMatchSweepJob> _log;

    public BankMatchSweepJob(LicenseDbContext db, PaymentMatcher matcher, ILogger<BankMatchSweepJob> log)
    {
        _db = db; _matcher = matcher; _log = log;
    }

    /// <summary>Eşleştirilen hareket sayısını döner.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-LookbackDays);
        var missed = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.Direction == BankTransactionDirection.Incoming && t.Amount != 0 && t.OccurredAt >= since
                && !_db.PaymentMatches.Any(m => m.BankTransactionId == t.Id))
            .OrderBy(t => t.OccurredAt).ThenBy(t => t.Id)
            .Take(MaxPerRun)
            .ToListAsync(ct);
        if (missed.Count == 0) return 0;

        var matched = 0;
        var failures = new List<Exception>();
        for (var i = 0; i < missed.Count; i++)
        {
            var tx = missed[i];
            try
            {
                await _matcher.MatchAsync(tx, ct);
                matched++;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Banka eşleştirme taraması: hareket {TransactionId} eşleştirilemedi; sonraki koşu yeniden dener", tx.Id);
                failures.Add(ex);
            }
            if ((i + 1) % TrackerClearInterval == 0) _db.ChangeTracker.Clear();
        }
        _db.ChangeTracker.Clear();

        _log.LogInformation("Banka eşleştirme taraması: {Matched} kaçırılmış hareket eşleştirildi, {Failed} düştü",
            matched, failures.Count);
        if (failures.Count > 0)
            throw new AggregateException($"Banka eşleştirme taraması {failures.Count} harekette düştü.", failures);
        return matched;
    }
}
