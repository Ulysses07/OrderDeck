using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sunucunun satış senkronu uçlarının test ikizi (spec 2026-10-13) — master'daki <c>LicensesSalesController</c>
/// ve <c>SalesReplicaService</c>'in aynası. W1-02 kopya uçlarını kurar (<c>me/licenses</c>, <c>sales/hello</c>,
/// <c>sales/state</c>); W1'in sonraki görevleri (sessions/orders sync v2, backfill) ve W2 (changes, commands)
/// bunu genişletir.
///
/// <para><b>Bağımsız kâhin:</b> tel biçimi üretimdeki kayıtların kopyası (<see cref="HelloBody"/>,
/// <see cref="StateBody"/>): istemcinin DTO'larıyla değil JSON'un kendisiyle konuşur — alan adı kayması da
/// yakalanır. Zarf doğrulaması <c>SalesEnvelope.TryRead</c> ve hello'nun alan kurallarıyla aynı; ihlal 400 döner
/// ve <see cref="Faults"/>'a yazılır (istemci geçersiz gövde göndermemeli).</para>
///
/// <para><b>Kopya satırı</b> (<see cref="Replica"/>): her v2 isteği kalp atışıdır (<c>LastSeenAt</c>,
/// <c>SeenMode</c>); hello ayrıca makine adını, yetenek düzeyini ve bildirilen kargoyu günceller,
/// <c>PreviousReplicaId</c>'yi bir kez yazar, parmak izinde ilk yazan kazanır.</para>
///
/// <para><b>Arıza:</b> <see cref="EnqueueStatus"/> bir ucun sıradaki yanıtını hata koduna, <see cref="EnqueueTimeout"/>
/// HttpClient zaman aşımına çevirir; <see cref="HangEndpoint"/> o uca gelen isteği iptal edilene kadar bekletir
/// (<see cref="RequestHanging"/> istek geldiğinde tamamlanır). Kasıtlı arızalar <see cref="Faults"/>'a yazılmaz.</para>
///
/// <para><b>Gönderim uçları</b> (W1-06): <c>sessions/sync</c>, <c>orders/sync</c> ve <c>shipments/sync</c>
/// (<see cref="SessionsSync"/>, <see cref="OrdersSync"/>, <see cref="ShipmentsSync"/>). Zarf ve <c>format</c>
/// doğrulaması sunucudaki <c>SalesEnvelope.TryRead</c> ve <c>TryReadFormat</c>'in aynası; sipariş deposu birim kuralıyla
/// yazar (W2-12'den beri K1–K7, aşağıda; ekleme sürüm 0, her işlenen satırda +1 — <c>Order.SyncVersion</c>).
/// <see cref="OrderVerdicts"/> bir siparişi yok sayılmış ya da reddedilmiş döndürür (W1-08: <c>Current</c> sunucunun
/// gerçeğini — <c>current</c>'ı — kurar, <c>WithoutCurrent</c> reddedilen eklemeyi taklit eder); <see cref="OrdersResponse"/>
/// eski sunucunun yanıtını (v1 listesi ya da <c>[]</c>) taklit eder. <see cref="Hold"/> bir ucu bırakılana kadar
/// bekletir, <see cref="DuringPush"/> istek "işlenirken" çalışır (uçuştaki yerel değişiklik), <see cref="Offline"/>
/// her isteği ağ hatasına çevirir; <see cref="MaxInFlight"/> eşzamanlı istek sayısının tepesini verir.</para>
///
/// <para><b>Oturumlar ve devir</b> (W1-07): sahiplik, <c>Release</c> ve <c>UnsentLabelCount</c> sunucunun kuralıyla
/// (<see cref="Session"/>); <see cref="WithheldReleases"/> geri alma penceresini, <see cref="ForceEnd"/> başka
/// bilgisayarın force-end'ini, <see cref="SetSessionOwner"/> başka kopyanın oturumunu, <see cref="SessionsResponse"/>
/// S1 öncesi sunucuyu taklit eder.</para>
///
/// <para><b>Geri doldurma</b> (W1-09): <c>sales/backfill</c> (<see cref="HandleBackfill"/>) yalnız boşu doldurur,
/// bildirimsiz ekler (<see cref="BackfillInserted"/>), mevcut siparişte birim ayrışmasını <c>diverged</c> ile döner;
/// <see cref="ForeignSessions"/> ve <see cref="ForeignOrders"/> başka lisansın Id'lerini, <see cref="SeedSession"/>,
/// <see cref="SeedOrder"/> ve <see cref="SeedShipment"/> v1 döneminden kalan sunucu satırlarını kurar.</para>
///
/// <para><b>Değişiklik akışı</b> (W2-03): <c>GET sales/changes</c> (<see cref="HandleChanges"/>) depodan BAĞIMSIZ kurulur —
/// <c>SalesChangesFeed</c> ve <c>SalesChangesPageBuilder</c>'ın aynası, istemcinin DTO'larını değil tel biçiminin kendi
/// kopyalarını yazar. Her satır yazımı depo geneli bir rowversion (<c>ChangeSeq</c>) alır; aynı değer yeniden yazılırsa
/// almaz (EF yalnız değişeni yazar). Devir oturuma <c>ReleasedAt</c>, ardından siparişlerine <c>SessionReleasedAt</c>
/// basar (siparişlerin seq'i oturumunkinden büyük); devredilmiş oturuma eklenen sipariş damgayı eklemede alır. Kaynaklar:
/// devredilmiş oturumlar, <c>SessionReleasedAt</c>'i dolu siparişler, dosyalar (iki kipte), lisans başına. Yan liste:
/// sayfadaki siparişlerin oturumları (güncel) ve müşteri taslakları — projeksiyonu bilinen müşteri asıl kayda (en çok üç
/// adım) çözülür, bilinmeyen ya da GUID olmayan müşterinin taslağı siparişin kendi alanlarından, ham Id'yle. KVKK: asıl
/// kaydı silinmiş müşterinin siparişi ve taslağı takma adsız/metinsiz. Ufuk sıradaki seq (<see cref="HorizonOverride"/>);
/// imleç eksi ya da ufkun üstündeyse sayfa baştan ve <c>cursorReset</c>; boş sayfada <c>nextAfterSeq = afterSeq</c>.
/// Kalp atışı state ile aynı. <see cref="OnChanges"/> sayfa kurulmadan önce koşar (arıza enjeksiyonu).</para>
///
/// <para><b>Komutlar</b> (W2-04): <c>POST sales/commands</c> (<see cref="HandleCommand"/>) — zarf, mod Off, UA'dan alt
/// sürüm, CommandId + kanonik içerikle yeniden oynatma (<see cref="CommandLog"/>), content-conflict, beklenen sürüm
/// (<c>stale</c> + <c>current</c>), görünürlük (<c>session-live</c>) ve beş tür sunucunun kuralıyla (damga, sürüm +1).
/// W2-06: <c>set-code-price</c> (planı sunucu kurar; <c>plan-stale</c> + <c>plan</c> + <c>current</c>) ve
/// <c>add-backup</c> (yeni geçici yedek satırı). Siparişler komutun damgalarını (<c>PriceSetAt</c>, <c>CancelSetAt</c>,
/// <c>BackupSetAt</c>) taşır. W2-08: <c>attach-shipment</c> (aday kümesi — kargo bağı bilinen, görünür siparişler —
/// <c>expected</c>'le birebir; en yeni türetilmiş açık dosya ya da önerilen Id'yle yeni dosya; tutar türetilir; havuz
/// yanıt anındaki açık dosyalardan) ve <c>shipment-decision</c> (U12, kargoya verirken türetilmiş dosyada iptalli bağ
/// çözülür, 409 <c>shipment-shipped</c>/<c>stale</c> + <c>currentShipments</c>); <c>ShipmentSetAt</c>,
/// <c>AmountDerivedAt</c> ve <c>CommandedAt</c> damgaları. W2-09: <c>force-end</c> (<see cref="ForceEndSession"/>:
/// yalnız canlı yayın, sessizlik eşiği <see cref="ForceEndSilenceMinutes"/>, <c>handing-over</c>, <c>owner-active</c>,
/// devredilmişte etkisiz başarı); sahibin sessizliği <see cref="SetReplicaLastSeen"/> ile kurulur,
/// <see cref="DeriveLiveSessions"/> state'in canlı yayın listesini sunucunun devredilmemiş oturumlarından türetir.
/// <see cref="LoseNextResponse"/> yanıtı kaybolan komutu taklit eder.</para>
///
/// <para><b>Mod On açılışı</b> (W2-10): açılışın 1. adımı dekont kuyruğunu da boşaltır — <c>payments/sync</c>
/// (<see cref="PaymentsSync"/>; kayıt, <see cref="EnqueueStatus"/>, <see cref="DuringPush"/>, yanıt boş yankı) ve
/// <c>payments/since</c> (boş). <see cref="RequestLog"/> bütün isteklerin sırasını tutar (geri yüklemeden sonra
/// gönderimin state yanıtını beklediğini sınamak için).</para>
///
/// <para><b>Bekleyen ödeme kayıtları</b> (W3-05): <c>payment-requests/backfill</c> ve <c>payment-requests/{id}/finish</c>
/// (<see cref="PaymentRequestsBackfill"/>, <see cref="PaymentRequestsFinish"/>; kayıt, betik, v2 zarfı). Kira ve kayıt
/// kuralları taklit edilmez. W3-07: <c>payment-requests/begin</c> (<see cref="PaymentRequestsBegin"/>; betik yoksa 200
/// <c>sending</c>, mod Off'ta <c>mode-off</c>), ödeme akışının bakiye uçları (<see cref="BalancePreview"/>) ve Cloud API
/// gönderimi (<see cref="WhatsAppSend"/>) — ödeme isteği uçtan uca sürülebilsin.</para>
///
/// <para><b>Birim kuralı ve açılış penceresi</b> (W2-12, iki bilgisayarlı yakınsama testleri için): <c>orders/sync</c>
/// güncelleme dalı §4.2 tablosunu uygular (<see cref="UnitRule"/>: K1 oluşum alanları yazılmaz, K2 damgalı birimi yalnız
/// <c>baseVersion = Version</c> taşıyan v2 yazar, K3 baskı ve <c>cancelled-elsewhere</c>, K4 bağ, K5 mod On'da devredilmiş
/// oturumda ret — zorla bitirilmiş oturumun sahibi ve açılış penceresi istisna, K6 kargolanmış dosyada fiyat reddi;
/// yanıtta <c>ignored</c>/<c>rejected</c> + <c>current</c>); ekleme dalı başka kopyanın canlı oturumuna v2 eklemesini
/// reddeder, bağı K4'e göre yazar, KVKK ayarıyla metni boşaltır. K7: mod On'da kabul edilen fiyat, iptal, yedek ve bağ
/// değişikliği — push ve komutlar (iptal, geri alma, yedek onayı, kod fiyatı) — türetilmiş dosyayı yeniden türetir.
/// Açılış penceresi (<see cref="IsOpeningWindow"/>, <c>SeenModeOnAt</c>) <c>orders/sync</c>'in K4/K5 istisnasını ve mod
/// On'daki <c>shipments/sync</c>'i yönetir: pencerede karar alanları alınır, tutar bağlardan türetilir; pencere dışında
/// hiçbir şey yazılmaz. <see cref="OrderVerdicts"/> verilen siparişte kuralın yerine geçer. Devralma (§8.7, Y8) bu ikizde
/// yok: geri yüklenmiş kopya önceki kopyanın oturumunun sahibi sayılmaz. <see cref="RenumberChangeSeqs"/> kopyalanmış
/// sunucu veritabanını (rowversion'lar yeniden verilir) taklit eder.</para>
/// </summary>
internal sealed class FakeSalesServer : HttpMessageHandler
{
    public const string Hello = "hello";
    public const string State = "state";

    /// <summary><c>GET sales/changes</c>'in uç adı (<see cref="EnqueueStatus"/>, <see cref="HangEndpoint"/>).</summary>
    public const string Changes = "changes";

    /// <summary>Sunucunun <c>SalesChangesFeed.MaxTake</c>'i.</summary>
    private const int MaxChangesTake = 500;
    public const string SessionsSync = "sessions/sync";
    public const string OrdersSync = "orders/sync";
    public const string ShipmentsSync = "shipments/sync";

    /// <summary>Dekont gönderimi (<c>PaymentSyncService</c>): <see cref="Calls"/>, <see cref="EnqueueStatus"/> ve
    /// <see cref="DuringPush"/> için.</summary>
    public const string PaymentsSync = "payments/sync";

    /// <summary>Bekleyen ödeme kayıtlarının uçları (W3-05, <c>PaymentRecordSender</c>): <see cref="Calls"/> ve
    /// <see cref="EnqueueStatus"/> için.</summary>
    public const string PaymentRequestsBackfill = "payment-requests/backfill";
    public const string PaymentRequestsFinish = "payment-requests/finish";

    /// <summary>Ödeme isteği kirası (W3-07, <c>PaymentLeaseCoordinator</c>): <see cref="Calls"/>, <see cref="EnqueueStatus"/>,
    /// <see cref="EnqueueProblem(string, int, string, IReadOnlyDictionary{string, object?})"/> ve
    /// <see cref="EnqueueTimeout"/> için. Kira kuralları taklit edilmez: betik yoksa 200 <c>sending</c>.</summary>
    public const string PaymentRequestsBegin = "payment-requests/begin";

    /// <summary>Ödeme akışının bakiye uçları (W3-07): <c>customer-balance/preview</c>, <c>scope</c>
    /// (<see cref="ScopeDeductions"/>'da yoksa 204) ve <c>apply</c> (<see cref="BalancePreview"/>'dan en çok istenen
    /// kadar).</summary>
    public const string BalancePreviewEndpoint = "customer-balance/preview";
    public const string BalanceScopeEndpoint = "customer-balance/scope";
    public const string BalanceApplyEndpoint = "customer-balance/apply";

    /// <summary>WhatsApp Cloud API gönderimi (W3-07): betik yoksa <c>{ok: true}</c>; <see cref="EnqueueJson"/> başka yanıt
    /// verir.</summary>
    public const string WhatsAppSend = "whatsapp/send";

    /// <summary>Bakiye önizlemesinin döneceği bakiye (her müşteri için aynı); 0 = bakiye yok, apply çağrılmaz.</summary>
    public decimal BalancePreview { get; set; }

    /// <summary>Betiksiz <c>begin</c> 200'ünün üyelerine eklenir (sunucunun asıl kayda çözdüğü, istemcinin bilmediği üye;
    /// W3-08 kapsam uzlaştırması).</summary>
    public List<string> ExtraBeginMembers { get; } = new();

    /// <summary><c>customer-balance/scope</c>'un müşteri başına yanıtı (geri alınmamış düşüm); olmayan müşteride 204.</summary>
    public ConcurrentDictionary<Guid, ScopeDeduction> ScopeDeductions { get; } = new();

    /// <summary><c>/scope</c>'un bulduğu düşüm (<c>LicensesCustomerBalanceApplyController.ScopeResponse</c>).</summary>
    internal sealed record ScopeDeduction(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal,
        DateTimeOffset CreatedAt);

    /// <summary><c>sales/backfill</c>'in uç adı: <see cref="EnqueueStatus"/> ve <see cref="EnqueueProblem"/> için.</summary>
    public const string Backfill = "backfill";

    /// <summary><c>sales/backfill</c>: <see cref="Hold"/>, <see cref="DuringPush"/> ve <see cref="BackfillCalls"/> için.</summary>
    public const string SalesBackfill = "sales/backfill";

    /// <summary><c>POST sales/commands</c>'ın uç adı (<see cref="EnqueueStatus"/>, <see cref="EnqueueProblem(string, int, string)"/>,
    /// <see cref="LoseNextResponse"/>).</summary>
    public const string Commands = "commands";

    /// <summary><c>sales/commands</c>: <see cref="DuringPush"/> ve <see cref="CommandCalls"/> için.</summary>
    public const string SalesCommands = "sales/commands";

    /// <summary>Sunucunun <c>orders/sync</c> parti tavanı ve <c>Orders.PrintBatchId</c> uzunluğu.</summary>
    private const int MaxPushBatch = 200;
    private const int PrintBatchIdMaxLength = 40;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const int MaxNameLength = 128;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _faults = new();
    private readonly Dictionary<(Guid LicenseId, Guid ReplicaId), Replica> _replicas = new();
    private readonly List<HelloCall> _hellos = new();
    private readonly List<StateCall> _states = new();
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _scripted = new();
    private TaskCompletionSource _hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid LicenseId { get; } = Guid.NewGuid();
    public string Lisans { get; } = $"lisans-{Guid.NewGuid():N}";

    /// <summary>Aynı hesabın ikinci lisansı (lisans değişimi senaryoları).</summary>
    public Guid SecondLicenseId { get; } = Guid.NewGuid();
    public string SecondLisans { get; } = $"lisans-{Guid.NewGuid():N}";

    // ── state yanıtı ───────────────────────────────────────────────────────

    public int Mode { get; set; }
    public DateTimeOffset? ModeChangedAt { get; set; }
    public string? MinClientVersion { get; set; }
    public bool ScrubOrderTextOnPurge { get; set; }
    public int ForceEndSilenceMinutes { get; set; } = 15;
    public ShippingBody? Shipping { get; set; }
    public List<LiveSessionBody> LiveSessions { get; } = new();
    public List<LegacyMachineBody> LegacyMachines { get; } = new();

    // ── kayıtlar ───────────────────────────────────────────────────────────

    public IReadOnlyCollection<string> Faults => _faults.ToArray();

    public IReadOnlyList<HelloCall> Hellos { get { lock (_gate) return _hellos.ToList(); } }

    public IReadOnlyList<StateCall> States { get { lock (_gate) return _states.ToList(); } }

    /// <summary><c>sales/changes</c> istekleri (kasıtlı arızalar dahil): lisans ve sorgu.</summary>
    public IReadOnlyList<StateCall> ChangesCalls { get { lock (_gate) return _changesCalls.ToList(); } }
    private readonly List<StateCall> _changesCalls = new();

    /// <summary><c>wpf-customers/sync</c> istekleri (ham gövde).</summary>
    public IReadOnlyList<PushCall> CustomerPushCalls { get { lock (_gate) return _customerPushCalls.ToList(); } }
    private readonly List<PushCall> _customerPushCalls = new();

    /// <summary><c>wpf-customers/changes</c> (müşteri akışı) istek sayısı.</summary>
    public int CustomerChangesCalls { get { lock (_gate) return _customerChangesCalls; } }
    private int _customerChangesCalls;

    /// <summary>Müşteri akışı isteği "işlenirken" çalışır (yanıttan önce): ödeme kirasının <c>members-stale</c> turunda
    /// kiraların bırakıldığını sınamak ve grup senkronunun gelişini taklit etmek için (W3-08).</summary>
    public Action? DuringCustomerChanges { get; set; }

    /// <summary><c>/sales/</c> altına gelen bütün istekler (kasıtlı arızalar dahil).</summary>
    public int SalesRequestCount { get { lock (_gate) return _salesRequests; } }
    private int _salesRequests;

    /// <summary>Gelen her istek, geliş sırasıyla: <c>"{yöntem} {yol}"</c> (ağ hatası taklidi hariç).</summary>
    public IReadOnlyList<string> RequestLog { get { lock (_gate) return _requestLog.ToList(); } }
    private readonly List<string> _requestLog = new();

    public Replica? ReplicaRow(Guid licenseId, Guid replicaId)
    {
        lock (_gate) return _replicas.TryGetValue((licenseId, replicaId), out var r) ? r.Clone() : null;
    }

    /// <summary><c>me/licenses</c> istekleri (lisans çözümü).</summary>
    public int LicenseRequestCount { get { lock (_gate) return _licenseRequests; } }
    private int _licenseRequests;

    /// <summary>Kargonun ters çekmesi (<c>shipments/since</c>) istek sayısı.</summary>
    public int ShipmentsSinceRequestCount { get { lock (_gate) return _shipmentsSinceRequests; } }
    private int _shipmentsSinceRequests;

    /// <summary>Gönderim uçlarına gelen bütün istekler (geçersizler dahil), uç adına göre.</summary>
    public IReadOnlyList<PushCall> PushCalls { get { lock (_gate) return _pushCalls.ToList(); } }
    private readonly List<PushCall> _pushCalls = new();

    public IReadOnlyList<PushCall> Calls(string endpoint) => PushCalls.Where(c => c.Endpoint == endpoint).ToList();

    /// <summary>Bir ucun aynı anda işlenen en çok istek sayısı (gönderim kapısı testleri).</summary>
    public int MaxInFlight(string endpoint)
    {
        lock (_gate) return _maxInFlight.GetValueOrDefault(endpoint);
    }
    private readonly Dictionary<string, int> _inFlight = new();
    private readonly Dictionary<string, int> _maxInFlight = new();

    /// <summary>Sunucudaki sipariş satırı (birim kuralı mod Off, damgasız).</summary>
    public ServerOrder? Order(Guid id)
    {
        lock (_gate) return _orders.TryGetValue(id, out var o) ? o with { } : null;
    }
    private readonly Dictionary<Guid, ServerOrder> _orders = new();

    /// <summary>Sunucudaki kargo dosyası.</summary>
    public ServerShipment? Shipment(Guid id)
    {
        lock (_gate) return _shipments.TryGetValue(id, out var s) ? s with { } : null;
    }
    private readonly Dictionary<Guid, ServerShipment> _shipments = new();

    // ── gönderim uçlarının davranışı ───────────────────────────────────────

    /// <summary>true iken her istek (lisans çözümü dahil) ağ hatası verir: <see cref="HttpRequestException"/>.</summary>
    public bool Offline
    {
        get => Volatile.Read(ref _offline);
        set => Volatile.Write(ref _offline, value);
    }
    private bool _offline;

    /// <summary>Gönderim isteği "işlenirken" — gövde okunduktan sonra, yanıt dönmeden önce — çalışır: uçuştaki yerel
    /// değişikliği taklit eder. Parametre uç adı.</summary>
    public Action<string>? DuringPush { get; set; }

    /// <summary>Siparişe zorlanan karar: <c>ignored</c> ya da <c>rejected</c> (yanıtta <c>current</c> ile). Satır
    /// yazılmaz; yok sayılan mevcut satırın sürümü yine artar (sunucu her işlenen satırda artırır).</summary>
    public ConcurrentDictionary<Guid, OrderVerdict> OrderVerdicts { get; } = new();

    /// <summary><c>orders/sync</c> yanıtının biçimi: <see cref="PushResponseKind.Auto"/> isteğin biçimine göre (format 2
    /// → öğe başına, değilse v1 listesi); diğerleri eski sunucuyu taklit eder.</summary>
    public PushResponseKind OrdersResponse { get; set; }

    /// <summary><c>shipments/sync</c> yanıtının biçimi: Auto = v2 zarfına akış öğesi, v1'e bugünkü liste.</summary>
    public PushResponseKind ShipmentsResponse { get; set; }

    private readonly Dictionary<string, PushHold> _holds = new();

    /// <summary>Bu uca gelen istekler <see cref="PushHold.Release"/>'e kadar bekler (iptal edilirse vazgeçer).</summary>
    public PushHold Hold(string endpoint)
    {
        var hold = new PushHold();
        lock (_gate) _holds[endpoint] = hold;
        return hold;
    }

    // ── arıza ──────────────────────────────────────────────────────────────

    /// <summary>Bu uca gelen istek iptal edilene kadar bekler (yanıt hiç gelmez).</summary>
    public string? HangEndpoint { get; set; }

    /// <summary>Bekletilen uca istek geldiğinde tamamlanır.</summary>
    public Task RequestHanging => _hanging.Task;

    public void EnqueueStatus(string endpoint, int status)
    {
        lock (_gate) Script(endpoint).Enqueue(() =>
            status == 204 ? FakeHttpMessageHandler.Empty(204) : FakeHttpMessageHandler.Problem(status, "fake-fault"));
    }

    /// <summary>Sıradaki yanıt başlıklı problem+json (ör. 400 <c>unknown-session</c>).</summary>
    public void EnqueueProblem(string endpoint, int status, string title)
    {
        lock (_gate) Script(endpoint).Enqueue(() => FakeHttpMessageHandler.Problem(status, title));
    }

    /// <summary>Sıradaki yanıt ek üyeli ProblemDetails (ASP.NET biçimi: ek üyeler köke yazılır, <c>traceId</c> dahil).</summary>
    public void EnqueueProblem(string endpoint, int status, string title, IReadOnlyDictionary<string, object?> extensions)
    {
        lock (_gate) Script(endpoint).Enqueue(() => ProblemResponse(status, title, "fake", extensions));
    }

    /// <summary>Bu uca gelen sıradaki istek NORMAL işlenir (komut uygulanır, günlüğe yazılır), ama yanıt istemciye
    /// varmaz: HttpClient zaman aşımı (<see cref="TaskCanceledException"/>). Yanıtı kaybolan komut.</summary>
    public void LoseNextResponse(string endpoint)
    {
        lock (_gate) _lostResponses.Add(endpoint);
    }
    private readonly HashSet<string> _lostResponses = new();

    /// <summary>HttpClient zaman aşımı: iptal edilmemiş belirteçle <see cref="TaskCanceledException"/>.</summary>
    public void EnqueueTimeout(string endpoint)
    {
        lock (_gate) Script(endpoint).Enqueue(() => throw new TaskCanceledException("fake timeout"));
    }

    /// <summary>Sıradaki yanıt verilen kod ve JSON gövdesi (ör. WhatsApp gönderiminin <c>{ok: false}</c>'u).</summary>
    public void EnqueueJson(string endpoint, int status, string json)
    {
        lock (_gate) Script(endpoint).Enqueue(() => FakeHttpMessageHandler.Json(status, json));
    }

    private Queue<Func<HttpResponseMessage>> Script(string endpoint) =>
        _scripted.TryGetValue(endpoint, out var q) ? q : _scripted[endpoint] = new Queue<Func<HttpResponseMessage>>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (Offline) throw new HttpRequestException("fake offline");
        lock (_gate) _requestLog.Add($"{req.Method} {path}");
        try
        {
            if (path == "/api/v1/me/licenses" && req.Method == HttpMethod.Get)
            {
                lock (_gate) _licenseRequests++;
                return FakeHttpMessageHandler.Json(200,
                    $"[{{\"id\":\"{LicenseId}\",\"licenseKey\":\"{Lisans}\"}},"
                  + $"{{\"id\":\"{SecondLicenseId}\",\"licenseKey\":\"{SecondLisans}\"}}]");
            }

            var push = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/(sessions|orders|shipments)/sync$");
            if (push.Success && req.Method == HttpMethod.Post)
                return await HandlePushAsync(req, Guid.Parse(push.Groups[1].Value), push.Groups[2].Value + "/sync", ct);

            // Dekont gönderimi ve ters çekmesi (W2-10: mod On açılışının 1. adımı dekont kuyruğunu da boşaltır).
            var payments = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/payments/(sync|since)$");
            if (payments.Success)
                return await HandlePaymentsAsync(req, Guid.Parse(payments.Groups[1].Value), payments.Groups[2].Value, ct);

            // Ödeme isteği kirası (W3-07): begin; kira kuralları taklit edilmez.
            var paymentBegin = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/payment-requests/begin$");
            if (paymentBegin.Success && req.Method == HttpMethod.Post)
                return await HandlePaymentBeginAsync(req, Guid.Parse(paymentBegin.Groups[1].Value), ct);

            // Ödeme akışının bakiye uçları ve Cloud API gönderimi (W3-07): ödeme isteği testleri akışı uçtan uca sürer.
            var balance = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/customer-balance/(preview|scope|apply)$");
            if (balance.Success)
                return await HandleBalanceAsync(req, Guid.Parse(balance.Groups[1].Value), balance.Groups[2].Value, ct);
            var whatsapp = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/whatsapp/send$");
            if (whatsapp.Success && req.Method == HttpMethod.Post)
                return await HandleWhatsAppSendAsync(req, Guid.Parse(whatsapp.Groups[1].Value), ct);

            // Bekleyen ödeme kayıtları (W3-05): kirasız kayıtların geri doldurması ve gönderilemeyen finish'ler.
            var paymentRequests = Regex.Match(path,
                @"^/api/v1/licenses/([0-9a-fA-F-]{36})/payment-requests/(backfill|([0-9a-fA-F-]{36})/finish)$");
            if (paymentRequests.Success && req.Method == HttpMethod.Post)
                return await HandlePaymentRequestAsync(req, Guid.Parse(paymentRequests.Groups[1].Value),
                    paymentRequests.Groups[3].Success ? Guid.Parse(paymentRequests.Groups[3].Value) : null, ct);

            // ShipmentSyncService'in ters çekmesi (uygulanmıyor, yalnız imleç): sunucuda başka yazıcı yok.
            if (Regex.IsMatch(path, @"^/api/v1/licenses/[0-9a-fA-F-]{36}/shipments/since$") && req.Method == HttpMethod.Get)
            {
                lock (_gate) _shipmentsSinceRequests++;
                return FakeHttpMessageHandler.Json(200, "[]");
            }

            // Satış akışı turunun ön gönderimi (W2-03): müşteri projeksiyonları. Birleştirme kuralı taklit edilmez
            // (FakeCustomerServer'ın işi); yalnız projeksiyon kaydı — akışın müşteri taslağı onu çözer.
            var customers = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/wpf-customers/sync$");
            if (customers.Success && req.Method == HttpMethod.Post)
                return await HandleCustomerPushAsync(req, Guid.Parse(customers.Groups[1].Value), ct);

            // Müşteri akışı: hep boş sayfa (birleştirme ve yönlendirme FakeCustomerServer'ın işi). Satış akışının
            // "müşteri akışı yetişti" gözlemi için (bekleyen müşterinin sınırı, §5.4).
            if (Regex.IsMatch(path, @"^/api/v1/licenses/[0-9a-fA-F-]{36}/wpf-customers/changes$")
                && req.Method == HttpMethod.Get)
            {
                lock (_gate) _customerChangesCalls++;
                DuringCustomerChanges?.Invoke();
                var afterSeq = Regex.Match(req.RequestUri!.Query, @"afterSeq=(\d+)") is { Success: true } m
                    ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0L;
                return FakeHttpMessageHandler.Json(200,
                    $"{{\"items\":[],\"nextAfterSeq\":{afterSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"cursorReset\":false}}");
            }

            var match = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/sales/([a-z-]+)$");
            if (!match.Success)
            {
                _faults.Enqueue($"beklenmeyen istek: {req.Method} {path}");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var licenseId = Guid.Parse(match.Groups[1].Value);
            var endpoint = match.Groups[2].Value;
            lock (_gate) _salesRequests++;

            if (licenseId != LicenseId && licenseId != SecondLicenseId)
            {
                _faults.Enqueue($"başka lisansa istek: {req.Method} {path}");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (HangEndpoint == endpoint)
            {
                _hanging.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            Func<HttpResponseMessage>? scripted = null;
            lock (_gate)
                if (_scripted.TryGetValue(endpoint, out var q) && q.Count > 0) scripted = q.Dequeue();

            switch (endpoint)
            {
                case Hello when req.Method == HttpMethod.Post:
                {
                    var raw = await req.Content!.ReadAsStringAsync(ct);
                    lock (_gate) _hellos.Add(new HelloCall(licenseId, raw));
                    if (scripted is not null) return scripted();
                    return HandleHello(licenseId, JsonSerializer.Deserialize<HelloBody>(raw, Web));
                }
                case State when req.Method == HttpMethod.Get:
                {
                    var query = req.RequestUri.Query;
                    lock (_gate) _states.Add(new StateCall(licenseId, query));
                    if (scripted is not null) return scripted();
                    return HandleState(licenseId, query);
                }
                case Changes when req.Method == HttpMethod.Get:
                {
                    var query = req.RequestUri.Query;
                    lock (_gate) _changesCalls.Add(new StateCall(licenseId, query));
                    PushHold? hold;
                    lock (_gate) _holds.TryGetValue(Changes, out hold);
                    if (hold is not null) await hold.WaitAsync(ct);
                    if (scripted is not null) return scripted();
                    var page = HandleChanges(licenseId, query);
                    ct.ThrowIfCancellationRequested();             // OnChanges'in iptali: yanıt istemciye varmaz
                    return page;
                }
                case Backfill when req.Method == HttpMethod.Post:
                {
                    var raw = await req.Content!.ReadAsStringAsync(ct);
                    lock (_gate) _backfillCalls.Add(new PushCall(SalesBackfill, licenseId, raw));
                    PushHold? hold;
                    lock (_gate) _holds.TryGetValue(SalesBackfill, out hold);
                    if (hold is not null) await hold.WaitAsync(ct);
                    if (scripted is not null) return scripted();
                    DuringPush?.Invoke(SalesBackfill);
                    return HandleBackfill(licenseId, raw);
                }
                case Commands when req.Method == HttpMethod.Post:
                {
                    var raw = await req.Content!.ReadAsStringAsync(ct);
                    lock (_gate) _commandCalls.Add(new PushCall(SalesCommands, licenseId, raw));
                    if (scripted is not null) return scripted();
                    DuringPush?.Invoke(SalesCommands);
                    var reply = HandleCommand(licenseId, raw, req.Headers.UserAgent.ToString());
                    bool lose;
                    lock (_gate) lose = _lostResponses.Remove(Commands);
                    if (lose) throw new TaskCanceledException("fake timeout after commit");
                    return reply;
                }
            }

            _faults.Enqueue($"beklenmeyen istek: {req.Method} {path}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw;                                                        // kasıtlı zaman aşımı
        }
        catch (Exception ex)
        {
            _faults.Enqueue($"{req.Method} {path}: {ex.GetType().Name}: {ex.Message}");
            return FakeHttpMessageHandler.Problem(500, "fake-server-fault", ex.Message);
        }
    }

    private HttpResponseMessage HandleHello(Guid licenseId, HelloBody? body)
    {
        if (body is null) return Invalid("hello gövdesi yok");
        if (!TryEnvelope(body.ReplicaId, body.Protocol, body.SeenMode, out var replicaId, out var seenMode, out var error)
            || body.Protocol is null)
            return Invalid(error ?? "hello v2 isteğidir");
        if (body.PreviousReplicaId == Guid.Empty || body.PreviousReplicaId == replicaId)
            return Invalid("invalid-previous-replica");
        if (body.MachineName is { Length: > MaxNameLength } || body.HardwareFingerprint is { Length: > MaxNameLength })
            return Invalid("field-too-long");
        if (body.FeatureLevel is not { } featureLevel || featureLevel is < 0 or > 255)
            return Invalid("invalid-feature-level");
        if (body.ReportedShipping is { } s && (s.Threshold < 0 || s.Fee < 0))
            return Invalid("invalid-shipping");

        lock (_gate)
        {
            var row = Touch(licenseId, replicaId, seenMode);
            if (body.MachineName is not null) row.MachineName = body.MachineName;
            if (!string.IsNullOrEmpty(body.HardwareFingerprint))
            {
                if (row.HardwareFingerprint is null) row.HardwareFingerprint = body.HardwareFingerprint;
                else if (row.HardwareFingerprint != body.HardwareFingerprint) row.HardwareFingerprintConflict = true;
            }
            row.FeatureLevel = featureLevel;
            if (row.PreviousReplicaId is null && body.PreviousReplicaId is { } previous) row.PreviousReplicaId = previous;
            if (body.ReportedShipping is { } shipping)
            {
                row.ReportsShipping = true;
                row.ReportedThreshold = shipping.Threshold;
                row.ReportedFee = shipping.Fee;
            }
            if (body.BackfillCompleted == true) row.BackfillCompleted = true;
            if (body.PaymentHistoryCompleted == true) row.PaymentHistoryCompleted = true;
        }
        return FakeHttpMessageHandler.Empty(204);
    }

    private HttpResponseMessage HandleState(Guid licenseId, string query)
    {
        var replicaRaw = QueryValue(query, "replicaId");
        var seenRaw = QueryValue(query, "seenMode");
        if (replicaRaw is not null || seenRaw is not null)
        {
            Guid? replica = Guid.TryParse(replicaRaw, out var r) ? r : null;
            int? seen = int.TryParse(seenRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
            if (!TryEnvelope(replica, 2, seen, out var replicaId, out var seenMode, out var error))
                return Invalid(error!);
            lock (_gate) Touch(licenseId, replicaId, seenMode);
        }

        StateBody state;
        lock (_gate)
        {
            var live = LiveSessions.ToList();
            if (DeriveLiveSessions)
                live.AddRange(_serverSessions.Values
                    .Where(s => s.LicenseId == licenseId && s.ReleasedAt is null)
                    .OrderBy(s => s.StartedAt)
                    .Select(s => new LiveSessionBody(s.Id, s.Title, s.StartedAt, s.EndedAt, s.OwnerMachineName,
                        s.OwnerReplicaId is { } owner && _replicas.TryGetValue((licenseId, owner), out var r)
                            ? r.LastSeenAt
                            : null,
                        s.EndedAt is null ? "live" : "handing-over", s.UnsentLabelCount)));
            state = new StateBody(Mode, ModeChangedAt, MinClientVersion, live, Shipping,
                ScrubOrderTextOnPurge, ForceEndSilenceMinutes, LegacyMachines.ToList());
        }
        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(state, Web));
    }

    // ── gönderim uçları (W1-06) ────────────────────────────────────────────

    private async Task<HttpResponseMessage> HandlePushAsync(
        HttpRequestMessage req, Guid licenseId, string endpoint, CancellationToken ct)
    {
        var raw = await req.Content!.ReadAsStringAsync(ct);
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(endpoint, licenseId, raw));
            var n = _inFlight[endpoint] = _inFlight.GetValueOrDefault(endpoint) + 1;
            if (n > _maxInFlight.GetValueOrDefault(endpoint)) _maxInFlight[endpoint] = n;
        }
        try
        {
            if (licenseId != LicenseId && licenseId != SecondLicenseId)
            {
                _faults.Enqueue($"başka lisansa istek: POST {endpoint}");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            PushHold? hold;
            lock (_gate) _holds.TryGetValue(endpoint, out hold);
            if (hold is not null) await hold.WaitAsync(ct);

            Func<HttpResponseMessage>? scripted = null;
            lock (_gate)
                if (_scripted.TryGetValue(endpoint, out var q) && q.Count > 0) scripted = q.Dequeue();
            if (scripted is not null) return scripted();

            DuringPush?.Invoke(endpoint);

            return endpoint switch
            {
                OrdersSync => HandleOrders(licenseId, raw),
                SessionsSync => HandleSessions(licenseId, raw),
                _ => HandleShipments(licenseId, raw),
            };
        }
        finally
        {
            lock (_gate) _inFlight[endpoint]--;
        }
    }

    /// <summary><c>orders/sync</c> — zarf ve biçim kuralı sunucunun aynası; güncelleme birim kuralıyla
    /// (<see cref="UnitRule"/>; damgasız birimde son gelen kazanır), v1'de v2'ye özgü alanlar "gönderilmedi" sayılır ve
    /// korunur; ekleme dalı §4.2'ye göre; mod On'da K7 türetmesi.</summary>
    private HttpResponseMessage HandleOrders(Guid licenseId, string raw)
    {
        var body = JsonSerializer.Deserialize<OrdersBody>(raw, Web);
        if (body?.Orders is null) return Invalid("orders/sync gövdesi yok");
        if (!TryReadEnvelope(body.ReplicaId, body.Protocol, body.SeenMode, out var envelope, out var error))
            return Invalid(error!);
        bool v2;
        switch (body.Format)
        {
            case null or 1: v2 = false; break;
            case 2 when envelope is not null: v2 = true; break;
            case 2: return Invalid("format 2 v2 zarfı (replicaId, protocol = 2, seenMode) ister.");
            default: return Invalid($"Desteklenmeyen biçim: {body.Format}.");
        }
        if (body.Orders.Count > MaxPushBatch) return Invalid("batch-too-large");
        if (v2 && body.Orders.FirstOrDefault(o => o.PrintBatchId is { Length: > PrintBatchIdMaxLength }) is { } tooLong)
            return Invalid($"invalid-order: printBatchId {tooLong.Id}");

        var now = DateTimeOffset.UtcNow;
        var results = new List<OrderResultBody>();
        var echoed = new List<ServerOrder>();
        lock (_gate)
        {
            var window = envelope is { } w && IsOpeningWindow(licenseId, w.ReplicaId, w.SeenMode);
            if (envelope is { } e) Touch(licenseId, e.ReplicaId, e.SeenMode);
            var affectedShipments = new List<Guid>();
            foreach (var order in body.Orders.GroupBy(o => o.Id).Select(g => g.Last()))
            {
                _orders.TryGetValue(order.Id, out var current);
                if (OrderVerdicts.TryGetValue(order.Id, out var verdict))
                {
                    if (verdict.WithoutCurrent)
                    {
                        results.Add(new OrderResultBody(order.Id, null, null, null,
                            verdict.Status, verdict.Units, verdict.Reason, null));
                        continue;
                    }
                    if (current is null && verdict.Current is not null)
                        current = new ServerOrder(v2 ? order : WithoutV2Fields(order), 0,
                            v2 ? envelope!.ReplicaId : null, now);
                    if (current is not null && verdict.Status == "ignored")
                        current = current with { Version = current.Version + 1, UpdatedAt = now };
                    var item = current?.ToItem();
                    if (item is not null && verdict.Current is { } truth)
                    {
                        item = truth(item);
                        current = current! with { Row = current.Row.WithUnits(item) };
                    }
                    if (current is not null) current = PutOrder(licenseId, current);
                    results.Add(new OrderResultBody(order.Id, current?.Version, current?.Row.ShipmentId, null,
                        verdict.Status, verdict.Units, verdict.Reason, item));
                    if (current is not null) echoed.Add(current);
                    continue;
                }

                if (current is null)
                {
                    // Ekleme dalı (§4.2): v2'de başka kopyanın CANLI oturumuna ekleme satır başına reddedilir (devralma
                    // bu ikizde yok). K4 eklemede: mod Off'ta v2'nin bağı, mod On'da yalnız açılış penceresindeki
                    // kopyanınki; aksi hâlde bağ yazılmaz ve satır yok sayılmış döner (current'ın boş bağı uygulanır).
                    ServerSession? owning = null;
                    if (order.SessionId is { } sid && _serverSessions.TryGetValue(sid, out var s) && s.LicenseId == licenseId)
                        owning = s;
                    if (v2 && owning is { EndedAt: null, OwnerReplicaId: { } owner } && owner != envelope!.ReplicaId)
                    {
                        results.Add(new OrderResultBody(order.Id, null, null, null, "rejected",
                            Array.Empty<string>(), "session-owned-elsewhere", null));
                        continue;
                    }
                    var linkAllowed = Mode != 1 || window;
                    var insertRow = v2
                        ? order with { BaseVersion = null, ShipmentId = linkAllowed ? order.ShipmentId : null }
                        : WithoutV2Fields(order);
                    // KVKK (§9, §4.2): ayar açıkken asıl kaydı silinmiş müşteriye geç gelen ekleme metni geri getirmez.
                    if (ScrubOrderTextOnPurge && ResolveCustomer(order.CustomerId).Canonical?.PurgedAt is not null)
                        insertRow = insertRow with { DisplayName = null, MessageText = "" };
                    var inserted = PutOrder(licenseId,
                        new ServerOrder(insertRow, 0, v2 ? envelope!.ReplicaId : null, now));
                    if (Mode == 1 && inserted.Row.ShipmentId is { } linked) affectedShipments.Add(linked);
                    echoed.Add(inserted);
                    var linkIgnored = v2 && order.ShipmentId is not null && !linkAllowed;
                    results.Add(linkIgnored
                        ? new OrderResultBody(order.Id, inserted.Version, inserted.Row.ShipmentId, inserted.SessionReleasedAt,
                            "ignored", ["shipment"], "shipment-server-owned", CurrentItem(inserted))
                        : new OrderResultBody(order.Id, inserted.Version, inserted.Row.ShipmentId, inserted.SessionReleasedAt,
                            "applied", Array.Empty<string>(), null, null));
                    continue;
                }

                // Güncelleme dalı (§4.2): birim kuralı K1–K6 (UnitRule), K7 aşağıda. Satır işlendiyse sürüm +1.
                ServerSession? session = null;
                if (current.Row.SessionId is { } currentSid && _serverSessions.TryGetValue(currentSid, out var cs)
                    && cs.LicenseId == licenseId)
                    session = cs;
                var context = new UnitContext(
                    ModeOn: Mode == 1,
                    Phase: session is null ? SessionPhase.None
                        : session.ReleasedAt is not null ? SessionPhase.Released
                        : session.EndedAt is not null ? SessionPhase.HandingOver
                        : SessionPhase.Live,
                    CallerOwnsSession: v2 && session?.OwnerReplicaId is { } sessionOwner && sessionOwner == envelope!.ReplicaId,
                    SessionForceEnded: session?.ForceEndedBy is not null,
                    OpeningWindow: window,
                    LinkedShipmentShipped: current.Row.ShipmentId is { } currentLink
                        && _shipments.TryGetValue(currentLink, out var linkedShipment)
                        && linkedShipment.Row.Status == "shipped");
                var (merged, changedUnits, ignored) = UnitRule(current, order, v2, context);
                var row = PutOrder(licenseId, current with { Row = merged, Version = current.Version + 1, UpdatedAt = now });
                if (Mode == 1 && changedUnits.Overlaps(["price", "cancel", "backup", "shipment"]))
                {
                    if (current.Row.ShipmentId is { } before) affectedShipments.Add(before);
                    if (row.Row.ShipmentId is { } after) affectedShipments.Add(after);
                }
                echoed.Add(row);
                if (ignored.Count == 0)
                {
                    results.Add(new OrderResultBody(order.Id, row.Version, row.Row.ShipmentId, row.SessionReleasedAt,
                        "applied", Array.Empty<string>(), null, null));
                    continue;
                }
                var rejection = ignored.FirstOrDefault(i => i.Reason is "session-released" or "shipment-shipped");
                var reason = rejection.Reason
                    ?? (ignored.Any(i => i.Reason == "cancelled-elsewhere") ? "cancelled-elsewhere" : ignored[0].Reason);
                results.Add(new OrderResultBody(order.Id, row.Version, row.Row.ShipmentId, row.SessionReleasedAt,
                    rejection.Reason is null ? "ignored" : "rejected", ignored.Select(i => i.Unit).Distinct().ToList(),
                    reason, CurrentItem(row)));
            }

            // K7 (mod On): kabul edilen fiyat, iptal, yedek ve bağ değişikliği ilgili türetilmiş dosyaları yeniden türetir.
            DeriveAffected(affectedShipments, now);
        }

        var kind = OrdersResponse == PushResponseKind.Auto
            ? (v2 ? PushResponseKind.V2 : PushResponseKind.V1List)
            : OrdersResponse;
        return kind switch
        {
            PushResponseKind.EmptyList => FakeHttpMessageHandler.Json(200, "[]"),
            PushResponseKind.V1List => FakeHttpMessageHandler.Json(200,
                JsonSerializer.Serialize(echoed.Select(o => o.ToV1()).ToList(), Web)),
            _ => FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(results, Web)),
        };
    }

    private static OrderBody WithoutV2Fields(OrderBody o) => o with
    {
        BaseVersion = null, ParentOrderId = null, PrintBatchId = null, PrintSeq = null,
        PriceCorrectedAt = null, PaidPrice = null, ShipmentId = null,
    };

    /// <summary>
    /// <c>sessions/sync</c> (W1-07) — <c>LicensesSessionsSyncController.SyncSessions</c>'ın aynası: EndedAt tekdüze;
    /// v2 eklemesinde çağıran sahip olur; devredilmiş oturumun başlık/platform/notu yok sayılır
    /// (<c>session-released</c>); sahip olmayan v2 kopyasının kapanışı ve <c>Release</c>'i yazılmaz
    /// (<c>session-owned-elsewhere</c>). Devir: bitmiş ∧ devredilmemiş ∧ (v1 ∨ sahipsiz ∨ sahibin
    /// <c>Release = true</c>'su); sahibin <c>Release = false</c>'u <c>UnsentLabelCount</c>'u yazar. Yanıt her
    /// biçimde <c>Protocol = 2</c> taşır (sunucu gibi); <see cref="SessionsResponse"/> eski sunucuyu taklit eder.
    /// </summary>
    private HttpResponseMessage HandleSessions(Guid licenseId, string raw)
    {
        var body = JsonSerializer.Deserialize<SessionsBody>(raw, Web);
        if (body?.Sessions is null) return Invalid("sessions/sync gövdesi yok");
        if (!TryReadEnvelope(body.ReplicaId, body.Protocol, body.SeenMode, out var envelope, out var error))
            return Invalid(error!);
        if (body.Sessions.Count > MaxPushBatch) return Invalid("batch-too-large");
        if (body.Sessions.FirstOrDefault(s => s.MachineName is { Length: > MaxNameLength } || s.UnsentLabelCount < 0)
            is { } invalid)
            return Invalid($"invalid-session: {invalid.Id}");
        if (envelope is null && body.Sessions.Any(s => s.Release || s.MachineName is not null || s.UnsentLabelCount is not null))
            return Invalid("v1 gövdesi v2 öğe alanı taşımamalı");

        var now = DateTimeOffset.UtcNow;
        var results = new List<SessionResultBody>();
        lock (_gate)
        {
            if (envelope is { } e) Touch(licenseId, e.ReplicaId, e.SeenMode);
            foreach (var item in body.Sessions)
            {
                string? reason = null;
                SessionRole role;
                var before = _serverSessions.TryGetValue(item.Id, out var existing) ? existing.Stamp() : null;
                if (_serverSessions.TryGetValue(item.Id, out var current))
                {
                    if (current.ReleasedAt is not null)
                    {
                        if (current.Title != item.Title || current.Platforms != item.Platforms || current.Notes != item.Notes)
                            reason = "session-released";
                        current.EndedAt ??= item.EndedAt;
                        CommitSession(current, before);
                        results.Add(current.ToResult(now, reason));
                        continue;
                    }
                    role = envelope is null ? SessionRole.Legacy
                        : current.OwnerReplicaId is null ? SessionRole.Unowned
                        : current.OwnerReplicaId == envelope.ReplicaId ? SessionRole.Owner
                        : SessionRole.NonOwner;
                    current.Title = item.Title;
                    current.Platforms = item.Platforms;
                    current.Notes = item.Notes;
                    if (role != SessionRole.NonOwner) current.EndedAt ??= item.EndedAt;
                    else if ((current.EndedAt is null && item.EndedAt is not null) || item.Release)
                        reason = "session-owned-elsewhere";
                }
                else
                {
                    current = new ServerSession
                    {
                        Id = item.Id, LicenseId = licenseId, Title = item.Title, StartedAt = item.StartedAt,
                        EndedAt = item.EndedAt, Platforms = item.Platforms, Notes = item.Notes,
                        OwnerReplicaId = envelope?.ReplicaId,
                        OwnerMachineName = envelope is null ? null : item.MachineName,
                    };
                    _serverSessions[item.Id] = current;
                    role = envelope is null ? SessionRole.Legacy : SessionRole.Owner;
                }

                if (current.EndedAt is not null)
                {
                    switch (role)
                    {
                        case SessionRole.Legacy or SessionRole.Unowned:
                            current.ReleasedAt = now;
                            break;
                        case SessionRole.Owner when item.Release:
                            if (!WithheldReleases.ContainsKey(item.Id)) current.ReleasedAt = now;
                            break;
                        case SessionRole.Owner:
                            current.UnsentLabelCount = item.UnsentLabelCount;
                            break;
                    }
                }
                CommitSession(current, before);
                results.Add(current.ToResult(now, reason));
            }
        }

        return SessionsResponse switch
        {
            PushResponseKind.EmptyList => FakeHttpMessageHandler.Json(200, "[]"),
            PushResponseKind.V1List => FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(
                results.Select(r => new V1SessionBody(r.Id, r.Title, r.StartedAt, r.EndedAt, r.Platforms, r.Notes,
                    r.UpdatedAt)).ToList(), Web)),
            _ => FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(results, Web)),
        };
    }
    private readonly Dictionary<Guid, ServerSession> _serverSessions = new();

    private enum SessionRole { Legacy, Unowned, Owner, NonOwner }

    /// <summary>Sunucudaki oturum satırı (anlık kopya).</summary>
    public ServerSessionView? Session(Guid id)
    {
        lock (_gate) return _serverSessions.TryGetValue(id, out var s) ? s.View() : null;
    }

    /// <summary><c>sessions/sync</c> yanıtının biçimi: Auto = sunucunun bugünkü yanıtı (<c>Protocol = 2</c>); V1List
    /// ve EmptyList eski sunucuyu (S1 öncesi) taklit eder.</summary>
    public PushResponseKind SessionsResponse { get; set; }

    /// <summary>Geri alma penceresi (§5.2, M2): bu oturumlara gelen sahibin <c>Release = true</c>'su devretmez —
    /// yanıt <c>Protocol = 2</c> taşır ama <c>ReleasedAt</c> boştur (eski imaj devri yazmadı).</summary>
    public ConcurrentDictionary<Guid, byte> WithheldReleases { get; } = new();

    /// <summary>
    /// Sunucu veritabanı kopyalandı (dışa/içe aktarma, yeni sunucuya taşıma): bütün satışlar ve değerleri (sürümler
    /// dahil) aynen kalır, ama rowversion'lar yeniden verilir — sayaç satır sayısına iner. Birden çok kez yazılmış satır
    /// varsa istemcilerin akış imleci yeni ufkun üstünde kalır: <c>cursorReset</c> (§4.4), akış baştan.
    /// </summary>
    public void RenumberChangeSeqs()
    {
        lock (_gate)
        {
            var rows = _serverSessions.Values.Select(s => (s.ChangeSeq, Kind: 0, Id: s.Id))
                .Concat(_orders.Values.Select(o => (o.ChangeSeq, Kind: 1, Id: o.Row.Id)))
                .Concat(_shipments.Values.Select(s => (s.ChangeSeq, Kind: 2, Id: s.Row.Id)))
                .OrderBy(r => r.ChangeSeq)
                .ToList();
            long next = 0;
            foreach (var (_, kind, id) in rows)
            {
                next++;
                switch (kind)
                {
                    case 0: _serverSessions[id].ChangeSeq = next; break;
                    case 1: _orders[id] = _orders[id] with { ChangeSeq = next }; break;
                    default: _shipments[id] = _shipments[id] with { ChangeSeq = next }; break;
                }
            }
            _seq = next;
        }
    }

    /// <summary>Bütün siparişler (anlık kopya; değişmez denetimleri).</summary>
    public IReadOnlyList<ServerOrder> AllOrders()
    {
        lock (_gate) return _orders.Values.ToList();
    }

    /// <summary>Bütün kargo dosyaları (anlık kopya; değişmez denetimleri).</summary>
    public IReadOnlyList<ServerShipment> AllShipments()
    {
        lock (_gate) return _shipments.Values.ToList();
    }

    /// <summary>Bir yayının sunucudaki siparişleri (anlık kopya).</summary>
    public IReadOnlyList<ServerOrder> OrdersOfSession(Guid sessionId)
    {
        lock (_gate) return _orders.Values.Where(o => o.Row.SessionId == sessionId).ToList();
    }

    /// <summary>Başka bilgisayardan force-end (§4.6): oturum sunucuda bitirilir ve devredilir.</summary>
    public void ForceEnd(Guid id)
    {
        lock (_gate)
        {
            var s = _serverSessions[id];
            var before = s.Stamp();
            var now = DateTimeOffset.UtcNow;
            s.EndedAt ??= now;
            s.ReleasedAt = now;
            s.ForceEndedBy = Guid.NewGuid().ToString();
            CommitSession(s, before);
        }
    }

    /// <summary>Bir kopyanın son kalp atışını (<c>WpfReplicas.LastSeenAt</c>) yazar; satır yoksa açar. Başka bilgisayarın
    /// (hiç istek atmayan) sahip kopyasının sessizliği <c>force-end</c> (§4.6, Y7) ve türetilen canlı yayın listesi
    /// (<see cref="DeriveLiveSessions"/>) bunu okur.</summary>
    public void SetReplicaLastSeen(Guid replicaId, DateTimeOffset lastSeenAt, Guid? licenseId = null)
    {
        lock (_gate)
        {
            var key = (licenseId ?? LicenseId, replicaId);
            if (!_replicas.TryGetValue(key, out var row))
                _replicas[key] = row = new Replica { ReplicaId = replicaId, FirstSeenAt = lastSeenAt };
            row.LastSeenAt = lastSeenAt;
        }
    }

    /// <summary>Sahibin <c>Release = false</c> kapanışının bildirdiği gönderilmemiş etiket sayısı (§4.1): devrediliyor
    /// yayının <c>handing-over</c>'ı ve çipi bunu taşır.</summary>
    public void SetUnsentLabelCount(Guid sessionId, int? count)
    {
        lock (_gate)
        {
            var s = _serverSessions[sessionId];
            var before = s.Stamp();
            s.UnsentLabelCount = count;
            CommitSession(s, before);
        }
    }

    /// <summary>true: <c>sales/state</c>'in <c>liveSessions</c>'ı elle verilen <see cref="LiveSessions"/>'a ek olarak
    /// sunucunun devredilmemiş oturumlarından da kurulur (<c>LicensesSalesController.State</c>'in aynası: bitmemiş =
    /// <c>live</c>, bitmiş = <c>handing-over</c>, son sinyal sahip kopyanın kalp atışı). false (varsayılan): yalnız elle
    /// verilen liste — eski testlerin beklediği.</summary>
    public bool DeriveLiveSessions { get; set; }

    /// <summary>Oturumun sahibini başka bir kopya yapar (§8.7: devralamayan geri yüklenmiş kopya).</summary>
    public void SetSessionOwner(Guid id, Guid ownerReplicaId)
    {
        lock (_gate)
        {
            var s = _serverSessions[id];
            var before = s.Stamp();
            s.OwnerReplicaId = ownerReplicaId;
            CommitSession(s, before);
        }
    }

    /// <summary>Başka bilgisayarın kapanışı (W2-03): oturum biter ve devredilir; siparişleri akışa girer.</summary>
    public void ReleaseSession(Guid id, string? ownerMachineName = null)
    {
        lock (_gate)
        {
            var s = _serverSessions[id];
            var before = s.Stamp();
            var now = DateTimeOffset.UtcNow;
            s.EndedAt ??= now;
            s.ReleasedAt ??= now;
            s.OwnerMachineName ??= ownerMachineName;
            CommitSession(s, before);
        }
    }

    /// <summary>Sunucuda bir siparişin birimlerini değiştirir (başka bilgisayarın komutu gibi): sürüm +1, yeni seq.</summary>
    public void MutateOrder(Guid id, Func<OrderBody, OrderBody> change)
    {
        lock (_gate)
        {
            var o = _orders[id];
            PutOrder(o.LicenseId, o with { Row = change(o.Row), Version = o.Version + 1, UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>
    /// <c>sales-sync disable --apply</c>'ın aynası (§4.10, M19; W2-11): mod 0 ve <c>ModeChangedAt</c>; damgalar kalır.
    /// Aynı işlemde Shipped olmayan her dosyanın <c>OriginReplicaId</c>'si bağlı siparişlerin sahibine göre yeniden
    /// yazılır — sahip oturumun <c>OwnerReplicaId</c>'si, boşsa siparişin kaynağı (geri yükleme zinciri bu ikizde yok).
    /// Bütün bağlı siparişler tek kopyanınsa dosya onun; karışık dosya bağlı tutarı (iptal edilmemiş fiyat) büyük
    /// olana, eşitlikte küçük Id'ye. Bağlı sipariş ya da bilinen sahip yoksa değer değişmez. Değişen dosya yeni seq alır:
    /// her istemci yeni değeri Off turunun son akışında görür (§4.4, §8.5-1).
    /// </summary>
    public void Disable(DateTimeOffset at)
    {
        lock (_gate)
        {
            Mode = 0;
            ModeChangedAt = at;
            foreach (var s in _shipments.Values.Where(s => s.Row.Status != "shipped").ToList())
            {
                var owners = _orders.Values
                    .Where(o => o.Row.ShipmentId == s.Row.Id)
                    .Select(o => (Owner: (o.Row.SessionId is { } sid && _serverSessions.TryGetValue(sid, out var ss)
                                      ? ss.OwnerReplicaId : null) ?? o.OriginReplicaId,
                                  Amount: o.Row.CancelledAt is null ? o.Row.Price : 0m))
                    .Where(x => x.Owner is not null)
                    .GroupBy(x => x.Owner!.Value)
                    .Select(g => (Owner: g.Key, Amount: g.Sum(x => x.Amount)))
                    .OrderByDescending(x => x.Amount)
                    .ThenBy(x => x.Owner.ToString("N"), StringComparer.Ordinal)
                    .ToList();
                if (owners.Count == 0 || s.OriginReplicaId == owners[0].Owner) continue;
                PutShipment(s.LicenseId, s with
                {
                    OriginReplicaId = owners[0].Owner, Version = s.Version + 1, UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
    }

    /// <summary>Sunucudaki bir kargo dosyasını değiştirir (başka bilgisayarın kararı gibi): sürüm +1, yeni seq.</summary>
    public void MutateShipment(Guid id, Func<ShipmentBody, ShipmentBody> change)
    {
        lock (_gate)
        {
            var s = _shipments[id];
            PutShipment(s.LicenseId, s with { Row = change(s.Row), Version = s.Version + 1, UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary><c>shipments/sync</c> — v2 boş <c>OriginReplicaId</c>'yi çağıranla doldurur; Shipped tek yönlü (§7.1).
    /// Mod On'da yalnız açılış penceresindeki kopyanın kararı alınır (<see cref="ShipmentsInOpeningWindow"/>), öbürleri
    /// hiçbir şey yazmaz. Yanıt v2'ye akış öğesi, v1'e bugünkü liste.</summary>
    private HttpResponseMessage HandleShipments(Guid licenseId, string raw)
    {
        var body = JsonSerializer.Deserialize<ShipmentsBody>(raw, Web);
        if (body?.Shipments is null) return Invalid("shipments/sync gövdesi yok");
        if (!TryReadEnvelope(body.ReplicaId, body.Protocol, body.SeenMode, out var envelope, out var error))
            return Invalid(error!);
        if (body.Shipments.Count > MaxPushBatch) return Invalid("batch-too-large");

        var now = DateTimeOffset.UtcNow;
        var rows = new List<ServerShipment>();
        lock (_gate)
        {
            var window = envelope is { } w && IsOpeningWindow(licenseId, w.ReplicaId, w.SeenMode);
            if (envelope is { } e) Touch(licenseId, e.ReplicaId, e.SeenMode);
            if (Mode == 1)
            {
                // §7.1: mod On'da dosya sunucunun — pencere dışında hiçbir şey yazılmaz, yanıt güncel satırlar.
                rows = window
                    ? ShipmentsInOpeningWindow(licenseId, envelope!.ReplicaId, body.Shipments, now)
                    : body.Shipments.Select(s => s.Id).Distinct()
                        .Where(id => _shipments.TryGetValue(id, out var known) && known.LicenseId == licenseId)
                        .Select(id => _shipments[id]).ToList();
                var windowV2 = envelope is not null && ShipmentsResponse != PushResponseKind.V1List;
                return FakeHttpMessageHandler.Json(200, windowV2
                    ? JsonSerializer.Serialize(rows.Select(r => r.ToItem()).ToList(), Web)
                    : JsonSerializer.Serialize(rows.Select(r => r.ToV1()).ToList(), Web));
            }
            foreach (var s in body.Shipments)
            {
                _shipments.TryGetValue(s.Id, out var current);
                var row = current is null
                    ? new ServerShipment(s, 0, envelope?.ReplicaId, now)
                    : current with
                    {
                        Row = current.Row.Status == "shipped" ? current.Row with { CustomerId = s.CustomerId } : s,
                        Version = current.Version + 1,
                        OriginReplicaId = current.OriginReplicaId ?? envelope?.ReplicaId,
                        UpdatedAt = now,
                    };
                rows.Add(PutShipment(licenseId, row));
            }
        }

        var v2 = envelope is not null && ShipmentsResponse != PushResponseKind.V1List;
        return FakeHttpMessageHandler.Json(200, v2
            ? JsonSerializer.Serialize(rows.Select(r => r.ToItem()).ToList(), Web)
            : JsonSerializer.Serialize(rows.Select(r => r.ToV1()).ToList(), Web));
    }

    /// <summary>
    /// Açılış penceresindeki kopyanın <c>shipments/sync</c>'i (<c>SyncOpeningWindowAsync</c>'in aynası; §7.1, §4.0): var
    /// olan dosya Shipped değilse ve <c>CommandedAt</c> boşsa karar alanlarını (Status, HeldAt, ShippedAt) alır; yeni
    /// dosya Pending ve tutarsız eklenir (asıl müşteri, kaynak çağıran, türetilmiş). Tutar hiçbir zaman gönderimden
    /// alınmaz: kargoya geçen türetilmiş dosyada o anda iptal edilmiş bağlar çözülür, sonra dosyalar bağlardan türetilir,
    /// sonra karar yazılır (sürüm yazım başına bir kez). Yanıt istek sırasıyla güncel satırlar.
    /// </summary>
    private List<ServerShipment> ShipmentsInOpeningWindow(
        Guid licenseId, Guid caller, IReadOnlyList<ShipmentBody> items, DateTimeOffset now)
    {
        var decisions = new List<(Guid Id, ShipmentBody Item, bool Inserted)>();
        foreach (var item in items.GroupBy(i => i.Id).Select(g => g.Last()))
        {
            if (_shipments.TryGetValue(item.Id, out var current))
            {
                if (current.LicenseId != licenseId || current.Row.Status == "shipped" || current.CommandedAt is not null)
                    continue;
                if (item.Status == current.Row.Status && item.HeldAt == current.Row.HeldAt
                    && item.ShippedAt == current.Row.ShippedAt)
                    continue;
                decisions.Add((item.Id, item, false));
            }
            else
            {
                PutShipment(licenseId, new ServerShipment(
                    new ShipmentBody(item.Id, ResolveCustomer(item.CustomerId).Id, "pending", 0m, item.CreatedAt, null, null),
                    0, caller, now) { AmountDerivedAt = now });
                decisions.Add((item.Id, item, true));
            }
        }

        foreach (var shipping in decisions.Where(d => d.Item.Status == "shipped" && _shipments[d.Id].AmountDerivedAt is not null))
            foreach (var order in _orders.Values
                         .Where(o => o.LicenseId == licenseId && o.Row.ShipmentId == shipping.Id && o.Row.CancelledAt is not null)
                         .OrderBy(o => o.Row.Id).ToList())
                PutOrder(licenseId, order with
                {
                    Row = order.Row with { ShipmentId = null }, ShipmentSetAt = now, Version = order.Version + 1,
                    UpdatedAt = now,
                });

        var derived = DeriveAffected(decisions.Select(d => d.Id), now).Select(d => d.Row.Id).ToHashSet();
        foreach (var (id, item, inserted) in decisions)
        {
            var s = _shipments[id];
            PutShipment(licenseId, s with
            {
                Row = s.Row with { Status = item.Status, HeldAt = item.HeldAt, ShippedAt = item.ShippedAt },
                Version = !inserted && !derived.Contains(id) ? s.Version + 1 : s.Version,
                UpdatedAt = now,
            });
        }
        return items.Select(i => i.Id).Distinct()
            .Where(id => _shipments.TryGetValue(id, out var row) && row.LicenseId == licenseId)
            .Select(id => _shipments[id]).ToList();
    }

    // ── geri doldurma (W1-09) ──────────────────────────────────────────────

    /// <summary><c>sales/backfill</c> istekleri (geçersizler ve kasıtlı arızalar dahil).</summary>
    public IReadOnlyList<PushCall> BackfillCalls { get { lock (_gate) return _backfillCalls.ToList(); } }
    private readonly List<PushCall> _backfillCalls = new();

    /// <summary>Sunucuda başka bir lisansa ait oturum Id'leri (§4.3 <c>foreign-license</c>).</summary>
    public ConcurrentDictionary<Guid, byte> ForeignSessions { get; } = new();

    /// <summary>Sunucuda başka bir lisansa ait sipariş Id'leri.</summary>
    public ConcurrentDictionary<Guid, byte> ForeignOrders { get; } = new();

    /// <summary>Geri doldurmanın (bildirimsiz) eklediği siparişler.</summary>
    public IReadOnlyCollection<Guid> BackfillInserted { get { lock (_gate) return _backfillInserted.ToList(); } }
    private readonly HashSet<Guid> _backfillInserted = new();

    /// <summary>Sunucuya önceden var olan bir oturum koyar (ör. v1 döneminde gönderilmiş, sahipsiz).</summary>
    /// <param name="licenseId">Satırın lisansı; verilmezse birinci lisans (<see cref="LicenseId"/>).</param>
    public void SeedSession(Guid id, DateTimeOffset startedAt, DateTimeOffset? endedAt, Guid? owner = null,
        DateTimeOffset? releasedAt = null, string? title = null, string platforms = "tiktok",
        string? ownerMachineName = null, Guid? licenseId = null)
    {
        lock (_gate)
        {
            var s = _serverSessions[id] = new ServerSession
            {
                Id = id, LicenseId = licenseId ?? LicenseId, Title = title, StartedAt = startedAt, EndedAt = endedAt,
                Platforms = platforms, OwnerReplicaId = owner, ReleasedAt = releasedAt, OwnerMachineName = ownerMachineName,
            };
            CommitSession(s, before: null);
        }
    }

    /// <summary>Sunucuya önceden var olan bir sipariş koyar.</summary>
    /// <param name="licenseId">Satırın lisansı; verilmezse birinci lisans (<see cref="LicenseId"/>).</param>
    public void SeedOrder(OrderBody row, int version = 0, Guid? origin = null, Guid? licenseId = null)
    {
        lock (_gate)
            PutOrder(licenseId ?? LicenseId,
                new ServerOrder(row with { BaseVersion = null }, version, origin, DateTimeOffset.UtcNow));
    }

    /// <summary>Sunucuya önceden var olan bir kargo dosyası koyar.</summary>
    /// <param name="licenseId">Satırın lisansı; verilmezse birinci lisans (<see cref="LicenseId"/>).</param>
    /// <param name="derived">Tutarı bağlardan türetilen dosya (<c>AmountDerivedAt</c> dolu; mod On'da açılmış ya da
    /// <c>enable --apply</c>'ın türettiği). <c>attach-shipment</c> yalnız böyle bir dosyayı hedef seçer.</param>
    public void SeedShipment(ShipmentBody row, Guid? origin = null, Guid? licenseId = null, bool derived = false)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
            PutShipment(licenseId ?? LicenseId,
                new ServerShipment(row, 0, origin, now) { AmountDerivedAt = derived ? now : null });
    }

    /// <summary>
    /// <c>sales/backfill</c> — <c>LicensesSalesController.Backfill</c> ve <c>OrderBackfillRule</c>'un aynası (mod Off,
    /// damga yok): yalnız v2; listede en çok 200; hiçbir lisansta olmayan oturuma bağlı sipariş partinin tamamını 400
    /// <c>unknown-session</c> ile düşürür (yazımdan önce). Oturum: yoksa eklenir (sahip çağıran), sahipsizse çağıran
    /// sahip olur, sahibin boş <c>EndedAt</c>'i ve makine adı dolar, devir yalnız sahibin <c>Release = true</c>'su ile
    /// (<see cref="WithheldReleases"/> hariç); başka kopyanın oturumuna dokunulmaz. Sipariş: yoksa bildirimsiz eklenir;
    /// varsa yalnız boş yeni alanlar dolar, birimler karşılaştırılır — fark <c>diverged</c> döner, yerel kazanmak için
    /// kopya hello'yla doğmuş, geri yüklenmemiş, klonsuz olmalı ve mod Off; sunucuda basılmış satırın baskı farkı
    /// kilitlidir (K3). Bağ yalnız boşsa yazılır, dosyanın boş kaynağı çağıranla dolar. Başka lisansın Id'si satır
    /// başına <c>foreign-license</c>. Satır değiştiyse sürüm bir kez artar.
    /// </summary>
    private HttpResponseMessage HandleBackfill(Guid licenseId, string raw)
    {
        var body = JsonSerializer.Deserialize<BackfillBody>(raw, Web);
        if (body is null) return Invalid("backfill gövdesi yok");
        if (!TryReadEnvelope(body.ReplicaId, body.Protocol, body.SeenMode, out var envelope, out var error))
            return Invalid(error!);
        if (envelope is null) return Invalid("backfill v2 isteğidir.");
        var sessionItems = body.Sessions ?? new List<SessionBody>();
        var orderItems = body.Orders ?? new List<OrderBody>();
        var links = body.Shipments ?? new List<BackfillLinkBody>();
        if (sessionItems.Count > MaxPushBatch || orderItems.Count > MaxPushBatch || links.Count > MaxPushBatch)
            return Invalid("batch-too-large");
        if (sessionItems.FirstOrDefault(s => s.MachineName is { Length: > MaxNameLength } || s.UnsentLabelCount < 0)
            is { } invalid)
            return Invalid($"invalid-session: {invalid.Id}");
        if (orderItems.FirstOrDefault(o => o.PrintBatchId is { Length: > PrintBatchIdMaxLength }) is { } tooLong)
            return Invalid($"invalid-order: printBatchId {tooLong.Id}");

        var now = DateTimeOffset.UtcNow;
        var caller = envelope.ReplicaId;
        var sessions = sessionItems.GroupBy(s => s.Id).Select(g => g.Last()).ToList();
        var orders = orderItems.GroupBy(o => o.Id).Select(g => g.Last()).ToList();
        var sessionResults = new List<BackfillSessionResultBody>();
        var orderResults = new List<BackfillOrderResultBody>();
        var diverged = new List<BackfillDivergedBody>();
        lock (_gate)
        {
            var before = _replicas.TryGetValue((licenseId, caller), out var row) ? row.Clone() : null;
            var requestSessionIds = sessions.Select(s => s.Id).ToHashSet();
            if (orders.Any(o => o.SessionId is { } sid && !_serverSessions.ContainsKey(sid)
                    && !requestSessionIds.Contains(sid) && !ForeignSessions.ContainsKey(sid)))
                return FakeHttpMessageHandler.Problem(400, "unknown-session", "Pakette bu lisansa ait olmayan SessionId var.");
            Touch(licenseId, caller, envelope.SeenMode);

            foreach (var item in sessions)
            {
                if (ForeignSessions.ContainsKey(item.Id))
                {
                    sessionResults.Add(new BackfillSessionResultBody(item.Id, null, null, null, "foreign-license"));
                    continue;
                }
                var sessionBefore = _serverSessions.TryGetValue(item.Id, out var known) ? known.Stamp() : null;
                if (!_serverSessions.TryGetValue(item.Id, out var session))
                {
                    session = new ServerSession
                    {
                        Id = item.Id, LicenseId = licenseId, Title = item.Title, StartedAt = item.StartedAt,
                        EndedAt = item.EndedAt, Platforms = item.Platforms, Notes = item.Notes, OwnerReplicaId = caller,
                        OwnerMachineName = item.MachineName,
                    };
                    _serverSessions[item.Id] = session;
                }
                else
                {
                    session.OwnerReplicaId ??= caller;
                    if (session.OwnerReplicaId == caller)
                    {
                        session.OwnerMachineName ??= item.MachineName;
                        session.EndedAt ??= item.EndedAt;
                    }
                }
                if (session.OwnerReplicaId == caller && item.Release && session.EndedAt is not null
                    && session.ReleasedAt is null && !WithheldReleases.ContainsKey(item.Id))
                    session.ReleasedAt = now;
                CommitSession(session, sessionBefore);
                sessionResults.Add(new BackfillSessionResultBody(
                    session.Id, session.OwnerReplicaId, session.EndedAt, session.ReleasedAt, null));
            }

            var localWinsAllowed = Mode == 0 && before is { PreviousReplicaId: null, HardwareFingerprintConflict: false };
            var inserted = new HashSet<Guid>();
            var changed = new HashSet<Guid>();
            var statuses = new Dictionary<Guid, (string Status, string? Reason)>();
            foreach (var item in orders)
            {
                var local = item with { BaseVersion = null };
                if (_orders.TryGetValue(item.Id, out var current))
                {
                    var (filled, units, locked) = BackfillRule(current.Row, local);
                    var next = current with { Row = filled };
                    if (filled != current.Row) changed.Add(item.Id);
                    if (next.OriginReplicaId is null)
                    {
                        next = next with { OriginReplicaId = caller };
                        changed.Add(item.Id);
                    }
                    PutOrder(licenseId, next);
                    if (units.Count > 0) diverged.Add(new BackfillDivergedBody(item.Id, units, localWinsAllowed && !locked));
                    statuses[item.Id] = ("unchanged", null);
                    continue;
                }
                if (ForeignOrders.ContainsKey(item.Id) || (item.SessionId is { } fs && ForeignSessions.ContainsKey(fs)))
                {
                    statuses[item.Id] = ("rejected", "foreign-license");
                    continue;
                }
                if (item.SessionId is { } sid && _serverSessions.TryGetValue(sid, out var owning)
                    && owning.EndedAt is null && owning.OwnerReplicaId is { } owner && owner != caller)
                {
                    statuses[item.Id] = ("rejected", "session-owned-elsewhere");
                    continue;
                }
                PutOrder(licenseId, new ServerOrder(local, 0, caller, now));
                inserted.Add(item.Id);
                _backfillInserted.Add(item.Id);
                statuses[item.Id] = ("inserted", null);
            }

            foreach (var link in links)
            {
                foreach (var labelId in (link.LabelIds ?? new List<Guid>()).Distinct())
                {
                    if (!_orders.TryGetValue(labelId, out var order) || statuses.GetValueOrDefault(labelId).Status == "rejected")
                        continue;
                    if (order.Row.ShipmentId == link.Id || order.Row.ShipmentId is not null) continue;
                    PutOrder(licenseId, order with { Row = order.Row with { ShipmentId = link.Id } });
                    if (!inserted.Contains(labelId)) changed.Add(labelId);
                }
                if (_shipments.TryGetValue(link.Id, out var shipment) && shipment.OriginReplicaId is null)
                    PutShipment(licenseId, shipment with { OriginReplicaId = caller, Version = shipment.Version + 1 });
            }

            foreach (var id in changed)
            {
                var order = _orders[id];
                PutOrder(licenseId, order with { Version = order.Version + 1, UpdatedAt = now });
                if (statuses.TryGetValue(id, out var s) && s.Status == "unchanged") statuses[id] = ("filled", null);
            }

            foreach (var item in orders)
            {
                var (status, reason) = statuses[item.Id];
                orderResults.Add(new BackfillOrderResultBody(
                    item.Id, status == "rejected" ? null : _orders[item.Id].Version, status, reason));
            }
        }

        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(
            new BackfillResponseBody(sessionResults, orderResults, diverged), Web));
    }

    /// <summary><c>OrderBackfillRule.Apply</c> (damgasız): boş yeni alanları doldurur, birimleri karşılaştırır.</summary>
    private static (OrderBody Filled, List<string> Units, bool Locked) BackfillRule(OrderBody s, OrderBody local)
    {
        var filled = s with
        {
            ParentOrderId = s.ParentOrderId ?? local.ParentOrderId,
            ShipmentId = s.ShipmentId ?? local.ShipmentId,
        };
        if (s.Price == local.Price)
            filled = filled with
            {
                PriceCorrectedAt = s.PriceCorrectedAt ?? local.PriceCorrectedAt,
                PaidPrice = s.PaidPrice ?? local.PaidPrice,
            };
        if (s.PrintedAt is not null)
            filled = filled with { PrintBatchId = s.PrintBatchId ?? local.PrintBatchId, PrintSeq = s.PrintSeq ?? local.PrintSeq };

        var units = new List<string>();
        if (filled.Price != local.Price || !SameSecond(filled.PriceCorrectedAt, local.PriceCorrectedAt)
            || filled.PaidPrice != local.PaidPrice)
            units.Add("price");
        if (!SameSecond(filled.CancelledAt, local.CancelledAt) || filled.CancelReason != local.CancelReason)
            units.Add("cancel");
        if (filled.IsTentativeBackup != local.IsTentativeBackup) units.Add("backup");
        if (!SameSecond(filled.PrintedAt, local.PrintedAt) || filled.PrintBatchId != local.PrintBatchId
            || filled.PrintSeq != local.PrintSeq)
            units.Add("print");
        var locked = units.Contains("print") && s.PrintedAt is not null;
        return (filled, units, locked);
    }

    private static bool SameSecond(DateTimeOffset? a, DateTimeOffset? b)
        => a is null || b is null ? a is null && b is null : a.Value.ToUnixTimeSeconds() == b.Value.ToUnixTimeSeconds();

    /// <summary>Siparişin oturumunun hâli (§2 tablosu).</summary>
    private enum SessionPhase { None, Live, HandingOver, Released }

    /// <summary>Birim kuralının koşulları (<c>OrderMergeContext</c>'in karşılığı). <paramref name="CallerOwnsSession"/>
    /// yalnız sahiplikten (geri yüklenmiş kopyanın devralması bu ikizde yok).</summary>
    private sealed record UnitContext(
        bool ModeOn, SessionPhase Phase, bool CallerOwnsSession, bool SessionForceEnded, bool OpeningWindow,
        bool LinkedShipmentShipped);

    /// <summary>
    /// <c>orders/sync</c> güncelleme dalının birim kuralı (§4.2 tablosu), sunucudan bağımsız yazılmış: K1 oluşum alanları
    /// hiç yazılmaz (ebeveyn yalnız boşsa); K2 damgasız birimde son gelen kazanır, damgalı birimi yalnız
    /// <c>baseVersion = Version</c> taşıyan v2 yazar, yedek yalnız 1 → 0; K3 baskı boşsa alınır ama şu anda komutla
    /// iptal edilmiş siparişe değil (<c>cancelled-elsewhere</c>), doluysa silinmez ve kaymaz (v2 boş grup alanlarını
    /// doldurur); K4 bağı v1 taşımaz, mod Off'ta v2 K2'nin yazılabilirliğiyle, mod On'da yalnız açılış penceresi boş bağı
    /// doldurur; K5 mod On'da devredilmiş oturumda fiyat, iptal ve yedek reddedilir — zorla bitirilmiş oturumun sahibi ve
    /// açılış penceresi (v2) yalnız K2 ile yargılanır; K6 mod On'da kargolanmış dosyadaki siparişin fiyatı reddedilir.
    /// Push'un farklı değer taşıyıp yazılmayan her birimi (birim, neden) olarak döner; aynı değer uygulanmış sayılır.
    /// Zaman damgaları saniyeyle karşılaştırılır.
    /// </summary>
    private static (OrderBody Row, HashSet<string> Changed, List<(string Unit, string Reason)> Ignored) UnitRule(
        ServerOrder server, OrderBody push, bool v2, UnitContext c)
    {
        var s = server.Row;
        var m = s;
        var ignored = new List<(string Unit, string Reason)>();
        var locked = c.ModeOn && c.Phase == SessionPhase.Released
                     && !(v2 && ((c.SessionForceEnded && c.CallerOwnsSession) || c.OpeningWindow));
        bool Writable(DateTimeOffset? setAt) => setAt is null || (v2 && push.BaseVersion == server.Version);

        var pushCorrected = v2 ? push.PriceCorrectedAt : s.PriceCorrectedAt;
        var pushPaid = v2 ? push.PaidPrice : s.PaidPrice;
        var priceDiffers = push.Price != s.Price || !SameSecond(pushCorrected, s.PriceCorrectedAt) || pushPaid != s.PaidPrice;
        if (locked) { if (priceDiffers) ignored.Add(("price", "session-released")); }
        else if (c.ModeOn && c.LinkedShipmentShipped) { if (priceDiffers) ignored.Add(("price", "shipment-shipped")); }
        else if (Writable(server.PriceSetAt)) m = m with { Price = push.Price, PriceCorrectedAt = pushCorrected, PaidPrice = pushPaid };
        else if (priceDiffers) ignored.Add(("price", "unit-stamped"));

        var cancelDiffers = !SameSecond(push.CancelledAt, s.CancelledAt) || push.CancelReason != s.CancelReason;
        if (locked) { if (cancelDiffers) ignored.Add(("cancel", "session-released")); }
        else if (Writable(server.CancelSetAt)) m = m with { CancelledAt = push.CancelledAt, CancelReason = push.CancelReason };
        else if (cancelDiffers) ignored.Add(("cancel", "unit-stamped"));

        if (push.IsTentativeBackup != s.IsTentativeBackup)
        {
            if (push.IsTentativeBackup) ignored.Add(("backup", "backup-one-way"));
            else if (locked) ignored.Add(("backup", "session-released"));
            else if (Writable(server.BackupSetAt)) m = m with { IsTentativeBackup = false };
            else ignored.Add(("backup", "unit-stamped"));
        }

        if (v2 && push.ShipmentId != s.ShipmentId)
        {
            if (!c.ModeOn)
            {
                if (Writable(server.ShipmentSetAt)) m = m with { ShipmentId = push.ShipmentId };
                else ignored.Add(("shipment", "unit-stamped"));
            }
            else if (c.OpeningWindow && s.ShipmentId is null && server.ShipmentSetAt is null)
                m = m with { ShipmentId = push.ShipmentId };
            else ignored.Add(("shipment", "shipment-server-owned"));
        }

        if (s.PrintedAt is null)
        {
            if (push.PrintedAt is not null)
            {
                if (m.CancelledAt is not null && server.CancelSetAt is not null) ignored.Add(("print", "cancelled-elsewhere"));
                else m = m with
                {
                    PrintedAt = push.PrintedAt,
                    PrintBatchId = v2 ? push.PrintBatchId : s.PrintBatchId,
                    PrintSeq = v2 ? push.PrintSeq : s.PrintSeq,
                };
            }
        }
        else
        {
            if (v2) m = m with { PrintBatchId = s.PrintBatchId ?? push.PrintBatchId, PrintSeq = s.PrintSeq ?? push.PrintSeq };
            if (!SameSecond(push.PrintedAt, s.PrintedAt)
                || (v2 && (push.PrintBatchId != m.PrintBatchId || push.PrintSeq != m.PrintSeq)))
                ignored.Add(("print", "already-printed"));
        }

        if (v2 && s.ParentOrderId is null) m = m with { ParentOrderId = push.ParentOrderId };

        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (s.Price != m.Price || s.PriceCorrectedAt != m.PriceCorrectedAt || s.PaidPrice != m.PaidPrice) changed.Add("price");
        if (s.CancelledAt != m.CancelledAt || s.CancelReason != m.CancelReason) changed.Add("cancel");
        if (s.IsTentativeBackup != m.IsTentativeBackup) changed.Add("backup");
        if (s.ShipmentId != m.ShipmentId) changed.Add("shipment");
        if (s.PrintedAt != m.PrintedAt || s.PrintBatchId != m.PrintBatchId || s.PrintSeq != m.PrintSeq) changed.Add("print");
        return (m, changed, ignored);
    }

    /// <summary>K7: verilen dosyalardan bu lisansın türetilmiş (<c>AmountDerivedAt</c> dolu) ve Shipped olmayanlarının
    /// tutarını bağlı siparişlerden yeniden türetir (sürüm +1, yeni seq). Türetilenleri döner.</summary>
    private List<ServerShipment> DeriveAffected(IEnumerable<Guid> shipmentIds, DateTimeOffset now)
    {
        var derived = new List<ServerShipment>();
        foreach (var id in shipmentIds.Distinct())
        {
            if (!_shipments.TryGetValue(id, out var shipment) || shipment.AmountDerivedAt is null
                || shipment.Row.Status == "shipped")
                continue;
            derived.Add(PutShipment(shipment.LicenseId, Derive(shipment, now)));
        }
        return derived;
    }

    /// <summary>Siparişin güncel akış öğesi (asıl kayıt + KVKK okuma maskesi): v2 yanıtının <c>current</c>'ı.</summary>
    private OrderItemBody CurrentItem(ServerOrder order)
    {
        var (customerId, canonical) = ResolveCustomer(order.Row.CustomerId);
        return FeedOrderOf(order, customerId, canonical?.PurgedAt is not null);
    }

    /// <summary><c>SalesEnvelope.TryRead</c>'in tamamı: protokolsüz istek (zarf alanı yok) v1 — <c>envelope</c> boş;
    /// yarım zarf 400.</summary>
    private static bool TryReadEnvelope(Guid? replicaId, int? protocol, int? seenMode,
        out PushEnvelope? envelope, out string? error)
    {
        envelope = null;
        error = null;
        if (protocol is null)
        {
            if (replicaId is null && seenMode is null) return true;
            error = "replicaId ve seenMode yalnız protocol ile gönderilir.";
            return false;
        }
        if (!TryEnvelope(replicaId, protocol, seenMode, out var replica, out var seen, out error)) return false;
        envelope = new PushEnvelope(replica, seen);
        return true;
    }

    /// <summary><c>SalesEnvelope.TryRead</c>'in aynası (yalnız v2 dalı).</summary>
    private static bool TryEnvelope(Guid? replicaId, int? protocol, int? seenMode,
        out Guid replica, out int seen, out string? error)
    {
        replica = Guid.Empty; seen = 0; error = null;
        if (protocol != 2) { error = $"Desteklenmeyen protokol: {protocol}."; return false; }
        if (replicaId is not { } id || id == Guid.Empty) { error = "v2 isteği replicaId taşımalı."; return false; }
        if (seenMode is not (0 or 1)) { error = "v2 isteği seenMode (0 ya da 1) taşımalı."; return false; }
        replica = id; seen = seenMode.Value;
        return true;
    }

    // ── değişiklik akışı (W2-03) ───────────────────────────────────────────

    /// <summary>Depo geneli rowversion: her satır yazımı sıradakini alır (<c>_gate</c> altında).</summary>
    private long _seq;

    /// <summary>Ufku elle verir (<c>MIN_ACTIVE_ROWVERSION</c>; ör. commit edilmemiş işlem). Boşsa sıradaki seq.</summary>
    public long? HorizonOverride { get; set; }

    /// <summary>Şu ana kadar verilen en büyük seq.</summary>
    public long LastSeq { get { lock (_gate) return _seq; } }

    /// <summary><c>sales/changes</c> sayfası kurulmadan önce koşar; parametre istemcinin imleci (arıza enjeksiyonu).</summary>
    public Action<long>? OnChanges { get; set; }

    /// <summary>Sayfa başına en çok öğe (istemcinin <c>take</c>'inden küçükse). Sunucu <c>take</c>'ten az da
    /// döndürebilir; dolu olmayan sayfa son sayılmaz — çok sayfalı yetişmeyi küçük veriyle sınar.</summary>
    public int? PageLimit { get; set; }

    private readonly Dictionary<Guid, CustomerProjection> _customers = new();

    /// <summary>Sunucudaki müşteri projeksiyonu (taslağın kaynağı, §4.4). <paramref name="mergedInto"/> doluysa kopya.</summary>
    public void SeedCustomer(Guid id, string platform, string username, string? displayName,
        DateTimeOffset? purgedAt = null, Guid? mergedInto = null)
    {
        lock (_gate) _customers[id] = new CustomerProjection(id, platform, username, displayName, purgedAt, mergedInto);
    }

    /// <summary>Asıl kaydın KVKK silmesi: projeksiyon boşaltılır, siparişler OKUMA ANINDA maskelenir (§9).</summary>
    public void PurgeCustomer(Guid id, DateTimeOffset at)
    {
        lock (_gate)
            _customers[id] = _customers[id] with { DisplayName = null, PurgedAt = at };
    }

    /// <summary>Siparişi yazar: içerik değişmediyse hiçbir şey (EF yalnız değişeni yazar), değiştiyse yeni seq;
    /// devredilmiş oturumun siparişi <c>SessionReleasedAt</c>'i eklemede alır (DM3). Lisans eklemede belirlenir.</summary>
    private ServerOrder PutOrder(Guid licenseId, ServerOrder order)
    {
        var id = order.Row.Id;
        if (_orders.TryGetValue(id, out var old))
        {
            order = order with { LicenseId = old.LicenseId, SessionReleasedAt = order.SessionReleasedAt ?? old.SessionReleasedAt };
            if (old with { ChangeSeq = 0 } == order with { ChangeSeq = 0 }) return old;
        }
        else
        {
            order = order with { LicenseId = licenseId };
        }
        if (order.SessionReleasedAt is null && order.Row.SessionId is { } sid
            && _serverSessions.TryGetValue(sid, out var session) && session.ReleasedAt is { } released)
            order = order with { SessionReleasedAt = released };
        return _orders[id] = order with { ChangeSeq = ++_seq };
    }

    /// <summary>Dosyayı yazar: değişmediyse hiçbir şey, değiştiyse yeni seq. Lisans eklemede belirlenir.</summary>
    private ServerShipment PutShipment(Guid licenseId, ServerShipment shipment)
    {
        var id = shipment.Row.Id;
        if (_shipments.TryGetValue(id, out var old))
        {
            shipment = shipment with { LicenseId = old.LicenseId };
            if (old with { ChangeSeq = 0 } == shipment with { ChangeSeq = 0 }) return old;
        }
        else
        {
            shipment = shipment with { LicenseId = licenseId };
        }
        return _shipments[id] = shipment with { ChangeSeq = ++_seq };
    }

    /// <summary>Oturum yazımının sonu: satır değiştiyse yeni seq; devir bu yazımda olduysa ardından siparişlerine
    /// <c>SessionReleasedAt</c> basılır (<c>SessionReleaser</c>: önce oturum, sonra siparişler — siparişlerin seq'i
    /// büyük).</summary>
    private void CommitSession(ServerSession s, SessionStamp? before)
    {
        if (before == s.Stamp()) return;
        s.ChangeSeq = ++_seq;
        if (s.ReleasedAt is not { } released || before?.ReleasedAt is not null) return;
        foreach (var order in _orders.Values.Where(o => o.Row.SessionId == s.Id && o.SessionReleasedAt is null)
                     .OrderBy(o => o.ChangeSeq).ToList())
            PutOrder(order.LicenseId, order with { SessionReleasedAt = released });
    }

    /// <summary>
    /// <c>GET sales/changes</c> — <c>LicensesSalesController.Changes</c> + <c>SalesChangesFeed.ReadAsync</c> +
    /// <c>SalesChangesPageBuilder</c>'ın aynası (§4.4). Bkz. sınıf özeti.
    /// </summary>
    private HttpResponseMessage HandleChanges(Guid licenseId, string query)
    {
        var replicaRaw = QueryValue(query, "replicaId");
        var seenRaw = QueryValue(query, "seenMode");
        if (replicaRaw is not null || seenRaw is not null)
        {
            Guid? replica = Guid.TryParse(replicaRaw, out var r) ? r : null;
            int? seen = int.TryParse(seenRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
            if (!TryEnvelope(replica, 2, seen, out var replicaId, out var seenMode, out var error))
                return Invalid(error!);
            lock (_gate) Touch(licenseId, replicaId, seenMode);
        }
        if (!long.TryParse(QueryValue(query, "afterSeq") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var afterSeq))
            return Invalid("afterSeq sayı değil");
        if (!int.TryParse(QueryValue(query, "take") ?? MaxChangesTake.ToString(CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var take) || take is < 1 or > MaxChangesTake)
            return Invalid($"take 1..{MaxChangesTake} dışında: istemci sunucunun kırpmasına güvenmemeli");

        OnChanges?.Invoke(afterSeq);
        if (PageLimit is { } limit) take = Math.Min(take, limit);

        ChangesPageBody page;
        lock (_gate)
        {
            var horizon = HorizonOverride ?? _seq + 1;
            var cursorReset = afterSeq < 0 || afterSeq >= horizon;
            if (cursorReset) afterSeq = 0;
            bool InRange(long seq) => seq > afterSeq && seq < horizon;

            var rows = _serverSessions.Values
                    .Where(s => s.LicenseId == licenseId && s.ReleasedAt is not null && InRange(s.ChangeSeq))
                    .Select(s => (Seq: s.ChangeSeq, Session: (ServerSession?)s, Order: (ServerOrder?)null,
                        Shipment: (ServerShipment?)null))
                .Concat(_orders.Values
                    .Where(o => o.LicenseId == licenseId && o.SessionReleasedAt is not null && o.Row.SessionId is not null
                                && InRange(o.ChangeSeq))
                    .Select(o => (Seq: o.ChangeSeq, Session: (ServerSession?)null, Order: (ServerOrder?)o,
                        Shipment: (ServerShipment?)null)))
                .Concat(_shipments.Values
                    .Where(s => s.LicenseId == licenseId && InRange(s.ChangeSeq))
                    .Select(s => (Seq: s.ChangeSeq, Session: (ServerSession?)null, Order: (ServerOrder?)null,
                        Shipment: (ServerShipment?)s)))
                .OrderBy(x => x.Seq)
                .Take(take)
                .ToList();

            var items = new List<ChangeItemBody>(rows.Count);
            var side = new List<SessionItemBody>();
            var seenSessions = new HashSet<Guid>();
            var drafts = new Dictionary<string, CustomerDraftBody>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.Session is { } session)
                {
                    items.Add(new ChangeItemBody("session", row.Seq, session.ToFeedItem(), null, null));
                }
                else if (row.Order is { } order)
                {
                    var (customerId, canonical) = ResolveCustomer(order.Row.CustomerId);
                    var masked = canonical?.PurgedAt is not null;
                    items.Add(new ChangeItemBody("order", row.Seq, null, FeedOrderOf(order, customerId, masked), null));
                    var sid = order.Row.SessionId!.Value;
                    if (seenSessions.Add(sid) && _serverSessions.TryGetValue(sid, out var orderSession)
                        && orderSession.LicenseId == licenseId)
                        side.Add(orderSession.ToFeedItem());
                    drafts[customerId] = canonical is { } c
                        ? new CustomerDraftBody(customerId, c.Platform, c.Username, masked ? null : c.DisplayName, c.PurgedAt)
                        : new CustomerDraftBody(customerId, order.Row.Platform, order.Row.Username, order.Row.DisplayName, null);
                }
                else if (row.Shipment is { } shipment)
                {
                    items.Add(new ChangeItemBody("shipment", row.Seq, null, null, shipment.ToFeedItem()));
                }
            }
            page = new ChangesPageBody(items, items.Count == 0 ? afterSeq : items[^1].ChangeSeq, cursorReset, side,
                drafts.Values.ToList());
        }
        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(page, Web));
    }

    /// <summary><c>SalesCustomerDirectory.Resolve</c>: "N" biçimli GUID kopya zinciri boyunca (en çok üç adım) asıl kayda;
    /// asıl kayıt Id'si değişmediyse ham yazım korunur. GUID olmayan ya da projeksiyonu olmayan müşteri ham değerle,
    /// projeksiyonsuz. Projeksiyonlar bu ikizde lisans başına ayrılmaz (Id'ler GUID; testler çakıştırmaz).</summary>
    private (string Id, CustomerProjection? Canonical) ResolveCustomer(string raw)
    {
        if (!Guid.TryParseExact(raw, "N", out var id)) return (raw, null);
        var current = id;
        for (var hop = 0; hop < 3 && _customers.TryGetValue(current, out var p) && p.MergedInto is { } target; hop++)
            current = target;
        return (current == id ? raw : current.ToString("N"), _customers.GetValueOrDefault(current));
    }

    /// <summary><c>SalesOrderItem.From(order, customer)</c>: asıl kaydın Id'si, KVKK okuma maskesi.</summary>
    private static OrderItemBody FeedOrderOf(ServerOrder o, string customerId, bool masked)
    {
        var r = o.Row;
        return new OrderItemBody(
            r.Id, r.SessionId, customerId, r.Platform, r.Username, masked ? null : r.DisplayName,
            masked ? "" : r.MessageText, r.Code, r.Price, r.AddedAt, r.PrintedAt, r.CancelledAt, r.CancelReason,
            r.IsShippingFee, r.IsBackupPromoted, r.IsTentativeBackup, r.ProductId, r.ProductVariantId, o.UpdatedAt,
            o.Version, r.ParentOrderId, r.ShipmentId, o.OriginReplicaId, r.PrintBatchId, r.PrintSeq, r.PriceCorrectedAt,
            r.PaidPrice, o.SessionReleasedAt, o.PriceSetAt, o.CancelSetAt, o.BackupSetAt, o.ShipmentSetAt, null, o.ChangeSeq);
    }

    /// <summary><c>wpf-customers/sync</c>: projeksiyonları kaydeder (Id, platform, kullanıcı adı, takma ad). Silinmiş
    /// projeksiyona yazılmaz. Yanıt <c>{synced, retroactiveMatches, redirects}</c>.</summary>
    private async Task<HttpResponseMessage> HandleCustomerPushAsync(HttpRequestMessage req, Guid licenseId, CancellationToken ct)
    {
        const string endpoint = "wpf-customers/sync";
        var raw = await req.Content!.ReadAsStringAsync(ct);
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _customerPushCalls.Add(new PushCall(endpoint, licenseId, raw));
            if (_scripted.TryGetValue(endpoint, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        if (scripted is not null) return scripted();

        using var doc = JsonDocument.Parse(raw);
        var synced = 0;
        lock (_gate)
            foreach (var c in doc.RootElement.GetProperty("customers").EnumerateArray())
            {
                var id = c.GetProperty("id").GetGuid();
                if (_customers.TryGetValue(id, out var known) && known.PurgedAt is not null) continue;
                _customers[id] = new CustomerProjection(id, c.GetProperty("platform").GetString()!,
                    c.GetProperty("username").GetString()!,
                    c.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null,
                    null, known?.MergedInto);
                synced++;
            }
        return FakeHttpMessageHandler.Json(200,
            $"{{\"synced\":{synced.ToString(CultureInfo.InvariantCulture)},\"retroactiveMatches\":0,\"redirects\":[]}}");
    }

    /// <summary><c>payments/sync</c> (dekont gönderimi: kayıt, betik, <see cref="DuringPush"/>, boş yankı) ve
    /// <c>payments/since</c> (boş). Sunucunun dekont kuralı taklit edilmez: açılışın 1. adımı yalnız kuyruğun boşalmasına
    /// bakar.</summary>
    private async Task<HttpResponseMessage> HandlePaymentsAsync(
        HttpRequestMessage req, Guid licenseId, string action, CancellationToken ct)
    {
        if (action == "since" && req.Method == HttpMethod.Get) return FakeHttpMessageHandler.Json(200, "[]");
        if (action != "sync" || req.Method != HttpMethod.Post)
        {
            _faults.Enqueue($"beklenmeyen istek: {req.Method} payments/{action}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var raw = await req.Content!.ReadAsStringAsync(ct);
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(PaymentsSync, licenseId, raw));
            if (_scripted.TryGetValue(PaymentsSync, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        if (scripted is not null) return scripted();
        DuringPush?.Invoke(PaymentsSync);
        return FakeHttpMessageHandler.Json(200, "[]");
    }

    /// <summary>Sunucunun aldığı kirasız kayıt Id'leri (<c>payment-requests/backfill</c>).</summary>
    public IReadOnlyCollection<Guid> PaymentRecordIds { get { lock (_gate) return _paymentRecordIds.ToList(); } }
    private readonly HashSet<Guid> _paymentRecordIds = new();

    /// <summary>
    /// <c>payment-requests/backfill</c> ve <c>payment-requests/{id}/finish</c> (W3-05, bekleyen ödeme kayıtları): kayıt
    /// (<see cref="Calls"/>), betik (<see cref="EnqueueStatus"/>) ve v2 zarfı denetimi. Geri doldurma her öğeye
    /// <c>applied</c> (aynı Id ikinci kez <c>unchanged</c>) döner, finish sonucu yankılar. Kira ve kayıt kuralları taklit
    /// edilmez: açılışın 1. adımı ve geri doldurma turu yalnız kuyruğun gönderilip boşalmasına bakar.
    /// </summary>
    private async Task<HttpResponseMessage> HandlePaymentRequestAsync(
        HttpRequestMessage req, Guid licenseId, Guid? finishId, CancellationToken ct)
    {
        var endpoint = finishId is null ? PaymentRequestsBackfill : PaymentRequestsFinish;
        var raw = await req.Content!.ReadAsStringAsync(ct);
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(endpoint, licenseId, raw, finishId));
            if (_scripted.TryGetValue(endpoint, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        if (scripted is not null) return scripted();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("replicaId", out var replica) || replica.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != 2
            || !root.TryGetProperty("seenMode", out _))
            return Invalid($"{endpoint}: v2 zarfı yok");

        if (finishId is { } recordId)
            return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
            {
                recordId, state = root.GetProperty("outcome").GetString(), finishedAt = DateTimeOffset.UtcNow,
            }, Web));

        var results = new List<object>();
        lock (_gate)
            foreach (var record in root.GetProperty("records").EnumerateArray())
            {
                var id = record.GetProperty("recordId").GetGuid();
                results.Add(new { recordId = id, status = _paymentRecordIds.Add(id) ? "applied" : "unchanged" });
            }
        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new { records = results }, Web));
    }

    /// <summary>Sunucuda kayıtlı, teslim edilmiş ya da sonucu bilinmeyen ödeme isteği kaydı (W3-09): <c>begin</c>'in (e)
    /// adımının öncülü (<see cref="AddPaymentPrior"/>).</summary>
    /// <param name="Members">Kaydın üyeleri; isteğin üyeleriyle kesişiyorsa aynı kişi sayılır.</param>
    internal sealed record PaymentPrior(Guid Id, string? Machine, IReadOnlyList<string> Members, string ScopeKey,
        decimal? NetAmount, decimal? TotalBeforeBalance, DateTimeOffset At, DateTimeOffset FinishedAt, bool Unconfirmed);

    private readonly List<PaymentPrior> _paymentPriors = new();

    /// <summary>Kaydı sunucuya ekler: sonraki <c>begin</c>'in (e) adımı onu görür.</summary>
    public void AddPaymentPrior(PaymentPrior prior)
    {
        lock (_gate) _paymentPriors.Add(prior);
    }

    /// <summary>
    /// <c>POST payment-requests/begin</c> (W3-07): kayıt (<see cref="Calls"/>), betik, v2 zarfı denetimi; mod Off'ta 409
    /// <c>mode-off</c> (§4.7). Kira kurallarından yalnız (e) taklit edilir (W3-09, #523'ün aynası,
    /// <see cref="AlreadyRequestedRule"/>); öbürleri (yeniden oynatma, üye kesişimi, sunucu toplamı, kargo) taklit
    /// edilmez: betik yoksa ve (e) geçerse 200, durum <c>sending</c>, üyeler istemcinin üyeleri ∪
    /// <see cref="ExtraBeginMembers"/>.
    /// </summary>
    private async Task<HttpResponseMessage> HandlePaymentBeginAsync(HttpRequestMessage req, Guid licenseId, CancellationToken ct)
    {
        var raw = await req.Content!.ReadAsStringAsync(ct);
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(PaymentRequestsBegin, licenseId, raw));
            if (_scripted.TryGetValue(PaymentRequestsBegin, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        if (scripted is not null) return scripted();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("replicaId", out var replica) || replica.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != 2
            || !root.TryGetProperty("seenMode", out _))
            return Invalid($"{PaymentRequestsBegin}: v2 zarfı yok");
        if (Mode != 1) return FakeHttpMessageHandler.Problem(409, "mode-off");
        if (AlreadyRequestedRule(root) is { } rejected) return rejected;

        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
        {
            recordId = root.GetProperty("recordId").GetGuid(),
            state = "sending",
            leaseUntil = DateTimeOffset.UtcNow.AddSeconds(120),
            members = root.GetProperty("memberIds").EnumerateArray().Select(m => m.GetString())
                .Concat(ExtraBeginMembers).Distinct(StringComparer.Ordinal).ToList(),
        }, Web));
    }

    /// <summary>
    /// <c>begin</c>'in onay denetimi ve (e) adımı, #523'ün aynası (<c>PaymentRequestLeaseService</c>): 500'ü aşan
    /// <c>confirmedPriorIds</c> 400 <c>invalid-confirmed-priors</c>, biçimi bozuk <c>confirmedPriorsDigest</c> 400
    /// <c>invalid-priors-digest</c>. Kişi × kapsamın kayıtları (<see cref="AddPaymentPrior"/>; üye kesişimi, aynı kapsam)
    /// Id listesinin dışında öncül bırakıyorsa onay özeti BÜTÜN kümenin özetine eşit olmalı; değilse 409
    /// <c>already-requested {priors, priorsDigest}</c> — <c>priors</c> listenin dışındakiler, kapanış sırasıyla. Geçerse null.
    /// </summary>
    private HttpResponseMessage? AlreadyRequestedRule(JsonElement root)
    {
        var confirmed = root.TryGetProperty("confirmedPriorIds", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(i => i.GetGuid()).ToHashSet()
            : new HashSet<Guid>();
        if (confirmed.Count > 500)
            return ProblemResponse(400, "invalid-confirmed-priors", "confirmedPriorIds en çok 500 dolu Id taşır.");
        byte[]? confirmedDigest = null;
        if (root.TryGetProperty("confirmedPriorsDigest", out var digestText) && digestText.ValueKind != JsonValueKind.Null
            && (digestText.ValueKind != JsonValueKind.String || !PriorsDigest.TryParse(digestText.GetString(), out confirmedDigest)))
            return ProblemResponse(400, "invalid-priors-digest", "confirmedPriorsDigest biçimi bozuk.");

        var scopeKey = root.GetProperty("scopeKey").GetString();
        var members = root.GetProperty("memberIds").EnumerateArray().Select(m => m.GetString()!).ToHashSet(StringComparer.Ordinal);
        List<PaymentPrior> open;
        lock (_gate)
            open = _paymentPriors
                .Where(p => p.ScopeKey == scopeKey && p.Members.Any(members.Contains))
                .OrderBy(p => p.FinishedAt).ThenBy(p => p.At).ThenBy(p => p.Id)
                .ToList();
        var priors = open.Where(p => !confirmed.Contains(p.Id)).ToList();
        if (priors.Count == 0) return null;

        var digest = PriorsDigest.Hash(open.Select(p => p.Id));
        if (confirmedDigest is not null && confirmedDigest.AsSpan().SequenceEqual(digest)) return null;
        return ProblemResponse(409, "already-requested", "Bu kişiden bu kapsamda daha önce ödeme istendi; tekrar gönderilsin mi?",
            new Dictionary<string, object?>
            {
                ["priors"] = priors.Select(p => new
                {
                    id = p.Id, machine = p.Machine, netAmount = p.NetAmount, totalBeforeBalance = p.TotalBeforeBalance,
                    at = p.At, finishedAt = p.FinishedAt, unconfirmed = p.Unconfirmed,
                }).ToList(),
                ["priorsDigest"] = PriorsDigest.Format(digest),
            });
    }

    /// <summary>
    /// <c>priorsDigest</c>'in hesabı, sunucunun <c>PaymentRequestPriorsDigest</c>'inin aynası (§6.4-e "Özetin biçimi"): Id'ler
    /// tekilleştirilir, RFC 9562 (büyük uçlu) baytlarının sırasıyla art arda SHA-256; telde <c>v1.</c> + dolgusuz base64url
    /// (46 karakter), tek yazım. İstemci özeti hesaplamaz — yalnız yankılar; bu ikiz sunucunun karşılaştırmasını taklit eder.
    /// </summary>
    internal static class PriorsDigest
    {
        private const string Prefix = "v1.";

        public static string Compute(IEnumerable<Guid> ids) => Format(Hash(ids));

        public static byte[] Hash(IEnumerable<Guid> ids)
        {
            var keys = ids.Distinct().Select(BigEndian).Order().ToArray();
            var buffer = new byte[keys.Length * 16];
            for (var i = 0; i < keys.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(buffer.AsSpan(i * 16, 16), keys[i]);
            return System.Security.Cryptography.SHA256.HashData(buffer);
        }

        public static string Format(byte[] hash) => Prefix + System.Buffers.Text.Base64Url.EncodeToString(hash);

        public static bool TryParse(string? value, out byte[]? hash)
        {
            hash = null;
            if (value is null || value.Length != Prefix.Length + 43 || !value.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            var encoded = value.AsSpan(Prefix.Length);
            foreach (var c in encoded)
                if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
            var decoded = new byte[32];
            try
            {
                if (!System.Buffers.Text.Base64Url.TryDecodeFromChars(encoded, decoded, out var written) || written != 32)
                    return false;
            }
            catch (FormatException) { return false; }
            if (Format(decoded) != value) return false;                    // tek yazım: artık bitler sıfır
            hash = decoded;
            return true;
        }

        private static UInt128 BigEndian(Guid id)
        {
            Span<byte> bytes = stackalloc byte[16];
            id.TryWriteBytes(bytes, bigEndian: true, out _);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(bytes);
        }
    }

    /// <summary>Bakiye uçları (W3-07): önizleme <see cref="BalancePreview"/>, kapsam <see cref="ScopeDeductions"/>'tan
    /// (yoksa 204, düşüm yok), apply en çok
    /// istenen kadar ve işlem kimliği = idempotency anahtarı (gerçek sunucu gibi). Kayıt <see cref="Calls"/>'ta.</summary>
    private async Task<HttpResponseMessage> HandleBalanceAsync(
        HttpRequestMessage req, Guid licenseId, string action, CancellationToken ct)
    {
        var endpoint = "customer-balance/" + action;
        var raw = req.Content is null ? req.RequestUri!.Query : await req.Content.ReadAsStringAsync(ct);
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(endpoint, licenseId, raw));
            if (_scripted.TryGetValue(endpoint, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        if (scripted is not null) return scripted();

        switch (action)
        {
            case "preview" when req.Method == HttpMethod.Get:
                return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
                {
                    wpfCustomerId = Guid.Empty, balance = BalancePreview, updatedAt = DateTimeOffset.UtcNow,
                }, Web));
            case "scope" when req.Method == HttpMethod.Get:
            {
                var customer = Regex.Match(req.RequestUri!.Query, @"wpfCustomerId=([0-9a-fA-F-]{36})");
                return customer.Success && ScopeDeductions.TryGetValue(Guid.Parse(customer.Groups[1].Value), out var found)
                    ? FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(found, Web))
                    : FakeHttpMessageHandler.Empty(204);
            }
            case "apply" when req.Method == HttpMethod.Post:
            {
                using var doc = JsonDocument.Parse(raw);
                var amount = doc.RootElement.GetProperty("amount").GetDecimal();
                var key = doc.RootElement.TryGetProperty("idempotencyKey", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetGuid()
                    : Guid.NewGuid();
                var applied = Math.Min(BalancePreview, amount);
                return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
                {
                    transactionId = key, appliedAmount = applied, remainingBalance = BalancePreview - applied,
                }, Web));
            }
            default:
                _faults.Enqueue($"beklenmeyen istek: {req.Method} {endpoint}");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>WhatsApp Cloud API gönderimi (W3-07): kayıt, betik; betik yoksa gönderildi.</summary>
    private async Task<HttpResponseMessage> HandleWhatsAppSendAsync(HttpRequestMessage req, Guid licenseId, CancellationToken ct)
    {
        var raw = await req.Content!.ReadAsStringAsync(ct);
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(WhatsAppSend, licenseId, raw));
            if (_scripted.TryGetValue(WhatsAppSend, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        if (scripted is not null) return scripted();
        return FakeHttpMessageHandler.Json(200,
            $"{{\"ok\":true,\"errorCode\":null,\"errorMessage\":null,\"messageId\":\"{Guid.NewGuid()}\"}}");
    }

    private Replica Touch(Guid licenseId, Guid replicaId, int seenMode)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_replicas.TryGetValue((licenseId, replicaId), out var row))
            _replicas[(licenseId, replicaId)] = row = new Replica { ReplicaId = replicaId, FirstSeenAt = now };
        row.LastSeenAt = now;
        row.SeenMode = seenMode;
        // SalesReplicaService.Heartbeat: kopyanın modu ilk gördüğü an; mod yeniden açıldıysa yeni dönemin ilk anı.
        if (seenMode == 1 && (row.SeenModeOnAt is null || (ModeChangedAt is { } changedAt && row.SeenModeOnAt < changedAt)))
            row.SeenModeOnAt = now;
        row.Requests++;
        return row;
    }

    /// <summary>
    /// <c>SalesOpeningWindow.Applies</c>'in aynası (§4.0): mod On'da <c>SeenMode = 0</c> bildiren v2 isteği yalnız kopya
    /// mod son açılmadan önce varsa, geri yüklenmemişse, klon izi yoksa ve bu açılıştan beri modu görmemişse Off
    /// kurallarıyla yargılanır. Kopya satırının bu isteğin kalp atışından ÖNCEKİ hâliyle — <see cref="Touch"/>'tan önce
    /// çağrılır. Değişim anı bilinmiyorsa ya da kopya bu istekle doğuyorsa pencere kapalı.
    /// </summary>
    private bool IsOpeningWindow(Guid licenseId, Guid replicaId, int seenMode)
    {
        if (Mode != 1 || seenMode != 0) return false;
        if (ModeChangedAt is not { } changedAt || !_replicas.TryGetValue((licenseId, replicaId), out var before))
            return false;
        return before.FirstSeenAt < changedAt
            && before.PreviousReplicaId is null
            && !before.HardwareFingerprintConflict
            && (before.SeenModeOnAt is null || before.SeenModeOnAt < changedAt);
    }

    private HttpResponseMessage Invalid(string error)
    {
        _faults.Enqueue($"geçersiz istek: {error}");
        return FakeHttpMessageHandler.Problem(400, "invalid-envelope", error);
    }

    private static string? QueryValue(string query, string name)
    {
        var m = Regex.Match(query, $@"[?&]{name}=([^&]*)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    // ── komutlar (W2-04) ───────────────────────────────────────────────────

    /// <summary><c>sales/commands</c> istekleri (geçersizler ve kasıtlı arızalar dahil), ham gövdeyle.</summary>
    public IReadOnlyList<PushCall> CommandCalls { get { lock (_gate) return _commandCalls.ToList(); } }
    private readonly List<PushCall> _commandCalls = new();

    /// <summary>Komut günlüğü satırı (<c>SalesCommandLogs</c>); yoksa null. <see cref="CommandLogEntry.Applications"/>
    /// komutun kaç kez UYGULANDIĞINI (hep 1), <see cref="CommandLogEntry.Replays"/> kaç kez yeniden oynatıldığını sayar.</summary>
    public CommandLogEntry? CommandLog(Guid commandId)
    {
        lock (_gate) return _commandLog.TryGetValue(commandId, out var e) ? e.Clone() : null;
    }
    private readonly Dictionary<Guid, CommandLogEntry> _commandLog = new();

    /// <summary>Komutun en çok sipariş/beklenen sürüm sayısı (<c>SalesCommandService.MaxOrders</c>).</summary>
    private const int MaxCommandOrders = 500;

    /// <summary>Kümeyi sunucunun kurduğu türlerde (<c>set-code-price</c>'ın planı) beklenen sürümlerin tavanı
    /// (<c>SalesCommandService.MaxSetOrders</c>).</summary>
    private const int MaxSetOrders = 10_000;

    private static readonly string[] KnownCommandKinds =
        ["cancel", "uncancel", "mark-printed", "reprint", "confirm-backup", "set-code-price", "add-backup",
         "attach-shipment", "shipment-decision", "force-end"];

    /// <summary><c>attach-shipment</c>'ın aday tavanı (<c>SalesCommandService.MaxSetOrders</c>); testte küçültülür
    /// (<c>too-many-candidates</c>).</summary>
    public int MaxAttachCandidates { get; set; } = MaxSetOrders;

    /// <summary>
    /// <c>POST sales/commands</c> — <c>SalesCommandService</c>'in aynası (§4.6), on tür: <c>cancel</c>,
    /// <c>uncancel</c>, <c>mark-printed</c>, <c>reprint</c>, <c>confirm-backup</c>, <c>set-code-price</c> (plan sunucuda
    /// kurulur, <see cref="SetCodePrice"/>), <c>add-backup</c> (<see cref="AddBackup"/>), <c>attach-shipment</c>
    /// (<see cref="AttachShipment"/>), <c>shipment-decision</c> (<see cref="DecideShipment"/>) ve <c>force-end</c>
    /// (<see cref="ForceEndSession"/>; W2-09).
    /// Sıra: zarf (v2) → commandId → beklenen sürümler → türün yükü (400'ler <see cref="Faults"/>'a yazılır: istemci
    /// geçersiz gövde göndermemeli) → kalp atışı → mod Off 409 <c>mode-off</c> → alt sürüm 409
    /// <c>client-update-required</c> (UA'daki "OrderDeck-WPF/x" ile <see cref="MinClientVersion"/>; ikisinden biri yoksa
    /// kapalı) → yeniden oynatma (aynı kimlik + aynı kanonik içerik → kayıtlı durum ve sonuç GÜNCEL satırlarla; içerik
    /// farklı ya da başka lisans 409 <c>content-conflict</c>; <c>reprint</c> sonucu güncel satırlardan yeniden kurulur)
    /// → bilinmeyen sipariş 400 <c>unknown-order</c> → görünürlük 409 <c>session-live</c> (devredilmemiş yayın;
    /// <c>reprint</c> çağıranın kendi yayınını görür) → beklenen sürüm 409 <c>stale</c> + <c>current</c> → uygulama:
    /// alanlar, damga sunucu saatiyle, değişen satırda sürüm +1 (yeni seq); bağlı türetilmiş dosya yeniden türetilir (K7,
    /// W2-12). Stok defteri yok.
    /// </summary>
    private HttpResponseMessage HandleCommand(Guid licenseId, string raw, string? userAgent)
    {
        CommandBody? body;
        try { body = JsonSerializer.Deserialize<CommandBody>(raw, Web); }
        catch (JsonException ex) { return Invalid($"komut gövdesi okunamadı: {ex.Message}"); }
        if (body is null) return Invalid("komut gövdesi yok");
        if (!TryEnvelope(body.ReplicaId, body.Protocol, body.SeenMode, out var replicaId, out var seenMode, out var envError))
            return Invalid(envError!);
        if (body.CommandId is not { } commandId || commandId == Guid.Empty)
            return InvalidCommand("invalid-command", "commandId boş olamaz.");
        if (!TryReadExpected(body.Expected,
                body.Kind is "set-code-price" or "attach-shipment" ? MaxSetOrders : MaxCommandOrders,
                out var expected, out var expectedError))
            return InvalidCommand("invalid-expected", expectedError!);
        if (!TryParseCommand(body.Kind, body.Payload, expected, out var command, out var code, out var error))
            return InvalidCommand(code!, error!);

        lock (_gate)
        {
            Touch(licenseId, replicaId, seenMode);
            if (Mode != 1)
                return ProblemResponse(409, "mode-off", "Satış senkronu bu lisansta kapalı.");
            if (!ClientAtLeast(userAgent, MinClientVersion))
                return ProblemResponse(409, "client-update-required", "Bu işlem için uygulamanın güncel sürümü gerekiyor.");

            var hash = string.Join("|", command.Kind, replicaId.ToString("N"), command.Canonical,
                string.Join(",", expected.OrderBy(e => e.OrderId).Select(e => $"{e.OrderId:N}:{e.Version}")));
            if (_commandLog.TryGetValue(commandId, out var logged))
            {
                if (logged.LicenseId != licenseId || logged.Hash != hash)
                    return ProblemResponse(409, "content-conflict", "Bu commandId farklı bir içerikle zaten kullanıldı.");
                logged.Replays++;
                if (command.Kind == "reprint")
                {
                    if (Visibility(licenseId, logged.ReplicaId, command) is { } hidden) return hidden;
                    return CommandSucceeded(licenseId, logged.Status, Reprint(command), command.OrderIds);
                }
                if (command.Kind is "attach-shipment" or "shipment-decision")
                    return ShipmentCommandSucceeded(licenseId, logged.Status, logged.ResultJson, command);
                if (command.Kind == "force-end")
                    return CommandSucceeded(licenseId, logged.Status, logged.ResultJson, [],
                        sessionIds: [command.SessionId!.Value]);
                return CommandSucceeded(licenseId, logged.Status, logged.ResultJson, command.OrderIds);
            }

            // force-end (§4.6, Y7, Y8): sipariş taşımaz; yayının hâline ve sahibin sessizliğine bakar.
            if (command.Kind == "force-end")
            {
                if (!ForceEndSession(licenseId, replicaId, command, DateTimeOffset.UtcNow, out var endStatus,
                        out var endResult, out var endRejected))
                    return endRejected!;
                _commandLog[commandId] = new CommandLogEntry
                {
                    CommandId = commandId, LicenseId = licenseId, ReplicaId = replicaId, Kind = command.Kind, Hash = hash,
                    Status = endStatus, ResultJson = endResult, Applications = 1,
                };
                return CommandSucceeded(licenseId, endStatus, endResult, [], sessionIds: [command.SessionId!.Value]);
            }

            // Kargo komutları (§4.6): yayın görünürlüğü yerine kendi süzgeçleri — kargo bağı her oturum hâlinde
            // sunucunun (K4).
            if (command.Kind is "attach-shipment" or "shipment-decision")
            {
                var now = DateTimeOffset.UtcNow;
                var ok = command.Kind == "attach-shipment"
                    ? AttachShipment(licenseId, replicaId, command, expected, now, out var shipResult, out var shipRejected)
                    : DecideShipment(licenseId, command, now, out shipResult, out shipRejected);
                if (!ok) return shipRejected!;
                var shipBody = JsonSerializer.Deserialize<CommandResultBody>(shipResult, Web)!;
                var shipStatus = shipBody.Changed.Count > 0 || shipBody.Decided is { Count: > 0 } ? "applied" : "unchanged";
                _commandLog[commandId] = new CommandLogEntry
                {
                    CommandId = commandId, LicenseId = licenseId, ReplicaId = replicaId, Kind = command.Kind, Hash = hash,
                    Status = shipStatus, ResultJson = shipResult, Applications = 1,
                };
                return ShipmentCommandSucceeded(licenseId, shipStatus, shipResult, command);
            }

            // Kümeyi sunucunun kurduğu / yeni satır ekleyen türler kendi denetimlerini yapar (§4.6 tablosu).
            if (command.Kind is "set-code-price" or "add-backup")
            {
                var now = DateTimeOffset.UtcNow;
                var applied = command.Kind == "set-code-price"
                    ? SetCodePrice(licenseId, command, expected, now, out var setResult, out var setRejected)
                    : AddBackup(licenseId, replicaId, command, now, out setResult, out setRejected);
                if (!applied) return setRejected!;
                var setBody = JsonSerializer.Deserialize<CommandResultBody>(setResult, Web)!;
                var setStatus = setBody.Changed.Count > 0 ? "applied" : "unchanged";
                _commandLog[commandId] = new CommandLogEntry
                {
                    CommandId = commandId, LicenseId = licenseId, ReplicaId = replicaId, Kind = command.Kind, Hash = hash,
                    Status = setStatus, ResultJson = setResult, Applications = 1,
                };
                return CommandSucceeded(licenseId, setStatus, setResult, command.OrderIds);
            }

            var unknown = command.OrderIds
                .Where(id => !_orders.TryGetValue(id, out var o) || o.LicenseId != licenseId)
                .ToList();
            if (unknown.Count > 0)
                return InvalidCommand("unknown-order", "Komutun siparişlerinden bazıları bu lisansta yok.",
                    new Dictionary<string, object?> { ["orderIds"] = unknown });
            if (Visibility(licenseId, replicaId, command) is { } live) return live;
            var expectedById = expected.ToDictionary(e => e.OrderId, e => e.Version);
            if (command.Kind != "reprint"
                && command.OrderIds.Any(id => expectedById.GetValueOrDefault(id, -1) != _orders[id].Version))
                return ProblemResponse(409, "stale", "Satırlar bu arada değişti; güncel sürümlerle yeniden deneyin.",
                    new Dictionary<string, object?> { ["current"] = CurrentOrderItems(licenseId, command.OrderIds) });

            string resultJson;
            if (command.Kind == "reprint")
            {
                resultJson = Reprint(command);
            }
            else if (!TryApplyCommand(licenseId, command, DateTimeOffset.UtcNow, out resultJson, out var rejected))
            {
                return rejected!;
            }
            var result = JsonSerializer.Deserialize<CommandResultBody>(resultJson, Web)!;
            var status = result.Changed.Count > 0 ? "applied" : "unchanged";
            _commandLog[commandId] = new CommandLogEntry
            {
                CommandId = commandId, LicenseId = licenseId, ReplicaId = replicaId, Kind = command.Kind, Hash = hash,
                Status = status, ResultJson = resultJson, Applications = 1,
            };
            return CommandSucceeded(licenseId, status, resultJson, command.OrderIds.Concat(result.Changed).Distinct().ToList());
        }
    }

    /// <summary>Görünürlük (§4.6 adım 3): her siparişin yayını devredilmiş olmalı; <c>reprint</c> çağıranın kendi
    /// yayınını da görür. Değilse 409 <c>session-live</c> (sahip makine ve sahibin son kalp atışı).</summary>
    private HttpResponseMessage? Visibility(Guid licenseId, Guid replicaId, ParsedCommand command)
    {
        foreach (var id in command.OrderIds)
        {
            var order = _orders[id];
            ServerSession? session = null;
            if (order.Row.SessionId is { } sid && _serverSessions.TryGetValue(sid, out var s) && s.LicenseId == licenseId)
                session = s;
            if (session?.ReleasedAt is not null) continue;
            if (command.Kind == "reprint" && session?.OwnerReplicaId == replicaId) continue;
            DateTimeOffset? lastSeen = session?.OwnerReplicaId is { } owner && _replicas.TryGetValue((licenseId, owner), out var r)
                ? r.LastSeenAt
                : null;
            return ProblemResponse(409, "session-live",
                "Bu yayın henüz sunucuya devredilmedi; yalnız onu açan bilgisayar değiştirebilir.",
                new Dictionary<string, object?>
                {
                    ["machine"] = session?.OwnerMachineName, ["ownerLastSeenAt"] = lastSeen, ["sessionId"] = session?.Id,
                });
        }
        return null;
    }

    /// <summary>
    /// <c>force-end</c> — <c>ForceEndCommandHandler</c>'ın aynası (§4.6, Y7, Y8, M18): yayın bu lisansta yoksa 400
    /// <c>unknown-session</c>; devredilmişse etkisiz başarı (<c>unchanged</c>, <c>alreadyReleased = true</c>; devir anı ve
    /// <c>ForceEndedBy</c> değişmez); devrediliyorsa (bitmiş, devredilmemiş) sahip ne kadar sessiz olursa olsun 409
    /// <c>handing-over</c> (sahibin bildirdiği gönderilmemiş etiket sayısı, makine ve son sinyal); sahipsiz (miras) canlı
    /// yayında 409 <c>owner-active</c> ve boş son sinyal; sahibin son kalp atışı <see cref="ForceEndSilenceMinutes"/>'ten
    /// yeniyse 409 <c>owner-active</c>. Kopya satırı hiç olmayan sahip sessiz sayılır. Etki: <c>EndedAt</c> = şimdi,
    /// <c>ForceEndedBy</c> = çağıran, devir (yayının siparişlerine <c>SessionReleasedAt</c>, yeni seq).
    /// </summary>
    private bool ForceEndSession(Guid licenseId, Guid replicaId, ParsedCommand command, DateTimeOffset now,
        out string status, out string resultJson, out HttpResponseMessage? rejected)
    {
        status = "";
        resultJson = "";
        rejected = null;
        var sessionId = command.SessionId!.Value;
        if (!_serverSessions.TryGetValue(sessionId, out var session) || session.LicenseId != licenseId)
        {
            rejected = InvalidCommand("unknown-session", "Yayın bu lisansta yok.",
                new Dictionary<string, object?> { ["sessionId"] = sessionId });
            return false;
        }

        DateTimeOffset? LastSeenOf(Guid? owner) =>
            owner is { } o && _replicas.TryGetValue((licenseId, o), out var r) ? r.LastSeenAt : null;

        if (session.ReleasedAt is not null)
        {
            status = "unchanged";
            resultJson = JsonSerializer.Serialize(new CommandResultBody([], [], [], []) { AlreadyReleased = true }, Web);
            return true;
        }
        if (session.EndedAt is not null)
        {
            rejected = ProblemResponse(409, "handing-over",
                "Yayın bitti ama etiketleri henüz gönderilmedi; yayın bilgisayarı bağlanınca yayın düzenlenebilir olur.",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId, ["unsentLabelCount"] = session.UnsentLabelCount,
                    ["machine"] = session.OwnerMachineName, ["ownerLastSeenAt"] = LastSeenOf(session.OwnerReplicaId),
                });
            return false;
        }
        if (session.OwnerReplicaId is not { } owner)
        {
            rejected = ProblemResponse(409, "owner-active", "Yayın bilgisayarı hâlâ sinyal veriyor; yayın buradan bitirilemez.",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId, ["machine"] = session.OwnerMachineName, ["lastSeenAt"] = null,
                });
            return false;
        }
        if (LastSeenOf(owner) is { } lastSeen && lastSeen > now - TimeSpan.FromMinutes(ForceEndSilenceMinutes))
        {
            rejected = ProblemResponse(409, "owner-active", "Yayın bilgisayarı hâlâ sinyal veriyor; yayın buradan bitirilemez.",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId, ["machine"] = session.OwnerMachineName, ["lastSeenAt"] = lastSeen,
                });
            return false;
        }

        var before = session.Stamp();
        session.EndedAt = now;
        session.ReleasedAt = now;
        session.ForceEndedBy = replicaId.ToString();
        CommitSession(session, before);
        status = "applied";
        resultJson = JsonSerializer.Serialize(new CommandResultBody([], [], [], []), Web);
        return true;
    }

    /// <summary>Yazan dört türün uygulaması (<c>cancel</c>, <c>uncancel</c>, <c>mark-printed</c>, <c>confirm-backup</c>).
    /// Ret (yalnız <c>confirm-backup</c>'ın K6'sı) hiçbir şey yazmaz.</summary>
    private bool TryApplyCommand(Guid licenseId, ParsedCommand command, DateTimeOffset now, out string resultJson,
        out HttpResponseMessage? rejected)
    {
        resultJson = "";
        rejected = null;
        var changed = new List<Guid>();
        var skipped = new List<Guid>();
        List<Guid>? marked = null, alreadyPrinted = null, cancelled = null;
        var writes = new List<ServerOrder>();
        switch (command.Kind)
        {
            case "cancel":
                foreach (var id in command.OrderIds)
                {
                    var o = _orders[id];
                    if (o.Row.CancelledAt is not null) { skipped.Add(id); continue; }
                    writes.Add(o with
                    {
                        Row = o.Row with { CancelledAt = now, CancelReason = command.Reason }, CancelSetAt = now,
                    });
                    changed.Add(id);
                }
                break;
            case "uncancel":
                foreach (var id in command.OrderIds)
                {
                    var o = _orders[id];
                    if (o.Row.CancelledAt is null) { skipped.Add(id); continue; }
                    writes.Add(o with { Row = o.Row with { CancelledAt = null, CancelReason = null }, CancelSetAt = now });
                    changed.Add(id);
                }
                break;
            case "mark-printed":
                marked = new List<Guid>();
                alreadyPrinted = new List<Guid>();
                cancelled = new List<Guid>();
                foreach (var id in command.OrderIds)
                {
                    var o = _orders[id];
                    if (o.Row.PrintedAt is not null) { alreadyPrinted.Add(id); continue; }
                    if (o.Row.CancelledAt is not null) { cancelled.Add(id); continue; }
                    writes.Add(o with
                    {
                        Row = o.Row with { PrintedAt = now, PrintBatchId = command.BatchId, PrintSeq = command.Seqs![id] },
                    });
                    marked.Add(id);
                }
                changed.AddRange(marked);
                skipped.AddRange(alreadyPrinted.Concat(cancelled));
                break;
            case "confirm-backup":
            {
                var id = command.OrderIds[0];
                var o = _orders[id];
                if (!o.Row.IsTentativeBackup) { skipped.Add(id); break; }
                var priceChanges = command.NewPrice is { } price && price != o.Row.Price;
                if (priceChanges && o.Row.ShipmentId is { } shipmentId && _shipments.TryGetValue(shipmentId, out var shipment)
                    && shipment.Row.Status == "shipped")
                {
                    rejected = ProblemResponse(409, "shipment-shipped", "Sipariş kargoya verilmiş bir dosyada; fiyatı değişmez.",
                        new Dictionary<string, object?>
                        {
                            ["orderId"] = id, ["shipmentId"] = shipmentId,
                            ["current"] = CurrentOrderItems(licenseId, command.OrderIds),
                        });
                    return false;
                }
                writes.Add(o with
                {
                    Row = o.Row with { IsTentativeBackup = false, Price = priceChanges ? command.NewPrice!.Value : o.Row.Price },
                    BackupSetAt = now,
                    PriceSetAt = priceChanges ? now : o.PriceSetAt,
                });
                changed.Add(id);
                break;
            }
        }

        var versions = new List<VersionBody>();
        foreach (var w in writes)
        {
            var row = PutOrder(licenseId, w with { Version = w.Version + 1, UpdatedAt = now });
            versions.Add(new VersionBody(row.Row.Id, row.Version));
        }
        // K7: iptal, geri alma ve yedek onayı bağlı türetilmiş dosyayı yeniden türetir (bağ kalır, §7.2); baskı tutara
        // girmez.
        var derived = command.Kind == "mark-printed"
            ? []
            : DeriveAffected(writes.Where(w => w.Row.ShipmentId is not null).Select(w => w.Row.ShipmentId!.Value), now);
        resultJson = JsonSerializer.Serialize(new CommandResultBody(changed, skipped, versions,
            derived.Select(d => new ShipmentAmountBody(d.Row.Id, d.Version, d.Row.CumulativeAmount)).ToList())
        {
            Marked = marked, AlreadyPrinted = alreadyPrinted, Cancelled = cancelled,
        }, Web);
        return true;
    }

    /// <summary><c>reprint</c>: yazma yok; basılmış ve iptal edilmemiş olanlar istenen sırayla, düzeltilmiş olanlar.</summary>
    private string Reprint(ParsedCommand command)
    {
        var reprint = command.OrderIds
            .Where(id => _orders[id].Row.PrintedAt is not null && _orders[id].Row.CancelledAt is null)
            .ToList();
        return JsonSerializer.Serialize(new CommandResultBody([], command.OrderIds, [], [])
        {
            ReprintIds = reprint,
            CorrectionIds = reprint.Where(id => _orders[id].Row.PriceCorrectedAt is not null).ToList(),
        }, Web);
    }

    /// <summary>409 <c>session-live</c>: sahip makine ve sahibin son kalp atışı.</summary>
    private HttpResponseMessage SessionLiveProblem(Guid licenseId, ServerSession? session)
    {
        DateTimeOffset? lastSeen = session?.OwnerReplicaId is { } owner && _replicas.TryGetValue((licenseId, owner), out var r)
            ? r.LastSeenAt
            : null;
        return ProblemResponse(409, "session-live",
            "Bu yayın henüz sunucuya devredilmedi; yalnız onu açan bilgisayar değiştirebilir.",
            new Dictionary<string, object?>
            {
                ["machine"] = session?.OwnerMachineName, ["ownerLastSeenAt"] = lastSeen, ["sessionId"] = session?.Id,
            });
    }

    /// <summary>
    /// <c>set-code-price</c> — <c>SetCodePriceCommandHandler</c>'ın aynası (§4.6): yayın bu lisansta (400
    /// <c>unknown-session</c>) ve devredilmiş (409 <c>session-live</c>) olmalı; seçilenler bu lisansta (400
    /// <c>unknown-order</c>). Planı sunucu kurar: yayının harf duyarsız (saklanan kod kırpılmadan) koda eşleşen
    /// siparişleri — kodsuz seçimde hiçbiri — ∪ seçilenler, ardından sınıflama: iptal edilmiş, kargo ücreti ve kargoya
    /// verilmiş dosyadaki sipariş planın dışında. Plan satırlarının yayını da devredilmiş olmalı; tavanı aşan plan 409
    /// <c>too-many-candidates</c>; plan (Id ve sürüm) <c>expected</c>'den farklıysa 409 <c>plan-stale</c> + güncel
    /// <c>plan</c> + <c>current</c> (plan ∪ komutun satırları), hiçbir şey yazılmaz. Etki fiyatı farklı plan satırında:
    /// <c>PaidPrice</c> boşsa ve sipariş ödenmişse (dosyaya bağlı ya da <c>paidFromBalanceIds</c>'te) eski fiyat;
    /// <c>Price</c>; <c>PriceCorrectedAt</c> yalnız basılmış satırda; <c>PriceSetAt</c>; sürüm +1. K7 türetmesi yok.
    /// </summary>
    private bool SetCodePrice(Guid licenseId, ParsedCommand command, List<(Guid OrderId, int Version)> expected,
        DateTimeOffset now, out string resultJson, out HttpResponseMessage? rejected)
    {
        resultJson = "";
        rejected = null;
        var sessionId = command.SessionId!.Value;
        if (!_serverSessions.TryGetValue(sessionId, out var session) || session.LicenseId != licenseId)
        {
            rejected = InvalidCommand("unknown-session", "Komutun yayını bu lisansta yok.",
                new Dictionary<string, object?> { ["sessionId"] = sessionId });
            return false;
        }
        if (session.ReleasedAt is null)
        {
            rejected = SessionLiveProblem(licenseId, session);
            return false;
        }
        var unknown = command.AlsoSelected!
            .Where(id => !_orders.TryGetValue(id, out var o) || o.LicenseId != licenseId)
            .ToList();
        if (unknown.Count > 0)
        {
            rejected = InvalidCommand("unknown-order", "Komutun siparişlerinden bazıları bu lisansta yok.",
                new Dictionary<string, object?> { ["orderIds"] = unknown });
            return false;
        }

        var codeOrders = command.Code is { } code
            ? _orders.Values
                .Where(o => o.LicenseId == licenseId && o.Row.SessionId == sessionId && o.Row.Code is not null
                            && string.Equals(o.Row.Code, code, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Row.Id)
            : [];
        var plan = codeOrders.Concat(command.AlsoSelected!).Distinct()
            .Select(id => _orders[id])
            .Where(o => o.Row.CancelledAt is null && !o.Row.IsShippingFee
                        && !(o.Row.ShipmentId is { } shipmentId && _shipments.TryGetValue(shipmentId, out var shipment)
                             && shipment.Row.Status == "shipped"))
            .OrderBy(o => o.Row.Id)
            .ToList();
        foreach (var order in plan)
        {
            ServerSession? orderSession = null;
            if (order.Row.SessionId is { } sid && _serverSessions.TryGetValue(sid, out var s) && s.LicenseId == licenseId)
                orderSession = s;
            if (orderSession?.ReleasedAt is not null) continue;
            rejected = SessionLiveProblem(licenseId, orderSession);
            return false;
        }
        if (plan.Count > MaxSetOrders)
        {
            rejected = ProblemResponse(409, "too-many-candidates",
                "Bu işlemin kapsadığı etiket sayısı tek komutun sınırını aşıyor.",
                new Dictionary<string, object?> { ["count"] = plan.Count, ["max"] = MaxSetOrders });
            return false;
        }
        var planSet = plan.Select(o => (o.Row.Id, o.Version)).ToHashSet();
        if (!planSet.SetEquals(expected))
        {
            rejected = ProblemResponse(409, "plan-stale", "Bu arada başka bir bilgisayarda değişti; plan yenilendi.",
                new Dictionary<string, object?>
                {
                    ["plan"] = plan.Select(o => new ExpectedBody(o.Row.Id, o.Version)).ToList(),
                    ["current"] = CurrentOrderItems(licenseId,
                        plan.Select(o => o.Row.Id).Concat(command.OrderIds).Distinct().ToList()),
                });
            return false;
        }

        var paidFromBalance = command.PaidFromBalance!.ToHashSet();
        var changed = new List<Guid>();
        var versions = new List<VersionBody>();
        foreach (var order in plan)
        {
            var newPrice = command.NewPrice!.Value;
            if (order.Row.Price == newPrice) continue;
            var paid = order.Row.PaidPrice is null
                       && (order.Row.ShipmentId is not null || paidFromBalance.Contains(order.Row.Id))
                ? order.Row.Price
                : order.Row.PaidPrice;
            var row = PutOrder(licenseId, order with
            {
                Row = order.Row with
                {
                    Price = newPrice,
                    PaidPrice = paid,
                    PriceCorrectedAt = order.Row.PrintedAt is not null ? now : order.Row.PriceCorrectedAt,
                },
                PriceSetAt = now,
                Version = order.Version + 1,
                UpdatedAt = now,
            });
            versions.Add(new VersionBody(row.Row.Id, row.Version));
            changed.Add(order.Row.Id);
        }
        // K7: fiyatı değişen siparişlerin bağlı türetilmiş dosyaları.
        var derived = DeriveAffected(
            changed.Select(id => _orders[id].Row.ShipmentId).OfType<Guid>(), now);
        resultJson = JsonSerializer.Serialize(
            new CommandResultBody(changed, command.OrderIds.Except(changed).ToList(), versions,
                derived.Select(d => new ShipmentAmountBody(d.Row.Id, d.Version, d.Row.CumulativeAmount)).ToList()), Web);
        return true;
    }

    /// <summary>
    /// <c>add-backup</c> — <c>AddBackupCommandHandler</c>'ın aynası (§4.6): ebeveyn bu lisansta (400
    /// <c>unknown-order</c>) ve devredilmiş bir yayında (409 <c>session-live</c>) olmalı; yeni Id sunucuda hiçbir
    /// lisansta olmamalı (409 <c>content-conflict</c>). Ekleme: ebeveynin yayını, müşteri asıl kayda çözülür,
    /// <c>IsTentativeBackup</c> ve <c>IsBackupPromoted</c>, <c>ParentOrderId</c>, <c>OriginReplicaId</c> = çağıran,
    /// <c>SessionReleasedAt</c> yayının devir anı, <c>AddedAt</c> sunucu saati, fiyat ve kod verilmediyse ebeveyninki;
    /// ürün ve varyant boş (§12). Birim damgası yok.
    /// </summary>
    private bool AddBackup(Guid licenseId, Guid replicaId, ParsedCommand command, DateTimeOffset now,
        out string resultJson, out HttpResponseMessage? rejected)
    {
        resultJson = "";
        rejected = null;
        var parentId = command.ParentOrderId!.Value;
        if (!_orders.TryGetValue(parentId, out var parent) || parent.LicenseId != licenseId)
        {
            rejected = InvalidCommand("unknown-order", "Komutun siparişlerinden bazıları bu lisansta yok.",
                new Dictionary<string, object?> { ["orderIds"] = new[] { parentId } });
            return false;
        }
        ServerSession? session = null;
        if (parent.Row.SessionId is { } sid && _serverSessions.TryGetValue(sid, out var s) && s.LicenseId == licenseId)
            session = s;
        if (session?.ReleasedAt is not { } releasedAt)
        {
            rejected = SessionLiveProblem(licenseId, session);
            return false;
        }
        var newId = command.OrderIds[0];
        if (_orders.ContainsKey(newId))
        {
            rejected = ProblemResponse(409, "content-conflict", "Bu sipariş Id'si sunucuda zaten kullanılıyor.",
                new Dictionary<string, object?> { ["orderId"] = newId });
            return false;
        }

        var customer = command.Customer!;
        var (customerId, _) = ResolveCustomer(customer.Id!);
        var row = new OrderBody(newId, session.Id, customerId, customer.Platform!, customer.Username!,
            customer.DisplayName, command.MessageText ?? "", command.Code ?? parent.Row.Code,
            command.NewPrice ?? parent.Row.Price, now, null, null, null, false, true, true, null, null, null, parentId,
            null, null, null, null, null);
        var inserted = PutOrder(licenseId,
            new ServerOrder(row, 1, replicaId, now) { SessionReleasedAt = releasedAt });
        resultJson = JsonSerializer.Serialize(
            new CommandResultBody([newId], [], [new VersionBody(newId, inserted.Version)], []), Web);
        return true;
    }

    // ── kargo komutları (W2-08) ────────────────────────────────────────────

    private static bool IsOpen(ServerShipment s) => s.Row.Status is "pending" or "held";

    /// <summary><c>LoadOpenShipmentsAsync</c> + <c>NewestFirst</c>: asıl müşterinin açık (Pending/Held) dosyaları, en
    /// yenisi başta (CreatedAt azalan, sonra "N" Id azalan, ordinal).</summary>
    private List<ServerShipment> OpenShipments(Guid licenseId, string customer) =>
        _shipments.Values
            .Where(s => s.LicenseId == licenseId && IsOpen(s) && ResolveCustomer(s.Row.CustomerId).Id == customer)
            .OrderByDescending(s => s.Row.CreatedAt)
            .ThenByDescending(s => s.Row.Id.ToString("N"), StringComparer.Ordinal)
            .ToList();

    /// <summary>DM7: dosyanın tutarı bağlı, iptal edilmemiş, geçici yedek olmayan siparişlerin fiyat toplamı; sürüm +1.</summary>
    private ServerShipment Derive(ServerShipment shipment, DateTimeOffset now)
    {
        var amount = _orders.Values
            .Where(o => o.LicenseId == shipment.LicenseId && o.Row.ShipmentId == shipment.Row.Id
                        && o.Row.CancelledAt is null && !o.Row.IsTentativeBackup)
            .Sum(o => o.Row.Price);
        return shipment with
        {
            Row = shipment.Row with { CumulativeAmount = amount }, Version = shipment.Version + 1, UpdatedAt = now,
        };
    }

    /// <summary>
    /// <c>attach-shipment</c> — <c>AttachShipmentCommandHandler</c>'ın aynası (§4.6, §7.1, M22). Müşteri asıl kayda
    /// çözülür. Adaylar: asıl müşterinin bağsız, iptal edilmemiş, geçici yedek olmayan, kargo bağı bilinen
    /// (<c>OriginReplicaId</c> dolu) ve görünür (yayını devredilmiş ya da çağıranın kendi yayını) siparişleri, Id
    /// sırasıyla. Tavanı aşan küme 409 <c>too-many-candidates</c>; küme (Id ve sürüm) <c>expected</c>'den farklıysa 409
    /// <c>stale</c> + <c>candidates</c> + <c>current</c> (adaylar ∪ beklenenler), hiçbir şey yazılmaz. Aday yoksa
    /// etkisiz (dosya açılmaz). Hedef en yeni türetilmiş Pending/Held dosya; yoksa <c>proposedShipmentId</c> ile açılır
    /// (Pending, <c>AmountDerivedAt</c>, kaynak çağıran; Id sunucuda varsa 409 <c>content-conflict</c>). Bağlama:
    /// <c>ShipmentId</c>, <c>ShipmentSetAt</c>, sürüm +1; hedef türetilir (DM7), <c>CommandedAt</c>. Sonuç:
    /// <c>shipmentId</c>, havuz (açık dosyaların toplamı) ve havuzun dosyaları.
    /// </summary>
    private bool AttachShipment(Guid licenseId, Guid replicaId, ParsedCommand command,
        List<(Guid OrderId, int Version)> expected, DateTimeOffset now, out string resultJson,
        out HttpResponseMessage? rejected)
    {
        resultJson = "";
        rejected = null;
        var (customer, _) = ResolveCustomer(command.CustomerId!);
        var open = OpenShipments(licenseId, customer);
        var candidates = _orders.Values
            .Where(o => o.LicenseId == licenseId && ResolveCustomer(o.Row.CustomerId).Id == customer
                        && o.Row.ShipmentId is null && o.Row.CancelledAt is null && !o.Row.IsTentativeBackup
                        && o.OriginReplicaId is not null
                        && o.Row.SessionId is { } sid && _serverSessions.TryGetValue(sid, out var session)
                        && session.LicenseId == licenseId
                        && (session.ReleasedAt is not null || session.OwnerReplicaId == replicaId))
            .OrderBy(o => o.Row.Id)
            .ToList();
        if (candidates.Count > MaxAttachCandidates)
        {
            rejected = ProblemResponse(409, "too-many-candidates",
                "Bu işlemin kapsadığı etiket sayısı tek komutun sınırını aşıyor.",
                new Dictionary<string, object?> { ["count"] = candidates.Count, ["max"] = MaxAttachCandidates });
            return false;
        }
        var expectedById = expected.ToDictionary(e => e.OrderId, e => e.Version);
        if (candidates.Count != expected.Count
            || candidates.Any(c => !expectedById.TryGetValue(c.Row.Id, out var v) || v != c.Version))
        {
            rejected = ProblemResponse(409, "stale",
                "Müşterinin bağlanmamış etiketleri bu arada değişti; güncel kümeyle yeniden deneyin.",
                new Dictionary<string, object?>
                {
                    ["candidates"] = candidates.Select(c => new ExpectedBody(c.Row.Id, c.Version)).ToList(),
                    ["current"] = CurrentOrderItems(licenseId,
                        candidates.Select(c => c.Row.Id).Concat(expected.Select(e => e.OrderId)).Distinct()),
                });
            return false;
        }

        ServerShipment? target = null;
        if (candidates.Count > 0)
        {
            target = open.FirstOrDefault(s => s.AmountDerivedAt is not null);
            if (target is null)
            {
                var proposed = command.ProposedShipmentId!.Value;
                if (_shipments.ContainsKey(proposed))
                {
                    rejected = ProblemResponse(409, "content-conflict", "Bu kargo dosyası Id'si sunucuda zaten kullanılıyor.",
                        new Dictionary<string, object?> { ["shipmentId"] = proposed });
                    return false;
                }
                target = PutShipment(licenseId,
                    new ServerShipment(new ShipmentBody(proposed, customer, "pending", 0m, now, null, null), 0, replicaId, now)
                    {
                        AmountDerivedAt = now,
                    });
                open.Insert(0, target);
            }

            var versions = new List<VersionBody>();
            foreach (var c in candidates)
            {
                var linked = PutOrder(licenseId, c with
                {
                    Row = c.Row with { ShipmentId = target.Row.Id }, ShipmentSetAt = now, Version = c.Version + 1,
                    UpdatedAt = now,
                });
                versions.Add(new VersionBody(linked.Row.Id, linked.Version));
            }
            target = PutShipment(licenseId, Derive(_shipments[target.Row.Id], now) with { CommandedAt = now });
            var pool = open.Select(s => _shipments[s.Row.Id]).Where(IsOpen).ToList();
            resultJson = JsonSerializer.Serialize(new CommandResultBody(candidates.Select(c => c.Row.Id).ToList(), [],
                versions, [new ShipmentAmountBody(target.Row.Id, target.Version, target.Row.CumulativeAmount)])
            {
                ShipmentId = target.Row.Id,
                Pool = pool.Sum(s => s.Row.CumulativeAmount),
                PoolShipmentIds = pool.Select(s => s.Row.Id).ToList(),
            }, Web);
            return true;
        }

        resultJson = JsonSerializer.Serialize(new CommandResultBody([], [], [], [])
        {
            Pool = open.Sum(s => s.Row.CumulativeAmount),
            PoolShipmentIds = open.Select(s => s.Row.Id).ToList(),
        }, Web);
        return true;
    }

    /// <summary>
    /// <c>shipment-decision</c> — <c>ShipmentDecisionCommandHandler</c>'ın aynası (§4.6, §7.2, §7.3, U12). Dosya bu
    /// lisansta (400 <c>unknown-shipment</c>), Shipped değil (409 <c>shipment-shipped</c> + <c>currentShipments</c>) ve
    /// sürümü <c>expectedVersion</c> (409 <c>stale</c> + <c>currentShipments</c>) olmalı. Hedef açıksa müşterinin öbür
    /// açık dosyaları da aynı karara uyar (U12). Kargoya verilen her türetilmiş dosyada bağlı ve şu anda iptal edilmiş
    /// siparişlerin bağı çözülür (<c>ShipmentId</c> boş, <c>ShipmentSetAt</c>, sürüm +1) ve tutar türetilip donar;
    /// kabul edilmiş (türetilmemiş) dosya saklanan tutarıyla, bağlarıyla donar. Her dosyada karar, <c>CommandedAt</c>,
    /// sürüm +1. Sonuç: çözülen siparişler ve <c>decided</c> (dosya, sürüm, tutar).
    /// </summary>
    private bool DecideShipment(Guid licenseId, ParsedCommand command, DateTimeOffset now, out string resultJson,
        out HttpResponseMessage? rejected)
    {
        resultJson = "";
        rejected = null;
        var id = command.ShipmentId!.Value;
        if (!_shipments.TryGetValue(id, out var target) || target.LicenseId != licenseId)
        {
            rejected = InvalidCommand("unknown-shipment", "Komutun kargo dosyası bu lisansta yok.",
                new Dictionary<string, object?> { ["shipmentId"] = id });
            return false;
        }
        if (target.Row.Status == "shipped" || target.Version != command.ExpectedVersion)
        {
            var shipped = target.Row.Status == "shipped";
            rejected = ProblemResponse(409, shipped ? "shipment-shipped" : "stale",
                shipped ? "Kargo dosyası zaten kargoya verilmiş; karar değişmez."
                    : "Kargo dosyası bu arada değişti; güncel sürümle yeniden deneyin.",
                new Dictionary<string, object?> { ["shipmentId"] = id, ["currentShipments"] = new[] { target.ToFeedItem() } });
            return false;
        }

        var decided = new List<Guid> { id };
        if (IsOpen(target))
            decided.AddRange(OpenShipments(licenseId, ResolveCustomer(target.Row.CustomerId).Id)
                .Select(s => s.Row.Id).Where(s => s != id));

        var unlinked = new List<VersionBody>();
        if (command.Decision == "ship-now")
        {
            foreach (var shipmentId in decided.Where(s => _shipments[s].AmountDerivedAt is not null))
            {
                foreach (var order in _orders.Values
                             .Where(o => o.LicenseId == licenseId && o.Row.ShipmentId == shipmentId
                                         && o.Row.CancelledAt is not null)
                             .OrderBy(o => o.Row.Id).ToList())
                {
                    var freed = PutOrder(licenseId, order with
                    {
                        Row = order.Row with { ShipmentId = null }, ShipmentSetAt = now, Version = order.Version + 1,
                        UpdatedAt = now,
                    });
                    unlinked.Add(new VersionBody(freed.Row.Id, freed.Version));
                }
                PutShipment(licenseId, Derive(_shipments[shipmentId], now));
            }
        }

        var amounts = new List<ShipmentAmountBody>();
        foreach (var shipmentId in decided)
        {
            var s = _shipments[shipmentId];
            var row = command.Decision switch
            {
                "ship-now" => s.Row with { Status = "shipped", ShippedAt = now },
                "hold" => s.Row with { Status = "held", HeldAt = s.Row.HeldAt ?? now },
                _ => s.Row with { Status = "recipientpays" },
            };
            var written = PutShipment(licenseId, s with
            {
                Row = row, Version = s.Version + 1, CommandedAt = now, UpdatedAt = now,
            });
            amounts.Add(new ShipmentAmountBody(written.Row.Id, written.Version, written.Row.CumulativeAmount));
        }

        resultJson = JsonSerializer.Serialize(new CommandResultBody(unlinked.Select(u => u.Id).ToList(), [],
            unlinked, [])
        {
            Decided = amounts,
        }, Web);
        return true;
    }

    /// <summary>Kargo komutunun başarı yanıtı (<c>SalesCommandService.SucceededAsync</c>): siparişler komutunkiler ve
    /// sonucun yazdıkları; dosyalar komutunki, türetilenler, havuz ve kararın dosyaları (güncel). Havuz yanıt anında
    /// güncel dosyalardan yeniden hesaplanır — yeniden oynatmada da.</summary>
    private HttpResponseMessage ShipmentCommandSucceeded(Guid licenseId, string status, string resultJson,
        ParsedCommand command)
    {
        var result = JsonSerializer.Deserialize<CommandResultBody>(resultJson, Web)!;
        if (result.PoolShipmentIds is { } poolIds)
        {
            var pool = poolIds.Where(id => _shipments.TryGetValue(id, out var s) && IsOpen(s))
                .Sum(id => _shipments[id].Row.CumulativeAmount);
            if (pool != result.Pool)
            {
                result = result with { Pool = pool };
                resultJson = JsonSerializer.Serialize(result, Web);
            }
        }
        var shipmentIds = (command.ShipmentId is { } own ? new[] { own } : [])
            .Concat(result.Shipments.Select(s => s.Id))
            .Concat(result.PoolShipmentIds ?? [])
            .Concat(result.Decided?.Select(d => d.Id) ?? [])
            .Distinct()
            .ToList();
        return CommandSucceeded(licenseId, status, resultJson, command.OrderIds.Concat(result.Changed).Distinct().ToList(),
            shipmentIds);
    }

    /// <summary>Başarı yanıtı: siparişler (istenen sırayla, güncel, asıl kayıt + KVKK maskesi), bağlı dosyaları ve
    /// verilen dosyalar, siparişlerin oturumları ve verilen oturumlar (<c>force-end</c>).</summary>
    private HttpResponseMessage CommandSucceeded(Guid licenseId, string status, string resultJson, IReadOnlyList<Guid> orderIds,
        IReadOnlyList<Guid>? shipmentIds = null, IReadOnlyList<Guid>? sessionIds = null)
    {
        var orders = orderIds.Where(id => _orders.TryGetValue(id, out var o) && o.LicenseId == licenseId)
            .Select(id => _orders[id]).ToList();
        var shipments = (shipmentIds ?? [])
            .Concat(orders.Where(o => o.Row.ShipmentId is not null).Select(o => o.Row.ShipmentId!.Value)).Distinct()
            .Where(id => _shipments.TryGetValue(id, out var s) && s.LicenseId == licenseId)
            .Select(id => _shipments[id].ToFeedItem()).ToList();
        var sessions = orders.Where(o => o.Row.SessionId is not null).Select(o => o.Row.SessionId!.Value)
            .Concat(sessionIds ?? []).Distinct()
            .Where(id => _serverSessions.TryGetValue(id, out var s) && s.LicenseId == licenseId)
            .Select(id => _serverSessions[id].ToFeedItem()).ToList();
        using var result = JsonDocument.Parse(resultJson);
        var response = new CommandResponseBody(status, CurrentOrderItems(licenseId, orderIds), shipments, sessions,
            result.RootElement.Clone());
        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(response, Web));
    }

    /// <summary>Siparişlerin güncel akış öğeleri (<c>current</c>, yanıtın <c>orders</c>'ı).</summary>
    private List<OrderItemBody> CurrentOrderItems(Guid licenseId, IEnumerable<Guid> orderIds) =>
        orderIds.Where(id => _orders.TryGetValue(id, out var o) && o.LicenseId == licenseId)
            .Select(id =>
            {
                var order = _orders[id];
                var (customerId, canonical) = ResolveCustomer(order.Row.CustomerId);
                return FeedOrderOf(order, customerId, canonical?.PurgedAt is not null);
            })
            .ToList();

    /// <summary><c>SalesCommandService.TryReadExpected</c>: Id dolu ve tekil, sürüm ≥ 0, en çok 500 (plan taşıyan türde
    /// 10.000).</summary>
    private static bool TryReadExpected(List<ExpectedBody?>? raw, int max,
        out List<(Guid OrderId, int Version)> expected, out string? error)
    {
        expected = new();
        error = null;
        if (raw is null) return true;
        if (raw.Count > max) { error = $"expected en çok {max} öğe taşır."; return false; }
        var seen = new HashSet<Guid>();
        foreach (var item in raw)
        {
            if (item?.OrderId is not { } id || id == Guid.Empty || item.Version is not { } version || version < 0)
            {
                error = "expected öğesi orderId ve 0 ya da daha büyük bir version taşımalı.";
                return false;
            }
            if (!seen.Add(id)) { error = "expected bir siparişi iki kez taşıyamaz."; return false; }
            expected.Add((id, version));
        }
        return true;
    }

    /// <summary>Türün yükü — işleyicilerin <c>TryParse</c>'ının aynası; kanonik biçim özet içindir (sıra ve yazım
    /// farkı aynı özeti verir).</summary>
    private static bool TryParseCommand(string? kind, JsonElement? payload, List<(Guid OrderId, int Version)> expected,
        out ParsedCommand command, out string? code, out string? error)
    {
        command = null!;
        code = "invalid-payload";
        error = null;
        if (kind is null || !KnownCommandKinds.Contains(kind))
        {
            code = "unknown-kind";
            error = $"Bu sahte sunucu '{kind}' türünü (henüz) tanımıyor.";
            return false;
        }
        if (payload is not { ValueKind: JsonValueKind.Object } p) { error = "payload bir nesne olmalı."; return false; }

        static bool Items(List<Guid>? raw, out List<Guid> items, out string? error)
        {
            items = new();
            error = null;
            if (raw is null || raw.Count == 0 || raw.Count > MaxCommandOrders || raw.Contains(Guid.Empty))
            {
                error = $"liste en az bir, en çok {MaxCommandOrders} dolu Id taşır.";
                return false;
            }
            items = raw.Distinct().Order().ToList();
            return true;
        }

        // SalesCommandPayload.TryReadOptionalIds: boş liste olabilir; en çok 500, boş Id yok.
        static bool OptionalIds(List<Guid>? raw, out List<Guid> ids, out string? error)
        {
            ids = new();
            error = null;
            if (raw is null || raw.Count == 0) return true;
            if (raw.Count > MaxCommandOrders || raw.Contains(Guid.Empty))
            {
                error = $"liste en çok {MaxCommandOrders} dolu Id taşır.";
                return false;
            }
            ids = raw.Distinct().Order().ToList();
            return true;
        }

        try
        {
            switch (kind)
            {
                case "cancel":
                {
                    var b = p.Deserialize<CancelBody>(Web);
                    if (!Items(b?.Items, out var items, out error)) return false;
                    if (string.IsNullOrWhiteSpace(b!.Reason) || b.Reason.Length > 500)
                    {
                        error = "reason boş olamaz, en çok 500 karakter.";
                        return false;
                    }
                    command = new ParsedCommand(kind, items, $"items={string.Join(",", items.Select(i => i.ToString("N")))};reason={b.Reason}")
                    {
                        Reason = b.Reason,
                    };
                    break;
                }
                case "uncancel":
                {
                    var b = p.Deserialize<ItemsBody>(Web);
                    if (!Items(b?.Items, out var items, out error)) return false;
                    command = new ParsedCommand(kind, items, $"items={string.Join(",", items.Select(i => i.ToString("N")))}");
                    break;
                }
                case "mark-printed":
                {
                    var b = p.Deserialize<MarkPrintedBody>(Web);
                    if (string.IsNullOrWhiteSpace(b?.BatchId) || b.BatchId.Length > PrintBatchIdMaxLength)
                    {
                        error = $"batchId boş olamaz, en çok {PrintBatchIdMaxLength} karakter.";
                        return false;
                    }
                    if (b.Items is null || b.Items.Count == 0 || b.Items.Count > MaxCommandOrders
                        || b.Items.Any(i => i?.OrderId is not { } id || id == Guid.Empty || i.Seq is not >= 1))
                    {
                        error = "items öğesi orderId ve 1 ya da daha büyük bir seq taşımalı.";
                        return false;
                    }
                    var seqs = new Dictionary<Guid, int>();
                    foreach (var i in b.Items)
                        if (!seqs.TryAdd(i!.OrderId!.Value, i.Seq!.Value)) { error = "items bir siparişi iki kez taşıyamaz."; return false; }
                    var items = seqs.Keys.Order().ToList();
                    command = new ParsedCommand(kind, items,
                        $"batch={b.BatchId};items={string.Join(",", items.Select(i => $"{i:N}:{seqs[i]}"))}")
                    {
                        BatchId = b.BatchId, Seqs = seqs,
                    };
                    break;
                }
                case "reprint":
                {
                    var b = p.Deserialize<ReprintBody>(Web);
                    if (b?.OrderIds is null || b.OrderIds.Count == 0 || b.OrderIds.Count > MaxCommandOrders
                        || b.OrderIds.Contains(Guid.Empty))
                    {
                        error = $"orderIds en az bir, en çok {MaxCommandOrders} dolu Id taşır.";
                        return false;
                    }
                    if (expected.Count > 0) { error = "reprint yazmaz; expected boş olmalı."; return false; }
                    var ids = b.OrderIds.Distinct().ToList();                    // kâğıt sırası: sıralanmaz
                    command = new ParsedCommand(kind, ids, $"orderIds={string.Join(",", ids.Select(i => i.ToString("N")))}");
                    return true;
                }
                case "confirm-backup":
                {
                    var b = p.Deserialize<ConfirmBackupBody>(Web);
                    if (b?.BackupOrderId is not { } id || id == Guid.Empty) { error = "backupOrderId boş olamaz."; return false; }
                    if (b.NewPrice is { } price && (price < 0m || decimal.Round(price, 2) != price))
                    {
                        error = "newPrice 0 ya da daha büyük, en çok iki ondalıklı bir tutar olmalı.";
                        return false;
                    }
                    var canonicalPrice = b.NewPrice is { } np
                        ? (decimal.Round(np, 2) + 0.00m).ToString("F2", CultureInfo.InvariantCulture)
                        : "null";
                    command = new ParsedCommand(kind, [id], $"backup={id:N};price={canonicalPrice}")
                    {
                        NewPrice = b.NewPrice is { } n ? decimal.Round(n, 2) : null,
                    };
                    break;
                }
                case "set-code-price":
                {
                    // SetCodePriceCommandHandler.TryParse; expected planın kendisidir (öğelerle eşleşmez).
                    var b = p.Deserialize<SetCodePriceBody>(Web);
                    if (b?.SessionId is not { } sessionId || sessionId == Guid.Empty) { error = "sessionId boş olamaz."; return false; }
                    string? trimmed = null;
                    if (b.Code is not null)
                    {
                        trimmed = b.Code.Trim();
                        if (trimmed.Length is 0 or > 64)
                        {
                            error = "code kodsuz seçimde null olmalı; değilse boş olmayan, en çok 64 karakter.";
                            return false;
                        }
                    }
                    if (!OptionalIds(b.AlsoSelectedIds, out var also, out error)) return false;
                    if (trimmed is null && also.Count == 0)
                    {
                        error = "code null ise alsoSelectedIds düzeltilecek siparişleri taşımalı.";
                        return false;
                    }
                    if (b.NewPrice is not { } price || price < 0m || decimal.Round(price, 2) != price)
                    {
                        error = "newPrice 0 ya da daha büyük, en çok iki ondalıklı bir tutar olmalı.";
                        return false;
                    }
                    if (!OptionalIds(b.PaidFromBalanceIds, out var paid, out error)) return false;
                    var newPrice = decimal.Round(price, 2);
                    command = new ParsedCommand(kind,
                        expected.Select(e => e.OrderId).Concat(also).Distinct().Order().ToList(),
                        $"session={sessionId:N};code={trimmed ?? "null"};also={string.Join(",", also.Select(i => i.ToString("N")))};"
                        + $"price={(newPrice + 0.00m).ToString("F2", CultureInfo.InvariantCulture)};"
                        + $"paid={string.Join(",", paid.Select(i => i.ToString("N")))}")
                    {
                        SessionId = sessionId, Code = trimmed, AlsoSelected = also, NewPrice = newPrice, PaidFromBalance = paid,
                    };
                    return true;
                }
                case "add-backup":
                {
                    // AddBackupCommandHandler.TryParse; expected boş olmalı (var olan satırı yazmaz).
                    var b = p.Deserialize<AddBackupBody>(Web);
                    if (b?.NewOrderId is not { } newId || newId == Guid.Empty
                        || b.ParentOrderId is not { } parentId || parentId == Guid.Empty)
                    {
                        error = "newOrderId ve parentOrderId boş olamaz.";
                        return false;
                    }
                    if (newId == parentId) { error = "newOrderId ebeveynin Id'si olamaz."; return false; }
                    if (b.Customer is not { } customer
                        || string.IsNullOrWhiteSpace(customer.Id) || customer.Id.Length > 64
                        || string.IsNullOrWhiteSpace(customer.Platform) || customer.Platform.Length > 32
                        || string.IsNullOrWhiteSpace(customer.Username) || customer.Username.Length > 200
                        || customer.DisplayName is { Length: > 200 })
                    {
                        error = "customer id, platform ve username taşımalı (sınırlar içinde).";
                        return false;
                    }
                    if (b.MessageText is { Length: > 2000 } || b.Code is { Length: > 64 })
                    {
                        error = "messageText en çok 2000, code en çok 64 karakter.";
                        return false;
                    }
                    if (b.Price is { } price && (price < 0m || decimal.Round(price, 2) != price))
                    {
                        error = "price 0 ya da daha büyük, en çok iki ondalıklı bir tutar olmalı.";
                        return false;
                    }
                    if (expected.Count > 0) { error = "add-backup var olan bir satırı yazmaz; expected boş olmalı."; return false; }
                    var canonicalPrice = b.Price is { } bp
                        ? (decimal.Round(bp, 2) + 0.00m).ToString("F2", CultureInfo.InvariantCulture)
                        : "null";
                    command = new ParsedCommand(kind, [newId],
                        $"new={newId:N};parent={parentId:N};customer={customer.Id}|{customer.Platform}|{customer.Username}|"
                        + $"{customer.DisplayName};text={b.MessageText ?? ""};price={canonicalPrice};code={b.Code ?? "null"}")
                    {
                        ParentOrderId = parentId, Customer = customer, MessageText = b.MessageText ?? "",
                        NewPrice = b.Price is { } np ? decimal.Round(np, 2) : null, Code = b.Code,
                    };
                    return true;
                }
                case "attach-shipment":
                {
                    // AttachShipmentCommandHandler.TryParse; expected aday kümesinin tamamıdır (öğelerle eşleşmez).
                    var b = p.Deserialize<AttachShipmentBody>(Web);
                    if (string.IsNullOrWhiteSpace(b?.CustomerId) || b.CustomerId.Length > 64)
                    {
                        error = "customerId boş olamaz, en çok 64 karakter.";
                        return false;
                    }
                    if (b.ProposedShipmentId is not { } proposed || proposed == Guid.Empty)
                    {
                        error = "proposedShipmentId boş olamaz.";
                        return false;
                    }
                    command = new ParsedCommand(kind, expected.Select(e => e.OrderId).Order().ToList(),
                        $"customer={b.CustomerId};proposed={proposed:N}")
                    {
                        CustomerId = b.CustomerId, ProposedShipmentId = proposed,
                    };
                    return true;
                }
                case "shipment-decision":
                {
                    // ShipmentDecisionCommandHandler.TryParse; dosyanın sürümü yükte, expected boş olmalı.
                    var b = p.Deserialize<ShipmentDecisionBody>(Web);
                    if (b?.ShipmentId is not { } shipmentId || shipmentId == Guid.Empty)
                    {
                        error = "shipmentId boş olamaz.";
                        return false;
                    }
                    if (b.Decision is not ("ship-now" or "hold" or "recipient-pays"))
                    {
                        error = "decision ship-now, hold ya da recipient-pays olmalı.";
                        return false;
                    }
                    if (b.ExpectedVersion is not { } expectedVersion || expectedVersion < 0)
                    {
                        error = "expectedVersion 0 ya da daha büyük olmalı.";
                        return false;
                    }
                    if (expected.Count > 0)
                    {
                        error = "shipment-decision sipariş sürümü taşımaz; expected boş olmalı.";
                        return false;
                    }
                    command = new ParsedCommand(kind, [],
                        $"shipment={shipmentId:N};decision={b.Decision};version={expectedVersion.ToString(CultureInfo.InvariantCulture)}")
                    {
                        ShipmentId = shipmentId, Decision = b.Decision, ExpectedVersion = expectedVersion,
                    };
                    return true;
                }
                case "force-end":
                {
                    // ForceEndCommandHandler.TryParse: onay her zaman true olmalı; sipariş sürümü taşımaz.
                    var b = p.Deserialize<ForceEndBody>(Web);
                    if (b?.SessionId is not { } sessionId || sessionId == Guid.Empty)
                    {
                        error = "sessionId boş olamaz.";
                        return false;
                    }
                    if (b.Confirm is not true)
                    {
                        error = "confirm true olmalı: yayını bitirmek operatörün onayını ister.";
                        return false;
                    }
                    if (expected.Count > 0)
                    {
                        error = "force-end sipariş sürümü taşımaz; expected boş olmalı.";
                        return false;
                    }
                    command = new ParsedCommand(kind, [], $"session={sessionId:N}") { SessionId = sessionId };
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            error = "payload okunamadı.";
            return false;
        }

        // İşleyicilerin TryMatchExpected'i: beklenen sürümler tam olarak öğeleri kapsamalı.
        if (expected.Count != command.OrderIds.Count || !expected.Select(e => e.OrderId).ToHashSet().SetEquals(command.OrderIds))
        {
            error = "expected her öğe için tam bir sürüm taşımalı (ne eksik ne fazla).";
            return false;
        }
        return true;
    }

    /// <summary><c>ClientVersion.IsAtLeast</c>: UA'daki "OrderDeck-WPF/x" alt sınırı karşılıyor mu; biri yoksa ya da
    /// ayrıştırılamıyorsa hayır (kapalı-hata). Aynı numarada ön sürüm eki yalnız aynı ekle geçer.</summary>
    private static bool ClientAtLeast(string? userAgent, string? min)
    {
        var m = Regex.Match(userAgent ?? "", @"\bOrderDeck-WPF/([0-9A-Za-z.\-]{1,32})");
        var client = ParseClientVersion(m.Success ? m.Groups[1].Value : null);
        var floor = ParseClientVersion(min);
        if (client is null || floor is null) return false;
        var byNumber = client.Value.Number.CompareTo(floor.Value.Number);
        if (byNumber != 0) return byNumber > 0;
        return client.Value.Pre is null || string.Equals(client.Value.Pre, floor.Value.Pre, StringComparison.OrdinalIgnoreCase);
    }

    private static (Version Number, string? Pre)? ParseClientVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        string? pre = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
            if (pre.Length == 0) return null;
        }
        if (!Version.TryParse(text, out var v)) return null;
        return (new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)), pre);
    }

    /// <summary>İstemci hatası sayılan 400 (başlıklı); <see cref="Faults"/>'a yazılır.</summary>
    private HttpResponseMessage InvalidCommand(string code, string error, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        _faults.Enqueue($"geçersiz komut ({code}): {error}");
        return ProblemResponse(400, code, error, extensions);
    }

    /// <summary>ASP.NET ProblemDetails biçimi: <c>type</c>, <c>title</c> (kod), <c>status</c>, <c>detail</c>, ek üyeler
    /// köke, <c>traceId</c>.</summary>
    private static HttpResponseMessage ProblemResponse(
        int status, string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://tools.ietf.org/html/rfc9110",
            ["title"] = code,
            ["status"] = status,
            ["detail"] = detail,
        };
        if (extensions is not null)
            foreach (var (key, value) in extensions)
                body[key] = value;
        body["traceId"] = "00-fake-trace";
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Web), System.Text.Encoding.UTF8,
                "application/problem+json"),
        };
    }

    // ── kayıtlar ve tel biçimi ─────────────────────────────────────────────

    internal sealed record HelloCall(Guid LicenseId, string RawJson)
    {
        public JsonElement Json
        {
            get
            {
                using var doc = JsonDocument.Parse(RawJson);
                return doc.RootElement.Clone();
            }
        }
        public HelloBody Body => JsonSerializer.Deserialize<HelloBody>(RawJson, Web)!;
    }

    internal sealed record StateCall(Guid LicenseId, string Query)
    {
        public string? ReplicaId => QueryValue(Query, "replicaId");
        public string? SeenMode => QueryValue(Query, "seenMode");
        public string? AfterSeq => QueryValue(Query, "afterSeq");
    }

    /// <summary>Sunucunun <c>WpfReplicas</c> satırının bu testlerin baktığı kısmı.</summary>
    internal sealed class Replica
    {
        public Guid ReplicaId;
        public DateTimeOffset FirstSeenAt;
        public DateTimeOffset LastSeenAt;
        public int Requests;
        public int SeenMode;

        /// <summary>Kopyanın kipi 1 bildirdiği ilk an; mod yeniden açılınca yeni dönemin ilk anı (<c>SeenModeOnAt</c>).
        /// Açılış penceresi (<see cref="IsOpeningWindow"/>) okur.</summary>
        public DateTimeOffset? SeenModeOnAt;
        public string? MachineName;
        public string? HardwareFingerprint;
        public bool HardwareFingerprintConflict;
        public int FeatureLevel;
        public Guid? PreviousReplicaId;
        public bool ReportsShipping;
        public decimal? ReportedThreshold;
        public decimal? ReportedFee;
        public bool BackfillCompleted;
        public bool PaymentHistoryCompleted;

        public Replica Clone() => (Replica)MemberwiseClone();
    }

    /// <summary><c>LicensesSalesController.HelloRequest</c>.</summary>
    internal sealed record HelloBody(
        Guid? ReplicaId, Guid? PreviousReplicaId, string? MachineName, string? HardwareFingerprint,
        int? Protocol, int? FeatureLevel, int? SeenMode, ReportedShippingBody? ReportedShipping,
        bool? BackfillCompleted, bool? PaymentHistoryCompleted);

    internal sealed record ReportedShippingBody(decimal? Threshold, decimal? Fee);

    /// <summary><c>LicensesSalesController.SalesStateDto</c>.</summary>
    internal sealed record StateBody(
        int Mode, DateTimeOffset? ModeChangedAt, string? MinClientVersion, IReadOnlyList<LiveSessionBody> LiveSessions,
        ShippingBody? Shipping, bool ScrubOrderTextOnPurge, int ForceEndSilenceMinutes,
        IReadOnlyList<LegacyMachineBody> LegacyMachines);

    internal sealed record LiveSessionBody(
        Guid Id, string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? OwnerMachineName,
        DateTimeOffset? OwnerLastSeenAt, string State, int? UnsentLabelCount);

    internal sealed record ShippingBody(int Version, decimal? FreeShippingThreshold, decimal? Fee);

    internal sealed record LegacyMachineBody(string? MachineName, string? AppVersion, DateTimeOffset LastSeenAt);

    // ── gönderim uçlarının kayıtları ve tel biçimi (W1-06) ─────────────────

    internal enum PushResponseKind
    {
        /// <summary>İsteğe göre: v2 biçimine öğe başına yanıt, v1'e bugünkü liste.</summary>
        Auto,
        /// <summary>Öğe başına yanıt (yalnız iç kullanım; Auto'nun v2 dalı).</summary>
        V2,
        /// <summary>Eski sunucu: bugünkü DTO listesi (durumsuz).</summary>
        V1List,
        /// <summary>Eski sunucu: boş liste.</summary>
        EmptyList,
    }

    /// <summary>Bir gönderim isteği: uç, lisans ve ham gövde.</summary>
    /// <param name="RecordId">Yolda taşınan kayıt Id'si (<c>payment-requests/{id}/finish</c>); öbür uçlarda null.</param>
    internal sealed record PushCall(string Endpoint, Guid LicenseId, string RawJson, Guid? RecordId = null)
    {
        public JsonElement Json
        {
            get
            {
                using var doc = JsonDocument.Parse(RawJson);
                return doc.RootElement.Clone();
            }
        }
        public OrdersBody Orders => JsonSerializer.Deserialize<OrdersBody>(RawJson, Web)!;
        public SessionsBody Sessions => JsonSerializer.Deserialize<SessionsBody>(RawJson, Web)!;
        public ShipmentsBody Shipments => JsonSerializer.Deserialize<ShipmentsBody>(RawJson, Web)!;
        public BackfillBody Backfill => JsonSerializer.Deserialize<BackfillBody>(RawJson, Web)!;
        public CommandBody Command => JsonSerializer.Deserialize<CommandBody>(RawJson, Web)!;
    }

    /// <summary><c>LicensesSalesController.BackfillRequest</c>.</summary>
    internal sealed record BackfillBody(
        List<SessionBody>? Sessions, List<OrderBody>? Orders, List<BackfillLinkBody>? Shipments, bool CatalogAware,
        Guid? ReplicaId, int? Protocol, int? SeenMode);

    /// <summary><c>LicensesSalesController.BackfillShipmentDto</c>.</summary>
    internal sealed record BackfillLinkBody(Guid Id, List<Guid>? LabelIds);

    /// <summary><c>LicensesSalesController.BackfillSessionResultDto</c>.</summary>
    internal sealed record BackfillSessionResultBody(
        Guid Id, Guid? OwnerReplicaId, DateTimeOffset? EndedAt, DateTimeOffset? ReleasedAt, string? Reason);

    /// <summary><c>LicensesSalesController.BackfillOrderResultDto</c>.</summary>
    internal sealed record BackfillOrderResultBody(Guid Id, int? Version, string Status, string? Reason);

    /// <summary><c>LicensesSalesController.BackfillDivergedDto</c>.</summary>
    internal sealed record BackfillDivergedBody(Guid Id, IReadOnlyList<string> Units, bool LocalWins);

    /// <summary><c>LicensesSalesController.BackfillResponse</c>.</summary>
    internal sealed record BackfillResponseBody(
        IReadOnlyList<BackfillSessionResultBody> Sessions, IReadOnlyList<BackfillOrderResultBody> Orders,
        IReadOnlyList<BackfillDivergedBody> Diverged);

    /// <summary><see cref="Hold"/>'un tuttuğu uç: <see cref="Arrived"/> ilk istek geldiğinde tamamlanır.</summary>
    internal sealed class PushHold
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        public void Release() => _released.TrySetResult();

        internal Task WaitAsync(CancellationToken ct)
        {
            _arrived.TrySetResult();
            return _released.Task.WaitAsync(ct);
        }
    }

    /// <param name="Status"><c>ignored</c> ya da <c>rejected</c>.</param>
    /// <param name="Units">Yok sayılan birimler (<c>price</c>, <c>cancel</c>, …).</param>
    /// <param name="Current">W1-08: sunucunun gerçeği — depodaki satırın akış öğesine uygulanır, sonucu yanıtın
    /// <c>current</c>'ı olur ve birim alanları depoya yazılır (sonraki turlar onu görür). Satır sunucuda yoksa önce
    /// push'un öğesiyle eklenir (eklemenin bir birimi yok sayıldı). Boşsa depodaki satır olduğu gibi döner.</param>
    /// <param name="WithoutCurrent">W1-08: reddedilen ekleme — sunucuda satır yok; yanıtta <c>current</c> ve sürüm
    /// boş, depoya hiçbir şey yazılmaz.</param>
    internal sealed record OrderVerdict(
        string Status, string? Reason, string[] Units,
        Func<OrderItemBody, OrderItemBody>? Current = null, bool WithoutCurrent = false);

    internal sealed record PushEnvelope(Guid ReplicaId, int SeenMode);

    /// <summary><c>LicensesSessionsSyncController.SyncOrdersRequest</c>.</summary>
    internal sealed record OrdersBody(
        List<OrderBody>? Orders, bool CatalogAware, Guid? ReplicaId, int? Protocol, int? SeenMode, int? Format);

    /// <summary><c>LicensesSessionsSyncController.SyncOrderItem</c>.</summary>
    internal sealed record OrderBody(
        Guid Id, Guid? SessionId, string CustomerId, string Platform, string Username, string? DisplayName,
        string MessageText, string? Code, decimal Price, DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, string? CancelReason, bool IsShippingFee, bool IsBackupPromoted,
        bool IsTentativeBackup, Guid? ProductId, Guid? ProductVariantId, int? BaseVersion, Guid? ParentOrderId,
        string? PrintBatchId, int? PrintSeq, DateTimeOffset? PriceCorrectedAt, decimal? PaidPrice, Guid? ShipmentId)
    {
        /// <summary>Birim alanları (spec §2) akış öğesinden; oluşum alanları değişmez.</summary>
        public OrderBody WithUnits(OrderItemBody item) => this with
        {
            Price = item.Price, PriceCorrectedAt = item.PriceCorrectedAt, PaidPrice = item.PaidPrice,
            CancelledAt = item.CancelledAt, CancelReason = item.CancelReason,
            IsTentativeBackup = item.IsTentativeBackup,
            ShipmentId = item.ShipmentId,
            PrintedAt = item.PrintedAt, PrintBatchId = item.PrintBatchId, PrintSeq = item.PrintSeq,
        };
    }

    /// <summary>Sunucudaki sipariş: son yazılan öğe, sürüm (<c>Order.SyncVersion</c>) ve kaynak kopya.</summary>
    internal sealed record ServerOrder(OrderBody Row, int Version, Guid? OriginReplicaId, DateTimeOffset UpdatedAt)
    {
        /// <summary>Satırın lisansı (eklemede yazılır).</summary>
        public Guid LicenseId { get; init; }

        /// <summary>rowversion (W2-03).</summary>
        public long ChangeSeq { get; init; }

        /// <summary>Akış süzgeci (DM3): devirde ya da devredilmiş oturuma eklemede dolar.</summary>
        public DateTimeOffset? SessionReleasedAt { get; init; }

        /// <summary>Birim damgaları (§2): yalnız komut basar (W2-04), sunucu saati; hiçbir yol silmez.</summary>
        public DateTimeOffset? PriceSetAt { get; init; }

        public DateTimeOffset? CancelSetAt { get; init; }

        public DateTimeOffset? BackupSetAt { get; init; }

        /// <summary>Kargo bağının damgası (W2-08): <c>attach-shipment</c> ve kargoya verirken bağı çözen
        /// <c>shipment-decision</c> basar.</summary>
        public DateTimeOffset? ShipmentSetAt { get; init; }

        /// <summary>Akış öğesi (<c>SalesOrderItem</c>); damgalar komutların bastığı.</summary>
        public OrderItemBody ToItem() => new(
            Row.Id, Row.SessionId, Row.CustomerId, Row.Platform, Row.Username, Row.DisplayName, Row.MessageText,
            Row.Code, Row.Price, Row.AddedAt, Row.PrintedAt, Row.CancelledAt, Row.CancelReason, Row.IsShippingFee,
            Row.IsBackupPromoted, Row.IsTentativeBackup, Row.ProductId, Row.ProductVariantId, UpdatedAt, Version,
            Row.ParentOrderId, Row.ShipmentId, OriginReplicaId, Row.PrintBatchId, Row.PrintSeq, Row.PriceCorrectedAt,
            Row.PaidPrice, null, PriceSetAt, CancelSetAt, BackupSetAt, ShipmentSetAt, null, Version);

        public V1OrderBody ToV1() => new(
            Row.Id, Row.SessionId, Row.CustomerId, Row.Platform, Row.Username, Row.DisplayName, Row.MessageText,
            Row.Code, Row.Price, Row.AddedAt, Row.PrintedAt, Row.CancelledAt, Row.CancelReason, Row.IsShippingFee,
            Row.IsBackupPromoted, Row.IsTentativeBackup, Row.ProductId, Row.ProductVariantId, UpdatedAt);
    }

    /// <summary><c>LicensesSessionsSyncController.SyncedOrderV2Dto</c>.</summary>
    internal sealed record OrderResultBody(
        Guid Id, int? Version, Guid? ShipmentId, DateTimeOffset? SessionReleasedAt, string Status,
        IReadOnlyList<string> IgnoredUnits, string? Reason, OrderItemBody? Current);

    /// <summary><c>SalesOrderItem</c> (sunucu, <c>Services/Sync/SalesFeedItems.cs</c>).</summary>
    internal sealed record OrderItemBody(
        Guid Id, Guid? SessionId, string CustomerId, string Platform, string Username, string? DisplayName,
        string MessageText, string? Code, decimal Price, DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, string? CancelReason, bool IsShippingFee, bool IsBackupPromoted,
        bool IsTentativeBackup, Guid? ProductId, Guid? ProductVariantId, DateTimeOffset UpdatedAt, int Version,
        Guid? ParentOrderId, Guid? ShipmentId, Guid? OriginReplicaId, string? PrintBatchId, int? PrintSeq,
        DateTimeOffset? PriceCorrectedAt, decimal? PaidPrice, DateTimeOffset? SessionReleasedAt,
        DateTimeOffset? PriceSetAt, DateTimeOffset? CancelSetAt, DateTimeOffset? BackupSetAt,
        DateTimeOffset? ShipmentSetAt, DateTimeOffset? TextScrubbedAt, long ChangeSeq);

    /// <summary><c>LicensesSessionsSyncController.SyncedOrderDto</c> (v1 yanıtı).</summary>
    internal sealed record V1OrderBody(
        Guid Id, Guid? SessionId, string CustomerId, string Platform, string Username, string? DisplayName,
        string MessageText, string? Code, decimal Price, DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, string? CancelReason, bool IsShippingFee, bool IsBackupPromoted,
        bool IsTentativeBackup, Guid? ProductId, Guid? ProductVariantId, DateTimeOffset UpdatedAt);

    /// <summary><c>LicensesSessionsSyncController.SyncSessionsRequest</c>.</summary>
    internal sealed record SessionsBody(List<SessionBody>? Sessions, Guid? ReplicaId, int? Protocol, int? SeenMode);

    /// <summary><c>LicensesSessionsSyncController.SyncSessionItem</c>.</summary>
    internal sealed record SessionBody(
        Guid Id, string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Platforms, string? Notes,
        bool Release, string? MachineName, int? UnsentLabelCount);

    /// <summary>Sunucunun <c>StreamSessions</c> satırının bu testlerin baktığı kısmı.</summary>
    private sealed class ServerSession
    {
        public Guid Id;
        public Guid LicenseId;
        public long ChangeSeq;
        public string? Title;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? EndedAt;
        public string Platforms = "";
        public string? Notes;
        public Guid? OwnerReplicaId;
        public string? OwnerMachineName;
        public DateTimeOffset? ReleasedAt;
        public int? UnsentLabelCount;
        public string? ForceEndedBy;

        public SessionResultBody ToResult(DateTimeOffset now, string? reason) => new(
            Id, Title, StartedAt, EndedAt, Platforms, Notes, now, OwnerReplicaId, ReleasedAt, 2,
            reason is null ? "applied" : "ignored", reason);

        public ServerSessionView View() => new(
            Id, Title, EndedAt, OwnerReplicaId, OwnerMachineName, ReleasedAt, UnsentLabelCount, ForceEndedBy);

        /// <summary>Satırın değeri (seq hariç): yazım bir şeyi değiştirdi mi.</summary>
        public SessionStamp Stamp() => new(
            Title, StartedAt, EndedAt, Platforms, Notes, OwnerReplicaId, OwnerMachineName, ReleasedAt, UnsentLabelCount,
            ForceEndedBy);

        /// <summary><c>SalesSessionItem.From</c>.</summary>
        public SessionItemBody ToFeedItem() => new(
            Id, Title, StartedAt, EndedAt, Platforms, Notes, ReleasedAt ?? StartedAt, OwnerReplicaId, OwnerMachineName,
            ForceEndedBy, ReleasedAt, UnsentLabelCount, ChangeSeq);
    }

    private sealed record SessionStamp(
        string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Platforms, string? Notes,
        Guid? OwnerReplicaId, string? OwnerMachineName, DateTimeOffset? ReleasedAt, int? UnsentLabelCount,
        string? ForceEndedBy);

    /// <summary>Sunucudaki <c>WpfCustomerProjection</c>'ın akışa giren kısmı.</summary>
    private sealed record CustomerProjection(
        Guid Id, string Platform, string Username, string? DisplayName, DateTimeOffset? PurgedAt, Guid? MergedInto);

    /// <summary><c>SalesSessionItem</c> (sunucu).</summary>
    internal sealed record SessionItemBody(
        Guid Id, string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Platforms, string? Notes,
        DateTimeOffset UpdatedAt, Guid? OwnerReplicaId, string? OwnerMachineName, string? ForceEndedBy,
        DateTimeOffset? ReleasedAt, int? UnsentLabelCount, long ChangeSeq);

    /// <summary><c>SalesCustomerDraft</c> (sunucu).</summary>
    internal sealed record CustomerDraftBody(
        string Id, string Platform, string Username, string? DisplayName, DateTimeOffset? PurgedAt);

    /// <summary><c>SalesChangeItem</c> (sunucu).</summary>
    internal sealed record ChangeItemBody(
        string Kind, long ChangeSeq, SessionItemBody? Session, OrderItemBody? Order, ShipmentItemBody? Shipment);

    /// <summary><c>SalesChangesPage</c> (sunucu).</summary>
    internal sealed record ChangesPageBody(
        IReadOnlyList<ChangeItemBody> Items, long NextAfterSeq, bool CursorReset, IReadOnlyList<SessionItemBody> SideSessions,
        IReadOnlyList<CustomerDraftBody> CustomerDrafts);

    internal sealed record ServerSessionView(
        Guid Id, string? Title, DateTimeOffset? EndedAt, Guid? OwnerReplicaId, string? OwnerMachineName,
        DateTimeOffset? ReleasedAt, int? UnsentLabelCount, string? ForceEndedBy);

    /// <summary><c>LicensesSessionsSyncController.SyncedSessionDto</c>.</summary>
    internal sealed record SessionResultBody(
        Guid Id, string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Platforms, string? Notes,
        DateTimeOffset UpdatedAt, Guid? OwnerReplicaId, DateTimeOffset? ReleasedAt, int Protocol, string Status,
        string? Reason);

    /// <summary>S1 öncesi sunucunun oturum yanıtı (yeni alan yok).</summary>
    internal sealed record V1SessionBody(
        Guid Id, string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Platforms, string? Notes,
        DateTimeOffset UpdatedAt);

    /// <summary><c>LicensesShipmentsSyncController.SyncRequest</c>.</summary>
    internal sealed record ShipmentsBody(List<ShipmentBody>? Shipments, Guid? ReplicaId, int? Protocol, int? SeenMode);

    /// <summary><c>LicensesShipmentsSyncController.SyncShipmentItem</c>.</summary>
    internal sealed record ShipmentBody(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount, DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt);

    internal sealed record ServerShipment(ShipmentBody Row, int Version, Guid? OriginReplicaId, DateTimeOffset UpdatedAt)
    {
        /// <summary>Satırın lisansı (eklemede yazılır).</summary>
        public Guid LicenseId { get; init; }

        /// <summary>rowversion (W2-03).</summary>
        public long ChangeSeq { get; init; }

        /// <summary>Doluysa tutar bağlı siparişlerden türetilir (DM7; W2-08: <c>attach-shipment</c>'ın açtığı dosya,
        /// <see cref="SeedShipment"/>'in <c>derived</c>'i).</summary>
        public DateTimeOffset? AmountDerivedAt { get; init; }

        /// <summary>Dosyaya sunucu komutu dokundu (W2-08).</summary>
        public DateTimeOffset? CommandedAt { get; init; }

        /// <summary><c>SalesShipmentItem.From</c> (akış).</summary>
        public ShipmentItemBody ToFeedItem() => new(
            Row.Id, Row.CustomerId, Row.Status, Row.CumulativeAmount, Row.CreatedAt, Row.HeldAt, Row.ShippedAt,
            UpdatedAt, Version, AmountDerivedAt, CommandedAt, OriginReplicaId, ChangeSeq);

        public ShipmentItemBody ToItem() => new(
            Row.Id, Row.CustomerId, Row.Status, Row.CumulativeAmount, Row.CreatedAt, Row.HeldAt, Row.ShippedAt,
            UpdatedAt, Version, AmountDerivedAt, CommandedAt, OriginReplicaId, Version);

        public V1ShipmentBody ToV1() => new(
            Row.Id, Row.CustomerId, Row.Status, Row.CumulativeAmount, Row.CreatedAt, Row.HeldAt, Row.ShippedAt,
            UpdatedAt);
    }

    /// <summary><c>SalesShipmentItem</c> (sunucu).</summary>
    internal sealed record ShipmentItemBody(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount, DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt, DateTimeOffset UpdatedAt, int Version,
        DateTimeOffset? AmountDerivedAt, DateTimeOffset? CommandedAt, Guid? OriginReplicaId, long ChangeSeq);

    /// <summary><c>LicensesShipmentsSyncController.SyncedShipmentDto</c> (v1 yanıtı).</summary>
    internal sealed record V1ShipmentBody(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount, DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt, DateTimeOffset UpdatedAt);

    // ── komutların kayıtları ve tel biçimi (W2-04) ─────────────────────────

    /// <summary><c>SalesCommandContracts.SalesCommandRequest</c>.</summary>
    internal sealed record CommandBody(
        Guid? CommandId, string? Kind, JsonElement? Payload, List<ExpectedBody?>? Expected, Guid? ReplicaId,
        int? Protocol, int? SeenMode);

    /// <summary><c>SalesCommandContracts.SalesCommandExpectedDto</c>.</summary>
    internal sealed record ExpectedBody(Guid? OrderId, int? Version);

    /// <summary><c>CancelCommandHandler.Body</c>.</summary>
    private sealed record CancelBody(List<Guid>? Items, string? Reason);

    /// <summary><c>UncancelCommandHandler.Body</c>.</summary>
    private sealed record ItemsBody(List<Guid>? Items);

    /// <summary><c>MarkPrintedCommandHandler.Body</c>.</summary>
    private sealed record MarkPrintedBody(string? BatchId, List<MarkPrintedItemBody?>? Items);

    private sealed record MarkPrintedItemBody(Guid? OrderId, int? Seq);

    /// <summary><c>ReprintCommandHandler.Body</c>.</summary>
    private sealed record ReprintBody(List<Guid>? OrderIds);

    /// <summary><c>ConfirmBackupCommandHandler.Body</c>.</summary>
    private sealed record ConfirmBackupBody(Guid? BackupOrderId, decimal? NewPrice);

    /// <summary><c>SetCodePriceCommandHandler.Body</c>.</summary>
    private sealed record SetCodePriceBody(
        Guid? SessionId, string? Code, List<Guid>? AlsoSelectedIds, decimal? NewPrice, List<Guid>? PaidFromBalanceIds);

    /// <summary><c>AddBackupCommandHandler.CustomerBody</c>.</summary>
    internal sealed record AddBackupCustomerBody(string? Id, string? Platform, string? Username, string? DisplayName);

    /// <summary><c>AddBackupCommandHandler.Body</c>.</summary>
    private sealed record AddBackupBody(
        Guid? NewOrderId, Guid? ParentOrderId, AddBackupCustomerBody? Customer, string? MessageText, decimal? Price,
        string? Code);

    /// <summary><c>AttachShipmentCommandHandler.Body</c>.</summary>
    private sealed record AttachShipmentBody(string? CustomerId, Guid? ProposedShipmentId);

    /// <summary><c>ShipmentDecisionCommandHandler.Body</c>.</summary>
    private sealed record ShipmentDecisionBody(Guid? ShipmentId, string? Decision, int? ExpectedVersion);

    /// <summary><c>ForceEndCommandHandler.Body</c>.</summary>
    private sealed record ForceEndBody(Guid? SessionId, bool? Confirm);

    /// <summary>Doğrulanmış komut: tür, siparişler, kanonik yük (özet için) ve türün alanları.</summary>
    private sealed record ParsedCommand(string Kind, IReadOnlyList<Guid> OrderIds, string Canonical)
    {
        /// <summary><c>attach-shipment</c>: müşteri (verildiği gibi; asıl kayda uygulamada çözülür) ve önerilen dosya.</summary>
        public string? CustomerId { get; init; }
        public Guid? ProposedShipmentId { get; init; }

        /// <summary><c>shipment-decision</c>: dosya, karar ve dosyanın beklenen sürümü.</summary>
        public Guid? ShipmentId { get; init; }
        public string? Decision { get; init; }
        public int? ExpectedVersion { get; init; }

        public string? Reason { get; init; }
        public string? BatchId { get; init; }
        public IReadOnlyDictionary<Guid, int>? Seqs { get; init; }

        /// <summary><c>confirm-backup</c> ve <c>set-code-price</c>'ın yeni fiyatı; <c>add-backup</c>'ın fiyatı.</summary>
        public decimal? NewPrice { get; init; }

        public Guid? SessionId { get; init; }

        /// <summary><c>set-code-price</c>'ın kırpılmış kodu; <c>add-backup</c>'ın kodu (verildiği gibi).</summary>
        public string? Code { get; init; }

        public IReadOnlyList<Guid>? AlsoSelected { get; init; }
        public IReadOnlyList<Guid>? PaidFromBalance { get; init; }
        public Guid? ParentOrderId { get; init; }
        public AddBackupCustomerBody? Customer { get; init; }
        public string? MessageText { get; init; }
    }

    /// <summary><c>SalesCommandLogs</c> satırı (kanonik içerik özeti dahil).</summary>
    internal sealed class CommandLogEntry
    {
        public Guid CommandId;
        public Guid LicenseId;
        public Guid ReplicaId;
        public string Kind = "";
        public string Hash = "";
        public string Status = "";
        public string ResultJson = "";
        public int Applications;
        public int Replays;

        public CommandLogEntry Clone() => (CommandLogEntry)MemberwiseClone();
    }

    /// <summary><c>SalesCommandResult</c> (türe özgü listeler yalnız o türde yazılır).</summary>
    internal sealed record CommandResultBody(
        IReadOnlyList<Guid> Changed, IReadOnlyList<Guid> Skipped, IReadOnlyList<VersionBody> Orders,
        IReadOnlyList<ShipmentAmountBody> Shipments)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Guid? ShipmentId { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public decimal? Pool { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<Guid>? PoolShipmentIds { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<ShipmentAmountBody>? Decided { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<Guid>? Marked { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<Guid>? AlreadyPrinted { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<Guid>? Cancelled { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<Guid>? ReprintIds { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<Guid>? CorrectionIds { get; init; }

        /// <summary><c>force-end</c>: yayın zaten devredilmişti; komut etkisiz.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? AlreadyReleased { get; init; }
    }

    /// <summary><c>SalesCommandOrderVersion</c>.</summary>
    internal sealed record VersionBody(Guid Id, int Version);

    /// <summary><c>SalesCommandShipmentAmount</c>.</summary>
    internal sealed record ShipmentAmountBody(Guid Id, int Version, decimal Amount);

    /// <summary><c>SalesCommandContracts.SalesCommandResponse</c>.</summary>
    internal sealed record CommandResponseBody(
        string Status, IReadOnlyList<OrderItemBody> Orders, IReadOnlyList<ShipmentItemBody> Shipments,
        IReadOnlyList<SessionItemBody> Sessions, JsonElement Result);
}
