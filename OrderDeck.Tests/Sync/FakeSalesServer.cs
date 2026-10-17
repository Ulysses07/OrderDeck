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
/// doğrulaması sunucudaki <c>SalesEnvelope.TryRead</c> ve <c>TryReadFormat</c>'in aynası; sipariş deposu mod Off'un
/// damgasız kuralıyla yazar (ekleme sürüm 0, her işlenen satırda +1 — <c>Order.SyncVersion</c>).
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

    /// <summary><c>sales/backfill</c>'in uç adı: <see cref="EnqueueStatus"/> ve <see cref="EnqueueProblem"/> için.</summary>
    public const string Backfill = "backfill";

    /// <summary><c>sales/backfill</c>: <see cref="Hold"/>, <see cref="DuringPush"/> ve <see cref="BackfillCalls"/> için.</summary>
    public const string SalesBackfill = "sales/backfill";

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

    /// <summary><c>/sales/</c> altına gelen bütün istekler (kasıtlı arızalar dahil).</summary>
    public int SalesRequestCount { get { lock (_gate) return _salesRequests; } }
    private int _salesRequests;

    public Replica? ReplicaRow(Guid licenseId, Guid replicaId)
    {
        lock (_gate) return _replicas.TryGetValue((licenseId, replicaId), out var r) ? r.Clone() : null;
    }

    /// <summary><c>me/licenses</c> istekleri (lisans çözümü).</summary>
    public int LicenseRequestCount { get { lock (_gate) return _licenseRequests; } }
    private int _licenseRequests;

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

    /// <summary>HttpClient zaman aşımı: iptal edilmemiş belirteçle <see cref="TaskCanceledException"/>.</summary>
    public void EnqueueTimeout(string endpoint)
    {
        lock (_gate) Script(endpoint).Enqueue(() => throw new TaskCanceledException("fake timeout"));
    }

    private Queue<Func<HttpResponseMessage>> Script(string endpoint) =>
        _scripted.TryGetValue(endpoint, out var q) ? q : _scripted[endpoint] = new Queue<Func<HttpResponseMessage>>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (Offline) throw new HttpRequestException("fake offline");
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

            // ShipmentSyncService'in ters çekmesi (uygulanmıyor, yalnız imleç): sunucuda başka yazıcı yok.
            if (Regex.IsMatch(path, @"^/api/v1/licenses/[0-9a-fA-F-]{36}/shipments/since$") && req.Method == HttpMethod.Get)
                return FakeHttpMessageHandler.Json(200, "[]");

            // Satış akışı turunun ön gönderimi (W2-03): müşteri projeksiyonları. Birleştirme kuralı taklit edilmez
            // (FakeCustomerServer'ın işi); yalnız projeksiyon kaydı — akışın müşteri taslağı onu çözer.
            var customers = Regex.Match(path, @"^/api/v1/licenses/([0-9a-fA-F-]{36})/wpf-customers/sync$");
            if (customers.Success && req.Method == HttpMethod.Post)
                return await HandleCustomerPushAsync(req, Guid.Parse(customers.Groups[1].Value), ct);

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
            state = new StateBody(Mode, ModeChangedAt, MinClientVersion, LiveSessions.ToList(), Shipping,
                ScrubOrderTextOnPurge, ForceEndSilenceMinutes, LegacyMachines.ToList());
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

    /// <summary><c>orders/sync</c> — zarf ve biçim kuralı sunucunun aynası; birimler mod Off'un damgasız kuralıyla
    /// (son gelen kazanır), v1'de v2'ye özgü alanlar "gönderilmedi" sayılır ve korunur.</summary>
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
            if (envelope is { } e) Touch(licenseId, e.ReplicaId, e.SeenMode);
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

                var row = current is null
                    ? new ServerOrder(v2 ? order : WithoutV2Fields(order), 0, v2 ? envelope!.ReplicaId : null, now)
                    : current with
                    {
                        // K4 mod On: v2 push'u kargo bağını almaz, bağ sunucunundur (applied yanıtı onu taşır).
                        Row = v2 ? (Mode == 1 ? order with { ShipmentId = current.Row.ShipmentId } : order) : order with
                        {
                            ParentOrderId = current.Row.ParentOrderId, PrintBatchId = current.Row.PrintBatchId,
                            PrintSeq = current.Row.PrintSeq, PriceCorrectedAt = current.Row.PriceCorrectedAt,
                            PaidPrice = current.Row.PaidPrice, ShipmentId = current.Row.ShipmentId,
                        },
                        Version = current.Version + 1,
                        UpdatedAt = now,
                    };
                row = PutOrder(licenseId, row);
                echoed.Add(row);
                results.Add(new OrderResultBody(order.Id, row.Version, row.Row.ShipmentId, null, "applied",
                    Array.Empty<string>(), null, null));
            }
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
    /// Yanıt v2'ye akış öğesi, v1'e bugünkü liste.</summary>
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
            if (envelope is { } e) Touch(licenseId, e.ReplicaId, e.SeenMode);
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
    public void SeedShipment(ShipmentBody row, Guid? origin = null, Guid? licenseId = null)
    {
        lock (_gate) PutShipment(licenseId ?? LicenseId, new ServerShipment(row, 0, origin, DateTimeOffset.UtcNow));
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
            r.PaidPrice, o.SessionReleasedAt, null, null, null, null, null, o.ChangeSeq);
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

    private Replica Touch(Guid licenseId, Guid replicaId, int seenMode)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_replicas.TryGetValue((licenseId, replicaId), out var row))
            _replicas[(licenseId, replicaId)] = row = new Replica { ReplicaId = replicaId, FirstSeenAt = now };
        row.LastSeenAt = now;
        row.SeenMode = seenMode;
        row.Requests++;
        return row;
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
    }

    /// <summary>Sunucunun <c>WpfReplicas</c> satırının bu testlerin baktığı kısmı.</summary>
    internal sealed class Replica
    {
        public Guid ReplicaId;
        public DateTimeOffset FirstSeenAt;
        public DateTimeOffset LastSeenAt;
        public int Requests;
        public int SeenMode;
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
    internal sealed record PushCall(string Endpoint, Guid LicenseId, string RawJson)
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

        /// <summary>Akış öğesi (<c>SalesOrderItem</c>); damga yok (mod Off, komut yok).</summary>
        public OrderItemBody ToItem() => new(
            Row.Id, Row.SessionId, Row.CustomerId, Row.Platform, Row.Username, Row.DisplayName, Row.MessageText,
            Row.Code, Row.Price, Row.AddedAt, Row.PrintedAt, Row.CancelledAt, Row.CancelReason, Row.IsShippingFee,
            Row.IsBackupPromoted, Row.IsTentativeBackup, Row.ProductId, Row.ProductVariantId, UpdatedAt, Version,
            Row.ParentOrderId, Row.ShipmentId, OriginReplicaId, Row.PrintBatchId, Row.PrintSeq, Row.PriceCorrectedAt,
            Row.PaidPrice, null, null, null, null, null, null, Version);

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

        /// <summary><c>SalesShipmentItem.From</c> (akış).</summary>
        public ShipmentItemBody ToFeedItem() => new(
            Row.Id, Row.CustomerId, Row.Status, Row.CumulativeAmount, Row.CreatedAt, Row.HeldAt, Row.ShippedAt,
            UpdatedAt, Version, null, null, OriginReplicaId, ChangeSeq);

        public ShipmentItemBody ToItem() => new(
            Row.Id, Row.CustomerId, Row.Status, Row.CumulativeAmount, Row.CreatedAt, Row.HeldAt, Row.ShippedAt,
            UpdatedAt, Version, null, null, OriginReplicaId, Version);

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
}
