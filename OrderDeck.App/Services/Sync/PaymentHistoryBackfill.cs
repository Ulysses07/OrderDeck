using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bir <see cref="PaymentHistoryBackfill"/> turunun sonucu.</summary>
public enum PaymentHistoryTourOutcome
{
    /// <summary>Lisans yok ya da LicenseId çözülemedi.</summary>
    NoLicense,
    /// <summary>Yerel kopya kimliği eksik ya da bozuk: kirasız kayıt ve tamamlanma hello'su yalnız v2 konuşur.</summary>
    NoReplica,
    /// <summary>Bu (lisans, kopya) için <c>sales/hello</c> henüz başarılı değil ya da geri yüklenmiş süreçte state ve
    /// kip adımı bekleniyor — hiçbir şey yapılmadı.</summary>
    WaitingForHello,
    /// <summary>Bu lisans ve kopya için ödeme geçmişi önceden tamamlandı.</summary>
    AlreadyCompleted,
    /// <summary>Bu turun sayfa bütçesi doldu; imleç kaldığı yerden sonraki turda sürer.</summary>
    InProgress,
    /// <summary>Bütün işler kuyruğa yazıldı; kuyruk henüz boşalmadı (gönderici götürecek).</summary>
    AwaitingQueue,
    /// <summary>Bu turda <c>hello {paymentHistoryCompleted: true}</c> gitti ve tamamlanma işaretlendi.</summary>
    Completed,
    /// <summary>Ağ ya da yerel hata; sonraki turda yeniden denenir.</summary>
    Failed,
}

/// <summary>
/// Ödeme geçmişinin geri doldurması (spec 2026-10-13 §6.4 "Ödeme geçmişi (mod Off'ta da)", §8.2-6, M17; W3-06). Ws'nin
/// ilk açılışında bu bilgisayarın yerel <c>PaymentJob</c>'larından, "zaten istendi" sorusuna giren her iş için
/// (<see cref="PaymentJob.IsDelivered"/>: teslim edilmiş ya da gitmiş olabilecek; miras işler dahil) bir kirasız kayıt
/// <c>PendingPaymentRecord</c>'a yazılır; kuyruğu <see cref="PaymentRecordSender"/> <c>payment-requests/backfill</c>'e
/// götürür. Kayıtlar sunucuda öbür bilgisayarın sorusuna, rozetlerine ve #508 planına girer: mod açılmadan önceki
/// istekler mod On'da da görünür. Mod kapısı her kopyada <c>PaymentHistoryCompletedAt</c> ister (§4.10-2).
///
/// <list type="number">
/// <item><b>Bekleme:</b> <see cref="SalesBackfillService"/>'in kapısıyla aynı — bu (lisans, kopya) için hello başarılı
/// ve geri yüklenmiş süreçte state ve kip adımı tamam (<see cref="SalesSyncEnvelope.IsReadyFor"/>); o ana kadar hiçbir
/// şey yapılmaz (§8.7).</item>
/// <item><b>Kuyruğa yazma:</b> imleç <c>SyncCursor('payment-history-out', LicenseKey)</c>, <c>Seq</c> = iş
/// <c>rowid</c>'i, <c>LastId</c> = kopya kimliği (<see cref="PaymentHistoryCursor"/>). Sayfa sayfa
/// (<see cref="PageSize"/>, turda en çok <see cref="MaxPagesPerTour"/>); imleç her sayfadan sonra yazılır, kesilen tur
/// kaldığı sayfadan sürer. Kuyruğa yazma kayıt Id'sine göre idempotent; yinelenen sayfa zararsız.</item>
/// <item><b>Kayıt</b> (<see cref="PaymentHistoryRecords.FromJob"/>): Id iş Id'si ile teslim ya da deneme sayısından;
/// sahip işin satırı, üyeler sahibin BUGÜNKÜ kişisi (≤ 100); kapsam işin asıl kapsamı; kapsadığı siparişler sahibin
/// kişisinin o kapsamdaki katkı veren etiketlerinden isteğin anından önce ya da o anda basılmış olanlar
/// (<c>Covers</c>: <c>PrintedAt ≤ PromptAt</c>, ikisi de bu bilgisayarın saati); asıl kapsamı bilinmeyen miras işte
/// boş. Kapsamı tanınmayan ya da sahibi tel biçimine sığmayan iş atlanır (sayısı günlükte).</item>
/// <item><b>Uçuştaki gönderim okunmaz:</b> sahibinin kişisinden bir üye ödeme akışında kiralıysa (ödeme akışıyla AYNI
/// <see cref="CustomerBusySet"/>) sayfa o işten önce kesilir, imleç onu geçmez, sonraki tur oradan sürer
/// (<see cref="ReadSettled"/>). Deneme yazılmış, teslimi henüz yazılmamış satırdan kurulan kayıt Unconfirmed(J,D,D+1)
/// olurdu, mod Off yazıcısı gönderim bitince Delivered(J,D+1) yazardı: tek mesaja iki Id. İş atlanmaz, beklenir: atlanan
/// işin önceki teslimini başka hiçbir yol göndermezdi.</item>
/// <item><b>Tamamlanma:</b> imleç bitti ∧ kuyruk boş (yazılan kayıtlar gönderildi ya da son durumla düştü) →
/// <see cref="SalesSyncStateService.ReportPaymentHistoryCompletedAsync"/> (<c>hello {paymentHistoryCompleted: true}</c>)
/// → imleçte tamamlanma işareti ve <c>SalesSyncState.PaymentHistorySentAt</c>. Bundan sonra bu (lisans, kopya) için
/// istek yok. Geri yüklemede kimlik döner: önceki kopya tamamladıysa işler yeniden kayda çevrilmez, yalnız yeni kopyanın
/// tamamlanması bildirilir (sonraki teslimler mod Off kaydı ya da kirayla sunucuda; kiranın kaydı işten türetilmez);
/// tamamlamadıysa imleç baştan başlar (aynı Id'ler; sunucu içerik aynıysa <c>unchanged</c>, arada değiştiyse
/// <c>content-conflict</c> sayar ve ilk kaydı tutar — <see cref="PaymentHistoryRecords"/>).</item>
/// </list>
///
/// <para><b>Sürücü</b> <see cref="SalesBackfillHostedService"/>'in 60 sn'lik turudur; o tur önce kuyruğu gönderir
/// (<see cref="SalesBackfillService"/>), sonra bu geri doldurma koşar: bu turda yazılan kayıtlar sonraki turun
/// gönderimiyle gider. Kendi ağ isteği yalnız lisans çözümü ve tamamlanma hello'su (100 istek/dk/IP bütçesi, §12 risk
/// 9). Moddan bağımsızdır. Canlı yol beklemez: arka plan iş parçacığında koşar; günlük yalnız kimlik ve sayı taşır
/// (tutar, ad yok).</para>
/// </summary>
public sealed class PaymentHistoryBackfill
{
    /// <summary>Bir sayfadaki en çok iş (bir geri doldurma partisi kadar).</summary>
    public const int DefaultPageSize = PaymentRequestLimits.MaxBackfillBatch;

    /// <summary>Bir turda en çok sayfa: kuyruğa yazılan kayıt göndericinin bir gönderimde götürebildiğinden çok
    /// büyümesin.</summary>
    public const int DefaultMaxPagesPerTour = 5;

    /// <summary>Süren bir hata serisinde iki uyarı arasındaki en kısa süre.</summary>
    internal static readonly TimeSpan FailureWarningInterval = TimeSpan.FromMinutes(10);

    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly SalesSyncStateService _state;
    private readonly SalesSyncStateRepository _stateRepo;
    private readonly PaymentJobRepository _jobs;
    private readonly CustomerRepository _customers;
    private readonly LabelRepository _labels;
    private readonly CustomerBusySet _busy;
    private readonly PaymentRecordSender _sender;
    private readonly PendingPaymentRecordRepository _pending;
    private readonly SyncCursorRepository _cursors;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<PaymentHistoryBackfill> _log;

    private readonly SemaphoreSlim _tourGate = new(1, 1);

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private (string LicenseKey, Guid ReplicaId)? _restartLoggedFor;
    private int? _lastLoggedRemaining;
    private string? _lastBusyJobId;
    private int _failures;
    private DateTimeOffset? _lastFailureWarnAt;

    public PaymentHistoryBackfill(
        LicenseApiClient api,
        SalesSyncEnvelope envelope,
        SalesSyncStateService state,
        SalesSyncStateRepository stateRepo,
        PaymentJobRepository jobs,
        CustomerRepository customers,
        LabelRepository labels,
        CustomerBusySet busy,
        PaymentRecordSender sender,
        PendingPaymentRecordRepository pending,
        SyncCursorRepository cursors,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<PaymentHistoryBackfill> log)
    {
        _api = api;
        _envelope = envelope;
        _state = state;
        _stateRepo = stateRepo;
        _jobs = jobs;
        _customers = customers;
        _labels = labels;
        _busy = busy;
        _sender = sender;
        _pending = pending;
        _cursors = cursors;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
    }

    /// <summary>Sayfa boyu (testler küçültür).</summary>
    internal int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Turdaki en çok sayfa (testler küçültür).</summary>
    internal int MaxPagesPerTour { get; init; } = DefaultMaxPagesPerTour;

    /// <summary>Bir tur. İptal <see cref="OperationCanceledException"/> ile çıkar; imleç son yazılan sayfada kalır.</summary>
    public Task<PaymentHistoryTourOutcome> TourOnceAsync(CancellationToken ct) =>
        Task.Run(async () =>
        {
            await _tourGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await TourCoreAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn(ex, "yerel hata");
                return PaymentHistoryTourOutcome.Failed;
            }
            finally
            {
                _tourGate.Release();
            }
        }, ct);

    private async Task<PaymentHistoryTourOutcome> TourCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return PaymentHistoryTourOutcome.NoLicense;
        if (_envelope.ReplicaId is not { } replicaId) return PaymentHistoryTourOutcome.NoReplica;

        var cursor = ReadCursor(licenseKey, replicaId);
        if (cursor.Phase == PaymentHistoryPhase.Completed) return PaymentHistoryTourOutcome.AlreadyCompleted;
        // SalesBackfillService'in kapısı: hello bu (lisans, kopya) için başarılı ve geri yüklenmiş süreçte state ve kip
        // adımı tamam (§8.7). Süreç HoldUntilRestart ile kapalıysa IsReadyFor false.
        if (!_envelope.HasHelloSucceeded(licenseKey, replicaId) || !_envelope.IsReadyFor(licenseKey, replicaId))
            return PaymentHistoryTourOutcome.WaitingForHello;

        if (cursor.Phase == PaymentHistoryPhase.Jobs)
        {
            var (next, finished) = EnqueuePages(licenseKey, replicaId, cursor, ct);
            cursor = next;
            if (!finished) return PaymentHistoryTourOutcome.InProgress;
        }

        // İmleç bitti: kuyruk boşalınca tamamlanma (bu kayıtlar gönderildi ya da son durumla düştü).
        var remaining = _pending.Count();
        if (remaining > 0)
        {
            if (_lastLoggedRemaining != remaining)
            {
                _lastLoggedRemaining = remaining;
                _log.LogInformation(
                    "Ödeme geçmişi: bütün işler kuyruğa yazıldı; kuyrukta {Remaining} kayıt gönderilmeyi bekliyor — "
                  + "boşalınca tamamlanacak (replica={ReplicaId})", remaining, replicaId);
            }
            return PaymentHistoryTourOutcome.AwaitingQueue;
        }

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
        if (licenseId is null) return PaymentHistoryTourOutcome.NoLicense;

        if (!await _state.ReportPaymentHistoryCompletedAsync(licenseKey, licenseId.Value, replicaId, ct)
                .ConfigureAwait(false))
        {
            Warn(null, "sales/hello (paymentHistoryCompleted) gönderilemedi");
            return PaymentHistoryTourOutcome.Failed;
        }
        SaveCursor(licenseKey, replicaId, PaymentHistoryCursor.Completed);
        _stateRepo.MarkPaymentHistorySent(_clock.UnixNow());
        _failures = 0;
        _lastFailureWarnAt = null;
        _log.LogInformation("Ödeme geçmişi geri doldurması tamamlandı; sunucuya bildirildi (replica={ReplicaId})",
            replicaId);
        return PaymentHistoryTourOutcome.Completed;
    }

    /// <summary>Turun sayfaları: imleçten sonraki işler kirasız kayda çevrilip kuyruğa yazılır, imleç her sayfadan sonra
    /// yazılır. <c>finished</c> = işler bitti (imleç <see cref="PaymentHistoryPhase.Done"/>). Kişisi ödeme akışında olan
    /// işte sayfa kesilir (<see cref="ReadSettled"/>): imleç o işten önceki satırda kalır, tur biter, sonraki tur oradan
    /// sürer.</summary>
    private (PaymentHistoryCursor Cursor, bool Finished) EnqueuePages(
        string licenseKey, Guid replicaId, PaymentHistoryCursor cursor, CancellationToken ct)
    {
        int pages = 0, scanned = 0, queued = 0, alreadyQueued = 0, skipped = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (pages >= MaxPagesPerTour) return (cursor, false);

                var rows = _jobs.GetAfterRowId(cursor.RowId, PageSize);
                if (rows.Count == 0)
                {
                    cursor = PaymentHistoryCursor.Done;
                    SaveCursor(licenseKey, replicaId, cursor);
                    _lastBusyJobId = null;
                    return (cursor, true);
                }

                long? passed = null;
                string? busyJobId = null;
                foreach (var (rowId, row) in rows)
                {
                    // Teslim edilmemiş satır kayda çevrilmez; okunduktan sonra başlayan bir gönderimi mod Off kaydı taşır.
                    if (row.IsDelivered)
                    {
                        var read = ReadSettled(row);
                        if (read.Busy)
                        {
                            busyJobId = row.Id;
                            break;
                        }
                        if (read.Job is { } job)
                        {
                            if (BuildRecord(job, read.Members) is not { } item) skipped++;
                            else if (_sender.EnqueueRecord(item)) queued++;
                            else alreadyQueued++;
                        }
                    }
                    scanned++;
                    passed = rowId;
                }
                if (passed is { } last)
                {
                    cursor = cursor.After(last);
                    SaveCursor(licenseKey, replicaId, cursor);
                }
                pages++;

                if (busyJobId is not null)
                {
                    if (_lastBusyJobId != busyJobId)
                    {
                        _lastBusyJobId = busyJobId;
                        _log.LogInformation(
                            "Ödeme geçmişi: işin kişisi şu an ödeme akışında (ya da iş okunurken değişti); sayfa bu işten "
                          + "önce kesildi, sonraki turda yeniden denenecek (job={JobId}, replica={ReplicaId})",
                            busyJobId, replicaId);
                    }
                    return (cursor, false);
                }
                _lastBusyJobId = null;
            }
        }
        finally
        {
            if (scanned > 0)
                _log.LogInformation(
                    "Ödeme geçmişi: {Pages} sayfa, {Scanned} iş tarandı; {Queued} kayıt kuyruğa alındı, {AlreadyQueued} "
                  + "zaten kuyruktaydı, {Skipped} iş kayda çevrilemedi (kapsam ya da sahip tel biçimine uymuyor) "
                  + "(replica={ReplicaId})",
                    pages, scanned, queued, alreadyQueued, skipped, replicaId);
        }
    }

    /// <summary>
    /// Teslim edilmiş görünen işin oturmuş satırı, <see cref="CustomerBusySet"/>'in kilidi altında. Ödeme akışı kişinin
    /// bütün üyelerini kiralar; gönderim denemesi, teslim ve mod Off kaydı o kira altında yazılır. Kira sürerken satır
    /// "deneme yazıldı, teslim yazılmadı" (<c>HasUnconfirmedSend</c>) görünebilir: ondan kurulan kayıt Unconfirmed(J,D,D+1)
    /// olur, gönderim bitince mod Off yazıcısı Delivered(J,D+1) yazar — tek mesaja iki Id, sunucu ikisini de <c>applied</c>
    /// sayar. Bu yüzden kişinin bir üyesi kiralıysa <c>Busy</c> döner (çağıran sayfayı keser).
    ///
    /// <para><b>Sıra önemli:</b> önce kira sorusu, SONRA satırın yeniden okunması. Kilit tutulurken yeni kira
    /// başlayamaz; kira bırakma ise kilitsizdir — satır önce okunsaydı arada biten bir akış uçuştaki satırı "oturmuş"
    /// gösterirdi. Kira yoksa yeniden okunan satır, o kişinin son akışının bütün yazılarını taşır (mod Off kaydı da
    /// kuyrukta). Satırın sahibi kira sorusundan sonra değiştiyse soru o kişi için sorulmamıştır: <c>Busy</c> (sonraki
    /// tur yeniden okur). Satır artık "teslim edilmiş ya da gitmiş olabilecek" değilse (<c>AbandonSend</c>) <c>Job</c>
    /// null.</para>
    ///
    /// <para>Kilit kısa tutulur (kişi ve iş okuması; kayıt kilit dışında kurulur). U17: hiçbir veritabanı işlemi açık
    /// değil; gövde yalnız okur.</para>
    /// </summary>
    private SettledJob ReadSettled(PaymentJob row) =>
        _busy.RunLocked(isBusy =>
        {
            var members = LocalMembers(row.CustomerId);
            if (members.Any(isBusy)) return new SettledJob(true, null, members);
            var job = _jobs.Get(row.Id);
            if (job is null || !job.IsDelivered) return new SettledJob(false, null, members);
            if (!string.Equals(job.CustomerId, row.CustomerId, StringComparison.Ordinal))
                return new SettledJob(true, null, members);
            return new SettledJob(false, job, members);
        });

    /// <summary><see cref="ReadSettled"/>'in sonucu.</summary>
    private readonly record struct SettledJob(bool Busy, PaymentJob? Job, IReadOnlyList<string> Members);

    /// <summary>Sahibin BUGÜNKÜ kişisi: satırı yönlendirildiyse (birleştirme, "@" ikizi) güncel satırın grubu. Sahip her
    /// durumda üyedir (sunucu sahibi üyelerde ister) ve önce gelir; satırı yoksa kişi yalnız sahiptir.</summary>
    private List<string> LocalMembers(string ownerId)
    {
        var person = _customers.GetPaymentPerson(ownerId);
        var localMembers = new List<string>(person.MemberIds.Count + 1) { ownerId };
        localMembers.AddRange(person.MemberIds);
        return localMembers;
    }

    /// <summary>İşin kirasız kaydı (<see cref="PaymentHistoryRecords.FromJob"/>); kurulamıyorsa null.</summary>
    /// <param name="localMembers">Sahibin kişisi (<see cref="LocalMembers"/>), kira sorusundaki okumadan.</param>
    private PaymentRequestBackfillItem? BuildRecord(PaymentJob job, IReadOnlyList<string> localMembers)
    {
        var scope = job.SaleScopeKey;
        if (!PaymentHistoryRecords.IsRecordScope(scope))
        {
            _log.LogWarning("Ödeme geçmişi: işin kapsamı kayıt biçimine uymuyor; atlandı (job={JobId})", job.Id);
            return null;
        }

        var members = PaymentHistoryRecords.WireMembers(job.CustomerId, localMembers);
        if (members is null)
        {
            _log.LogWarning("Ödeme geçmişi: işin sahip Id'si tel biçimine sığmıyor; atlandı (job={JobId})", job.Id);
            return null;
        }

        // Kapsanan siparişler yalnız kira kapsamında kurulabilir; asıl kapsamı bilinmeyen miras işte boş.
        IReadOnlyList<Guid> orders = [];
        if (PaymentHistoryRecords.IsLeaseScope(scope))
        {
            var printed = _labels.GetPrintedTotal(
                localMembers.Distinct(StringComparer.Ordinal).ToList(),
                PaymentHistoryRecords.SessionIdOf(scope),
                PaymentHistoryRecords.PromptAtOf(job));
            orders = PaymentHistoryRecords.WireOrders(printed.LabelIds);
        }
        return PaymentHistoryRecords.FromJob(job, members, orders);
    }

    // ── imleç ────────────────────────────────────────────────────────────

    /// <summary>
    /// İmleç (<see cref="PaymentHistoryCursor"/>): satır yoksa ya da çözülemiyorsa baştan. Başka kopyanınsa (geri yükleme,
    /// §8.7): önceki kopya geçmişi TAMAMLADIYSA işler yeniden kayda çevrilmez, yalnız yeni kopyanın tamamlanması kalır
    /// (<see cref="PaymentHistoryCursor.Done"/>: kuyruk boşalınca <c>hello {paymentHistoryCompleted: true}</c>) — o andan
    /// sonraki her teslim mod Off kaydıyla ya da kirayla zaten sunucuda; kiranın kaydı rastgele Id'lidir, işten türetilen
    /// kayıt onun ikinci öncülü olur ve eski istek en yeni sayılırdı. Tamamlamadıysa baştan (Id'ler işten türetilir: önceki
    /// kopyanın gönderdikleri sunucuda <c>unchanged</c>, içerik arada değiştiyse <c>content-conflict</c> ve ilk kayıt
    /// kalır).
    /// </summary>
    private PaymentHistoryCursor ReadCursor(string licenseKey, Guid replicaId)
    {
        var row = _cursors.Get(PaymentHistoryCursor.Name, licenseKey);
        if (row is null) return PaymentHistoryCursor.Start;
        if (row.LastId != replicaId)
        {
            var completed = row.Seq is { } previous
                            && PaymentHistoryCursor.Decode(previous) is { Phase: PaymentHistoryPhase.Completed };
            if (_restartLoggedFor != (licenseKey, replicaId))
            {
                _restartLoggedFor = (licenseKey, replicaId);
                if (completed)
                    _log.LogInformation(
                        "Ödeme geçmişi imleci başka bir kopya kimliğine ait (geri yükleme) ve o kopya geçmişi tamamlamıştı; "
                      + "işler yeniden gönderilmiyor, yalnız yeni kopyanın tamamlanması bildirilecek (replica={ReplicaId})",
                        replicaId);
                else
                    _log.LogInformation(
                        "Ödeme geçmişi imleci başka bir kopya kimliğine ait (geri yükleme); yeni kopya için baştan başlıyor "
                      + "(replica={ReplicaId})", replicaId);
            }
            return completed ? PaymentHistoryCursor.Done : PaymentHistoryCursor.Start;
        }
        if (row.Seq is not { } seq || PaymentHistoryCursor.Decode(seq) is not { } cursor)
        {
            _log.LogWarning("Ödeme geçmişi imleci çözülemedi ({Seq}); baştan başlıyor", row.Seq);
            return PaymentHistoryCursor.Start;
        }
        return cursor;
    }

    private void SaveCursor(string licenseKey, Guid replicaId, PaymentHistoryCursor cursor) =>
        _cursors.Upsert(PaymentHistoryCursor.Name, licenseKey,
            seq: cursor.Encode(), updatedAt: DateTimeOffset.UtcNow, lastId: replicaId);

    // ── yardımcılar ──────────────────────────────────────────────────────

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey) return _cachedLicenseId;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null)
            {
                _log.LogDebug("Ödeme geçmişi: geçerli lisans anahtarının LicenseId'si bulunamadı");
                return null;
            }
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn(ex, "LicenseId çözülemedi");
            return null;
        }
    }

    /// <summary>Hata uyarısı: serinin ilk hatası ve sürdükçe en çok <see cref="FailureWarningInterval"/>'da bir;
    /// arası Debug.</summary>
    private void Warn(Exception? ex, string what)
    {
        _failures++;
        var now = DateTimeOffset.UtcNow;
        if (_lastFailureWarnAt is { } last && now - last < FailureWarningInterval)
        {
            _log.LogDebug(ex, "Ödeme geçmişi: {What} (art arda {Failures}. hata)", what, _failures);
            return;
        }
        _lastFailureWarnAt = now;
        _log.LogWarning(ex, "Ödeme geçmişi: {What} (art arda {Failures}. hata); sonraki turda yeniden denenecek",
            what, _failures);
    }
}
