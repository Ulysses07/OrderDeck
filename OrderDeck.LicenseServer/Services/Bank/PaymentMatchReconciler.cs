using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// İnsan kararını gölge önerisine bağlar (spec §6). Aday = aynı lisans, gelen, tutar eşit,
/// ±2 gün, henüz bir dekonta bağlanmamış ve başka bir müşteriye elle verilmemiş. Tek aday → bağla; çoklu → gönderen
/// adının tüm tokenları açıklamada geçen TEK aday; hâlâ çoklu/yok → PaymentMatchGap. Onay IBAN öğretir; ret hiçbir şey
/// yapmaz.
/// <para><b>Bir insan kararı ötekini ezmez.</b> Başka müşteriye elle verilmiş hareket onayda aday değildir; aynı müşteriye
/// verilmişse dekont satıra yalnız iliştirilir, kararın durumu ve anı korunur. Elle eşleme yalnız karara bağlanmamış
/// (<see cref="PaymentMatch.DecidedAt"/> ve <see cref="PaymentMatch.PaymentId"/> boş) harekete verilir; yeniden karar önce
/// kaldırmadan geçer. Kaldırma hafıza satırını SİLER ve öneriyi yeniden hesaplar. Kimlikler admin formundan gelir: bu
/// kural serviste uygulanır.</para>
/// <para><b>Kaldırma dekontu ölçümden düşürmez.</b> Dekonta bağlı satırın kaldırılmasında dekont açık gap'e döner, aynı
/// SaveChanges'te: gap'i varsa (gap çözümüyle bağlanmışsa) yeniden açılır, yoksa (onayda bağlanmışsa)
/// <see cref="PaymentMatchGapReason.UnlinkedByAdmin"/> gap'i yazılır. Bu kural olmasaydı dekont ne bağlı ne gap kümesinde
/// kalırdı, çözülmüş gap de bağı kalkmış hareketi gösterirdi. Kaldırılan hareketle gap çözümü koşulmaz (yalnız eşleştirici yeniden
/// hesaplar): admin'in kaldırdığı bağ kendiliğinden geri gelmez.</para>
/// <para>Sonradan gelen hareket açık gap'i ancak dekontun aday seçimi yeniden koşulduğunda TEK aday kendisiyse çözer
/// (spec §3 ResolvedBankTransactionId). Onaydaki belirsizlik sürüyorsa (iki aday hâlâ bağsız) ya da aynı tutarlı birden
/// çok hareket geldiyse gap açık kalır: belirsizlik tahminle çözülmez.</para>
/// <para><see cref="BankHasher"/> istemez: yalnız harekette ve hafızada saklı hash'leri karşılaştırır.</para>
/// <para><b>Eşzamanlılık.</b> <see cref="PaymentMatch.UpdatedAt"/> eşzamanlılık jetonudur ve her yazan onu ilerletir. Bir
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
/// <para>Düşen deneme izleyicide iz bırakmaz (bu üç tablonun izlenen satırları ayrılır): kapsamın sonraki SaveChanges'i onu
/// yeniden denemez. Hareketler izlenmeden okunur.</para>
/// <para>Günlüğe açıklama, ad ya da IBAN yazılmaz; yalnız kimlikler.</para>
/// </summary>
public sealed class PaymentMatchReconciler
{
    public static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(2);

    /// <summary>Admin yolunda ikinci çakışmanın mesajı.</summary>
    public const string ConflictMessage = "Eşleşme bu sırada başka bir işlemle değişti; sayfayı yenileyip yeniden deneyin.";

    /// <summary>Karara bağlanmış harekete elle eşleme denendi: yeniden karar önce kaldırmadan geçer.</summary>
    public const string AlreadyDecidedMessage = "Bu hareket zaten bir karara bağlı; önce kaldırın.";

    private readonly LicenseDbContext _db;
    private readonly ILogger<PaymentMatchReconciler> _log;
    public PaymentMatcher Matcher { get; }

    public PaymentMatchReconciler(LicenseDbContext db, PaymentMatcher matcher, ILogger<PaymentMatchReconciler> log)
    { _db = db; Matcher = matcher; _log = log; }

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

    /// <exception cref="ObifinValidationException">Hareket ya da müşteri bu lisansta yok; ya da iki denemede de çakışma.</exception>
    public async Task ManualMatchAsync(Guid licenseId, Guid transactionId, Guid wpfCustomerId, CancellationToken ct)
    {
        if (!await RetryOnceAsync(() => ManualMatchOnceAsync(licenseId, transactionId, wpfCustomerId, ct), ct))
            throw new ObifinValidationException(ConflictMessage);
    }

    /// <exception cref="ObifinValidationException">Hareket bu lisansta yok; iki denemede de çakışma; ya da yeniden denemede
    /// satırın insan kararı ilk denemenin gördüğünden farklı.</exception>
    public async Task UnmatchAsync(Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        var attempts = new UnmatchAttempts();
        if (!await RetryOnceAsync(() => UnmatchOnceAsync(licenseId, transactionId, attempts, ct), ct))
            throw new ObifinValidationException(ConflictMessage);
    }

    private async Task ReconcileApprovalOnceAsync(Payment payment, CancellationToken ct)
    {
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

        await LinkAsync(candidates[0], payment.Id, wpfCustomerId.Value, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task TryResolveGapOnceAsync(BankTransaction tx, CancellationToken ct)
    {
        if (tx.Direction != BankTransactionDirection.Incoming) return;
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
        await LinkAsync(tx, pending.PaymentId, wpfCustomerId.Value, ct);
        gap.ResolvedBankTransactionId = tx.Id; gap.ResolvedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ManualMatchOnceAsync(Guid licenseId, Guid transactionId, Guid wpfCustomerId, CancellationToken ct)
    {
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
        await LearnIbanAsync(tx, wpfCustomerId, IbanMemorySource.ManualMatch, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task UnmatchOnceAsync(Guid licenseId, Guid transactionId, UnmatchAttempts attempts, CancellationToken ct)
    {
        var tx = await _db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException("Hareket bulunamadı.");
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct);
        // Kaldırma admin'in gördüğü karara yöneliktir. Yeniden denemede karar değiştiyse (arada dekont bağlandı, karar
        // verildi ya da kaldırıldı) yeniden uygulanmaz: admin'in görmediği bir bağı kaldırıp dekontu yetim bırakırdı.
        var decision = new HumanDecision(match?.PaymentId, match?.ActualWpfCustomerId, match?.DecidedAt);
        if (attempts.Seen is null) attempts.Seen = decision;
        else if (attempts.Seen != decision) throw new ObifinValidationException(ConflictMessage);

        var learned = await _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId && m.SourceBankTransactionId == tx.Id).ToListAsync(ct);
        _db.CustomerIbanMemories.RemoveRange(learned); // spec §3: iptal bayrağı değil, silme
        if (match is not null)
        {
            var now = DateTimeOffset.UtcNow;
            if (match.PaymentId is { } paymentId)
            {
                // Dekont ölçümde kalır (sınıf özeti). Ödeme başına tek gap (tekil indeks): varsa yeniden açılır, yoksa yazılır.
                var gap = await _db.PaymentMatchGaps.FirstOrDefaultAsync(g => g.PaymentId == paymentId, ct);
                if (gap is null)
                {
                    _db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = licenseId, PaymentId = paymentId,
                        Reason = PaymentMatchGapReason.UnlinkedByAdmin, CreatedAt = now });
                }
                else
                {
                    gap.ResolvedAt = null; gap.ResolvedBankTransactionId = null;
                }
            }
            match.PaymentId = null; match.ActualWpfCustomerId = null; match.DecidedAt = null;
            match.Status = PaymentMatchStatus.NoProposal; // insan-kararı kilidi kalkar, Matcher yeniden hesaplar
            match.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        // Yalnız eşleştirici: gap çözümü bu hareketle koşsaydı az önce kaldırılan bağı hemen geri kurardı.
        if (match is not null) await Matcher.MatchAsync(tx, ct);
    }

    private async Task<Guid?> ResolveWpfCustomerAsync(Guid? shopperId, Guid licenseId, CancellationToken ct)
    {
        if (shopperId is null) return null;
        return await _db.ShopperBroadcasterLinks.AsNoTracking()
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.WpfCustomerId != null)
            .OrderByDescending(l => l.JoinedAt).Select(l => l.WpfCustomerId).FirstOrDefaultAsync(ct);
    }

    /// <summary>Dekontun aday hareketleri (sınıf özeti): aynı lisans, gelen, tutar eşit, ödeme anının ±2 günü; bir dekonta
    /// bağlanmış ya da başka müşteriye elle verilmiş hareket aday değildir. Birden çok aday kalırsa gönderen adının tüm
    /// token'larını açıklamasında taşıyan TEK aday seçilir; seçilemezse liste olduğu gibi döner. Onay da gap çözümü de bunu
    /// kullanır: ikisi aynı kuralla karar verir.</summary>
    private async Task<List<BankTransaction>> CandidatesAsync(Guid licenseId, decimal amount, DateTimeOffset paidAt,
        string payerName, Guid wpfCustomerId, CancellationToken ct)
    {
        var windowStart = paidAt - CandidateWindow; var windowEnd = paidAt + CandidateWindow;
        var taken = _db.PaymentMatches
            .Where(m => m.LicenseId == licenseId
                        && (m.PaymentId != null || (m.ActualWpfCustomerId != null && m.ActualWpfCustomerId != wpfCustomerId)))
            .Select(m => m.BankTransactionId);
        var candidates = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && t.Direction == BankTransactionDirection.Incoming
                        && t.Amount == amount && t.OccurredAt >= windowStart && t.OccurredAt <= windowEnd && !taken.Contains(t.Id))
            .ToListAsync(ct);

        if (candidates.Count > 1)
        {
            var payer = BankTextNormalizer.Tokenize(payerName).Tokens.Where(t => t.Length >= 3).ToList();
            if (payer.Count > 0)
            {
                var narrowed = candidates.Where(t => { var d = BankTextNormalizer.Tokenize(t.Description).Tokens; return payer.All(p => d.Contains(p)); }).ToList();
                if (narrowed.Count == 1) candidates = narrowed;
            }
        }
        return candidates;
    }

    private async Task LinkAsync(BankTransaction tx, Guid paymentId, Guid wpfCustomerId, CancellationToken ct)
    {
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct) ?? await Matcher.MatchAsync(tx, ct);
        // Aday seçimi satırı izlemeden okudu; arada başka bir karara bağlandıysa seçim bayattır, yeniden deneme taze seçer.
        // Buradan kayda kadarki pencereyi jeton korur.
        if (match.PaymentId is not null || (match.ActualWpfCustomerId is { } decided && decided != wpfCustomerId))
            throw new StaleCandidateException();
        var now = DateTimeOffset.UtcNow;
        match.PaymentId = paymentId; match.UpdatedAt = now;
        // Aynı müşteriye elle verilmiş karar: dekont yalnız iliştirilir. Kararın durumu ve anı admin'inki kalır; IBAN'ı elle
        // eşleme zaten öğretti.
        if (match.ActualWpfCustomerId is not null) return;
        match.ActualWpfCustomerId = wpfCustomerId; match.DecidedAt = now;
        match.Status = match.ProposedWpfCustomerId is null ? PaymentMatchStatus.ManualOnly
            : match.ProposedWpfCustomerId == wpfCustomerId ? PaymentMatchStatus.ConfirmedByHuman : PaymentMatchStatus.Contradicted;
        await LearnIbanAsync(tx, wpfCustomerId, IbanMemorySource.HumanApproval, ct);
    }

    private async Task LearnIbanAsync(BankTransaction tx, Guid wpfCustomerId, IbanMemorySource source, CancellationToken ct)
    {
        if (tx.CounterpartyIbanHash is null) return;
        var existing = await _db.CustomerIbanMemories.FirstOrDefaultAsync(m => m.LicenseId == tx.LicenseId && m.IbanHash == tx.CounterpartyIbanHash, ct);
        if (existing is not null)
        {
            if (existing.WpfCustomerId != wpfCustomerId)
                _log.LogWarning("IBAN hafızası çelişkisi: hareket {TransactionId} başka müşteriye kayıtlı IBAN'dan geldi", tx.Id);
            return;
        }
        _db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = tx.LicenseId, WpfCustomerId = wpfCustomerId, IbanHash = tx.CounterpartyIbanHash,
            IbanMasked = tx.CounterpartyIbanMasked ?? "?", LearnedFrom = source, SourceBankTransactionId = tx.Id, CreatedAt = DateTimeOffset.UtcNow,
        });
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
    /// okunmuş Unchanged) ayrılır. Yeniden deneme onları DB'den taze okur; kapsamın sonraki SaveChanges'i düşen yazıyı
    /// tekrar denemez.</summary>
    private void Discard()
    {
        foreach (var entry in _db.ChangeTracker.Entries()
                     .Where(e => e.Entity is PaymentMatch or PaymentMatchGap or CustomerIbanMemory).ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>Satırın insan kararı: dekont bağı, gerçek müşteri, karar anı. Satır yoksa üçü de boş.</summary>
    private readonly record struct HumanDecision(Guid? PaymentId, Guid? ActualWpfCustomerId, DateTimeOffset? DecidedAt);

    /// <summary>Kaldırmanın denemeleri arasında taşınan durum: ilk denemenin gördüğü insan kararı.</summary>
    private sealed class UnmatchAttempts
    {
        public HumanDecision? Seen { get; set; }
    }

    /// <summary>Seçilen aday, seçim sorgusuyla satırın izlenerek okunması arasında başka bir karara bağlandı. Jetonun
    /// yakaladığı çakışma gibi işlenir: iz atılır, seçim taze satırlarla bir kez yeniden yapılır.</summary>
    private sealed class StaleCandidateException() : Exception("Aday hareket seçildikten sonra başka bir karara bağlandı.");
}
