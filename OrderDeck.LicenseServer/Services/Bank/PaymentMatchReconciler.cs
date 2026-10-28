using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// İnsan kararını gölge önerisine bağlar (spec §6). Aday = aynı lisans, gelen, tutar eşit,
/// ±2 gün, henüz bir dekonta bağlanmamış, başka bir müşteriye karar verilmemiş ve eşleştiricinin dışlamadığı
/// (<see cref="PaymentMatcher.IsExcluded"/>: POS tahsilatı vb. müşteri havalesi değildir). Tek aday → bağla; çoklu → gönderen
/// adının tüm tokenları açıklamada geçen TEK aday; hâlâ çoklu/yok → PaymentMatchGap. Ret hiçbir şey yapmaz. Elle eşleme
/// dışlanan kodlu hareketi de kabul eder: admin'in açık kararıdır. Onun karşı IBAN'ını ise öğretmez: POS mutabakatının
/// karşı IBAN'ı (varsa) genellikle üye iş yeri havuz hesabıdır, müşterinin değil.
/// <para><b>Onay IBAN'ı yalnız doğrulanmış bağdan öğretir</b>: öneri dekontun müşterisini gösteriyorsa ya da gönderen
/// adının (≥3 harfli) tüm token'ları hareketin açıklamasında geçiyorsa. Tek aday bağı yalnız tutar ve zamana dayanabilir;
/// sabit fiyatlı satışta aynı tutarlı başka bir müşterinin havalesi tek aday kalabilir (dekontun kendi havalesi henüz
/// çekilmemiş, ya da gap'e ilk gelen aynı tutarlı havale). O bağdan öğrenilen IBAN iki müşterinin hafızasını takas eder
/// ve admin kaldırana dek sonraki havalelere yanlış öneri üretirdi. Doğrulanmamış bağ yine kurulur ve karşılaştırılır
/// (ölçüm), yalnız öğretmez; günlüğe yalnız kimlikler yazılır. Onay işinin gecikmesi
/// (<see cref="PaymentMatchReconcileJob.ApprovalDelay"/>) bu tek adayların çoğunu baştan önler.</para>
/// <para><b>Bir insan kararı ötekini ezmez.</b> Başka müşteriye elle verilmiş hareket onayda aday değildir; aynı müşteriye
/// verilmişse dekont satıra yalnız iliştirilir, kararın durumu ve anı korunur. Kararın kilidi <see cref="PaymentMatch.DecidedAt"/>'tir,
/// müşteri değil: KVKK silmesi kararın müşterisini boşaltır, anını ve sonucunu ölçüm için bırakır
/// (<see cref="BankSubjectScrub.MatchForCustomer"/>); o satır da başka bir kişinin kararıdır, kimsenin dekontuna aday olmaz
/// (yoksa silinen kişinin havalesi başka müşterinin ödemesi sayılırdı). Elle eşleme yalnız karara bağlanmamış
/// (<see cref="PaymentMatch.DecidedAt"/> ve <see cref="PaymentMatch.PaymentId"/> boş) harekete verilir; yeniden karar önce
/// kaldırmadan geçer. Kaldırma, hareketin öğrettiği hafıza satırını SİLER; başka havalenin öğrettiği bağı tazelemişse bağın
/// süresini kalan doğrulamalara geri çeker, kalan yoksa bağı imha kaydıyla siler (<see cref="RewindIbanConfirmationAsync"/>);
/// öneriyi yeniden hesaplar. Kimlikler admin formundan gelir: bu kural serviste uygulanır.</para>
/// <para><b>Kaldırma dekontu ölçümden düşürmez.</b> Dekonta bağlı satırın kaldırılmasında dekont açık gap'e döner, aynı
/// SaveChanges'te: gap'i varsa (gap çözümüyle bağlanmışsa) yeniden açılır, yoksa (onayda bağlanmışsa)
/// <see cref="PaymentMatchGapReason.UnlinkedByAdmin"/> gap'i yazılır (<see cref="ReturnPaymentsToGapsAsync"/>; hesap reddi de
/// bunu kullanır). Bu kural olmasaydı dekont ne bağlı ne gap kümesinde kalırdı, çözülmüş gap de bağı kalkmış hareketi gösterirdi. Kaldırılan hareketle gap çözümü koşulmaz (yalnız eşleştirici yeniden
/// hesaplar): admin'in kaldırdığı bağ kendiliğinden geri gelmez.</para>
/// <para>Sonradan gelen hareket açık gap'i ancak dekontun aday seçimi yeniden koşulduğunda TEK aday kendisiyse çözer
/// (spec §3 ResolvedBankTransactionId). Onaydaki belirsizlik sürüyorsa (iki aday hâlâ bağsız) ya da aynı tutarlı birden
/// çok hareket geldiyse gap açık kalır: belirsizlik tahminle çözülmez.</para>
/// <para><b>Durdurulmuş bağlantı (KVKK K-6).</b> Lisansın bağlantısı eşleştirmeye kapalıysa
/// (<see cref="ObifinConnectionService.MatchingActive"/> yanlış: Disabled ya da yerel verisi silinmiş) onay bağdaştırması ve gap
/// çözümü hiçbir şey yazmaz, elle eşleme <see cref="MatchingStoppedMessage"/> fırlatır: saklı hareket işlenmez, IBAN öğrenilmez,
/// LastConfirmedAt tazelenmez, verisi silinmiş bağlantıda gap birikmez. Bağlantısı hiç olmayan lisans bugünkü gibi. Kaldırma
/// açık kalır (bağ siler), ama ardından yeniden hesap koşmaz: saklı hareketin açıklamasından ve IBAN hafızasından yeni öneri
/// yazmak durdurulmuş bağlantının verisini işlemek olurdu. Satır öneri yok kalır, <see cref="UnmatchAsync"/> false döner.</para>
/// <para><see cref="BankHasher"/> istemez: yalnız harekette ve hafızada saklı hash'leri karşılaştırır.</para>
/// <para><b>Eşzamanlılık.</b> <see cref="PaymentMatch.UpdatedAt"/> eşzamanlılık jetonudur; her kayıtta DbContext ilerletir. Bir
/// işlem okuduğu satır kaydetmeden önce değiştiyse (DbUpdateConcurrencyException: eşleştiricinin yeniden hesabı, başka bir
/// insan kararı), seçtiği aday seçimle satırın izlenerek okunması arasında başka bir karara bağlandıysa (bayat seçim;
/// jeton bu pencereyi görmez) ya da tekil bir indekse takıldıysa (DbUpdateException: aynı dekont iki harekete —
/// filtreli PaymentId indeksi —, aynı dekonta ikinci gap, aynı IBAN'a ikinci hafıza satırı) denemenin izi atılır ve işlem
/// BİR kez baştan koşar:
/// başındaki "zaten bağlı / gap var / hafıza var" denetimleri eşzamanlı yazanın sonucunu görür, yani indeks ihlali "zaten
/// bağlı" olarak sessizce sonuçlanır. İkinci çakışmada vazgeçilir: iş yolları (<see cref="ReconcileApprovalAsync"/>,
/// <see cref="TryResolveGapAsync"/>) uyarı loglar, admin yolları (<see cref="ManualMatchAsync"/>,
/// <see cref="UnmatchAsync"/>) admin'e gösterilebilir <see cref="ObifinValidationException"/> fırlatır. Kaldırma yeniden
/// denemede kendini ancak taze satırdaki insan kararı (dekont bağı, müşteri, karar anı) ilk denemenin gördüğüyle aynıysa
/// yeniden uygular; değiştiyse (ör. arada dekont onayı satıra dekontu ekledi) <see cref="ConflictMessage"/> fırlatır:
/// admin'in görmediği bir bağı ya da kararı kaldırmaz.</para>
/// <para><b>Kaldırmanın ardından yeniden hesap denemenin parçası değildir.</b> Kaldırma kaydedildikten SONRA, yeniden denenen
/// denemenin dışında koşar. Düşerse (iptal, eşleştiricinin iki çakışması, DB hatası) kaldırma geri alınmaz ve başarılıdır:
/// günlüğe hareket kimliği, istisna türü ve yığın izi yazılır, <see cref="UnmatchAsync"/> false döner. Satır insan kararsız,
/// öneri yok durumunda kalır; öneri alanlarını kaldırma durumla birlikte boşaltmıştır (eşleştiricinin hiç yazmadığı "öneri yok
/// + eski öneri" karışımı sonraki dekont bağını ya da elle eşlemeyi bayat öneriye göre sınıflandırırdı). Telafi taraması
/// satırı olan hareketi yalnız IBAN hafızası değiştiyse yeniden hesapladığı için öneri çoğunlukla kendiliğinden yeniden
/// üretilmez, admin satırı yine elle eşleyebilir.
/// Yeniden hesap denemenin içinde olsaydı kaydedilmiş kaldırma yeniden denenip "çakışma" diye raporlanır, iptal de sayfanın
/// audit'ini düşürürdü.</para>
/// <para>Düşen deneme izleyicide iz bırakmaz (bu üç tablonun izlenen satırları ve denemenin eklediği imha kaydı ayrılır):
/// kapsamın sonraki SaveChanges'i onu yeniden denemez. Hareketler izlenmeden okunur.</para>
/// <para><b>IBAN hafızası eski havalelere de uygulanır.</b> Eşleştirici hafızayı yalnız hesap anında okur. Elle eşleme YENİ bir
/// bağ yazınca ya da kaldırma bir bağı silince (öğretilmiş satır ya da süre geri çekmesinin silmesi), aynı lisans ve IBAN
/// özetinin kararsız satırları hafızanın güncel haliyle yeniden hesaplanır (<see cref="ReevaluateUndecidedAsync"/>). Elle eşleme
/// var olan bağı TAZELEYİNCE yalnız bağdan önce hesaplanmış kararsız satırlar (satırın jetonu bağın oluşturulmasından eski):
/// bağ değişmez, ondan sonra hesaplananlar onu zaten gördü; bağ dekont onayıyla öğrenildiyse ya da bu kuraldan önce kurulduysa
/// öncekiler taramayı beklemesin. Kararsız: öneri ya da öneri yok, karar anı ve dekont boş; açıklaması silinmemiş, dışlanmamış;
/// pencere <see cref="ReevaluationSince"/> (taramanınkinden ve eşleşmeyen açıklamanın saklama süresinden bir gün içeride: saklama
/// işi açıklamayı jetonu ilerletmeden siler, sınırdaki satırın yeniden hesabı onunla çakışmasız yarışırdı). Özeti KVKK Ö-7
/// kesmesinden önce çekilmiş satır (HashValid=false) da hedeftir: geçerli bir bağın özetine eşitlik aynı tam IBAN demektir; öneri
/// alır, öğretmez. Sonuç yalnız öneridir: karar, dekont ya da yeni bağ yazılmaz (zincirleme öğrenme yok, öğrenme yalnız insan
/// kararından; Faz 1 gölge mod). Açıklamadaki kullanıcı adı başka müşteriyi gösteriyorsa eşleştiricinin kuralı geçerlidir: öneri
/// yok. Kanıt <see cref="PaymentMatcher.ReevaluationEvidenceSuffix"/> ekini taşır (Faz 2 ölçümü bu satırların dekont kararlarını
/// eşikten ayırır, <see cref="PaymentMatchMetrics"/>). Kararın kaydından SONRA, denemenin dışında koşar (kaldırmanın yeniden
/// hesabıyla aynı kalıp); düşerse karar geri alınmaz (<see cref="ManualMatchResult.ReevaluationFailed"/>). Tetik başına en çok
/// <see cref="MaxReevaluationsPerTrigger"/> satır. Telafisi, dekont onayı yollarının bağları ve düzeltmeden önceki kararların
/// geriye dönük doldurması <see cref="BankMatchSweepJob"/>'ın 2. aşamasıdır. KVKK K-6: durdurulmuş bağlantıda koşmaz.</para>
/// <para>Günlüğe açıklama, ad ya da IBAN yazılmaz; yalnız kimlikler.</para>
/// </summary>
public sealed class PaymentMatchReconciler
{
    public static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(2);

    /// <summary>Bir elle eşlemenin ya da kaldırmanın yeniden değerlendirdiği en çok satır; kalanını telafi taraması yapar.</summary>
    public const int MaxReevaluationsPerTrigger = 100;

    /// <summary>Yeniden değerlendirme penceresinin eşleşmeyen açıklamanın saklama sınırına bıraktığı pay (gün;
    /// <see cref="ReevaluationSince"/>).</summary>
    public const int ReevaluationRetentionMarginDays = 1;

    /// <summary>Admin yolunda ikinci çakışmanın mesajı.</summary>
    public const string ConflictMessage = "Eşleşme bu sırada başka bir işlemle değişti; sayfayı yenileyip yeniden deneyin.";

    /// <summary>Karara bağlanmış harekete elle eşleme denendi: yeniden karar önce kaldırmadan geçer.</summary>
    public const string AlreadyDecidedMessage = "Bu hareket zaten bir karara bağlı; önce kaldırın.";

    /// <summary>Durdurulmuş ya da yerel verisi silinmiş bağlantıda elle eşleme (KVKK K-6).</summary>
    public const string MatchingStoppedMessage =
        "Bu lisansın banka bağlantısı durdurulmuş; durdurulmuş bağlantının verisi eşleştirilmez (KVKK). Önce Etkinleştir.";

    private readonly LicenseDbContext _db;
    private readonly BankOptions _bank;
    private readonly ILogger<PaymentMatchReconciler> _log;
    public PaymentMatcher Matcher { get; }

    public PaymentMatchReconciler(LicenseDbContext db, PaymentMatcher matcher, IOptions<BankOptions> bank,
        ILogger<PaymentMatchReconciler> log)
    { _db = db; Matcher = matcher; _bank = bank.Value; _log = log; }

    /// <summary>Sink ve telafi taramasının ortak yolu: öneriyi yazar, sonra hareketle açık gap'i çözmeyi dener. İki yol da
    /// bunu çağırmalı; yoksa taramanın eşleştirdiği hareket bekleyen gap'i hiç çözmez.</summary>
    public async Task MatchAndResolveGapAsync(BankTransaction tx, CancellationToken ct)
    {
        await Matcher.MatchAsync(tx, ct);
        await TryResolveGapAsync(tx, ct);
    }

    public async Task ReconcileApprovalAsync(Payment payment, CancellationToken ct)
    {
        if (!await RetryOnceAsync(() => ReconcileApprovalOnceAsync(payment, ct), ct))
            _log.LogWarning("Gölge bağdaştırma: ödeme {PaymentId} iki denemede de eşzamanlı bir yazıyla çakıştı; vazgeçildi",
                payment.Id);
    }

    /// <summary>Yeni gelen hareket: aynı tutar ±2 gün, tek AÇIK gap → bağla ve gap'i çöz. Çoklu → bekle.</summary>
    public async Task TryResolveGapAsync(BankTransaction tx, CancellationToken ct)
    {
        if (!await RetryOnceAsync(() => TryResolveGapOnceAsync(tx, ct), ct))
            _log.LogWarning("Gölge bağdaştırma: hareket {TransactionId} ile gap çözümü iki denemede de eşzamanlı bir yazıyla "
                + "çakıştı; vazgeçildi", tx.Id);
    }

    /// <returns>Hareketin IBAN'ının öğrenilip öğrenilmediği; öğrenildiyse aynı IBAN'ın yeniden değerlendirilen kararsız
    /// satırları (sınıf özeti, "IBAN hafızası eski havalelere de uygulanır").</returns>
    /// <exception cref="ObifinValidationException">Hareket ya da müşteri bu lisansta yok; ya da iki denemede de çakışma. Yeniden
    /// değerlendirme düşerse fırlatılmaz: karar kaydedildi.</exception>
    public async Task<ManualMatchResult> ManualMatchAsync(Guid licenseId, Guid transactionId, Guid wpfCustomerId, CancellationToken ct)
    {
        var attempt = new ManualMatchAttempt();
        if (!await RetryOnceAsync(() => ManualMatchOnceAsync(licenseId, transactionId, wpfCustomerId, attempt, ct), ct))
            throw new ObifinValidationException(ConflictMessage);
        // Atanmamış sonuç sessizce "öğrenildi" sayılıp yeniden değerlendirme tetiklemesin.
        var outcome = attempt.Outcome
            ?? throw new InvalidOperationException("Elle eşleme kaydedildi ama IBAN sonucu atanmadı.");
        // Karar kaydedildi. Yeni bağ ve tazelenen bağ tetikler; tazelemede yalnız bağdan önce hesaplanmışlar (sınıf özeti).
        if (outcome is not (ManualMatchIbanOutcome.Learned or ManualMatchIbanOutcome.Refreshed) || attempt.IbanHash is not { } hash)
            return new ManualMatchResult(outcome);
        var r = await TryReevaluateSameIbanAsync(licenseId, hash, transactionId, wpfCustomerId,
            onlyComputedBeforeBond: outcome == ManualMatchIbanOutcome.Refreshed, ct);
        return r is null
            ? new ManualMatchResult(outcome, ReevaluationFailed: true)
            : new ManualMatchResult(outcome, r.Reevaluated, r.ProposedToCustomer, r.More);
    }

    /// <param name="actor">Kaldırma IBAN bağını silerse (<see cref="RewindIbanConfirmationAsync"/>) imha kaydının yapanı: yönetici
    /// sayfası <c>DataDestruction.ActorOf(User)</c> geçirir (KVKK Ö-14; kim yaptığı 24 ayda budanan denetim satırına kalmasın).
    /// Boşsa <see cref="DataDestruction.AdminActor"/>.</param>
    /// <returns>true: öneri yeniden hesaplandı (ya da hareketin satırı yoktu); false: kaldırma kaydedildi, öneri yeniden
    /// hesaplanmadı — hesap düştü ya da bağlantı eşleştirmeye kapalı (sınıf özeti). Kaldırma bir IBAN bağını sildiyse o bağdan
    /// gelen öneriler de geri alınır; bunun düşmesi dönüşü değiştirmez (telafi taraması tamamlar).</returns>
    /// <exception cref="ObifinValidationException">Hareket bu lisansta yok; iki denemede de çakışma; ya da yeniden denemede
    /// satırın insan kararı ilk denemenin gördüğünden farklı. Kaldırma kaydedildiyse fırlatılmaz.</exception>
    public async Task<bool> UnmatchAsync(Guid licenseId, Guid transactionId, CancellationToken ct, string? actor = null)
    {
        var attempts = new UnmatchAttempts();
        var by = string.IsNullOrWhiteSpace(actor) ? DataDestruction.AdminActor : actor;
        if (!await RetryOnceAsync(() => UnmatchOnceAsync(licenseId, transactionId, by, attempts, ct), ct))
            throw new ObifinValidationException(ConflictMessage);
        var recomputed = attempts.Recompute is not { } tx || await RecomputeAfterUnmatchAsync(licenseId, tx, ct);
        // Kaldırma bir bağı sildiyse o bağdan gelen öneriler geri alınır (sınıf özeti). Durdurulmuş bağlantıda koşmaz; düşerse
        // telafi taraması ("iban-memory" kanıtlı, bağı olmayan satır) tamamlar.
        foreach (var hash in attempts.DeletedBonds)
            await TryReevaluateSameIbanAsync(licenseId, hash, transactionId, null, onlyComputedBeforeBond: false, ct);
        return recomputed;
    }

    /// <summary>Kaldırılan hareketin önerisini yeniden hesaplar (sınıf özeti, "Kaldırmanın ardından yeniden hesap denemenin
    /// parçası değildir").</summary>
    private async Task<bool> RecomputeAfterUnmatchAsync(Guid licenseId, BankTransaction tx, CancellationToken ct)
    {
        // Kaldırma kaydedildi. Yalnız eşleştirici: gap çözümü bu hareketle koşsaydı az önce kaldırılan bağı hemen geri kurardı.
        try
        {
            // KVKK K-6: durdurulmuş ya da yerel verisi silinmiş bağlantıda yeniden hesap koşmaz (sınıf özeti). Denetim de
            // denemenin içinde: düşerse kaldırma yine başarılıdır.
            if (await ObifinConnectionService.MatchingStateAsync(_db, licenseId, ct) == false)
            {
                _log.LogInformation("Gölge bağdaştırma: hareket {TransactionId} eşlemesi kaldırıldı; bağlantı eşleştirmeye kapalı, "
                    + "öneri yeniden hesaplanmadı", tx.Id);
                return false;
            }
            await Matcher.MatchAsync(tx, ct);
            return true;
        }
        catch (Exception ex)
        {
            // Eşleştirici kaydedemediğini izlemede bırakmaz; yine de kapsamın sonraki kaydına (audit) iz taşınmasın.
            Discard();
            // Mesaj DEĞİL tür adı ve yığın izi: istisna mesajı SQL parametresi taşıyabilir, yığın izi yalnız çağrı yerlerini;
            // programlama hatası da iz bırakır.
            _log.LogWarning("Gölge bağdaştırma: hareket {TransactionId} eşlemesi kaldırıldı ama öneri yeniden hesaplanamadı "
                + "({ErrorType}); satır öneri yok durumunda kaldı. Yığın: {StackTrace}", tx.Id, ex.GetType().Name, ex.StackTrace);
            return false;
        }
    }

    private async Task ReconcileApprovalOnceAsync(Payment payment, CancellationToken ct)
    {
        // KVKK K-6: durdurulmuş ya da yerel verisi silinmiş bağlantıda saklı hareket işlenmez; verisi silinmiş bağlantıda her onay
        // yeni bir gap yazardı (sınıf özeti). Bağlantısız lisans (null) bugünkü gibi.
        if (await ObifinConnectionService.MatchingStateAsync(_db, payment.LicenseId, ct) == false) return;
        var wpfCustomerId = await ResolveWpfCustomerAsync(payment.ShopperId, payment.LicenseId, ct);
        if (wpfCustomerId is null) { _log.LogInformation("Reconcile: shopper→WPF müşteri bağı yok (ödeme {PaymentId})", payment.Id); return; }
        // İdempotent: iş yeniden koşsa da, eşzamanlı bir koşu (ya da gap çözümü) kazanmış olsa da ikinci satır yazılmaz.
        if (await _db.PaymentMatchGaps.AnyAsync(g => g.PaymentId == payment.Id, ct)
            || await _db.PaymentMatches.AnyAsync(m => m.PaymentId == payment.Id, ct))
        {
            _log.LogDebug("Reconcile: ödeme {PaymentId} zaten bağlı ya da gap'i var", payment.Id);
            return;
        }

        var candidates = await CandidatesAsync(payment.LicenseId, payment.Amount, payment.PaidAt, payment.PayerName,
            wpfCustomerId.Value, ct);
        if (candidates.Count != 1)
        {
            _db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = payment.LicenseId, PaymentId = payment.Id,
                Reason = candidates.Count == 0 ? PaymentMatchGapReason.NoCandidate : PaymentMatchGapReason.AmbiguousCandidates, CreatedAt = DateTimeOffset.UtcNow });
            await _db.SaveChangesAsync(ct);
            return;
        }

        await LinkAsync(candidates[0], payment.Id, payment.PayerName, wpfCustomerId.Value, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task TryResolveGapOnceAsync(BankTransaction tx, CancellationToken ct)
    {
        // Giden, sıfır tutarlı ya da dışlanan kodlu (POS tahsilatı vb.) hareket hiçbir dekontun adayı değildir.
        if (Matcher.IsExcluded(tx)) return;
        // KVKK K-6: durdurulmuş ya da yerel verisi silinmiş bağlantının hareketi gap çözmez (sınıf özeti).
        if (await ObifinConnectionService.MatchingStateAsync(_db, tx.LicenseId, ct) == false) return;
        if (await _db.PaymentMatches.AnyAsync(m => m.BankTransactionId == tx.Id && m.PaymentId != null, ct)) return;
        var windowStart = tx.OccurredAt - CandidateWindow; var windowEnd = tx.OccurredAt + CandidateWindow;
        // Bağlı ödemenin açık gap'i (iki eşzamanlı bağdaştırmanın biri gap, biri bağ yazdıysa) aday değildir: onu ikinci bir
        // harekete bağlamak filtreli PaymentId indeksine takılırdı.
        var open = await (from g in _db.PaymentMatchGaps
                          join p in _db.Payments on g.PaymentId equals p.Id
                          where g.LicenseId == tx.LicenseId && g.ResolvedAt == null && p.Status == PaymentStatus.Approved
                                && p.Amount == tx.Amount && p.PaidAt >= windowStart && p.PaidAt <= windowEnd
                                && !_db.PaymentMatches.Any(m => m.PaymentId == p.Id)
                          select new { GapId = g.Id, PaymentId = p.Id, p.ShopperId, p.LicenseId, p.Amount, p.PaidAt, p.PayerName })
            .ToListAsync(ct);
        // Pencerede birden çok açık gap: hareketin hangi dekonta ait olduğu tahmin edilmez.
        if (open.Count != 1) return;
        var pending = open[0];
        var wpfCustomerId = await ResolveWpfCustomerAsync(pending.ShopperId, pending.LicenseId, ct);
        if (wpfCustomerId is null) return;
        // Dekontun aday seçimi onaydaki kuralla yeniden koşar; hareket TEK aday değilse gap açık kalır. AmbiguousCandidates'in
        // adayları hâlâ bağsızsa gerçek hareket büyük olasılıkla onlardan biridir; aynı tutarlı iki geç hareketten ilk
        // işleneni seçmek de tahmin olurdu.
        var candidates = await CandidatesAsync(pending.LicenseId, pending.Amount, pending.PaidAt, pending.PayerName,
            wpfCustomerId.Value, ct);
        if (candidates.Count != 1 || candidates[0].Id != tx.Id) return;
        var gap = await _db.PaymentMatchGaps.FirstAsync(g => g.Id == pending.GapId, ct);
        await LinkAsync(tx, pending.PaymentId, pending.PayerName, wpfCustomerId.Value, ct);
        gap.ResolvedBankTransactionId = tx.Id; gap.ResolvedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ManualMatchOnceAsync(Guid licenseId, Guid transactionId, Guid wpfCustomerId, ManualMatchAttempt attempt,
        CancellationToken ct)
    {
        // Düşen önceki denemenin sonucu bu denemeye taşınmasın.
        attempt.Outcome = null; attempt.IbanHash = null;
        // KVKK K-6: elle eşleme de IBAN öğretir ve LastConfirmedAt'i tazeler; durdurulmuş bağlantıda kural delinmesin (sınıf
        // özeti). RetryOnceAsync bu istisnayı yeniden denemeden yukarı verir.
        if (await ObifinConnectionService.MatchingStateAsync(_db, licenseId, ct) == false)
            throw new ObifinValidationException(MatchingStoppedMessage);
        // Giden hareketin karşı tarafı müşteri değildir: onun IBAN'ı bir müşteriye öğretilmez.
        var tx = await _db.BankTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId && t.Direction == BankTransactionDirection.Incoming, ct)
            ?? throw new ObifinValidationException("Hareket bulunamadı.");
        if (!await _db.WpfCustomerProjections.AnyAsync(c => c.Id == wpfCustomerId && c.LicenseId == licenseId && c.PurgedAt == null, ct))
            throw new ObifinValidationException("Müşteri bulunamadı.");
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct) ?? await Matcher.MatchAsync(tx, ct);
        // Karara bağlanmış satırın üstüne yazılmaz: onayla dekonta bağlı satır "Y'nin ödemesi X'in" derdi, elle verilmiş
        // karar da sessizce silinirdi. Yeniden karar önce kaldırmadan (UnmatchAsync) geçer.
        if (match.DecidedAt is not null || match.PaymentId is not null)
            throw new ObifinValidationException(AlreadyDecidedMessage);
        var now = DateTimeOffset.UtcNow;
        match.ActualWpfCustomerId = wpfCustomerId; match.DecidedAt = now; match.UpdatedAt = now;
        match.Status = match.ProposedWpfCustomerId == wpfCustomerId ? PaymentMatchStatus.ConfirmedByHuman : PaymentMatchStatus.ManualOnly;
        // Dışlanan hareketin (POS tahsilatı vb.) karşı IBAN'ı müşterinin değildir (sınıf özeti): karar yazılır, öğretilmez.
        // Yeniden denemede sonuç bu denemeninkiyle değişir (ör. eşzamanlı bir karar aynı bağı öğrettiyse tazeleme olur).
        attempt.Outcome = Matcher.IsExcluded(tx)
            ? ManualMatchIbanOutcome.Excluded
            : await LearnIbanAsync(match, tx, wpfCustomerId, IbanMemorySource.ManualMatch, ct);
        attempt.IbanHash = tx.CounterpartyIbanHash;
        await _db.SaveChangesAsync(ct);
    }

    private async Task UnmatchOnceAsync(Guid licenseId, Guid transactionId, string actor, UnmatchAttempts attempts,
        CancellationToken ct)
    {
        var tx = await _db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException("Hareket bulunamadı.");
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct);
        // Kaldırma admin'in gördüğü karara yöneliktir. Yeniden denemede karar değiştiyse (arada dekont bağlandı, karar
        // verildi ya da kaldırıldı) yeniden uygulanmaz: admin'in görmediği bir bağı kaldırıp dekontu yetim bırakırdı.
        var decision = new HumanDecision(match?.PaymentId, match?.ActualWpfCustomerId, match?.DecidedAt);
        if (attempts.Seen is null) attempts.Seen = decision;
        else if (attempts.Seen != decision) throw new ObifinValidationException(ConflictMessage);

        // Bu denemenin sildiği bağlar (öğretilmiş satır ya da süre geri çekmesinin silmesi): kaydedilirse UnmatchAsync onların
        // önerilerini geri alır. Yeniden deneme baştan sayar.
        attempts.DeletedBonds.Clear();
        var learned = await _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId && m.SourceBankTransactionId == tx.Id).ToListAsync(ct);
        _db.CustomerIbanMemories.RemoveRange(learned); // spec §3: iptal bayrağı değil, silme
        attempts.DeletedBonds.UnionWith(learned.Select(m => m.IbanHash));
        // KVKK K-6: kaldırılan karar başka bir havalenin öğrettiği bağı tazelemiş olabilir; süresi artık doğrulanmış sayılmayan
        // havalede kalmasın. Bağı doğrulamamış karar (işaretsiz: doğrulanmamış dekont bağı, dışlanan hareket, çatışma) süreyi
        // taşımadı, kaldırılması da değiştirmez.
        if (match is { ActualWpfCustomerId: { } decidedFor, IbanConfirmedAt: { } confirmed } && !Matcher.IsExcluded(tx)
            && tx.CounterpartyIbanHash is { } hash)
        {
            var removedId = tx.Id;
            if (await RewindIbanConfirmationAsync(tx, decidedFor, confirmed, t => t.Id == removedId, "kaldırma",
                    DataDestruction.Triggers.Retention, actor, ct))
                attempts.DeletedBonds.Add(hash);
        }
        if (match is not null)
        {
            var now = DateTimeOffset.UtcNow;
            // Dekont ölçümde kalır (sınıf özeti; kural ReturnPaymentsToGapsAsync'te, hesap reddiyle ortak).
            if (match.PaymentId is { } paymentId) await ReturnPaymentsToGapsAsync(licenseId, [paymentId], ct);
            match.PaymentId = null; match.ActualWpfCustomerId = null; match.DecidedAt = null; match.IbanConfirmedAt = null;
            match.Status = PaymentMatchStatus.NoProposal; // insan-kararı kilidi kalkar, UnmatchAsync yeniden hesaplatır
            // Öneri durumla birlikte boşaltılır: yeniden hesap düşerse satır tutarlı "öneri yok" kalır (sınıf özeti).
            // Başarılı yeniden hesap bunların üstüne yazar.
            match.ProposedWpfCustomerId = null; match.Layer = PaymentMatchLayer.None; match.Confidence = 0m; match.Evidence = null;
            match.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        // Kaydedildi: yeniden hesap bu denemenin dışında, UnmatchAsync'te koşar (sınıf özeti).
        attempts.Recompute = match is not null ? tx : null;
    }

    /// <summary>Kaldırma kuralı (sınıf özeti, "Kaldırma dekontu ölçümden düşürmez"): dekont bağı kaldırılan ödemeler açık gap'e
    /// döner. Gap'i varsa (gap çözümüyle bağlanmışsa) yeniden açılır, yoksa (onayda bağlanmışsa)
    /// <see cref="PaymentMatchGapReason.UnlinkedByAdmin"/> gap'i yazılır; ödeme başına tek gap (tekil indeks). Kuralın TEK yeri:
    /// kaldırma (<see cref="UnmatchAsync"/>) ve hesap reddi (<c>BankConnectionTerminator.RejectAccountAsync</c>) kullanır, biri
    /// değişirse öbürü ayrışmasın. Değişen ve eklenen satır izlenir, kaydetmez: çağıranın SaveChanges'i bağın kaldırılmasıyla
    /// birlikte yazar. Açılan ya da yazılan gap sayısını döner.</summary>
    public async Task<int> ReturnPaymentsToGapsAsync(Guid licenseId, IReadOnlyCollection<Guid> paymentIds, CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        var existing = await _db.PaymentMatchGaps.Where(g => ids.Contains(g.PaymentId)).ToListAsync(ct);
        foreach (var gap in existing) { gap.ResolvedAt = null; gap.ResolvedBankTransactionId = null; }
        var now = DateTimeOffset.UtcNow;
        foreach (var paymentId in ids.Except(existing.Select(g => g.PaymentId)))
            _db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = licenseId, PaymentId = paymentId,
                Reason = PaymentMatchGapReason.UnlinkedByAdmin, CreatedAt = now });
        return ids.Count;
    }

    private async Task<Guid?> ResolveWpfCustomerAsync(Guid? shopperId, Guid licenseId, CancellationToken ct)
    {
        if (shopperId is null) return null;
        return await _db.ShopperBroadcasterLinks.AsNoTracking()
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.WpfCustomerId != null)
            .OrderByDescending(l => l.JoinedAt).Select(l => l.WpfCustomerId).FirstOrDefaultAsync(ct);
    }

    /// <summary>Dekontun aday hareketleri (sınıf özeti): aynı lisans, gelen, tutar eşit, ödeme anının ±2 günü; bir dekonta
    /// bağlanmış, başka müşteriye karar verilmiş (karar anı dolu, müşterisi bu dekontunki değil — KVKK silmesiyle boşalmış
    /// müşteri dahil) ya da eşleştiricinin dışladığı (<see cref="PaymentMatcher.IsExcluded"/>) hareket aday değildir. Birden çok aday kalırsa gönderen adının tüm
    /// token'larını açıklamasında taşıyan TEK aday seçilir; seçilemezse liste olduğu gibi döner. Onay da gap çözümü de bunu
    /// kullanır: ikisi aynı kuralla karar verir.</summary>
    private async Task<List<BankTransaction>> CandidatesAsync(Guid licenseId, decimal amount, DateTimeOffset paidAt,
        string payerName, Guid wpfCustomerId, CancellationToken ct)
    {
        var windowStart = paidAt - CandidateWindow; var windowEnd = paidAt + CandidateWindow;
        // Karar kilidi DecidedAt'tir, müşteri değil: KVKK silmesi kararın müşterisini boşaltır, anını bırakır
        // (BankSubjectScrub.MatchForCustomer). Müşterisi boş karar da başka bir kişinin kararıdır (EF null != X'i doğru çevirir).
        var taken = _db.PaymentMatches
            .Where(m => m.LicenseId == licenseId
                        && (m.PaymentId != null || (m.DecidedAt != null && m.ActualWpfCustomerId != wpfCustomerId)))
            .Select(m => m.BankTransactionId);
        var candidates = (await _db.BankTransactions.AsNoTracking()
                .Where(t => t.LicenseId == licenseId && t.Direction == BankTransactionDirection.Incoming
                            && t.Amount == amount && t.OccurredAt >= windowStart && t.OccurredAt <= windowEnd && !taken.Contains(t.Id))
                .ToListAsync(ct))
            // Eşleştiricinin dışladığı hareket (POS tahsilatı vb.) müşteri havalesi değildir. Ad daraltmasından ÖNCE atılır:
            // aynı tutarlı bir POS tahsilatı havaleyi belirsizliğe düşürmesin, tek başına da dekonta bağlanmasın.
            .Where(t => !Matcher.IsExcluded(t))
            .ToList();

        if (candidates.Count > 1)
        {
            var payer = PayerTokens(payerName);
            var narrowed = candidates.Where(t => NamesPayer(payer, t.Description)).ToList();
            if (narrowed.Count == 1) candidates = narrowed;
        }
        return candidates;
    }

    /// <summary>Gönderen adının ≥3 harfli token'ları; kısa token (baş harf, "A.") ayırt etmez.</summary>
    private static List<string> PayerTokens(string? payerName)
        => BankTextNormalizer.Tokenize(payerName).Tokens.Where(t => t.Length >= 3).ToList();

    /// <summary>Açıklama gönderen adının tüm token'larını taşıyor mu. Token'ı olmayan ad (boş, KVKK'yla silinmiş, yalnız
    /// kısa token) hiçbir açıklamayı doğrulamaz.</summary>
    private static bool NamesPayer(List<string> payerTokens, string? description)
    {
        if (payerTokens.Count == 0) return false;
        var d = BankTextNormalizer.Tokenize(description).Tokens;
        return payerTokens.All(p => d.Contains(p));
    }

    private async Task LinkAsync(BankTransaction tx, Guid paymentId, string? payerName, Guid wpfCustomerId, CancellationToken ct)
    {
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct) ?? await Matcher.MatchAsync(tx, ct);
        // Aday seçimi satırı izlemeden okudu; arada başka bir karara bağlandıysa seçim bayattır, yeniden deneme taze seçer.
        // Buradan kayda kadarki pencereyi jeton korur. Ölçüt aday seçimininkiyle aynı: kilit DecidedAt (KVKK silmesinin
        // müşterisini boşalttığı karar da başkasınındır).
        if (match.PaymentId is not null || (match.DecidedAt is not null && match.ActualWpfCustomerId != wpfCustomerId))
            throw new StaleCandidateException();
        var now = DateTimeOffset.UtcNow;
        match.PaymentId = paymentId; match.UpdatedAt = now;
        // Aynı müşteriye elle verilmiş karar: dekont yalnız iliştirilir. Kararın durumu ve anı admin'inki kalır; IBAN'ı elle
        // eşleme zaten öğretti.
        if (match.ActualWpfCustomerId is not null) return;
        match.ActualWpfCustomerId = wpfCustomerId; match.DecidedAt = now;
        match.Status = match.ProposedWpfCustomerId is null ? PaymentMatchStatus.ManualOnly
            : match.ProposedWpfCustomerId == wpfCustomerId ? PaymentMatchStatus.ConfirmedByHuman : PaymentMatchStatus.Contradicted;
        // Yalnız doğrulanmış bağ öğretir (sınıf özeti): tutar ve zamandan ibaret bağ başka müşterinin havalesi olabilir.
        if (match.ProposedWpfCustomerId == wpfCustomerId || NamesPayer(PayerTokens(payerName), tx.Description))
            await LearnIbanAsync(match, tx, wpfCustomerId, IbanMemorySource.HumanApproval, ct);
        else if (tx.CounterpartyIbanHash is not null)
            _log.LogInformation("Gölge bağdaştırma: hareket {TransactionId} ödeme {PaymentId}'e yalnız tutar ve zamanla bağlandı; "
                + "IBAN öğrenilmedi", tx.Id, paymentId);
    }

    /// <summary>Doğrulanmış bağdan IBAN'ı öğretir; aynı müşteriye kayıtlı bağı tazeler (KVKK K-6: bağ son doğrulanmış havaleden
    /// 12 ay yaşar, <see cref="CustomerIbanMemory.LastConfirmedAt"/>). Süre havalenin işlem tarihinden işler, doğrulama anından
    /// değil: politikadaki bütün banka süreleri gibi. Bağı doğrulayan karar (<paramref name="match"/>) işaretlenir
    /// (<see cref="PaymentMatch.IbanConfirmedAt"/>): kaldırmada bağın süresi yalnız işaretli kararlardan geri hesaplanır.
    /// IBAN'sız hareket ve başka müşteriye kayıtlı bağ doğrulama değildir, işaretlenmez. Özeti geçerli IBAN'dan üretilmemiş
    /// hareket (<see cref="BankTransaction.CounterpartyIbanHashValid"/> false: KVKK Ö-7 kesme anından önce çekilmiş) ne öğretir ne
    /// tazeler. Sonucu döner (elle eşlemenin bildirimi; dekont onayı yolu kullanmaz).</summary>
    private async Task<ManualMatchIbanOutcome> LearnIbanAsync(PaymentMatch match, BankTransaction tx, Guid wpfCustomerId,
        IbanMemorySource source, CancellationToken ct)
    {
        // Banka karşı IBAN'ı boş ya da maskeli gönderdi: özet yok (ObifinPollJob, BankHasher.NormalizeIban).
        if (tx.CounterpartyIbanHash is null) return ManualMatchIbanOutcome.NoValidIban;
        // KVKK Ö-7: yalnız geçerli IBAN'dan üretilmiş özet öğretir. Kesme anından (Görev 11) önce çekilen hareketin özeti maskeli
        // IBAN'dan olabilir; --reset-iban-memory hafızayı silse de o özet buradan geri gelirdi.
        if (!tx.CounterpartyIbanHashValid) return ManualMatchIbanOutcome.LegacyHash;
        var now = DateTimeOffset.UtcNow;
        var confirmedAt = ConfirmedAt(tx, now);
        var existing = await _db.CustomerIbanMemories.FirstOrDefaultAsync(m => m.LicenseId == tx.LicenseId && m.IbanHash == tx.CounterpartyIbanHash, ct);
        if (existing is not null)
        {
            if (existing.WpfCustomerId != wpfCustomerId)
            {
                _log.LogWarning("IBAN hafızası çelişkisi: hareket {TransactionId} başka müşteriye kayıtlı IBAN'dan geldi", tx.Id);
                return ManualMatchIbanOutcome.ConflictOtherCustomer;
            }
            // Aynı müşteriye her doğrulama süreyi tazeler. Saklamanın başlangıcı (LastConfirmedAt, yoksa CreatedAt; saklama işiyle
            // aynı ifade) yalnız ileri gider: daha eski bir havalenin geç doğrulanması onu geri çekmez. Karar yine de
            // işaretlenir: daha yeni doğrulama kaldırılırsa bu havale kalan doğrulamadır.
            if ((existing.LastConfirmedAt ?? existing.CreatedAt) < confirmedAt) existing.LastConfirmedAt = confirmedAt;
            match.IbanConfirmedAt = confirmedAt;
            return ManualMatchIbanOutcome.Refreshed;
        }
        _db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = tx.LicenseId, WpfCustomerId = wpfCustomerId, IbanHash = tx.CounterpartyIbanHash,
            IbanMasked = tx.CounterpartyIbanMasked ?? "?", LearnedFrom = source, SourceBankTransactionId = tx.Id, CreatedAt = now,
            LastConfirmedAt = confirmedAt,
        });
        match.IbanConfirmedAt = confirmedAt;
        return ManualMatchIbanOutcome.Learned;
    }

    /// <summary>Yeniden değerlendirme penceresinin başı (sınıf özeti): <see cref="BankMatchSweepJob.LookbackDays"/> ile eşleşmeyen
    /// açıklamanın saklama süresinin (<see cref="BankOptions.UnmatchedDescriptionRetentionDays"/>) kısası, eksi
    /// <see cref="ReevaluationRetentionMarginDays"/>. Saklama işinin 3. adımı eşleşmeyen açıklamayı satırın jetonunu ilerletmeden
    /// siler: sınırdaki satırın yeniden hesabı onunla çakışmasız yarışıp silinmiş metinden türeyen kanıtı yazabilirdi. Elle eşlemenin
    /// tetiği ve telafi taramasının 2. aşaması aynı pencereyi kullanır.</summary>
    public DateTimeOffset ReevaluationSince(DateTimeOffset now)
        => now.AddDays(-Math.Max(0, Math.Min(BankMatchSweepJob.LookbackDays, _bank.UnmatchedDescriptionRetentionDays)
            - ReevaluationRetentionMarginDays));

    /// <summary>Kararsız satırı IBAN hafızasının güncel haliyle yeniden hesaplar (sınıf özeti): yalnız eşleştirici
    /// (<see cref="PaymentMatcher.ReevaluateAsync"/>). Gap çözümü koşmaz: tutar/zaman kuralı hafızadan bağımsızdır, kaldırılan bağı
    /// da geri kurmasın. Sonuç yalnız öneri ya da öneri yok olur; insan kararına, dekonta ve hafızaya dokunmaz. Eşleştirici satırı
    /// izlenerek taze okur, insan kararı kilidini yeniden denetler ve jetonla yazar.
    /// <para>Hareket her denemeden önce taze okunur: seçimden sonra ya da çakışan yazıyla açıklaması silindiyse (saklama, KVKK
    /// silmesi, veri talebi) ya da hareket silindiyse dokunulmaz — bayat açıklamayla hesaplanan öneri silinmiş metnin izini
    /// (kullanıcı adı anahtarı) kanıta geri taşırdı. Çakışmada (jeton) bu yüzden eşleştiricinin kendi yeniden denemesi değil bu
    /// döngü koşar: hareket yeniden okunur, eşleştiricinin aday önbelleği atılır (karşı yazan KVKK silmesiyse silinen müşteri aday
    /// kalmasın), bir kez yeniden hesaplanır; araya insan kararı girdiyse eşleştirici dokunmaz. İkinci çakışmada satır atlanır:
    /// karşı yazan, bağ kaydedildikten sonra okuduğu için doğru hesapladı. Telafi taraması (<see cref="BankMatchSweepJob"/>) da
    /// bunu kullanır.</para></summary>
    /// <returns>Yeniden hesaplanan kararsız satır; null: atlandı (iki çakışma, açıklaması silinmiş ya da silinmiş hareket, araya
    /// insan kararı girmiş satır — sayaçlar onu yeniden değerlendirilmiş saymaz).</returns>
    public async Task<PaymentMatch?> ReevaluateUndecidedAsync(BankTransaction tx, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var fresh = await _db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tx.Id, ct);
            if (fresh is null || fresh.DescriptionPurgedAt is not null) return null;
            // Satır bu bağlamda okunmuş haliyle izleniyorsa izleyen sorgu onu DB'den tazelemez (kimlik çözümlemesi): ayrılır,
            // eşleştirici taze okur. Yalnız bekleyen yazısı olmayan girdi.
            foreach (var entry in _db.ChangeTracker.Entries<PaymentMatch>()
                         .Where(e => e.Entity.BankTransactionId == fresh.Id && e.State == EntityState.Unchanged).ToList())
                entry.State = EntityState.Detached;
            PaymentMatch row;
            try
            {
                row = await Matcher.ReevaluateAsync(fresh, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                Matcher.ForgetCandidates(fresh.LicenseId);
                _log.LogInformation("Gölge bağdaştırma: hareket {TransactionId} yeniden değerlendirmesi eşzamanlı bir yazıyla çakıştı; "
                    + "hareket taze okunup bir kez yeniden deneniyor", tx.Id);
                continue;
            }
            catch (DbUpdateConcurrencyException)
            {
                _log.LogInformation("Gölge bağdaştırma: hareket {TransactionId} yeniden değerlendirmesi iki denemede de eşzamanlı bir "
                    + "yazıyla çakıştı; atlandı", tx.Id);
                return null;
            }
            // Araya insan kararı girdiyse eşleştirici satıra dokunmadan döndü: yeniden değerlendirilmiş sayılmaz.
            return row.Status is PaymentMatchStatus.Proposed or PaymentMatchStatus.NoProposal ? row : null;
        }
    }

    /// <summary>Aynı lisans ve IBAN özetinin kararsız satırlarını yeniden değerlendirir (sınıf özeti): <see cref="ReevaluationSince"/>
    /// penceresinde, gelen, açıklaması silinmemiş, dışlanmamış; tetikleyen hareket (<paramref name="triggerTransactionId"/>) hariç;
    /// <paramref name="onlyComputedBeforeBond"/> ise (tazeleme) yalnız <paramref name="customer"/>'ın bu özetteki bağından önce
    /// hesaplanmışlar (bağ arada silindiyse hiçbiri); en yeniden başlayarak en çok <see cref="MaxReevaluationsPerTrigger"/> satır.
    /// Seçim izlenmeden. KVKK K-6: bağlantı eşleştirmeye kapalıysa koşmaz. <paramref name="customer"/> verilirse ona öneri alan
    /// satırlar sayılır.</summary>
    private async Task<IbanReevaluation> ReevaluateSameIbanAsync(Guid licenseId, string ibanHash, Guid triggerTransactionId,
        Guid? customer, bool onlyComputedBeforeBond, CancellationToken ct)
    {
        var none = new IbanReevaluation(0, 0, false);
        if (await ObifinConnectionService.MatchingStateAsync(_db, licenseId, ct) == false) return none;
        DateTimeOffset? bondCreatedAt = null;
        if (onlyComputedBeforeBond)
        {
            bondCreatedAt = await _db.CustomerIbanMemories.AsNoTracking()
                .Where(b => b.LicenseId == licenseId && b.IbanHash == ibanHash && b.WpfCustomerId == customer)
                .Select(b => (DateTimeOffset?)b.CreatedAt).FirstOrDefaultAsync(ct);
            if (bondCreatedAt is null) return none;
        }
        var since = ReevaluationSince(DateTimeOffset.UtcNow);
        var query = from t in _db.BankTransactions.AsNoTracking()
                    join m in _db.PaymentMatches.AsNoTracking() on t.Id equals m.BankTransactionId
                    where t.LicenseId == licenseId && t.CounterpartyIbanHash == ibanHash && t.Id != triggerTransactionId
                          && t.Direction == BankTransactionDirection.Incoming && t.Amount != 0 && t.OccurredAt >= since
                          && t.DescriptionPurgedAt == null
                          && (m.Status == PaymentMatchStatus.Proposed || m.Status == PaymentMatchStatus.NoProposal)
                          && m.DecidedAt == null && m.PaymentId == null
                    select new { t, m.UpdatedAt };
        if (bondCreatedAt is { } bondAt) query = query.Where(x => x.UpdatedAt < bondAt);
        var targets = await query.OrderByDescending(x => x.t.OccurredAt).ThenBy(x => x.t.Id).Select(x => x.t)
            .Take(MaxReevaluationsPerTrigger + 1)
            .ToListAsync(ct);
        int reevaluated = 0, proposed = 0;
        foreach (var tx in targets.Take(MaxReevaluationsPerTrigger))
        {
            // Dışlanan hareket yeniden değerlendirilmiş sayılmaz: bildirim onu "kullanıcı adı başka müşteriyi gösterdi" diye sayardı.
            if (Matcher.IsExcluded(tx)) continue;
            if (await ReevaluateUndecidedAsync(tx, ct) is not { } row) continue;
            reevaluated++;
            if (customer is not null && row.Status == PaymentMatchStatus.Proposed && row.ProposedWpfCustomerId == customer) proposed++;
        }
        return new IbanReevaluation(reevaluated, proposed, targets.Count > MaxReevaluationsPerTrigger);
    }

    /// <summary><see cref="ReevaluateSameIbanAsync"/>; düşerse (iptal, DB hatası, programlama hatası) kararı ya da kaldırmayı geri
    /// almaz: izi atar, günlüğe tetikleyen hareketin kimliği, istisna türü ve yığın izi yazılır (mesaj değil: SQL parametresi
    /// taşıyabilir), null döner. Telafi taraması tamamlar.</summary>
    private async Task<IbanReevaluation?> TryReevaluateSameIbanAsync(Guid licenseId, string ibanHash, Guid triggerTransactionId,
        Guid? customer, bool onlyComputedBeforeBond, CancellationToken ct)
    {
        try
        {
            return await ReevaluateSameIbanAsync(licenseId, ibanHash, triggerTransactionId, customer, onlyComputedBeforeBond, ct);
        }
        catch (Exception ex)
        {
            // Eşleştirici kaydedemediğini izlemede bırakmaz; yine de kapsamın sonraki kaydına (audit) iz taşınmasın.
            Discard();
            _log.LogWarning("Gölge bağdaştırma: hareket {TransactionId} eşlemesi değişti ama aynı IBAN'ın kararsız satırları yeniden "
                + "değerlendirilemedi ({ErrorType}); telafi taraması tamamlar. Yığın: {StackTrace}", triggerTransactionId,
                ex.GetType().Name, ex.StackTrace);
            return null;
        }
    }

    /// <summary>Doğrulamanın saklama başlangıcı: havalenin işlem tarihi, ileri tarihliyse (valör) şimdiye kırpılır.</summary>
    private static DateTimeOffset ConfirmedAt(BankTransaction tx, DateTimeOffset now) => tx.OccurredAt < now ? tx.OccurredAt : now;

    /// <summary>
    /// KVKK K-6 (Görev 10'un sonraki görev kuralı): kararıyla birlikte silinecek havalelerin (<paramref name="removed"/>) tazelediği,
    /// başka havalenin öğrettiği IBAN bağlarının başlangıcını kalan doğrulamalara geri çeker; kalan yoksa bağı siler ve imha
    /// kaydını yazar (ölçütte <paramref name="reason"/>, tetikleyici <paramref name="trigger"/>, yapan <paramref name="actor"/>).
    /// Kural kaldırmanınkiyle aynı (<see cref="RewindIbanConfirmationAsync"/>): yalnız işaretli kararlar
    /// (<see cref="PaymentMatch.IbanConfirmedAt"/>) süreyi taşır, dışlanan hareket doğrulama değildir, silinen kümedeki bir
    /// havalenin öğrettiği bağa dokunulmaz (o bağ öğreten havaleyle birlikte silinir). <paramref name="removedSet"/> silinen
    /// kümenin TAMAMI (ör. reddedilen hesabın bütün hareketleri, partilerle silinse de): kalan doğrulamalar onun dışından sayılır,
    /// sonraki partide silinecek havale kalan sayılmaz. Hesap reddi (<c>BankConnectionTerminator.RejectAccountAsync</c>) kullanır.
    /// Değişen ve silinen satır izlenir, kaydetmez: çağıranın SaveChanges'i silmeyle birlikte yazar. Silinen bağ sayısını döner.
    /// </summary>
    public async Task<int> RewindIbanConfirmationsAsync(IReadOnlyCollection<(PaymentMatch Match, BankTransaction Tx)> removed,
        Expression<Func<BankTransaction, bool>> removedSet, string reason, string trigger, string actor, CancellationToken ct)
    {
        var deleted = 0;
        // Aynı bağı tazelemiş birden çok karar: bağ başına en yeni işaret karar verir (kalanlar kümeden bağımsız sayılır).
        var latestPerBond = removed
            .Where(r => r.Match is { ActualWpfCustomerId: not null, IbanConfirmedAt: not null }
                && r.Tx.CounterpartyIbanHash is not null && !Matcher.IsExcluded(r.Tx))
            .GroupBy(r => (Hash: r.Tx.CounterpartyIbanHash!, Customer: r.Match.ActualWpfCustomerId!.Value))
            .Select(g => g.MaxBy(r => r.Match.IbanConfirmedAt!.Value));
        foreach (var r in latestPerBond)
            if (await RewindIbanConfirmationAsync(r.Tx, r.Match.ActualWpfCustomerId!.Value, r.Match.IbanConfirmedAt!.Value,
                    removedSet, reason, trigger, actor, ct))
                deleted++;
        return deleted;
    }

    /// <summary>
    /// <paramref name="customer"/>'a verilmiş ve aynı IBAN'ın bu müşteriye kayıtlı bağını doğrulamış (<paramref name="confirmed"/>
    /// = kararın <see cref="PaymentMatch.IbanConfirmedAt"/> işareti) kararı kaldırılan ya da silinen <paramref name="removed"/>,
    /// bağın son doğrulamasıysa (işaret bağın <see cref="CustomerIbanMemory.LastConfirmedAt"/>'inden eski değil) bağın başlangıcı
    /// kalan doğrulamaların en yenisine geri çekilir; hiç ileri gitmez (KVKK K-6: "son doğrulanmış ödemeden 12 ay"). Kalan
    /// doğrulama (<see cref="RemainingConfirmationsAsync"/>; <paramref name="removedSet"/> dışından) yalnız işaretli kararlar ve
    /// öğreten havaledir: insan kararı tek başına doğrulama sayılmaz. Yalnız tutar ve zamanla kurulmuş dekont bağı kalan sayılsaydı
    /// bağ son gerçek doğrulamadan uzun yaşardı.
    /// Kalan yoksa bağ silinir: öğreten havale 180 günlük kuralla silinmiş, tarihi bilinmiyor. Öğrenme anı ya da tahmini bir
    /// tarih süreyi politikanın ötesine taşıyabilirdi (öğrenme havaleden aylar sonra olabilir). Bu silme bir düzeltme değil,
    /// saklama gerekçeli imhadır (başlangıç artık belirlenemiyor): kalıcı imha kaydı aynı SaveChanges'te yazılır (Silme
    /// Yönetmeliği md. 7/3; ölçütte <paramref name="reason"/>, tetikleyici <paramref name="trigger"/> — kaldırmada
    /// <c>retention</c>, hesap reddinde <c>account-rejected</c> —, yapan <paramref name="actor"/>). Silinen kümedeki bir havalenin
    /// öğrettiği bağ zaten o havaleyle siliniyor, dokunulmaz; P1 öncesi, hiç tazelenmemiş bağın (<c>LastConfirmedAt</c> boş) süresi
    /// öğrenme anından işler, kaldırma onu değiştirmez. P1 öncesi kararlar işaretsizdir: hata yönü erken silmedir. Değişen satır
    /// izlenir; çağıranın SaveChanges'i yazar. Bağ silindiyse true.
    /// </summary>
    private async Task<bool> RewindIbanConfirmationAsync(BankTransaction removed, Guid customer, DateTimeOffset confirmed,
        Expression<Func<BankTransaction, bool>> removedSet, string reason, string trigger, string actor, CancellationToken ct)
    {
        if (removed.CounterpartyIbanHash is not { } hash) return false;
        var memory = await _db.CustomerIbanMemories.FirstOrDefaultAsync(m => m.LicenseId == removed.LicenseId && m.IbanHash == hash
            && m.WpfCustomerId == customer, ct);
        if (memory is null || _db.Entry(memory).State == EntityState.Deleted || memory.SourceBankTransactionId == removed.Id
            || memory.LastConfirmedAt is not { } anchor) return false;
        if (confirmed < anchor) return false; // son doğrulama değildi
        // Bağı silinen kümedeki başka bir havale öğrettiyse bağ o havaleyle birlikte siliniyor (hesap reddinin sonraki partisi).
        if (memory.SourceBankTransactionId is { } source
            && await _db.BankTransactions.AsNoTracking().Where(t => t.Id == source).AnyAsync(removedSet, ct)) return false;
        var remaining = await RemainingConfirmationsAsync(memory, removedSet, ct);
        if (remaining.Count == 0)
        {
            _db.CustomerIbanMemories.Remove(memory);
            DataDestruction.Record(_db, memory.LicenseId, "CustomerIbanMemories", DataDestruction.Actions.Deleted, 1,
                $"LastConfirmedAt+{_bank.IbanMemoryRetentionMonths}ay, {reason}, kalan doğrulama yok", trigger, actor);
            return true;
        }
        var latest = remaining.Max();
        if (latest < anchor) memory.LastConfirmedAt = latest;
        return false;
    }

    /// <summary>Bağın <paramref name="removedSet"/> (silinen ya da kararı kaldırılan küme) dışındaki doğrulamalarının saklama
    /// başlangıçları: aynı lisans ve IBAN, bağın müşterisine verilmiş, dışlanmamış hareketlerden (a) kararı işaretli olanlar
    /// (işaretin değeri) ve (b) öğreten havale (<see cref="CustomerIbanMemory.SourceBankTransactionId"/>; tanımı gereği
    /// doğrulamadır, P1 öncesi öğrenilmişse işaretsizdir; değeri işlem tarihi, öğrenme anına kırpılmış — öğrenmenin yazdığı değerin
    /// aynısı). Öğreten havale 180 günlük kuralla silindiyse yoktur.</summary>
    private async Task<List<DateTimeOffset>> RemainingConfirmationsAsync(CustomerIbanMemory memory,
        Expression<Func<BankTransaction, bool>> removedSet, CancellationToken ct)
    {
        var sourceId = memory.SourceBankTransactionId ?? Guid.Empty;
        var kept = Expression.Lambda<Func<BankTransaction, bool>>(Expression.Not(removedSet.Body), removedSet.Parameters);
        var candidates = _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == memory.LicenseId && t.CounterpartyIbanHash == memory.IbanHash)
            .Where(kept);
        var rows = await (from m in _db.PaymentMatches.AsNoTracking()
                          join t in candidates on m.BankTransactionId equals t.Id
                          where m.LicenseId == memory.LicenseId && m.ActualWpfCustomerId == memory.WpfCustomerId
                                && (m.IbanConfirmedAt != null || t.Id == sourceId)
                          select new { Tx = t, m.IbanConfirmedAt })
            .ToListAsync(ct);
        return rows.Where(r => !Matcher.IsExcluded(r.Tx))
            .Select(r => r.IbanConfirmedAt ?? ConfirmedAt(r.Tx, memory.CreatedAt)).ToList();
    }

    /// <summary>Bir denemeyi koşar; çakışma, bayat seçim ya da tekil indeks ihlalinde (bkz. sınıf özeti) izini atıp BİR kez
    /// yeniden dener. false: iki denemede de satır okunduktan (ya da seçildikten) sonra değişti. Başka her hata (ikinci
    /// indeks ihlali dahil) izi atılıp fırlatılır.</summary>
    private async Task<bool> RetryOnceAsync(Func<Task> attempt, CancellationToken ct)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                await attempt();
                return true;
            }
            catch (Exception ex) when ((ex is DbUpdateException or StaleCandidateException) && !ct.IsCancellationRequested)
            {
                Discard();
                if (i == 1)
                {
                    // Mesaj DEĞİL yalnız tür adı: istisna mesajı SQL parametresi taşıyabilir.
                    _log.LogInformation("Gölge bağdaştırma: kayıt eşzamanlı bir yazıyla çakıştı ({ErrorType}); taze satırlarla "
                        + "bir kez yeniden deneniyor", ex.GetType().Name);
                    continue;
                }
                if (ex is DbUpdateConcurrencyException or StaleCandidateException) return false;
                throw;
            }
            catch
            {
                Discard();
                throw;
            }
        }
    }

    /// <summary>Düşen denemenin izini atar: bağdaştırıcının yazdığı tabloların izlenen satırları (Added/Modified/Deleted ve
    /// okunmuş Unchanged) ve denemenin eklediği imha kaydı (<see cref="RewindIbanConfirmationAsync"/>; yalnız Added, kaydedilmiş
    /// kayda dokunulmaz) ayrılır. Yeniden deneme onları DB'den taze okur ya da yeniden ekler; kapsamın sonraki SaveChanges'i
    /// düşen yazıyı tekrar denemez, imha kaydı iki kez yazılmaz.</summary>
    private void Discard()
    {
        foreach (var entry in _db.ChangeTracker.Entries()
                     .Where(e => e.Entity is PaymentMatch or PaymentMatchGap or CustomerIbanMemory
                         || (e.Entity is DataDestructionRecord && e.State == EntityState.Added)).ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>Satırın insan kararı: dekont bağı, gerçek müşteri, karar anı. Satır yoksa üçü de boş.</summary>
    private readonly record struct HumanDecision(Guid? PaymentId, Guid? ActualWpfCustomerId, DateTimeOffset? DecidedAt);

    /// <summary>Kaldırmanın denemeleri arasında taşınan durum: ilk denemenin gördüğü insan kararı, kaydedilen denemenin
    /// yeniden hesaplatacağı hareket (satırı yoksa null) ve sildiği IBAN bağlarının özetleri.</summary>
    private sealed class UnmatchAttempts
    {
        public HumanDecision? Seen { get; set; }
        public BankTransaction? Recompute { get; set; }
        public HashSet<string> DeletedBonds { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Elle eşlemenin kaydedilen denemesinin IBAN sonucu ve hareketin özeti (yeniden deneme üzerine yazar). Sonuç
    /// atanmadıysa null: enum'un varsayılanı (<see cref="ManualMatchIbanOutcome.Learned"/>) sessizce yeniden değerlendirme
    /// tetiklerdi.</summary>
    private sealed class ManualMatchAttempt
    {
        public ManualMatchIbanOutcome? Outcome { get; set; }
        public string? IbanHash { get; set; }
    }

    /// <summary>Bir tetiğin yeniden değerlendirmesi: yeniden hesaplanan kararsız satır, bunlardan müşteriye öneri alan ve sınıra
    /// takılıp takılmadığı.</summary>
    private sealed record IbanReevaluation(int Reevaluated, int ProposedToCustomer, bool More);

    /// <summary>Seçilen aday, seçim sorgusuyla satırın izlenerek okunması arasında başka bir karara bağlandı. Jetonun
    /// yakaladığı çakışma gibi işlenir: iz atılır, seçim taze satırlarla bir kez yeniden yapılır.</summary>
    private sealed class StaleCandidateException() : Exception("Aday hareket seçildikten sonra başka bir karara bağlandı.");
}
