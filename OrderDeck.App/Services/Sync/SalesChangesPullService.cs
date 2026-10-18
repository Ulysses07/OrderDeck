using Microsoft.Extensions.Logging;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bir satış akışı turunun sonucu (<see cref="SalesChangesPullService.TourOnceAsync"/>).</summary>
public enum SalesPullOutcome
{
    /// <summary>Lisans yok ya da çözülemedi: hiçbir istek gitmedi.</summary>
    NoLicense,
    /// <summary>Yerel kip Off: tur yalnız <c>sales/state</c> kalp atışıydı; akış istenmedi, hiçbir şey yazılmadı.</summary>
    ModeOff,
    /// <summary>Sunucu modu On, yerel kip açılamadı (§8.4): bir giden kuyruk boşalmadı ya da kilit altında yeniden dolu
    /// bulundu. Akış istenmedi; sonraki tur açılışı 1. adımdan yeniden dener.</summary>
    Opening,
    /// <summary><c>sales/state</c> başarısız (ağ, sunucu, kimlik), sayfa/ağ hatası (429 dahil), uygulanamayan öğe
    /// (atlanana kadar), yerel ortam hatası ya da temizlik yazılamadı. İmleç son uygulanan öğede.</summary>
    Failed,
    /// <summary>Gönderilmemiş yerel etiket (U5; yerel kip 0'daki <c>disable</c> turunda dosya da) ve durmadan sonraki
    /// gönderim onu götüremedi. İmleç o öğede; deneme sayılmadı.</summary>
    Stalled,
    /// <summary>Etiketin müşterisi yerelde yok, kimliği başka Id'de: yönlendirmeyi müşteri akışı getirecek. İmleç o
    /// öğede. Deneme yalnız müşteri akışı öğeden sonra yetiştikçe sayılır; sınırda öğe atlanır (projeksiyonu olmayan
    /// Id'ye yönlendirme hiç gelmez).</summary>
    PendingCustomer,
    /// <summary>Etiketin müşterisi ödeme akışında (U13) ya da lisans değişimi temizliği müşterisi meşgul olduğu için
    /// bekliyor. İmleç o öğede; deneme sayılmadı.</summary>
    Busy,
    /// <summary>Tur başına sayfa sınırına ulaşıldı (<see cref="SalesChangesPullService.MaxPagesPerTour"/>); akış sonraki
    /// turda kaldığı yerden sürer.</summary>
    MorePending,
    /// <summary>Akış boş sayfaya kadar uygulandı.</summary>
    CaughtUp,
    /// <summary><c>disable</c> turu (§8.5; W2-11) bu turda tamamlandı: son akış uygulandı, yerel kip 0, uzak satırlar
    /// silindi, bekleyen komutlar yerel yazıma çevrildi (biri düştüyse kuyrukta kalır, sonraki turda yeniden).</summary>
    Disabled,
}

/// <summary>Akıştan inen ve arayüzün tazeleyeceği satırlar (<see cref="SalesChangesPullService.SalesChanged"/>; §5.4-6).
/// Lisans değişimi temizliğinde listeler boş olabilir: uzak satırlar silindi, ekranlar baştan okunmalı.</summary>
public sealed class SalesChangedEventArgs(
    IReadOnlyList<string> labelIds, IReadOnlyList<string> sessionIds, IReadOnlyList<string> shipmentIds) : EventArgs
{
    public IReadOnlyList<string> LabelIds { get; } = labelIds;
    public IReadOnlyList<string> SessionIds { get; } = sessionIds;
    public IReadOnlyList<string> ShipmentIds { get; } = shipmentIds;
}

/// <summary>Satış akışının durum satırı girdisi (§5.7 "Akış"); yalnız mod On'da gösterilir.</summary>
/// <param name="ModeOn">Yerel kip On ve lisans var: satır görünür.</param>
/// <param name="LastCaughtUpAt">Bu süreçte akışın boş sayfaya kadar uygulandığı son an.</param>
/// <param name="CatchingUp">Son tur sayfa sınırında kaldı ya da hiç yetişilmedi.</param>
/// <param name="WaitingItems">Akış bir öğede bekliyor (U5, bekleyen müşteri, ödeme akışı): o sayfada bekleyen öğe
/// sayısı (en az; arkasındaki sayfalar sayılmaz).</param>
public sealed record SalesFeedStatus(bool ModeOn, DateTimeOffset? LastCaughtUpAt, bool CatchingUp, int WaitingItems)
{
    public static SalesFeedStatus Off { get; } = new(false, null, false, 0);

    /// <summary>Mod On açılışı bekliyor (§8.4-1): sunucu modu On, yerel kip bir giden kuyruk boşalmadığı için hâlâ Off.
    /// Satır yerel kip Off'ken de görünür: "Senkron açılıyor; bekleyen gönderim var".</summary>
    public static SalesFeedStatus Opening { get; } = new(false, null, false, 0) { OpeningPending = true };

    /// <summary>Sunucu modu On, yerel kip açılamadı (bkz. <see cref="Opening"/>).</summary>
    public bool OpeningPending { get; init; }
}

/// <summary>Mod On'un ilk yetişmesi ufukta tamamlandı (<see cref="SalesChangesPullService.FirstCatchUpCompleted"/>; §8.4-4).
/// Açılış başına bir kez.</summary>
/// <param name="CatchUpOverwrites">Bu bilgisayarın kendi etiketinde sunucunun değeriyle ezilen birim sayısı (§5.4);
/// 0'dan büyükse kabuk bir kez uyarır.</param>
/// <param name="AggregateRows">Toplamı yeniden türetilen müşteri satırı.</param>
public sealed record SalesFirstCatchUp(int CatchUpOverwrites, int AggregateRows);

/// <summary>
/// Satış değişiklik akışının turu (spec 2026-10-13 §5.4 Servis, §4.4, §4.5, §3.2/M5, §5.7, §12 risk 9). Diğer
/// bilgisayarların DEVREDİLMİŞ yayınlarını ve etiketlerini, komutların sonuçlarını ve kargo dosyalarını yerele indirir
/// (<see cref="SalesFeedRepository.ApplyItem"/>). Tek 30 sn'lik sürücüdür: <c>sales/state</c> kalp atışı da bu turun
/// ilk adımıdır (<see cref="SalesChangesPullHostedService"/>; ayrı bir state zamanlayıcısı yok).
///
/// <para><b>Tur</b> (kendi tur kilidi altında):
/// <list type="number">
///   <item><c>sales/state</c> — turda TAM BİR kez (<see cref="SalesSyncStateService.TourOnceAsync"/>), her kipte:
///   kalp atışı kesilirse başka bilgisayar bu bilgisayarın canlı yayınını sahibi susmuş sanıp bitirebilirdi (Y7).
///   Lisans değişimi temizliği (yerel). Başarılı state'in hemen ardından kip adımı (W2-10,
///   <see cref="SalesModeCoordinator"/>, §8.4): sunucu modu 1 ve yerel kip 0 ise açılış — giden kuyruklar görülen kip 0
///   ile boşaltılır, kilit altında <c>Mode = 1</c> yazılır, imleç 0; açılamazsa tur <see cref="SalesPullOutcome.Opening"/>
///   ile biter. Yerel <c>SalesSyncState.Mode</c> 1 değilse tur burada biter: mod Off'ta
///   (bugün her lisans) akış istenmez ve hiçbir şey yazılmaz. Mod On'da <c>liveSessions</c>
///   <c>RemoteLiveSession</c>'a aynalanır (<see cref="RemoteLiveSessionRepository.Mirror"/>; kendi oturumunun Id'si
///   alınmaz, <c>StreamSession</c>'a hiç yazılmaz).</item>
///   <item>Ön gönderim: müşteri (<see cref="WpfCustomerProjectionSyncService"/>), ardından oturum ve etiket
///   (<see cref="SessionOrderSyncService.SyncOnceAsync(CancellationToken)"/>). Gönderilmemiş yerel etiket akışı
///   durdururdu (U5); önce gitsin. Ardından bekleyen satış komutları (§5.5, <see cref="PendingSalesCommandSender"/>):
///   çevrimdışıyken kuyruğa giren kalıntı iptali ve ilk baskısı, akış isteğinden ÖNCE. Sonra devri bekleyen kendi
///   yayınlarının kuyruğu ölçülür (W2-09, <see cref="HandoverStallTracker"/>; yalnız sunucuya ulaşan turda):
///   <see cref="StuckHandovers"/>.</item>
///   <item>Sayfalar <see cref="PageSize"/>'lık, turda en çok <see cref="MaxPagesPerTour"/> (müşteri akışıyla paylaşılan
///   100 istek/dk/IP bütçesi). Her sayfa müşteri akışının tur kilidi altında uygulanır
///   (<see cref="CustomerChangesPullService.EnterTourGateAsync"/>); kilit HİÇBİR HTTP isteği boyunca tutulmaz. Kilit
///   sırası: önce bu servisin kilidi, sonra müşterininki.</item>
///   <item>İmleç <c>SyncCursor(</c><see cref="CursorName"/><c>, LicenseKey)</c> her sayfadan sonra ve iptalde
///   kaydedilir. <c>cursorReset</c> imleci 0'a çeker, akış baştan uygulanır (idempotent).</item>
///   <item>Değişen satırlar <see cref="SalesChanged"/> ile bildirilir (arka plan iş parçacığında; dinleyici
///   dispatcher'a taşır).</item>
///   <item>Akış ufka ulaştıysa (boş sayfa, bekleyen öğe yok) ve ilk yetişme sürüyorsa: toplamlar türetilir,
///   <c>FirstCatchUpAt</c> yazılır (<see cref="SalesModeCoordinator.CompleteFirstCatchUp"/>, §8.4-4) ve
///   <see cref="FirstCatchUpCompleted"/> bir kez yayılır.</item>
/// </list></para>
///
/// <para><b><c>disable</c> turu</b> (W2-11, §8.5): turun başında, state'ten önce, yarıda kalmış 3. adım yeniden koşar
/// (<see cref="SalesModeCoordinator.ResumeModeOff"/>; yerel kip 0 ∧ bekleyen komut). Kip adımı
/// <see cref="SalesModeStep.Disabling"/> derse tur bekleyen komutları GÖNDERMEZ (komut ucu 409 <c>mode-off</c>), canlı
/// yayın aynasını yazmaz; ön gönderimden (müşteri, oturum ve etiket, kargo — yerel kip 0'da dekont dosyayı yerelde
/// yazmayı sürdürür) sonra son akışı kendi satırları ve dosyalarla boş sayfaya kadar uygular, sonra
/// 2. ve 3. adım (<see cref="SalesModeCoordinator.CompleteModeOff"/>). Tamamlanınca durum Off, ekranlar baştan okunur
/// (<see cref="SalesChanged"/>) ve tur <see cref="SalesPullOutcome.Disabled"/> döner. Komut ucunun <c>mode-off</c>'u
/// (<see cref="SalesCommandClient.ModeOffDetected"/>) bir turu hemen başlatır (<see cref="RequestModeOffRound"/>):
/// <see cref="ModeOffStarted"/> şeridi açar, isteği gören tur sonuçlanınca <see cref="ModeOffFinished"/> kapatır.</para>
///
/// <para><b>Bekleyen öğe:</b> U5 (gönderilmemiş yerel etiket; yerel kip 0'daki <c>disable</c> turunda gönderilmemiş
/// yerel dosya da) durdurur: etikette oturum ve etiket, dosyada kargo bir kez daha gönderilir; duran satır onaylandıysa
/// (gönderim filigranı ilerledi) akış aynı turda bir kez daha denenir, değilse tur biter.
/// Bekleyen müşteri ve ödeme akışındaki müşteri o öğede durur. U5 ve ödeme akışı deneme sayılmaz, öğe kendiliğinden
/// atlanmaz. Bekleyen müşteri, müşteri akışı öğeden sonra takılmadan yetiştiği her turda bir deneme sayılır ve
/// <see cref="MaxAttemptsBeforeSkip"/>'te atlanır (<see cref="PendingCustomerExhausted"/>): projeksiyonu olmayan Id'ye
/// yönlendirme hiç gelmez, tek öğe bütün akışı tutmamalı.</para>
///
/// <para><b>Uygulanamayan öğe (U10):</b> <see cref="SalesFeedFailureRepository"/>'de kalıcı sayılır;
/// <see cref="MaxAttemptsBeforeSkip"/> turdan sonra atlanır (durum satırı "M öğe atlandı", dikkat sayacı). Ortam
/// hataları sayılmaz: SQLITE_BUSY/LOCKED ve öbür yerel ortam hataları, sızmış kilit satırı (müşteri akışının tur
/// başı temizliği kaldırır), kilit yeniden girişi, ağ hatası, 429.</para>
///
/// <para><b>Lisans değişimi:</b> süreç içinde lisans anahtarı değişince ya da başka bir lisansa ait akış imleci bulunca
/// (süreçler arası) <c>Origin = 'remote'</c> oturum ve etiketler ile <c>RemoteLiveSession</c> silinir, etkilenen
/// müşterilerin toplamları türetilir (<see cref="SalesFeedRepository.PurgeRemoteRows"/>), akış hata kayıtları silinir,
/// geçerli lisansın akışı baştan alınır ve öbür lisansların imleçleri silinir. Yerel satırlara dokunulmaz. Temizlik
/// bitmeden yeni lisansın akışı uygulanmaz (müşterisi ödeme akışındaysa sonraki tura kalır).</para>
///
/// <para><b>Canlı yol beklemez:</b> her <c>await</c> <c>ConfigureAwait(false)</c>; öğe uygulaması
/// <c>CustomerBusySet</c> kilidini eşzamanlı bekler, arayüz iş parçacığında koşmamalı. Hiçbir SQLite bağlantısı ağ
/// isteğini beklemez. Günlük yalnız kimlik ve sayı taşır.</para>
/// </summary>
public sealed class SalesChangesPullService
{
    public const string CursorName = "sales-changes-in";

    /// <summary>Sayfa boyu (sunucunun <c>SalesChangesFeed.MaxTake</c>'i).</summary>
    internal const int PageSize = 500;

    /// <summary>§12 risk 9: tur başına en çok sayfa (5.000 öğe); kalanı sonraki tur.</summary>
    internal const int MaxPagesPerTour = 10;

    /// <summary>U10: bu kadar başarısız turdan sonra öğe atlanır (30 sn ritimde ~2,5 dk).</summary>
    internal const int MaxAttemptsBeforeSkip = 5;

    /// <summary>Mod On'un olağan turu: dosyalar uygulanır, ilk yetişmede kendi satırının üzerine yazım izlenir (§8.4;
    /// iz yalnız <c>FirstCatchUpAt</c> boşken düşer).</summary>
    private static readonly SalesFeedApplyOptions ModeOnOptions = new(ApplyShipments: true, CountCatchUpOverwrite: true);

    /// <summary><c>disable</c> turunun son akışı (§8.5-1): dosyalar uygulanır, yalnız kendi satırları yazılır (uzak
    /// satırları 2. adım siler; mod On dönemini görmemiş kopyada uzak etiket hiç inmez, §6.5-1), ilk yetişme izi yok.</summary>
    private static readonly SalesFeedApplyOptions ModeOffRoundOptions =
        new(ApplyShipments: true, CountCatchUpOverwrite: false, OwnRowsOnly: true);

    private readonly LicenseApiClient _api;
    private readonly SalesSyncStateService _state;
    private readonly SalesSyncStateRepository _stateRepo;
    private readonly SalesSyncEnvelope _envelope;
    private readonly SalesFeedRepository _feed;
    private readonly SalesFeedFailureRepository _failures;
    private readonly RemoteLiveSessionRepository _liveSessions;
    private readonly SyncCursorRepository _cursors;
    private readonly LabelRepository _labels;
    private readonly WpfCustomerProjectionSyncService _customerPush;
    private readonly SessionOrderSyncService _salesPush;
    private readonly CustomerChangesPullService _customerPull;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<SalesChangesPullService> _log;
    private readonly PendingSalesCommandSender? _pendingCommands;
    private readonly SessionRepository? _sessions;
    private readonly SalesModeCoordinator? _modeCoordinator;
    private readonly ShipmentSyncService? _shipmentPush;
    private readonly ShipmentRepository? _shipments;

    /// <summary>"Devredilemedi" kuralı (§5.7, §12 risk 4): yalnız sunucuya ulaşan mod On turlarında gözlenir.</summary>
    private readonly HandoverStallTracker _handoverStall = new();
    private volatile IReadOnlyList<StuckHandover> _stuckHandovers = Array.Empty<StuckHandover>();

    /// <summary>Tek tur kuralı: zamanlayıcının turu ile ileride elle tetiklenen tur (baskı öncesi 3 sn'lik tur, §5.6)
    /// üst üste binmez; imleç yazımları sıralı kalır.</summary>
    private readonly SemaphoreSlim _tourGate = new(1, 1);

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private string? _lastLicenseKey;
    private bool _cleanupPending;
    private int _pagesThisTour;
    private bool _offlineLogged;
    private bool _environmentErrorLogged;
    private volatile SalesFeedStatus _status = SalesFeedStatus.Off;

    /// <summary>Komut ucunun <c>mode-off</c> istekleri (<see cref="RequestModeOffRound"/>) ve sonuçlanmış olanlar: bir tur,
    /// BAŞLADIĞINDA gördüğü istekleri sonuçlandırır (state başarılı ve kip adımı Off turu istemedi, ya da tur bu turda
    /// tamamlandı). Tur sürerken gelen istek sonraki turu bekler.</summary>
    private long _modeOffRequests;
    private long _modeOffResolved;

    /// <summary>Kuyruğa alınmış ama henüz başlamamış <c>mode-off</c> turu (<see cref="QueueModeOffRound"/>): peş peşe istekler
    /// ona katılır. Tur başlarken (tur kilidini beklemeden önce) boşalır; sonra gelen istek yenisini kurar.</summary>
    private Task? _queuedModeOffTour;
    private readonly object _modeOffQueueLock = new();

    /// <summary>Bu tur <c>mode-off</c> isteklerini sonuçlandırdı (tur kilidi altında yazılır).</summary>
    private bool _modeOffSettled;

    private readonly record struct PassResult(
        SalesPullOutcome Outcome, string? BlockedItemId = null, int Waiting = 0, string? BlockedKind = null);

    /// <summary><see cref="SalesFeedFailureRepository"/>'deki bekleyen müşteri denemesinin hata metni (kişisel veri yok).</summary>
    internal const string PendingCustomerError = "PendingCustomer";

    /// <summary>Bekleyen müşteride duran öğe ve o an müşteri akışının yetişme sayısı (tur kilidi altında).</summary>
    private readonly record struct PendingCustomerWatch(SalesFeedItemKey Key, long ChangeSeq, long CustomerRounds);
    private PendingCustomerWatch? _pendingCustomer;

    /// <summary>Bir turun birikimi: değişen kimlikler ve sayılar.</summary>
    private sealed class TourTally
    {
        public readonly HashSet<string> Labels = new(StringComparer.Ordinal);
        public readonly HashSet<string> Sessions = new(StringComparer.Ordinal);
        public readonly HashSet<string> Shipments = new(StringComparer.Ordinal);
        public int Applied, Unchanged, Ignored, Skipped, CatchUpOverwrites, AggregateRows;
        public bool Any => Labels.Count + Sessions.Count + Shipments.Count > 0;
    }

    public SalesChangesPullService(
        LicenseApiClient api,
        SalesSyncStateService state,
        SalesSyncStateRepository stateRepo,
        SalesSyncEnvelope envelope,
        SalesFeedRepository feed,
        SalesFeedFailureRepository failures,
        RemoteLiveSessionRepository liveSessions,
        SyncCursorRepository cursors,
        LabelRepository labels,
        WpfCustomerProjectionSyncService customerPush,
        SessionOrderSyncService salesPush,
        CustomerChangesPullService customerPull,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<SalesChangesPullService> log,
        PendingSalesCommandSender? pendingCommands = null,
        SessionRepository? sessions = null,
        SalesModeCoordinator? modeCoordinator = null,
        SalesCommandClient? commands = null,
        ShipmentSyncService? shipmentPush = null,
        ShipmentRepository? shipments = null)
    {
        _pendingCommands = pendingCommands;
        _sessions = sessions;
        _modeCoordinator = modeCoordinator;
        _shipmentPush = shipmentPush;
        _shipments = shipments;
        if (_pendingCommands is not null)
            _pendingCommands.Settled += (_, e) => PendingCommandsSettled?.Invoke(this, e);
        // W2-11 (§5.3 mode-off): komut ucu modun kapandığını söyledi — state hemen okunur, Off turu başlar.
        if (commands is not null) commands.ModeOffDetected += (_, _) => RequestModeOffRound();
        _api = api;
        _state = state;
        _stateRepo = stateRepo;
        _envelope = envelope;
        _feed = feed;
        _failures = failures;
        _liveSessions = liveSessions;
        _cursors = cursors;
        _labels = labels;
        _customerPush = customerPush;
        _salesPush = salesPush;
        _customerPull = customerPull;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
    }

    /// <summary>Akıştan inen satırlar (§5.4-6). Turun iş parçacığında çağrılır; dinleyici arayüzü dispatcher'a taşımalı.
    /// Dinleyicinin hatası turu düşürmez.</summary>
    public event EventHandler<SalesChangedEventArgs>? SalesChanged;

    /// <summary>Durum satırının girdisi (§5.7); mod Off'ta <see cref="SalesFeedStatus.ModeOn"/> false.</summary>
    public SalesFeedStatus Status => _status;

    /// <summary>Devredilemeyen kendi yayınları (§5.7 "Yayın X devredilemedi: N etiket gönderilemiyor"; kural
    /// <see cref="HandoverStallTracker"/>). Durum satırı ve dikkat sayacı okur; mod Off'ta boş.</summary>
    public IReadOnlyList<StuckHandover> StuckHandovers => _stuckHandovers;

    /// <summary>Bekleyen komutların sonucu (<see cref="PendingSalesCommandSender.Settled"/>): "kâğıtları imha et" ve
    /// kalıcı ret uyarısı (§5.5). Gönderenin iş parçacığında; dinleyici arayüzü dispatcher'a taşımalı.</summary>
    public event EventHandler<PendingSalesCommandsSettled>? PendingCommandsSettled;

    /// <summary>Mod On'un ilk yetişmesi tamamlandı (§8.4-4): açılış başına bir kez, turun iş parçacığında. Kabuk üstüne
    /// yazım sayısı 0'dan büyükse bir kez uyarır (§5.4); dinleyici arayüzü dispatcher'a taşımalı. Dinleyicinin hatası
    /// turu düşürmez.</summary>
    public event EventHandler<SalesFirstCatchUp>? FirstCatchUpCompleted;

    /// <summary>
    /// Yerel kip On ve ilk yetişme bitmedi (<c>FirstCatchUpAt</c> boş): yayın başlatmanın Faz 0 sorusu bunu da bekler
    /// (§5.7). Kip ya da durum okunamazsa <c>false</c> (soru sorulmaz; yayın internetsiz de başlayabilmeli).
    /// </summary>
    public bool IsFirstCatchUpPending()
    {
        try
        {
            var state = _stateRepo.Get();
            return state.Mode == 1 && state.FirstCatchUpAt is null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Satış senkronunun ilk yetişme durumu okunamadı");
            return false;
        }
    }

    /// <summary>
    /// Bekleyen satış komutlarını hemen gönderir (spec 2026-10-13 §5.5; ödeme isteğinin ön gönderiminde İLK sıra, §6.4-1
    /// — W3 bağlar). Turun gönderim adımıyla aynı kapıdan; yerel kip On değilse hiçbir şey göndermez. Yanıttan inen
    /// satırlar <see cref="SalesChanged"/> ile bildirilir. Kuyrukta kalan (gönderilemeyen) komutların örttüğü etiketler
    /// sonrasında <see cref="PendingSalesCommandRepository.GetCoveredLabelIds"/> ile sorulur.
    /// </summary>
    public async Task<PendingSalesCommandFlush> FlushPendingSalesCommandsAsync(CancellationToken ct)
    {
        if (_pendingCommands is null) return PendingSalesCommandFlush.Empty;
        var flush = await _pendingCommands.FlushAsync(ct).ConfigureAwait(false);
        if (flush.Settled.ChangedLabelIds.Count > 0)
            RaiseSalesChanged(flush.Settled.ChangedLabelIds, Array.Empty<string>(), Array.Empty<string>());
        return flush;
    }

    /// <summary>
    /// Sınırlı akış turu: baskıdan hemen önce (spec 2026-10-13 §5.6-2) ve ödeme isteği kirasının <c>stale-total</c>
    /// yeniden denemesinde (§6.4, W3-08). Bir tur, en çok <paramref name="budget"/> kadar beklenir. Süre dolarsa beklemeden
    /// döner (tur iptal edilir ve arka planda kendiliğinden biter; imleç her sayfada kaydedildiği için uygulanmış öğe
    /// kaybolmaz) — çağıran yerel aynanın güncel hâliyle devam eder.
    /// </summary>
    /// <returns>Tur süre içinde akışı sonuna kadar uyguladıysa true.</returns>
    public async Task<bool> RefreshAsync(TimeSpan budget, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tour = Task.Run(() => TourOnceAsync(cts.Token), CancellationToken.None);
        // Kaynak tur bitince bırakılır (süre dolsa da tur belirteci kullanmayı sürdürür).
        _ = tour.ContinueWith(t =>
        {
            _ = t.Exception;                                     // gözlenmemiş istisna kalmasın
            cts.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var delay = Task.Delay(budget, CancellationToken.None);
        var finished = await Task.WhenAny(tour, delay).ConfigureAwait(false) == tour;
        if (!finished)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }                  // tur tam bu arada bitti
            _log.LogInformation("Sınırlı satış akışı turu {Budget} sn içinde bitmedi; yerel satırlarla devam ediliyor",
                budget.TotalSeconds);
            ct.ThrowIfCancellationRequested();
            return false;
        }
        try
        {
            var outcome = await tour.ConfigureAwait(false);
            return outcome == SalesPullOutcome.CaughtUp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sınırlı satış akışı turu düştü; yerel satırlarla devam ediliyor");
            return false;
        }
    }

    /// <summary>Bir tur bitti — her kipte ve her sonuçla (iptal ve beklenmeyen hata hariç); tur kilidi bırakıldıktan
    /// sonra, turun iş parçacığında. Uzak yayın çipleri (W2-09, §5.7) aynayı her turda bununla yeniden okur; dinleyici
    /// arayüzü dispatcher'a taşımalı. Dinleyicinin hatası turu düşürmez.</summary>
    public event EventHandler? TourCompleted;

    /// <summary>Komut ucu <c>mode-off</c> döndü (W2-11, §5.3): kabuk "Senkron kapatıldı; ekran yenileniyor." şeridini
    /// açar. Çağıranın iş parçacığında; dinleyici arayüzü dispatcher'a taşımalı.</summary>
    public event EventHandler? ModeOffStarted;

    /// <summary>Açık <c>mode-off</c> isteklerinin hepsi sonuçlandı: Off turu tamamlandı ya da sunucu artık Off demiyor
    /// (şerit kapanır). Tur kilidi bırakıldıktan sonra, turun iş parçacığında.</summary>
    public event EventHandler? ModeOffFinished;

    /// <summary>
    /// Komut ucunun <c>mode-off</c>'u (§5.3, §8.5): istemci modu yanlış biliyor — <c>sales/state</c> hemen okunsun ve Off
    /// turu başlasın. Arka planda bir tur başlatır (süren turun kilidini bekler; peş peşe istekler birleşir) ve
    /// <see cref="ModeOffStarted"/>'ı yükseltir. Beklemez: komutun kendi gönderimi turun içinden de gelebilir.
    /// </summary>
    public void RequestModeOffRound() => _ = QueueModeOffRound();

    /// <summary>
    /// <see cref="RequestModeOffRound"/>'ın beklenebilir biçimi: dönen görev bu isteği gören turdur (kuyrukta bekleyen tur
    /// varsa o). Ödeme isteği kirasının <c>mode-off</c>'u (§6.4, §8.5; W3-08) Off turunu sınırlı süre bekler, sonra isteği
    /// mod Off yolunda yeniden kurar. Görev fırlatmaz (turun hatası günlüğe düşer); süre dolsa da tur iptal edilmez.
    /// </summary>
    public Task QueueModeOffRound()
    {
        Interlocked.Increment(ref _modeOffRequests);
        _log.LogInformation("Satış komutu mode-off aldı: sales/state hemen okunuyor, Off turu başlıyor");
        try { ModeOffStarted?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "Satış akışı: mode-off bildirimi arayüze iletilemedi"); }
        lock (_modeOffQueueLock)
        {
            if (_queuedModeOffTour is { } queued) return queued;
            // Görev başlamadan alana yazılır: tur başlarken alanı boşaltan adım yazımdan sonra koşar.
            var start = new Task<Task>(async () =>
            {
                lock (_modeOffQueueLock) _queuedModeOffTour = null;    // bu turun kilidi beklerken gelen istek yenisini kurar
                try { await TourOnceAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "Satış akışı: mode-off sonrası tur düştü; sonraki turda yeniden"); }
            });
            var tour = start.Unwrap();
            _queuedModeOffTour = tour;
            start.Start(TaskScheduler.Default);
            return tour;
        }
    }

    public async Task<SalesPullOutcome> TourOnceAsync(CancellationToken ct)
    {
        await _tourGate.WaitAsync(ct).ConfigureAwait(false);
        SalesPullOutcome outcome;
        var modeOffFinished = false;
        try
        {
            var requests = Interlocked.Read(ref _modeOffRequests);
            _modeOffSettled = false;
            outcome = await TourCoreAsync(ct).ConfigureAwait(false);
            if (_modeOffSettled && requests > _modeOffResolved)
            {
                _modeOffResolved = requests;
                modeOffFinished = requests == Interlocked.Read(ref _modeOffRequests);
            }
        }
        finally { _tourGate.Release(); }
        if (modeOffFinished)
        {
            try { ModeOffFinished?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _log.LogWarning(ex, "Satış akışı: mode-off sonucu arayüze iletilemedi"); }
        }
        try { TourCompleted?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "Satış akışı: tur bitti bildirimi arayüze iletilemedi"); }
        return outcome;
    }

    private async Task<SalesPullOutcome> TourCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            _status = SalesFeedStatus.Off;
            return SalesPullOutcome.NoLicense;                         // lisanssız: hiçbir istek yok
        }

        // 0) Off turunun yarıda kalmış 3. adımı (W2-11, §8.5; yerel): state'ten önce — tamamlanırsa bu turun state'i
        //    görülen kip 0 taşır.
        ResumeModeOff();

        // 1) Kalp atışı — turda tam bir kez, her kipte.
        var stateOutcome = await _state.TourOnceAsync(ct).ConfigureAwait(false);
        if (stateOutcome == SalesStateTourOutcome.NoLicense)
        {
            _status = SalesFeedStatus.Off;
            return SalesPullOutcome.NoLicense;
        }

        // Lisans değişimi temizliği (yerel): yeni lisansın akışı eski lisansın uzak satırlarının üstüne inmesin.
        var cleanup = CleanUpAfterLicenseChange(licenseKey);

        // Kip adımı (W2-10, §8.4; W2-11, §8.5): başarılı state'in hemen ardından — bekleyen komutlardan ve akıştan önce.
        var modeStep = SalesModeStep.None;
        if (_modeCoordinator is not null && stateOutcome == SalesStateTourOutcome.Ok && cleanup is null)
            modeStep = await _modeCoordinator.RunAsync(licenseKey, ct).ConfigureAwait(false);
        if (modeStep == SalesModeStep.Disabling) return await ModeOffRoundAsync(licenseKey, ct).ConfigureAwait(false);
        // Sunucunun kipi bu turda görüldü ve Off turu gerekmiyor: açık mode-off istekleri sonuçlandı.
        if (stateOutcome == SalesStateTourOutcome.Ok && cleanup is null) _modeOffSettled = true;

        int mode;
        try { mode = _stateRepo.GetMode(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış akışı: yerel kip okunamadı; tur akışsız bitti");
            return SalesPullOutcome.Failed;
        }
        if (mode != 1)
        {
            ResetHandoverStall();
            if (modeStep == SalesModeStep.OpeningPending)
            {
                _status = SalesFeedStatus.Opening;
                return SalesPullOutcome.Opening;
            }
            _status = SalesFeedStatus.Off;
            return stateOutcome == SalesStateTourOutcome.Ok ? SalesPullOutcome.ModeOff : SalesPullOutcome.Failed;
        }

        _status = _status with { ModeOn = true, OpeningPending = false };
        if (stateOutcome != SalesStateTourOutcome.Ok) return SalesPullOutcome.Failed;
        if (cleanup is { } blocked) return blocked;

        MirrorLiveSessions(licenseKey);

        // 2) Ön gönderim: müşteri, ardından oturum ve etiket; sonra bekleyen satış komutları (§5.5) — akıştan ÖNCE:
        //    sonuçları yanıttan yazılır, örtü kalkar; akış aynı satırı sonra yeniden getirse de fark üretmez.
        await TryPushCustomersAsync(ct).ConfigureAwait(false);
        await TryPushSalesAsync(ct).ConfigureAwait(false);
        var tally = new TourTally();
        await TrySendPendingCommandsAsync(tally, ct).ConfigureAwait(false);
        // Tur sunucuya ulaştı (state başarılı) ve ön gönderim yapıldı: devri bekleyen yayınların kuyruğu ölçülür.
        ObserveHandoverStall();

        // 3–5) Akış.
        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
        if (licenseId is null || _envelope.ReplicaId is not { } replicaId)
        {
            if (tally.Any) RaiseSalesChanged(tally.Labels.ToList(), tally.Sessions.ToList(), tally.Shipments.ToList());
            return SalesPullOutcome.Failed;
        }
        var seenMode = _envelope.SeenMode;

        _pagesThisTour = 0;
        var pass = await PullPassAsync(licenseKey, licenseId.Value, replicaId, seenMode, tally, ModeOnOptions, ct)
            .ConfigureAwait(false);
        if (pass.Outcome == SalesPullOutcome.Stalled && await PushForStallAsync(pass, ct).ConfigureAwait(false))
            pass = await PullPassAsync(licenseKey, licenseId.Value, replicaId, seenMode, tally, ModeOnOptions, ct)
                .ConfigureAwait(false);

        LogTally(tally);
        UpdateStatus(pass);
        if (pass.Outcome is SalesPullOutcome.CaughtUp or SalesPullOutcome.MorePending) _environmentErrorLogged = false;

        // 6) Arayüz.
        if (tally.Any) RaiseSalesChanged(tally.Labels.ToList(), tally.Sessions.ToList(), tally.Shipments.ToList());

        // 7) Ufuk: ilk yetişme sürüyorsa tamamlanır (§8.4-4) — toplamlar tek işlemde, FirstCatchUpAt.
        if (pass.Outcome == SalesPullOutcome.CaughtUp) CompleteFirstCatchUp();
        return pass.Outcome;
    }

    // ── disable turu (W2-11, §8.5) ──────────────────────────────────────────

    /// <summary>
    /// <c>disable</c> turu: ön gönderim (kendi gönderilmemiş satırı son akışı durdururdu, U5), son akış boş sayfaya kadar
    /// dosyalarla ve yalnız kendi satırlarıyla (1. adım), ardından 2. ve 3. adım. Bekleyen komutlar gönderilmez (komut
    /// ucu 409 <c>mode-off</c>), canlı yayın aynası yazılmaz (2. adım siler). Son akış bu turda bitmezse sonraki tur
    /// kaldığı yerden sürer; yerel kip o ana kadar değişmez.
    /// </summary>
    private async Task<SalesPullOutcome> ModeOffRoundAsync(string licenseKey, CancellationToken ct)
    {
        ResetHandoverStall();
        await TryPushCustomersAsync(ct).ConfigureAwait(false);
        await TryPushSalesAsync(ct).ConfigureAwait(false);
        // Yerel kip 0'da (mod On dönemini görmemiş kopya) dekontun kargo adımı yerelde yazmayı sürdürür: gönderilmemiş
        // dosya son akışı durdururdu (U5'in dosya karşılığı). Kip 1'de kargo gönderimi istek atmaz (§7.1).
        await TryPushShipmentsAsync(ct).ConfigureAwait(false);

        var tally = new TourTally();
        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
        if (licenseId is null || _envelope.ReplicaId is not { } replicaId) return SalesPullOutcome.Failed;
        var seenMode = _envelope.SeenMode;

        _pagesThisTour = 0;
        var pass = await PullPassAsync(licenseKey, licenseId.Value, replicaId, seenMode, tally, ModeOffRoundOptions, ct)
            .ConfigureAwait(false);
        if (pass.Outcome == SalesPullOutcome.Stalled && await PushForStallAsync(pass, ct).ConfigureAwait(false))
            pass = await PullPassAsync(licenseKey, licenseId.Value, replicaId, seenMode, tally, ModeOffRoundOptions, ct)
                .ConfigureAwait(false);
        LogTally(tally);

        var done = pass.Outcome == SalesPullOutcome.CaughtUp
            ? _modeCoordinator!.CompleteModeOff(licenseKey, replicaId)
            : null;
        if (done is not { Local.Busy: false })
        {
            if (tally.Any) RaiseSalesChanged(tally.Labels.ToList(), tally.Sessions.ToList(), tally.Shipments.ToList());
            return pass.Outcome != SalesPullOutcome.CaughtUp ? pass.Outcome
                : done is null ? SalesPullOutcome.Failed : SalesPullOutcome.Busy;
        }

        _status = SalesFeedStatus.Off;
        _modeOffSettled = true;
        if (done.Conversion.Any) RaisePendingCommandsSettled(done.Conversion.Settled);
        // Uzak satırlar silindi, kendi yayınları yerel: ekranlar baştan okunur.
        tally.Labels.UnionWith(done.Conversion.Settled.ChangedLabelIds);
        RaiseSalesChanged(tally.Labels.ToList(), tally.Sessions.ToList(), tally.Shipments.ToList());
        return SalesPullOutcome.Disabled;
    }

    /// <summary>Turun başı: yarıda kalmış 3. adım (yerel kip 0 ∧ bekleyen komut). Sonucu arayüze; hatası turu düşürmez.</summary>
    private void ResumeModeOff()
    {
        if (_modeCoordinator?.ResumeModeOff() is not { Any: true } resumed) return;
        RaisePendingCommandsSettled(resumed.Settled);
        RaiseSalesChanged(resumed.Settled.ChangedLabelIds, Array.Empty<string>(), Array.Empty<string>());
    }

    private void RaisePendingCommandsSettled(PendingSalesCommandsSettled settled)
    {
        try { PendingCommandsSettled?.Invoke(this, settled); }
        catch (Exception ex) { _log.LogWarning(ex, "Satış akışı: bekleyen komutların sonucu arayüze iletilemedi"); }
    }

    /// <summary>Akış ufka ulaştı: ilk yetişme sürüyorsa tamamlanır ve bir kez bildirilir. Hatası turu düşürmez.</summary>
    private void CompleteFirstCatchUp()
    {
        if (_modeCoordinator?.CompleteFirstCatchUp() is not { } done) return;
        try
        {
            FirstCatchUpCompleted?.Invoke(this,
                new SalesFirstCatchUp(done.CatchUpOverwrites, done.Aggregates.ChangedRows));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Satış akışı: ilk yetişme bildirimi arayüze iletilemedi");
        }
    }

    // ── akış ───────────────────────────────────────────────────────────────

    private async Task<PassResult> PullPassAsync(
        string licenseKey, Guid licenseId, Guid replicaId, int seenMode, TourTally tally, SalesFeedApplyOptions options,
        CancellationToken ct)
    {
        var after = _cursors.Get(CursorName, licenseKey)?.Seq ?? 0L;
        try
        {
            var failing = _failures.GetIds();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_pagesThisTour >= MaxPagesPerTour) return new(SalesPullOutcome.MorePending);
                var page = await _api.GetSalesChangesAsync(licenseId, after, PageSize, replicaId, seenMode, ct)
                    .ConfigureAwait(false);
                _pagesThisTour++;
                _offlineLogged = false;                                  // sunucuya ulaşıldı

                if (page.CursorReset)
                {
                    // Sunucu imleci geçersiz buldu (sunucu veritabanı yedekten döndü ya da kopyalandı): sayfa zaten
                    // baştan; uygulama idempotent.
                    _log.LogWarning("Satış akışı imleci sunucuda geçersiz (seq {After}); akış baştan uygulanıyor", after);
                    after = 0;
                    _cursors.Upsert(CursorName, licenseKey, seq: 0);
                }

                var items = page.Items!;
                if (items.Count == 0) break;

                var sideSessions = SalesFeedMapper.SideSessions(page);
                var drafts = SalesFeedMapper.Drafts(page);
                var now = _clock.UnixNow();
                using (await _customerPull.EnterTourGateAsync(ct).ConfigureAwait(false))
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var wire = items[i];
                        var key = KeyOf(wire);
                        SalesFeedApplyResult? result = null;
                        try
                        {
                            result = _feed.ApplyItem(SalesFeedMapper.ToCore(wire), sideSessions, drafts, options, now);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException
                                                   && !CustomerChangesPullService.IsNotItemSpecific(ex))
                        {
                            // U10: öğeye özgü hata (sözleşme dışı öğe, bozuk tel, kısıt). Deneme kalıcı sayılır.
                            var attempts = _failures.Record(key.Kind, key.ItemId, wire.ChangeSeq,
                                $"{ex.GetType().Name}: {ex.Message}", now);
                            if (attempts < MaxAttemptsBeforeSkip)
                            {
                                _cursors.Upsert(CursorName, licenseKey, seq: after);
                                if (attempts == 1)
                                    _log.LogWarning(ex,
                                        "Satış akışı öğesi {Kind} {ItemId} (seq {Seq}) uygulanamadı — deneme {Attempts}/{Max}, sonraki turda yeniden",
                                        key.Kind, key.ItemId, wire.ChangeSeq, attempts, MaxAttemptsBeforeSkip);
                                else
                                    _log.LogWarning(
                                        "Satış akışı öğesi {Kind} {ItemId} (seq {Seq}) yine uygulanamadı ({Error}) — deneme {Attempts}/{Max}",
                                        key.Kind, key.ItemId, wire.ChangeSeq, ex.GetType().Name, attempts, MaxAttemptsBeforeSkip);
                                return new(SalesPullOutcome.Failed);
                            }
                            _failures.MarkSkipped(key.Kind, key.ItemId, now);
                            _log.LogError(
                                "Satış akışı öğesi {Kind} {ItemId} (seq {Seq}) {Max} turda uygulanamadı ({Error}) — ATLANDI; durum satırı uyarı gösterir",
                                key.Kind, key.ItemId, wire.ChangeSeq, MaxAttemptsBeforeSkip, ex.GetType().Name);
                            tally.Skipped++;
                        }

                        if (result is { Status: SalesFeedApplyStatus.PendingCustomer }
                            && PendingCustomerExhausted(key, wire.ChangeSeq, now))
                        {
                            tally.Skipped++;
                            after = wire.ChangeSeq;
                            continue;
                        }

                        if (result is { Status: SalesFeedApplyStatus.Stalled or SalesFeedApplyStatus.PendingCustomer
                                or SalesFeedApplyStatus.Busy })
                        {
                            _cursors.Upsert(CursorName, licenseKey, seq: after);
                            _log.LogDebug("Satış akışı öğe {Kind} {ItemId} (seq {Seq}) için bekliyor: {Status}",
                                key.Kind, key.ItemId, wire.ChangeSeq, result.Status);
                            var outcome = result.Status switch
                            {
                                SalesFeedApplyStatus.Stalled => SalesPullOutcome.Stalled,
                                SalesFeedApplyStatus.PendingCustomer => SalesPullOutcome.PendingCustomer,
                                _ => SalesPullOutcome.Busy,
                            };
                            return new(outcome, key.ItemId, items.Count - i, key.Kind);
                        }

                        if (result is not null)
                        {
                            Count(result, tally);
                            if (failing.Contains(key)) _failures.Clear(key.Kind, key.ItemId);
                        }
                        after = wire.ChangeSeq;
                    }
                }
                after = page.NextAfterSeq;
                _cursors.Upsert(CursorName, licenseKey, seq: after);
            }
        }
        catch (OperationCanceledException)
        {
            // Uygulanan öğeler yeniden uygulanmasın diye imleç kaydedilir (yine de zararsız olurdu).
            try { _cursors.Upsert(CursorName, licenseKey, seq: after); }
            catch (Exception saveEx) when (saveEx is not OperationCanceledException)
            {
                _log.LogDebug(saveEx, "İptalde satış akışı imleci kaydedilemedi");
            }
            throw;
        }
        catch (Exception ex)
        {
            try { _cursors.Upsert(CursorName, licenseKey, seq: after); }
            catch (Exception saveEx) when (saveEx is not OperationCanceledException)
            {
                _log.LogDebug(saveEx, "Satış akışı imleci kaydedilemedi");
            }
            LogPassFailure(ex, after);
            return new(SalesPullOutcome.Failed);
        }
        return new(SalesPullOutcome.CaughtUp);
    }

    /// <summary>
    /// Bekleyen müşterinin sınırı (spec 2026-10-13 §5.4 "kalıcı park yok", §4.4 "tek satır bütün akışı tutmamalı"):
    /// öğe, kimliği yerelde başka Id'de duran bir müşteriye ait ve yönlendirmeyi müşteri akışının getirmesi bekleniyor.
    /// Projeksiyonu olmayan Id'ye (sunucuya hiç gönderilmemiş müşteri: GUID'siz eski Id, gönderilmeden silinmiş ya da
    /// sunucunun kabul etmediği satır) müşteri akışı HİÇ yönlendirme getirmez; sınırsız bekleme bütün satış akışını
    /// durdururdu. Öğenin ilk görüldüğü andan sonra müşteri akışı her takılmadan yetiştiğinde
    /// (<see cref="CustomerChangesPullService.CaughtUpRounds"/>) bir deneme sayılır — müşteri akışı gerideyken hiç
    /// sayılmaz; <see cref="MaxAttemptsBeforeSkip"/> denemede öğe U10 gibi atlanır (durum satırı "M öğe atlandı",
    /// dikkat sayacı; satırın yeni bir değişikliği gelirse yeniden denenir). Yerel yönlendirme yazılmaz: aynı
    /// (platform, kullanıcı adı) farklı kişiler olabilir (ör. boş kullanıcı adı), parayı yanlış kişiye bağlamaktansa
    /// atlanır.
    /// </summary>
    /// <returns>Öğe şimdi atlandı.</returns>
    private bool PendingCustomerExhausted(SalesFeedItemKey key, long changeSeq, long now)
    {
        var rounds = _customerPull.CaughtUpRounds;
        if (_pendingCustomer is not { } watch || watch.Key != key || watch.ChangeSeq != changeSeq)
        {
            _pendingCustomer = new PendingCustomerWatch(key, changeSeq, rounds);
            return false;
        }
        if (rounds <= watch.CustomerRounds) return false;                  // müşteri akışı o zamandan beri yetişmedi
        _pendingCustomer = watch with { CustomerRounds = rounds };

        var attempts = _failures.Record(key.Kind, key.ItemId, changeSeq, PendingCustomerError, now);
        if (attempts < MaxAttemptsBeforeSkip)
        {
            if (attempts == 1)
                _log.LogWarning(
                    "Satış akışı öğesi {Kind} {ItemId} (seq {Seq}): müşterisinin yönlendirmesi müşteri akışı yetiştiği hâlde gelmedi — deneme {Attempts}/{Max}",
                    key.Kind, key.ItemId, changeSeq, attempts, MaxAttemptsBeforeSkip);
            return false;
        }
        _failures.MarkSkipped(key.Kind, key.ItemId, now);
        _pendingCustomer = null;
        _log.LogError(
            "Satış akışı öğesi {Kind} {ItemId} (seq {Seq}): müşterisi {Max} müşteri akışı turunda çözülmedi (kimlik yerelde başka Id'de, yönlendirme yok) — ATLANDI; durum satırı uyarı gösterir",
            key.Kind, key.ItemId, changeSeq, MaxAttemptsBeforeSkip);
        return true;
    }

    /// <summary>Uygulanamayan öğenin anahtarı: tür ve satırın Id'si; satırsız öğede seq.</summary>
    private static SalesFeedItemKey KeyOf(SalesChangeItem wire)
    {
        var id = wire.Session?.Id ?? wire.Order?.Id ?? wire.Shipment?.Id;
        return new SalesFeedItemKey(wire.Kind ?? "?", id?.ToString("N") ?? $"seq-{wire.ChangeSeq}");
    }

    private static void Count(SalesFeedApplyResult result, TourTally tally)
    {
        switch (result.Status)
        {
            case SalesFeedApplyStatus.Applied: tally.Applied++; break;
            case SalesFeedApplyStatus.Ignored: tally.Ignored++; break;
            default: tally.Unchanged++; break;
        }
        tally.Labels.UnionWith(result.LabelIds);
        tally.Sessions.UnionWith(result.SessionIds);
        tally.Shipments.UnionWith(result.ShipmentIds);
        tally.CatchUpOverwrites += result.CatchUpOverwrites;
        tally.AggregateRows += result.Aggregates.ChangedRows;
    }

    // ── gönderim ───────────────────────────────────────────────────────────

    /// <summary>Müşteri gönderimi. HTTP hatası gönderimin içinde kalır; buraya çıkan yalnız yerel hata — akış yine
    /// denenir (bekleyen müşteri öğesi U5'e düşmez, sayılmaz).</summary>
    private async Task TryPushCustomersAsync(CancellationToken ct)
    {
        try { await _customerPush.SyncOnceAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış akışı ön gönderimi: müşteri gönderimi yerel hatayla düştü; akış sürer");
        }
    }

    /// <summary>Oturum ve etiket gönderimi (gönderim kapısından). Yerel hata turu düşürmez.</summary>
    private async Task TryPushSalesAsync(CancellationToken ct)
    {
        try { await _salesPush.SyncOnceAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış akışı ön gönderimi: oturum/etiket gönderimi yerel hatayla düştü; akış sürer");
        }
    }

    /// <summary>Bekleyen satış komutları (§5.5). Yanıttan inen satırlar turun değişikliklerine katılır. Yerel hata turu
    /// düşürmez; komut kuyrukta kalır, sonraki turda yeniden.</summary>
    private async Task TrySendPendingCommandsAsync(TourTally tally, CancellationToken ct)
    {
        if (_pendingCommands is null) return;
        try
        {
            var flush = await _pendingCommands.FlushAsync(ct).ConfigureAwait(false);
            tally.Labels.UnionWith(flush.Settled.ChangedLabelIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış akışı: bekleyen satış komutları gönderilemedi (yerel hata); akış sürer");
        }
    }

    /// <summary>Kargo gönderimi (<see cref="ShipmentSyncService"/>; yerel kip 1'de istek atmaz). Yerel hata turu
    /// düşürmez.</summary>
    private async Task TryPushShipmentsAsync(CancellationToken ct)
    {
        if (_shipmentPush is null) return;
        try { await _shipmentPush.SyncOnceAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış akışı ön gönderimi: kargo gönderimi yerel hatayla düştü; akış sürer");
        }
    }

    /// <summary>U5: duran satırın kuyruğu bir kez daha gönderilir — etikette oturum ve etiket, dosyada kargo.
    /// Filigran = duran satırın onayı: artık onaylıysa (bu gönderimle ya da arada koşan zamanlayıcı turuyla) akış aynı
    /// turda yeniden denenir; değilse ikinci deneme aynı öğede yine dururdu — istek harcanmaz.</summary>
    private async Task<bool> PushForStallAsync(PassResult pass, CancellationToken ct)
    {
        if (pass.BlockedItemId is not { } id) return false;
        var shipment = pass.BlockedKind == SalesFeedItemKind.Shipment;
        if (shipment) await TryPushShipmentsAsync(ct).ConfigureAwait(false);
        else await TryPushSalesAsync(ct).ConfigureAwait(false);
        try
        {
            return shipment
                ? _shipments?.GetById(id)?.SyncedAt is not null
                : _labels.GetById(id)?.SyncedAt is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Duran satırın onayı okunamadı ({Kind} {ItemId})", pass.BlockedKind, id);
            return false;
        }
    }

    // ── devredilemeyen yayın ────────────────────────────────────────────────

    /// <summary>"Devredilemedi" kuralının bir gözlemi (§5.7, §12 risk 4). Yeni devredilemeyen yayın bir kez günlüğe
    /// düşer (kimlik ve sayı). Yerel hata turu düşürmez; seri olduğu gibi kalır.</summary>
    private void ObserveHandoverStall()
    {
        if (_sessions is null) return;
        try
        {
            var previous = _stuckHandovers;
            var stuck = _handoverStall.Observe(_sessions.GetPendingHandovers());
            foreach (var s in stuck.Where(s => previous.All(p => p.SessionId != s.SessionId)))
                _log.LogWarning(
                    "Oturum {SessionId} devredilemiyor: {Unsent} etiket {Tours} turdur sunucuya gönderilemiyor (durum satırı ve dikkat sayacı)",
                    s.SessionId, s.UnsentLabels, HandoverStallTracker.StuckAfterTours);
            _stuckHandovers = stuck;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Devri bekleyen yayınlar okunamadı; sonraki turda yeniden");
        }
    }

    private void ResetHandoverStall()
    {
        _handoverStall.Reset();
        _stuckHandovers = Array.Empty<StuckHandover>();
    }

    // ── canlı yayın aynası ──────────────────────────────────────────────────

    /// <summary>Son <c>sales/state</c>'in <c>liveSessions</c>'ı (bu lisansın) <c>RemoteLiveSession</c>'a. Hatası turu
    /// düşürmez; sonraki tur yeniden aynalar.</summary>
    private void MirrorLiveSessions(string licenseKey)
    {
        if (_state.LastState is not { } state || state.LicenseKey != licenseKey) return;
        try
        {
            var rows = state.LiveSessions
                .Select(s => new RemoteLiveSessionRow(
                    s.Id.ToString("N"), s.Title, s.StartedAt.ToUnixTimeSeconds(), s.EndedAt?.ToUnixTimeSeconds(),
                    s.OwnerMachineName, s.OwnerLastSeenAt?.ToUnixTimeSeconds(), s.State, s.UnsentLabelCount))
                .ToList();
            var changed = _liveSessions.Mirror(rows);
            if (changed > 0) _log.LogDebug("Uzak canlı yayın aynası: {Changed} satır değişti ({Count} yayın)", changed, rows.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Uzak canlı yayın aynası yazılamadı; sonraki turda yeniden");
        }
    }

    // ── lisans değişimi ─────────────────────────────────────────────────────

    /// <returns>null: temizlik gerekmedi ya da bitti; aksi hâlde akış bu turda uygulanmaz (sonuç).</returns>
    private SalesPullOutcome? CleanUpAfterLicenseChange(string licenseKey)
    {
        try
        {
            var changedInProcess = _lastLicenseKey is not null
                                   && !string.Equals(_lastLicenseKey, licenseKey, StringComparison.Ordinal);
            if (!_cleanupPending && !changedInProcess && !_cursors.ExistsForOtherLicense(CursorName, licenseKey))
            {
                _lastLicenseKey = licenseKey;
                return null;
            }

            var purge = _feed.PurgeRemoteRows();
            if (purge.Busy)
            {
                _cleanupPending = true;
                _log.LogInformation("Lisans değişti: uzak satır temizliği ödeme akışındaki müşteri yüzünden sonraki tura kaldı");
                return SalesPullOutcome.Busy;
            }
            var failures = _failures.ClearAll();
            _cursors.Upsert(CursorName, licenseKey, seq: 0);
            var cursors = _cursors.DeleteForOtherLicenses(CursorName, licenseKey);
            _cleanupPending = false;
            _lastLicenseKey = licenseKey;
            _status = _status with { LastCaughtUpAt = null, CatchingUp = false, WaitingItems = 0 };
            _log.LogInformation(
                "Lisans değişti: {Sessions} uzak oturum, {Labels} uzak etiket, {Live} canlı yayın başlığı ve {Failures} akış hata kaydı silindi; "
              + "{Rows} müşterinin toplamı türetildi; önceki lisansın {Cursors} akış imleci silindi, akış baştan",
                purge.Sessions, purge.Labels, purge.LiveSessions, failures, purge.Aggregates.ChangedRows, cursors);
            if (purge.Sessions + purge.Labels + purge.LiveSessions > 0)
                RaiseSalesChanged(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _cleanupPending = true;
            _log.LogWarning(ex, "Lisans değişimi temizliği yazılamadı; akış sonraki tura kaldı");
            return SalesPullOutcome.Failed;
        }
    }

    // ── durum, olay ve günlük ───────────────────────────────────────────────

    private void UpdateStatus(PassResult pass)
    {
        var previous = _status;
        _status = pass.Outcome switch
        {
            SalesPullOutcome.CaughtUp => new SalesFeedStatus(true, DateTimeOffset.UtcNow, false, 0),
            SalesPullOutcome.MorePending => new SalesFeedStatus(true, previous.LastCaughtUpAt, true, 0),
            SalesPullOutcome.Stalled or SalesPullOutcome.PendingCustomer or SalesPullOutcome.Busy =>
                new SalesFeedStatus(true, previous.LastCaughtUpAt, previous.CatchingUp, pass.Waiting),
            _ => previous with { ModeOn = true },
        };
    }

    private void RaiseSalesChanged(IReadOnlyList<string> labels, IReadOnlyList<string> sessions, IReadOnlyList<string> shipments)
    {
        try
        {
            SalesChanged?.Invoke(this, new SalesChangedEventArgs(labels, sessions, shipments));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Satış akışı: değişiklik bildirimi arayüze iletilemedi");
        }
    }

    private void LogTally(TourTally t)
    {
        if (t.Applied + t.Ignored + t.Skipped + t.CatchUpOverwrites == 0) return;
        _log.LogInformation(
            "Satış akışı: {Applied} öğe uygulandı, {Unchanged} değişmedi, {Ignored} öğe yok sayıldı, {Skipped} uygulanamayıp atlandı; "
          + "{Labels} etiket, {Sessions} oturum, {Shipments} dosya değişti; {Rows} müşterinin toplamı türetildi",
            t.Applied, t.Unchanged, t.Ignored, t.Skipped, t.Labels.Count, t.Sessions.Count, t.Shipments.Count, t.AggregateRows);
        if (t.CatchUpOverwrites > 0)
            _log.LogWarning(
                "Satış akışı ilk yetişmesi: {Count} etikette sunucudaki değer bu bilgisayardakinden farklıydı; sunucunun değeri alındı (SalesConflictLog catchup-overwrite)",
                t.CatchUpOverwrites);
    }

    private void LogPassFailure(Exception ex, long after)
    {
        if (CustomerChangesPullService.IsRateLimited(ex))
            _log.LogWarning("Satış akışı sunucu hız sınırına takıldı (429) — kalan sonraki turda (seq {After})", after);
        else if (ex is LicenseApiNetworkException)
        {
            if (!_offlineLogged)
            {
                _offlineLogged = true;
                _log.LogWarning(ex, "Satış akışı sunucuya ulaşamadı (seq {After}); sonraki turda yeniden", after);
            }
            else
                _log.LogDebug("Satış akışı hâlâ sunucuya ulaşamıyor (seq {After})", after);
        }
        else if (CustomerChangesPullService.IsNotItemSpecific(ex))
        {
            // Ortam hatası (kilit çekişmesi, disk, sızmış kilit satırı): deneme sayılmadı; seride yığın izi bir kez.
            if (!_environmentErrorLogged)
            {
                _environmentErrorLogged = true;
                _log.LogWarning(ex, "Satış akışı yerel ortam hatasına takıldı (seq {After}); öğe deneme sayılmadı, sonraki turda yeniden", after);
            }
            else
                _log.LogWarning("Satış akışı yine yerel ortam hatasına takıldı ({Error}, seq {After})", ex.GetType().Name, after);
        }
        else
            _log.LogWarning(ex, "Satış akışı turu başarısız (seq {After}); sonraki turda yeniden", after);
    }

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey) return _cachedLicenseId;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return null;
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Satış akışı: LicenseId çözülemedi");
            return null;
        }
    }
}
