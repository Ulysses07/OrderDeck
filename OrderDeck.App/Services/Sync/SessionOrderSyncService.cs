using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF App ↔ LicenseServer StreamSession + Order senkronizasyonu
/// (PR siparis-sync 2026-05-13). Yayıncı ve ekibi mobile Panel
/// "Siparişler" ekranında geçmiş yayınların sipariş listesini görmesi için.
///
/// WPF authoritative — yayın başlatma/bitirme + label oluşturma WPF'te
/// olur, server pasif replika. Pattern: <see cref="PaymentSyncService"/>
/// ile paralel. Push-only (reverse sync gerekmiyor).
///
/// <para><b>v2 etiket gönderimi</b> (spec 2026-10-13 §4.2, §5.2; W1-06): istek v2 zarfını (kopya kimliği,
/// <c>Protocol = 2</c>, görülen kip) ve <c>Format = 2</c>'yi taşır; her etiket 048 alanlarını, yedek ebeveynini,
/// kargo bağını ve <c>baseVersion = ServerVersion</c>'ı. Yanıt öğe başınadır: <c>applied</c> satırda
/// <c>SyncedAt</c>, <c>StockSyncedAt</c> ve <c>ServerVersion</c> tek kısa işlemde, F05 karşılaştır-ve-yaz kuralıyla
/// yazılır. Durumsuz öğe ya da v1 listesi eski sunucudur — bugünkü gibi onaylanır, sürüm bilinmez. Kopya kimliği
/// eksik ya da bozuksa gönderim durmaz, zarfsız (v1) gövde gider (<see cref="SalesSyncEnvelope.ForPush"/>); geri
/// yüklemeden sonra hello gidene kadar tur atlanır (§8.7).</para>
///
/// <para><b>Yok sayılan ve reddedilen satır</b> (§5.2, §4.2; W1-08): sunucunun güncel satırı (<c>current</c>)
/// etiket başına kendi kilitli işleminde uygulanır (<see cref="SalesPushApply"/>): <c>ignored</c>'da yok sayılan
/// birimler, <c>rejected</c>'da bütün birimler; mod Off'ta Y5 dosya bloğu dosya tutarını düzeltir, müşteri toplamı
/// türetilir, satır <c>ServerVersion = current.Version</c> ile onaylanır (Revision değişmez, DM8) ve
/// <c>SalesConflictLog</c>'a tek iz düşer. Uçuşta değişen satıra hiçbir şey yazılmaz. Uygulanan satırlar
/// <see cref="PushOutcome"/> ile arayüze gider: reddedilenler ("N değişiklik uygulanamadı") ve başka bilgisayarda
/// iptal edilmiş basılı kâğıtlar ("kâğıtları imha et"). Günlüğe yalnız sayılar ve kimlikler düşer (§9).</para>
///
/// <para><b>Oturumlar ve devir</b> (§4.1, §5.2, M2; W1-07): oturumlar da v2 zarfıyla gider, her öğe makine adını
/// taşır. Tur sırası: oturumlar → etiketler → etiketleri bu turda giden bitmiş oturumlar bir kez daha. Bitmiş,
/// <c>Authority = 'local'</c> ve devri henüz görülmemiş oturum için <see cref="HandoverGate"/> altında
/// <c>Label WHERE SessionId = @id AND SyncedAt IS NULL</c> sayılır: 0 ise <c>Release = true</c>, değilse
/// <c>Release = false</c> + <c>UnsentLabelCount</c>. Kapı o oturumun yanıtı uygulanana kadar tutulur. Yanıtı Core
/// uygular (<see cref="SessionRepository.ApplyRelease"/>): devir görülene kadar bitmiş oturum onaylanmaz, kuyrukta
/// kalır (geri alma penceresinde kapanış kaybolmaz); mod Off'ta <c>Authority</c> hiç yazılmaz. Bekletilen oturumun
/// gönderimi "gönderilen kayıt" sayılmaz (<see cref="SyncResult.SessionsPushed"/>): kapanıştaki boşaltma döngüsü
/// boşuna dönmez. Canlı ritimde (<c>liveCadence</c>) bekletilen oturum en çok <see cref="HeldResendInterval"/>'da bir
/// yeniden gider — gönderilmemiş etiket sayısı değişmedikçe.</para>
///
/// <para><b>Gönderim kapısı</b> (§5.2): <see cref="SyncOnceAsync(CancellationToken)"/> birden çok yerden eşzamanlı çağrılır —
/// zamanlayıcı (<see cref="SessionOrderSyncHostedService"/>), kapanıştaki "gönder ve kapat"
/// (<see cref="SyncFlushService.ForClose"/>) ve geri doldurmanın boşaltma adımı (<see cref="SalesBackfillService"/>,
/// §8.2-2; geri doldurmanın kendi partileri kapıyı tutmaz). Bütün tur tek bir
/// <see cref="SemaphoreSlim"/> altındadır: aynı parti iki kez gitmez, onay sırası karışmaz.</para>
///
/// <para><b>Ucuz tur</b> (sahip kararı Y9): yayında tur ≈5 sn'de bir koşar
/// (<see cref="SessionOrderSyncHostedService"/>). Gönderilecek oturum ya da etiket yoksa hiçbir istek atılmaz —
/// lisans çözümü dahil; kuyruk iki kısmi dizinden birer satırla okunur. Gönderim hatası uyarısı en çok
/// <see cref="FailureWarningInterval"/>'da bir yazılır, arası Debug.</para>
///
/// <para><b>Canlı yol beklemez:</b> tur arka plan iş parçacığında koşar ve her <c>await</c>
/// <c>ConfigureAwait(false)</c>'tır. Kuyruk okuması kendi bağlantısını HTTP'den ÖNCE kapatır, onay HTTP'den SONRA
/// kısa bir işlemde yazılır: hiçbir SQLite bağlantısı ya da yazma kilidi ağ isteğini beklemez — yavaş ya da düşmüş
/// sunucu etiket yazmayı, baskıyı ve iptali durdurmaz.</para>
/// </summary>
public sealed class SessionOrderSyncService
{
    private const int SessionBatchSize = 50;

    /// <summary>Turdaki en çok etiket (sunucunun tavanı 200).</summary>
    internal const int OrderBatchSize = 100;

    /// <summary>Sunucudaki <c>Orders.PrintBatchId</c> kolonunun uzunluğu. Uzun değer partinin TAMAMINI 400'e
    /// düşürürdü; bozuk tek satır bütün kuyruğu (ve devri) tutmasın diye boş gönderilir.</summary>
    internal const int PrintBatchIdMaxLength = 40;

    /// <summary>Durum satırındaki gönderim ilerlemesinin adı (D2 incelemesi I-3).</summary>
    public const string PushStatusName = "oturum-siparis";

    private readonly LicenseApiClient _api;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly SalesSyncEnvelope _envelope;
    private readonly HandoverGate _handover;
    private readonly SalesPushApply _pushApply;
    private readonly IClock _clock;
    private readonly ILogger<SessionOrderSyncService> _log;
    private readonly SyncStatusTracker? _tracker;

    /// <summary>Gönderim kapısı: bütün tur bunun altında (§5.2).</summary>
    private readonly SemaphoreSlim _pushGate = new(1, 1);

    /// <summary>İki gönderim hatası uyarısı arasındaki en kısa süre (Y9: yayında tur ≈5 sn'de bir).</summary>
    internal static readonly System.TimeSpan FailureWarningInterval = System.TimeSpan.FromMinutes(1);

    /// <summary>Canlı ritimde bekletilen (devri görülmemiş) bir oturumun iki gönderimi arasındaki en kısa süre —
    /// gönderilmemiş etiket sayısı değişmedikçe (§5.2; Y9'un ≈5 sn'lik turu her seferinde yinelemesin).</summary>
    public static readonly System.TimeSpan HeldResendInterval = System.TimeSpan.FromSeconds(60);

    private System.Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private long? _lastFailureWarnAt;

    /// <summary>Bekletilen oturumların son gönderimi (canlı ritimdeki seyreltme). Yalnız gönderim kapısı altında
    /// okunur ve yazılır.</summary>
    private readonly Dictionary<string, HeldSend> _held = new(System.StringComparer.Ordinal);

    private readonly record struct HeldSend(long Revision, int Unsent, long SentAt);

    /// <summary>Bu turun ilk geçişinde bekletilen oturum ve o anki gönderilmemiş etiket sayısı.</summary>
    private readonly record struct HeldInTour(string Id, int Unsent);

    public SessionOrderSyncService(
        LicenseApiClient api,
        SessionRepository sessions,
        LabelRepository labels,
        ICurrentLicenseProvider licenseProvider,
        SalesSyncEnvelope envelope,
        HandoverGate handover,
        SalesPushApply pushApply,
        IClock clock,
        ILogger<SessionOrderSyncService> log,
        SyncStatusTracker? statusTracker = null)
    {
        _api = api;
        _sessions = sessions;
        _labels = labels;
        _licenseProvider = licenseProvider;
        _envelope = envelope;
        _handover = handover;
        _pushApply = pushApply;
        _clock = clock;
        _log = log;
        _tracker = statusTracker;
        _tracker?.RegisterPush(PushStatusName);
    }

    /// <summary>Bir turda sunucunun değeri uygulanan satırlar (W1-08).</summary>
    public event EventHandler<SalesPushOutcome>? PushOutcome;

    /// <param name="SessionsPushed">Onaylanan (ya da uçuşta değiştiği için yeniden gidecek) oturumlar. Bekletilen
    /// (devri görülmemiş) oturumun gönderimi sayılmaz (§5.2): kapanıştaki boşaltma döngüsü yalnız bir şey
    /// gönderildikçe yineler.</param>
    public readonly record struct SyncResult(int SessionsPushed, int OrdersPushed);

    /// <summary>Bir gönderim turu; gönderim kapısından geçer (eşzamanlı çağrılar sıraya girer). Belirteç kapıyı
    /// beklerken de dinlenir: kapanışın payı dolunca beklemeden çıkar.</summary>
    public Task<SyncResult> SyncOnceAsync(System.Threading.CancellationToken ct = default)
        => SyncOnceAsync(liveCadence: false, ct);

    /// <param name="liveCadence">Canlı yayın ritmindeki tur (≈5 sn, Y9;
    /// <see cref="SessionOrderSyncHostedService"/>): bekletilen oturum yalnız gönderilmemiş etiket sayısı değiştiyse
    /// ya da son gönderiminden <see cref="HeldResendInterval"/> geçtiyse yeniden gider. Boşta ritim, kapanış ve diğer
    /// çağıranlar seyreltmez.</param>
    /// <param name="ct">Belirteç.</param>
    public async Task<SyncResult> SyncOnceAsync(bool liveCadence, System.Threading.CancellationToken ct = default)
    {
        await EnterGateAsync(_pushGate, ct).ConfigureAwait(false);
        try
        {
            return await SyncCoreAsync(liveCadence, ct).ConfigureAwait(false);
        }
        finally
        {
            _pushGate.Release();
        }
    }

    /// <summary>
    /// Gönderim kapısına girer. Belirteç kapıyı beklerken iptal edildiyse kapı alınmış olsa bile geri bırakılır ve
    /// <see cref="System.OperationCanceledException"/> fırlar: <see cref="System.Threading.CancellationTokenSource.Cancel()"/>
    /// önce bayrağı kurar, <see cref="System.Threading.SemaphoreSlim"/>'in bekleyeni listeden ancak sonra (iş havuzunda)
    /// çıkar. O arada gelen <c>Release</c> kapıyı iptal edilmiş bekleyene verirdi; kapanışın payı dolduktan sonra bir
    /// tur daha giderdi.
    /// </summary>
    internal static async Task EnterGateAsync(System.Threading.SemaphoreSlim gate, System.Threading.CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        if (!ct.IsCancellationRequested) return;
        gate.Release();
        ct.ThrowIfCancellationRequested();
    }

    private async Task<SyncResult> SyncCoreAsync(bool liveCadence, System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_licenseProvider.CurrentLicenseKey))
        {
            _log.LogDebug("Session/Order sync skipped — no license key");
            return default;
        }

        // Y9: yayında tur ≈5 sn'de bir koşar; gönderilecek bir şey yoksa HİÇBİR istek atılmaz — lisans çözümü dahil.
        // İki kısmi dizinden birer satırlık okuma.
        if (!_sessions.HasUnsynced() && !_labels.HasUnsynced())
        {
            _tracker?.MarkPushOk(PushStatusName, System.DateTimeOffset.UtcNow);   // I-3: gönderecek bir şey yok
            return default;
        }

        // null: geri yüklemeden sonra hello bekleniyor — hiçbir gönderim yapılmaz (§8.7).
        if (_envelope.ForPush() is not { } envelope)
        {
            _log.LogDebug("Session/Order sync skipped — waiting for sales/hello after restore");
            return default;
        }

        var licenseId = await ResolveLicenseIdAsync(ct).ConfigureAwait(false);
        if (licenseId is null)
        {
            _log.LogDebug("Session/Order sync skipped — no active license resolved");
            return default;
        }

        // Kapanış sırası (§5.2, M2): önce oturumlar (Order.SessionId FK olduğu için; bitmiş oturum gönderilmemiş
        // etiket sayısıyla bekletilir), sonra etiketler, sonra etiketleri bu turda giden bitmiş oturumlar yeniden —
        // kuyrukları boşaldıysa Release = true ile.
        var heldThisTour = new List<HeldInTour>();
        var (sessionsPushed, sessionsOk) = await PushSessionsAsync(
            licenseId.Value, envelope, liveCadence, heldThisTour, ct).ConfigureAwait(false);
        var (ordersPushed, ordersOk) = await PushOrdersAsync(licenseId.Value, envelope, ct).ConfigureAwait(false);
        var (releasesPushed, releasesOk) = await PushReleasesAsync(
            licenseId.Value, envelope, heldThisTour, ct).ConfigureAwait(false);
        sessionsPushed += releasesPushed;

        // I-3: gönderimler başardı ya da gönderecek bir şey yoktu — durum satırı sağlıklı sayar.
        if (sessionsOk && ordersOk && releasesOk) _tracker?.MarkPushOk(PushStatusName, System.DateTimeOffset.UtcNow);

        if (sessionsPushed > 0 || ordersPushed > 0)
            _log.LogInformation(
                "Session/Order sync: sessions={Sessions} orders={Orders}",
                sessionsPushed, ordersPushed);

        return new SyncResult(sessionsPushed, ordersPushed);
    }

    /// <summary>Devir adayı (§5.2): bitmiş, bu bilgisayarın yazdığı ve devri henüz görülmemiş oturum.</summary>
    private static bool IsHandoverCandidate(StreamSession s) =>
        s.EndedAt is not null && s.Authority == StreamSessionAuthority.Local && s.ReleasedAt is null;

    /// <summary>
    /// Turun ilk geçişi: kuyruktaki oturumlar. v2'de devir adaylarının kilidi alınır, gönderilmemiş etiketleri sayılır
    /// ve kilit yanıt uygulanana kadar tutulur. Canlı ritimde son gönderiminden bu yana sayısı değişmemiş ve
    /// <see cref="HeldResendInterval"/> dolmamış bekletilen oturum atlanır.
    /// </summary>
    /// <param name="heldOut">Bu geçişte bekletilen oturumlar (turun son geçişinin girdisi).</param>
    /// <returns>Sayılan (bekletilmeyen) oturum sayısı; <c>Ok</c> = gönderim başarılı ya da gönderecek bir şey
    /// yoktu.</returns>
    private async Task<(int Pushed, bool Ok)> PushSessionsAsync(
        System.Guid licenseId, SalesPushEnvelope envelope, bool liveCadence, List<HeldInTour> heldOut,
        System.Threading.CancellationToken ct)
    {
        var batch = _sessions.GetUnsynced(SessionBatchSize);
        if (batch.Count == 0) return (0, true);

        // v1 gövdesi (kimlik bozuk) devir bayrağı taşımaz: sunucu bugünkü gibi "EndedAt dolu gelmesi yeter" der.
        var candidates = envelope.IsV2
            ? batch.Where(IsHandoverCandidate).Select(s => s.Id).ToList()
            : new List<string>();
        var leases = await EnterHandoverAsync(candidates, ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UnixNow();
            var unsent = new Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var id in candidates) unsent[id] = _labels.CountUnsyncedBySession(id);

            var send = new List<StreamSession>(batch.Count);
            foreach (var s in batch)
            {
                if (liveCadence && unsent.TryGetValue(s.Id, out var count) && IsThrottled(s, count, now))
                {
                    // Sayısı değişmemiş bekletilen oturum: istek yok, kapısı hemen açılır. Etiketleri bu turda
                    // giderse son geçiş onu yine devreder.
                    if (leases.Remove(s.Id, out var lease)) lease.Dispose();
                    heldOut.Add(new HeldInTour(s.Id, count));
                    continue;
                }
                send.Add(s);
            }
            if (send.Count == 0) return (0, true);

            return await SendSessionsAsync(licenseId, envelope, send, unsent, now, heldOut, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseAll(leases);
        }
    }

    /// <summary>
    /// Turun son geçişi (§5.2 "Etiketler gittikten sonra oturum aynı turda bir kez daha gönderilir"): ilk geçişte
    /// gönderilmemiş etiketleri yüzünden bekletilen oturum, kilit altında yeniden sayılır; kuyruğu boşaldıysa
    /// <c>Release = true</c> ile gider. Hâlâ etiketi bekleyen oturum bu turda yeniden gönderilmez.
    /// </summary>
    private async Task<(int Pushed, bool Ok)> PushReleasesAsync(
        System.Guid licenseId, SalesPushEnvelope envelope, IReadOnlyList<HeldInTour> heldThisTour,
        System.Threading.CancellationToken ct)
    {
        var due = heldThisTour.Where(h => h.Unsent > 0).Select(h => h.Id).ToList();
        if (due.Count == 0) return (0, true);

        var leases = await EnterHandoverAsync(due, ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UnixNow();
            var unsent = new Dictionary<string, int>(System.StringComparer.Ordinal);
            var send = new List<StreamSession>(due.Count);
            foreach (var id in due)
            {
                var s = _sessions.GetById(id);
                if (s is not null && s.SyncedAt is null && IsHandoverCandidate(s)
                    && _labels.CountUnsyncedBySession(id) == 0)
                {
                    unsent[id] = 0;
                    send.Add(s);
                }
                else if (leases.Remove(id, out var lease))
                {
                    lease.Dispose();
                }
            }
            if (send.Count == 0) return (0, true);

            return await SendSessionsAsync(licenseId, envelope, send, unsent, now, heldOut: null, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseAll(leases);
        }
    }

    /// <summary>Tek <c>sessions/sync</c> isteği ve yanıtının uygulanması. Çağıran devir adaylarının kilidini
    /// tutar.</summary>
    /// <param name="unsent">Devir adaylarının gönderilmemiş etiket sayısı (kilit altında okundu).</param>
    private async Task<(int Pushed, bool Ok)> SendSessionsAsync(
        System.Guid licenseId, SalesPushEnvelope envelope, IReadOnlyList<StreamSession> send,
        IReadOnlyDictionary<string, int> unsent, long now, List<HeldInTour>? heldOut,
        System.Threading.CancellationToken ct)
    {
        var machineName = string.IsNullOrEmpty(_envelope.MachineName) ? null : _envelope.MachineName;
        var items = send.Select(s => ToSyncSessionItem(s, envelope.IsV2, machineName, unsent)).ToList();
        var request = envelope.IsV2
            ? new SyncSessionsRequest(items, envelope.ReplicaId, envelope.Protocol, envelope.SeenModeField)
            : new SyncSessionsRequest(items);

        List<SyncedSessionDto> response;
        try
        {
            response = await _api.SyncSessionsAsync(licenseId, request, ct).ConfigureAwait(false);
        }
        catch (LicenseApiException ex)
        {
            LogFailure(ex, "Session outbox push failed: {Code}", ex.Code);
            return (0, false);
        }

        var byId = new Dictionary<System.Guid, SyncedSessionDto>(response.Count);
        foreach (var r in response) byId[r.Id] = r;

        var pushed = 0;
        foreach (var s in send)
        {
            // Yanıtta olmayan oturum ya da protokolsüz yanıt: eski sunucu — bugünkü gibi onay.
            var result = byId.TryGetValue(System.Guid.Parse(s.Id), out var dto)
                ? new SessionPushResult(dto.Protocol, dto.ReleasedAt?.ToUnixTimeSeconds(), dto.Status, dto.Reason)
                : SessionPushResult.Legacy;
            var outcome = _sessions.ApplyRelease(s.Id, s.Revision, result, now);
            if (outcome == SessionAckOutcome.Held)
            {
                var count = unsent.GetValueOrDefault(s.Id);
                _held[s.Id] = new HeldSend(s.Revision, count, now);
                heldOut?.Add(new HeldInTour(s.Id, count));
                _log.LogDebug("Oturum {SessionId} devir bekliyor: {Unsent} etiket gönderilmedi", s.Id, count);
                continue;
            }

            _held.Remove(s.Id);
            pushed++;
            switch (outcome)
            {
                case SessionAckOutcome.Released:
                    _log.LogInformation("Oturum {SessionId} sunucuya devredildi", s.Id);
                    break;
                case SessionAckOutcome.OwnedElsewhere:
                    _log.LogWarning(
                        "Oturum {SessionId} sunucuda başka kopyanın; kapanışı yok sayıldı, iz SalesConflictLog'da", s.Id);
                    break;
            }
        }
        return (pushed, true);
    }

    /// <summary>v2 öğesi (§4.1): makine adı her öğede; devir adayında <c>Release</c> (kuyruk boşsa) ya da
    /// <c>UnsentLabelCount</c>, öbür oturumlarda <c>Release = false</c>. v1'de üçü de yazılmaz. Geri doldurma
    /// (<see cref="SalesBackfillService"/>) aynı öğeyi kurar.</summary>
    internal static SyncSessionItem ToSyncSessionItem(
        StreamSession s, bool v2, string? machineName, IReadOnlyDictionary<string, int> unsent)
    {
        var count = unsent.TryGetValue(s.Id, out var n) ? n : (int?)null;
        return new SyncSessionItem(
            Id: System.Guid.Parse(s.Id),
            Title: s.Title,
            StartedAt: System.DateTimeOffset.FromUnixTimeSeconds(s.StartedAt),
            EndedAt: s.EndedAt.HasValue ? System.DateTimeOffset.FromUnixTimeSeconds(s.EndedAt.Value) : null,
            Platforms: string.Join(",", s.Platforms),
            Notes: s.Notes,
            Release: v2 ? count == 0 : null,
            MachineName: v2 ? machineName : null,
            UnsentLabelCount: v2 && count > 0 ? count : null);
    }

    /// <summary>Canlı ritimde bekletilen oturumun yeniden gönderimi seyreltilir mi: aynı satır, aynı gönderilmemiş
    /// sayı ve son gönderiminden <see cref="HeldResendInterval"/> geçmedi.</summary>
    private bool IsThrottled(StreamSession s, int unsent, long now) =>
        _held.TryGetValue(s.Id, out var last)
        && last.Revision == s.Revision
        && last.Unsent == unsent
        && now - last.SentAt < (long)HeldResendInterval.TotalSeconds;

    /// <summary>Devir kilitlerini kimlik sırasıyla alır (geri doldurma da aynı sırayla alır: kilitlenme olmaz).
    /// Belirteç iptal olursa alınanlar bırakılır.</summary>
    private async Task<Dictionary<string, System.IDisposable>> EnterHandoverAsync(
        IEnumerable<string> sessionIds, System.Threading.CancellationToken ct)
    {
        var leases = new Dictionary<string, System.IDisposable>(System.StringComparer.Ordinal);
        try
        {
            foreach (var id in sessionIds.Distinct(System.StringComparer.Ordinal).OrderBy(i => i, System.StringComparer.Ordinal))
                leases[id] = await _handover.EnterAsync(id, ct).ConfigureAwait(false);
            return leases;
        }
        catch
        {
            ReleaseAll(leases);
            throw;
        }
    }

    private static void ReleaseAll(Dictionary<string, System.IDisposable> leases)
    {
        foreach (var lease in leases.Values) lease.Dispose();
        leases.Clear();
    }

    /// <returns>Gönderilen sayı; <c>Ok</c> = gönderim başarılı ya da gönderecek bir şey yoktu.</returns>
    private async Task<(int Pushed, bool Ok)> PushOrdersAsync(
        System.Guid licenseId, SalesPushEnvelope envelope, System.Threading.CancellationToken ct)
    {
        var batch = _labels.GetUnsyncedForPush(OrderBatchSize);
        if (batch.Count == 0) return (0, true);

        var items = batch.Select(ToSyncOrderItem).ToList();
        // Bu istemci katalog kimliği gönderebiliyor; sunucu stok hareketini yalnız bu bayrak açıkken üretiyor.
        var request = envelope.IsV2
            ? new SyncOrdersRequest(items, CatalogAware: true,
                ReplicaId: envelope.ReplicaId, Protocol: envelope.Protocol, SeenMode: envelope.SeenModeField,
                Format: OrderFormatV2)
            : new SyncOrdersRequest(items, CatalogAware: true);

        List<SyncedOrderV2Dto> response;
        try
        {
            response = await _api.SyncOrdersV2Async(licenseId, request, ct).ConfigureAwait(false);
        }
        catch (LicenseApiException ex)
        {
            LogFailure(ex, "Order outbox push failed: {Code}", ex.Code);
            return (0, false);
        }

        var now = _clock.UnixNow();
        var (verdicts, unknown) = Acknowledge(batch, items, response, now);
        var outcome = ApplyVerdicts(verdicts, now);
        if (unknown > 0)
            _log.LogWarning(
                "orders/sync: {Unknown} etiketin durumu tanınmadı; satırlar sürümsüz onaylandı", unknown);
        if (outcome is not null)
            RaisePushOutcome(outcome);
        return (batch.Count, true);
    }

    /// <summary>v2 öğe biçimi (§4.2).</summary>
    private const int OrderFormatV2 = 2;

    /// <summary>
    /// Yanıtı satırlarla eşler. <c>applied</c>: sürümüyle (<c>ServerVersion</c>) onay. Eski sunucu (durumsuz öğe, v1
    /// listesi, yanıtta olmayan satır): bugünkü gibi, sürümsüz onay. Tanınmayan durum: sürümsüz onay (sürüm yazmak
    /// sonraki gönderimi "güncel sürümü gördüm" diye damgalı birimi ezebilir hâle getirirdi, K2). Onaylar tek işlemde
    /// yazılır. <c>ignored</c> ve <c>rejected</c> onaylanmaz: <see cref="ApplyVerdicts"/>'e döner.
    /// </summary>
    private (List<LabelPushVerdict> Verdicts, int Unknown) Acknowledge(
        IReadOnlyList<LabelPushRow> batch, IReadOnlyList<SyncOrderItem> items, IReadOnlyList<SyncedOrderV2Dto> response,
        long now)
    {
        var byId = new Dictionary<System.Guid, SyncedOrderV2Dto>(response.Count);
        foreach (var r in response) byId[r.Id] = r;

        var unknown = 0;
        var verdicts = new List<LabelPushVerdict>();
        var acks = new List<LabelPushAck>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            int? version = null;
            if (byId.TryGetValue(items[i].Id, out var r) && !r.IsLegacy)
            {
                switch (r.Status)
                {
                    case SyncedOrderV2Dto.StatusApplied:
                        version = r.Version;
                        break;
                    case SyncedOrderV2Dto.StatusIgnored:
                    case SyncedOrderV2Dto.StatusRejected:
                        verdicts.Add(ToVerdict(batch[i], r));
                        continue;
                    default:
                        unknown++;
                        break;
                }
            }
            acks.Add(new LabelPushAck(batch[i].Id, batch[i].Revision, version));
        }

        // F05: Revision karşılaştırmalı; uçuş sırasında değişen satıra yazılmaz, sonraki tur güncel hâliyle gönderir.
        _labels.MarkSyncedBatch(acks, now);
        return (verdicts, unknown);
    }

    /// <summary>Tel yanıtından Core'un girdisi: gönderilen satırın Revision'ı (F05) ve <c>current</c>'ın birim
    /// alanları yerel biçimde (unix saniye, "N" kimlik). Oluşum alanları ve CustomerId taşınmaz.</summary>
    internal static LabelPushVerdict ToVerdict(LabelPushRow pushed, SyncedOrderV2Dto r) => new(
        pushed.Id,
        pushed.Revision,
        r.Status == SyncedOrderV2Dto.StatusRejected ? LabelPushVerdictKind.Rejected : LabelPushVerdictKind.Ignored,
        r.IgnoredUnits ?? System.Array.Empty<string>(),
        r.Reason,
        r.Current is { } c
            ? new LabelServerState(
                c.Version, c.Price, c.PriceCorrectedAt?.ToUnixTimeSeconds(), c.PaidPrice,
                c.CancelledAt?.ToUnixTimeSeconds(), c.CancelReason, c.IsTentativeBackup,
                c.ShipmentId?.ToString("N"),
                c.PrintedAt?.ToUnixTimeSeconds(), c.PrintBatchId, c.PrintSeq)
            : null);

    /// <summary>
    /// <c>ignored</c> ve <c>rejected</c> satırlarda sunucunun değeri — etiket başına kendi kilitli işleminde
    /// (<see cref="SalesPushApply"/>; §5.2): birimler, Y5 dosya bloğu, müşteri toplamı, sürümlü onay ve iz. Uçuşta
    /// değişen satır atlanır, yeni hâli sonraki turda gider. Günlüğe yalnız sayılar düşer (§9).
    /// </summary>
    /// <returns>Bu turda uygulanan satır varsa arayüze gidecek sonuç; yoksa <c>null</c>.</returns>
    private SalesPushOutcome? ApplyVerdicts(IReadOnlyList<LabelPushVerdict> verdicts, long now)
    {
        if (verdicts.Count == 0) return null;

        int ignored = 0, stale = 0, missing = 0, missingFiles = 0, shippedFiles = 0;
        var rejected = new List<RejectedLabelPush>();
        var discard = new List<string>();
        foreach (var verdict in verdicts)
        {
            var result = _pushApply.Apply(verdict, now);
            switch (result.Status)
            {
                case LabelPushApplyStatus.Applied or LabelPushApplyStatus.Terminal:
                    if (verdict.Kind == LabelPushVerdictKind.Rejected)
                        rejected.Add(new RejectedLabelPush(verdict.LabelId, verdict.Reason));
                    else
                        ignored++;
                    if (result.PrintDiscard) discard.Add(verdict.LabelId);
                    missingFiles += result.Shipments.Missing;
                    shippedFiles += result.Shipments.Shipped.Count;
                    _log.LogDebug("orders/sync: etiket {LabelId} {Status}, neden {Reason}, değişen birim {Units}",
                        verdict.LabelId, result.Status, verdict.Reason, string.Join(",", result.ChangedUnits));
                    break;
                case LabelPushApplyStatus.Stale:
                    stale++;
                    break;
                case LabelPushApplyStatus.Missing:
                    missing++;
                    break;
            }
        }

        _log.LogWarning(
            "orders/sync: {Ignored} etiket yok sayıldı, {Rejected} etiket reddedildi; sunucunun değeri uygulandı. "
          + "{Stale} etiket uçuşta değişti (sonraki turda yeniden gider), {Missing} etiket yerelde yok, "
          + "{Discard} kâğıt imha edilmeli; kargo bloğu {MissingFiles} dosyayı yerelde bulamadı, "
          + "{ShippedFiles} kargolanmış dosyaya dokunmadı",
            ignored, rejected.Count, stale, missing, discard.Count, missingFiles, shippedFiles);

        return ignored + rejected.Count == 0 ? null : new SalesPushOutcome(ignored, rejected, discard);
    }

    /// <summary>Sonucu dinleyenlere verir. Dinleyicinin hatası turu düşürmez: satırlar zaten uygulandı ve
    /// onaylandı, bir sonraki tur aynı yanıtı yeniden almaz.</summary>
    private void RaisePushOutcome(SalesPushOutcome outcome)
    {
        try
        {
            PushOutcome?.Invoke(this, outcome);
        }
        catch (System.Exception ex)
        {
            _log.LogWarning(ex, "orders/sync: gönderim sonucu arayüze iletilemedi");
        }
    }

    /// <summary>Yerel satırdan tel öğesi (§4.2): TEXT "N" kimlikler Guid'e, unix saniyeler DateTimeOffset'e,
    /// <c>baseVersion = ServerVersion</c>. Ayrıştırılamayan kimlik boş gider: bozuk tek satır bütün partiyi
    /// düşürmemeli.</summary>
    internal static SyncOrderItem ToSyncOrderItem(LabelPushRow l) => new(
        Id: System.Guid.Parse(l.Id),
        SessionId: ParseGuid(l.SessionId),
        CustomerId: l.CustomerId,
        Platform: l.Platform,
        Username: l.Username,
        DisplayName: l.DisplayName,
        MessageText: l.MessageText,
        Code: l.Code,
        Price: l.Price,
        AddedAt: System.DateTimeOffset.FromUnixTimeSeconds(l.AddedAt),
        PrintedAt: FromUnix(l.PrintedAt),
        CancelledAt: FromUnix(l.CancelledAt),
        CancelReason: l.CancelReason,
        IsShippingFee: l.IsShippingFee,
        IsBackupPromoted: l.IsBackupPromoted,
        IsTentativeBackup: l.IsTentativeBackup,
        ProductId: ParseGuid(l.ProductId),
        ProductVariantId: ParseGuid(l.ProductVariantId),
        BaseVersion: l.ServerVersion is { } v ? (int)v : null,
        ParentOrderId: ParseGuid(l.ParentLabelId),
        PrintBatchId: l.PrintBatchId is { Length: <= PrintBatchIdMaxLength } batchId ? batchId : null,
        PrintSeq: l.PrintSeq is { } seq ? (int)seq : null,
        PriceCorrectedAt: FromUnix(l.PriceCorrectedAt),
        PaidPrice: l.PaidPrice,
        ShipmentId: ParseGuid(l.ShipmentId));

    private static System.Guid? ParseGuid(string? value) => System.Guid.TryParse(value, out var g) ? g : null;

    private static System.DateTimeOffset? FromUnix(long? seconds) =>
        seconds is { } s ? System.DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private async Task<System.Guid?> ResolveLicenseIdAsync(System.Threading.CancellationToken ct)
    {
        var key = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null)
            {
                LogFailure(null, "LicenseId not found for current license key");
                return null;
            }
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (LicenseApiException ex)
        {
            LogFailure(ex, "Failed to resolve LicenseId: {Code}", ex.Code);
            return null;
        }
    }

    /// <summary>
    /// Gönderim hatası günlüğü (Y9): yayında tur ≈5 sn'de bir koştuğu için çevrimdışı bir bilgisayar her turda uyarı
    /// yazardı. Uyarı en çok <see cref="FailureWarningInterval"/>'da bir; arası Debug. Tur kapı altında koştuğu için
    /// durum tek iş parçacığından okunur ve yazılır.
    /// </summary>
    private void LogFailure(System.Exception? ex, string message, params object?[] args)
    {
        var now = _clock.UnixNow();
        if (_lastFailureWarnAt is { } last && now - last < (long)FailureWarningInterval.TotalSeconds)
        {
            _log.LogDebug(ex, message, args);
            return;
        }
        _lastFailureWarnAt = now;
        _log.LogWarning(ex, message, args);
    }
}
