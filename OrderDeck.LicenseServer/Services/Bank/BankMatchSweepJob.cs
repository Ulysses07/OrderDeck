using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Telafi taraması: <see cref="MatchingBankTransactionSink"/>'in kaçırdığı gelen hareketleri eşleştirir. Çekim işi bir
/// hareketi sink'e yalnız bir kez verir (<see cref="IBankTransactionSink"/> sözleşmesi); sink düşerse (DB kesintisi, hata)
/// hareket sonsuza dek önerisiz kalırdı. Obifin bağlantısı olan lisansların son <see cref="LookbackDays"/> gündeki, tutarı
/// sıfır olmayan, en az <see cref="FetchGrace"/> önce çekilmiş, hiç <see cref="PaymentMatch"/>'i olmayan GELEN hareketleri en
/// eskiden başlanarak koşu başına en çok <see cref="MaxPerRun"/> tane eşleştirilir. Eşleştirici her işlenen harekete bir satır
/// yazar (Proposed ya da dışlanan dahil NoProposal): işlenen hareket bir daha taranmaz. Sink gibi eşleştirmenin ardından
/// hareketle açık gap'i de çözmeyi dener (<see cref="PaymentMatchReconciler.MatchAndResolveGapAsync"/>); yoksa taramanın
/// eşleştirdiği hareketi bekleyen gap hiç çözülmezdi. Öneri yazılıp gap çözümü düşerse hareket bir daha taranmaz, gap açık
/// kalır.
/// <para>Lisans süzgeci sorguyu (LicenseId, Direction, OccurredAt) indeksinde lisans başına aramaya çevirir (bağlantısız
/// lisansın hareketi de gelmez). Tampon süre: uzun bir çekim hareketi kaydedip henüz sink'e vermemişken (çekim :05'te
/// başlar, tarama :07'de) tarama aynı hareketi kapmasın; sink bir hareketi çekimden saniyeler sonra işler.</para>
/// <para>Bir hareketin hatası sıradakileri bekletmez: loglanır (hareket Id'si + istisna; açıklama, ad, tutar değil),
/// sıradakine geçilir, sonda tek <see cref="AggregateException"/> fırlar (Hangfire koşuyu Failed gösterir). Aksi hâlde en
/// eskiden başlayan tarama kalıcı düşen tek bir harekette durup arkasındakileri aç bırakırdı. Yalnız ilk
/// <see cref="MaxReportedFailures"/> hata Error düzeyinde loglanır ve istisnaya konur (gerisi Debug; toplam mesajda):
/// eşleştiricideki bir hata her satırı düşürürse koşu yüzlerce yığın izi üretmesin. Art arda
/// <see cref="ConsecutiveFailureLimit"/> hata zehirli satır değil kesintidir: koşu kesilir. Yalnız işin kendi iptali
/// döngüyü hemen keser, hata sayılmaz.</para>
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
    /// <summary>Koşu başına Error düzeyinde loglanan ve <see cref="AggregateException"/>'a konan en fazla hata.</summary>
    public const int MaxReportedFailures = 5;
    /// <summary>Arada tek başarı olmadan bu kadar hata: kesinti sayılır, koşu kesilir.</summary>
    public const int ConsecutiveFailureLimit = 20;
    /// <summary>Çekimden bu kadar süre geçmemiş hareket taranmaz: sink'in sırasındadır.</summary>
    public static readonly TimeSpan FetchGrace = TimeSpan.FromMinutes(15);

    private readonly LicenseDbContext _db;
    private readonly PaymentMatchReconciler _reconciler;
    private readonly ILogger<BankMatchSweepJob> _log;

    public BankMatchSweepJob(LicenseDbContext db, PaymentMatchReconciler reconciler, ILogger<BankMatchSweepJob> log)
    {
        _db = db; _reconciler = reconciler; _log = log;
    }

    /// <summary>Eşleştirilen hareket sayısını döner.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-LookbackDays);
        var settled = now - FetchGrace;
        var missed = await _db.BankTransactions.AsNoTracking()
            .Where(t => _db.ObifinConnections.Select(c => c.LicenseId).Contains(t.LicenseId)
                && t.Direction == BankTransactionDirection.Incoming && t.Amount != 0 && t.OccurredAt >= since
                && t.FetchedAt < settled
                && !_db.PaymentMatches.Any(m => m.BankTransactionId == t.Id))
            .OrderBy(t => t.OccurredAt).ThenBy(t => t.Id)
            .Take(MaxPerRun)
            .ToListAsync(ct);
        if (missed.Count == 0) return 0;

        var matched = 0;
        var failed = 0;
        var consecutive = 0;
        var reported = new List<Exception>();
        for (var i = 0; i < missed.Count; i++)
        {
            var tx = missed[i];
            try
            {
                await _reconciler.MatchAndResolveGapAsync(tx, ct);
                matched++;
                consecutive = 0;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _db.ChangeTracker.Clear();
                failed++;
                consecutive++;
                if (reported.Count < MaxReportedFailures)
                {
                    reported.Add(ex);
                    _log.LogError(ex, "Banka eşleştirme taraması: hareket {TransactionId} eşleştirilemedi; sonraki koşu yeniden dener", tx.Id);
                }
                else
                {
                    _log.LogDebug(ex, "Banka eşleştirme taraması: hareket {TransactionId} eşleştirilemedi; sonraki koşu yeniden dener", tx.Id);
                }
                if (consecutive >= ConsecutiveFailureLimit) break;
            }
            if ((i + 1) % TrackerClearInterval == 0) _db.ChangeTracker.Clear();
        }
        _db.ChangeTracker.Clear();

        var aborted = consecutive >= ConsecutiveFailureLimit;
        _log.LogInformation("Banka eşleştirme taraması: {Matched} kaçırılmış hareket eşleştirildi, {Failed} düştü",
            matched, failed);
        if (aborted)
            _log.LogWarning("Banka eşleştirme taraması: art arda {Limit} hata — kesinti sayıldı, koşu kesildi", ConsecutiveFailureLimit);
        if (failed > 0)
            throw new AggregateException(
                $"Banka eşleştirme taraması {failed} harekette düştü"
                + (aborted ? $"; art arda {ConsecutiveFailureLimit} hata, koşu kesildi" : "")
                + $" (ilk {reported.Count} hata içeride).", reported);
        return matched;
    }
}
