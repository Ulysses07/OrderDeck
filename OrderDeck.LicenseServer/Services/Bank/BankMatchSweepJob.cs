using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Telafi taraması: <see cref="MatchingBankTransactionSink"/>'in kaçırdığı gelen hareketleri eşleştirir. Çekim işi bir
/// hareketi sink'e yalnız bir kez verir (<see cref="IBankTransactionSink"/> sözleşmesi); sink düşerse (DB kesintisi, hata)
/// hareket sonsuza dek önerisiz kalırdı. Eşleştirmeye açık Obifin bağlantısı (<see cref="ObifinConnectionService.MatchingActive"/>;
/// KVKK K-6) olan lisansların son <see cref="LookbackDays"/> gündeki, tutarı
/// sıfır olmayan, en az <see cref="FetchGrace"/> önce çekilmiş, hiç <see cref="PaymentMatch"/>'i olmayan GELEN hareketleri en
/// eskiden başlanarak koşu başına en çok <see cref="MaxPerRun"/> tane eşleştirilir. Eşleştirici her işlenen harekete bir satır
/// yazar (Proposed ya da dışlanan dahil NoProposal): işlenen hareket bir daha taranmaz. Sink gibi eşleştirmenin ardından
/// hareketle açık gap'i de çözmeyi dener (<see cref="PaymentMatchReconciler.MatchAndResolveGapAsync"/>); yoksa taramanın
/// eşleştirdiği hareketi bekleyen gap hiç çözülmezdi. Öneri yazılıp gap çözümü düşerse hareket bir daha taranmaz, gap açık
/// kalır.
/// <para><b>2. aşama: IBAN hafızası değişince.</b> Eşleştirici hafızayı yalnız hesap anında okur. Kural: insan kararı olmayan bir
/// satırın önerisi hafızanın GÜNCEL haliyle hesaplanmış olmalı. Elle eşleme ve kaldırma bunu anında uygular
/// (<see cref="PaymentMatchReconciler"/>, "IBAN hafızası eski havalelere de uygulanır"); bu aşama telafisidir: o yeniden
/// değerlendirme düştüyse, bağ dekont onayıyla öğrenildiyse ya da karar bu kuraldan önce verildiyse (ilk koşu geriye dönük
/// doldurmayı kendisi yapar). Aynı lisans kümesi; pencere <see cref="PaymentMatchReconciler.ReevaluationSince"/> (eşleşmeyen
/// açıklamanın saklama sınırından bir gün içeride). Satırı olan, kararsız (öneri ya da öneri yok, karar anı ve dekont boş),
/// açıklaması silinmemiş, IBAN özetli gelen hareketlerden şunlar: (a) özetinin AYNI lisansta bağı var, satır bağdan ÖNCE
/// hesaplanmış ve bağın özeti lisansta özeti geçerli (<see cref="BankTransaction.CounterpartyIbanHashValid"/>) bir harekette
/// görünüyor — yoksa eski maskeli özetten öğrenilmiş bir bağ aynı maskeyi taşıyan başka göndericilere yayılırdı; ya da (b)
/// kanıtında "iban-memory" geçiyor ve aynı lisansta bağ artık yok (kaldırmanın anlık geri alması koşamadı). En yeniden
/// başlanarak koşu başına en çok <see cref="MaxReevaluationsPerRun"/>. Hesaplandıktan sonra dışlama listesine giren kodlu
/// hareket bellekte atlanmaz: eşleştirici ona "excluded:KOD" yazar, sonraki koşunun seçimi o öneki eler (atlansaydı her saat
/// yeniden seçilip kotada yer kaplardı). Yeniden hesap <see cref="PaymentMatchReconciler.ReevaluateUndecidedAsync"/> (yalnız
/// eşleştirici, insan kararı kilidi ve jeton; hareket her denemeden önce taze okunur, iki çakışmada satır atlanır); günlükteki
/// sayı yalnız kararsız kalan yeniden hesapları sayar. İdempotent: yeniden hesaplanan satır bağdan yenidir, (b)'nin kanıtı artık
/// bağsız "iban-memory" taşımaz. Bilinen sınır: bağdan sonra hesaplanmış ama o an müşteri projeksiyonu henüz gelmemiş satırı
/// yakalamaz. Performans: (LicenseId, CounterpartyIbanHash) indeksi yok; (a)'nın iç içe EXISTS'i etkin lisansların hareketlerini
/// tarar. Bugünkü hacimde kabul edilebilir; büyürse filtreli indeks ayrı bir göçle eklenir.</para>
/// <para>Lisans süzgeci sorguyu (LicenseId, Direction, OccurredAt) indeksinde lisans başına aramaya çevirir (bağlantısız,
/// durdurulmuş ya da yerel verisi silinmiş lisansın hareketi de gelmez). Tampon süre: uzun bir çekim hareketi kaydedip henüz sink'e vermemişken (çekim :05'te
/// başlar, tarama :07'de) tarama aynı hareketi kapmasın; sink bir hareketi çekimden saniyeler sonra işler.</para>
/// <para>Bir hareketin hatası sıradakileri bekletmez: loglanır (hareket Id'si + istisna; açıklama, ad, tutar değil),
/// sıradakine geçilir, sonda tek <see cref="AggregateException"/> fırlar (Hangfire koşuyu Failed gösterir). Aksi hâlde en
/// eskiden başlayan tarama kalıcı düşen tek bir harekette durup arkasındakileri aç bırakırdı. Yalnız ilk
/// <see cref="MaxReportedFailures"/> hata Error düzeyinde loglanır ve istisnaya konur (gerisi Debug; toplam mesajda):
/// eşleştiricideki bir hata her satırı düşürürse koşu yüzlerce yığın izi üretmesin. Art arda
/// <see cref="ConsecutiveFailureLimit"/> hata zehirli satır değil kesintidir: koşu kesilir (2. aşama da koşmaz). Sayaçlar iki
/// aşamada ortaktır. Yalnız işin kendi iptali döngüyü hemen keser, hata sayılmaz.</para>
/// <para>Çekimle eşzamanlı koşabilir: aynı hareketi sink de eşleştiriyorsa eşleştirici tek satırda buluşturur (hareket başına
/// tek öneri indeksi).</para></summary>
[DisableConcurrentExecution(LockResource, 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankMatchSweepJob
{
    public const string LockResource = "bank-match-sweep";
    public const int LookbackDays = 30;
    public const int MaxPerRun = 500;
    /// <summary>2. aşamada (IBAN hafızası değişince) koşu başına en çok yeniden değerlendirilen satır.</summary>
    public const int MaxReevaluationsPerRun = 500;
    /// <summary>Bu kadar hareketten bir izleyici boşaltılır: öneri satırları koşu boyunca birikmesin.</summary>
    public const int TrackerClearInterval = 50;
    /// <summary>Koşu başına Error düzeyinde loglanan ve <see cref="AggregateException"/>'a konan en fazla hata.</summary>
    public const int MaxReportedFailures = 5;
    /// <summary>Arada tek başarı olmadan bu kadar hata: kesinti sayılır, koşu kesilir.</summary>
    public const int ConsecutiveFailureLimit = 20;
    /// <summary>Çekimden bu kadar süre geçmemiş hareket taranmaz: sink'in sırasındadır.</summary>
    public static readonly TimeSpan FetchGrace = TimeSpan.FromMinutes(15);

    /// <summary>Eşleştiricinin dışlanan harekete yazdığı kanıtın öneki (<c>excluded:KOD</c>).</summary>
    private const string ExcludedEvidencePrefix = "excluded:";
    /// <summary>Eşleştiricinin IBAN hafızasından gelen önerinin kanıtında geçen işaret.</summary>
    private const string IbanMemoryEvidence = "iban-memory";

    private readonly LicenseDbContext _db;
    private readonly PaymentMatchReconciler _reconciler;
    private readonly ILogger<BankMatchSweepJob> _log;

    public BankMatchSweepJob(LicenseDbContext db, PaymentMatchReconciler reconciler, ILogger<BankMatchSweepJob> log)
    {
        _db = db; _reconciler = reconciler; _log = log;
    }

    /// <summary>1. aşamada eşleştirilen hareket sayısını döner (2. aşamanın sayısı günlükte).</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-LookbackDays);
        var settled = now - FetchGrace;
        // KVKK K-6: yalnız eşleştirmeye açık bağlantının lisansı (ObifinConnectionService.MatchingActive). Alt sorgu yerel
        // değişkende (DisableEndedLicensesAsync'teki activeLicenseIds kalıbı): ifade özelliği lambda'nın içinde çağrılsaydı EF onu
        // çeviremeyebilirdi.
        var matchingLicenses = _db.ObifinConnections.Where(ObifinConnectionService.MatchingActive).Select(c => c.LicenseId);
        var missed = await _db.BankTransactions.AsNoTracking()
            .Where(t => matchingLicenses.Contains(t.LicenseId)
                && t.Direction == BankTransactionDirection.Incoming && t.Amount != 0 && t.OccurredAt >= since
                && t.FetchedAt < settled
                && !_db.PaymentMatches.Any(m => m.BankTransactionId == t.Id))
            .OrderBy(t => t.OccurredAt).ThenBy(t => t.Id)
            .Take(MaxPerRun)
            .ToListAsync(ct);

        var failures = new Failures();
        var matched = 0;
        for (var i = 0; i < missed.Count; i++)
        {
            var tx = missed[i];
            try
            {
                await _reconciler.MatchAndResolveGapAsync(tx, ct);
                matched++;
                failures.Consecutive = 0;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _db.ChangeTracker.Clear();
                if (Fail(failures, ex, tx.Id, reevaluation: false)) break;
            }
            if ((i + 1) % TrackerClearInterval == 0) _db.ChangeTracker.Clear();
        }
        _db.ChangeTracker.Clear();
        if (missed.Count > 0)
            _log.LogInformation("Banka eşleştirme taraması: {Matched} kaçırılmış hareket eşleştirildi, {Failed} düştü",
                matched, failures.Failed);

        var reevaluated = failures.Aborted ? 0
            : await ReevaluateAfterIbanMemoryChangesAsync(matchingLicenses, _reconciler.ReevaluationSince(now), failures, ct);
        if (reevaluated > 0)
            _log.LogInformation("Banka eşleştirme taraması: IBAN hafızası değiştikten sonra {Reevaluated} kararsız satır yeniden "
                + "değerlendirildi", reevaluated);

        if (failures.Aborted)
            _log.LogWarning("Banka eşleştirme taraması: art arda {Limit} hata — kesinti sayıldı, koşu kesildi", ConsecutiveFailureLimit);
        if (failures.Failed > 0)
            throw new AggregateException(
                $"Banka eşleştirme taraması {failures.Failed} harekette düştü"
                + (failures.Aborted ? $"; art arda {ConsecutiveFailureLimit} hata, koşu kesildi" : "")
                + $" (ilk {failures.Reported.Count} hata içeride).", failures.Reported);
        return matched;
    }

    /// <summary>2. aşama (sınıf özeti): hafızanın güncel haliyle hesaplanmamış kararsız satırlar. Yeniden hesaplanıp kararsız kalan
    /// satır sayısını döner (araya insan kararı giren ya da atlanan satır sayılmaz).</summary>
    private async Task<int> ReevaluateAfterIbanMemoryChangesAsync(IQueryable<Guid> matchingLicenses, DateTimeOffset since,
        Failures failures, CancellationToken ct)
    {
        var stale = await (from t in _db.BankTransactions.AsNoTracking()
                           join m in _db.PaymentMatches.AsNoTracking() on t.Id equals m.BankTransactionId
                           where matchingLicenses.Contains(t.LicenseId)
                                 && t.Direction == BankTransactionDirection.Incoming && t.Amount != 0 && t.OccurredAt >= since
                                 && t.DescriptionPurgedAt == null && t.CounterpartyIbanHash != null
                                 && (m.Status == PaymentMatchStatus.Proposed || m.Status == PaymentMatchStatus.NoProposal)
                                 && m.DecidedAt == null && m.PaymentId == null
                                 && !(m.Evidence != null && m.Evidence.StartsWith(ExcludedEvidencePrefix))
                                 && (_db.CustomerIbanMemories.Any(b => b.LicenseId == t.LicenseId && b.IbanHash == t.CounterpartyIbanHash
                                         && m.UpdatedAt < b.CreatedAt
                                         && _db.BankTransactions.Any(v => v.LicenseId == b.LicenseId && v.CounterpartyIbanHash == b.IbanHash
                                             && v.CounterpartyIbanHashValid))
                                     || (m.Evidence != null && m.Evidence.Contains(IbanMemoryEvidence)
                                         && !_db.CustomerIbanMemories.Any(b => b.LicenseId == t.LicenseId
                                             && b.IbanHash == t.CounterpartyIbanHash)))
                           orderby t.OccurredAt descending, t.Id
                           select t)
            .Take(MaxReevaluationsPerRun)
            .ToListAsync(ct);

        var reevaluated = 0;
        for (var i = 0; i < stale.Count; i++)
        {
            var tx = stale[i];
            // Dışlama kodları ayardır, kanıt öneki hesaplandığı andaki listeyi yansıtır. Sonradan dışlanan hareket bellekte
            // atlanmaz: eşleştirici "excluded:KOD" yazar, sonraki koşunun seçimi öneki eler (sınıf özeti).
            try
            {
                if (await _reconciler.ReevaluateUndecidedAsync(tx, ct) is not null) reevaluated++;
                failures.Consecutive = 0;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _db.ChangeTracker.Clear();
                if (Fail(failures, ex, tx.Id, reevaluation: true)) break;
            }
            if ((i + 1) % TrackerClearInterval == 0) _db.ChangeTracker.Clear();
        }
        _db.ChangeTracker.Clear();
        return reevaluated;
    }

    /// <summary>Bir hareketin hatasını sayar ve loglar (sınıf özeti); koşu kesilmeliyse true.</summary>
    private bool Fail(Failures failures, Exception ex, Guid transactionId, bool reevaluation)
    {
        failures.Failed++;
        failures.Consecutive++;
        var level = failures.Reported.Count < MaxReportedFailures ? LogLevel.Error : LogLevel.Debug;
        if (level == LogLevel.Error) failures.Reported.Add(ex);
        if (reevaluation)
            _log.Log(level, ex, "Banka eşleştirme taraması: hareket {TransactionId} IBAN hafızasıyla yeniden değerlendirilemedi; "
                + "sonraki koşu yeniden dener", transactionId);
        else
            _log.Log(level, ex, "Banka eşleştirme taraması: hareket {TransactionId} eşleştirilemedi; sonraki koşu yeniden dener",
                transactionId);
        return failures.Aborted;
    }

    /// <summary>Koşunun iki aşamasında ortak hata sayaçları.</summary>
    private sealed class Failures
    {
        public int Failed { get; set; }
        public int Consecutive { get; set; }
        public List<Exception> Reported { get; } = new();
        public bool Aborted => Consecutive >= ConsecutiveFailureLimit;
    }
}
