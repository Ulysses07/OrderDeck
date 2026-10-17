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
/// <see cref="OrderVerdicts"/> bir siparişi yok sayılmış ya da reddedilmiş döndürür; <see cref="OrdersResponse"/>
/// eski sunucunun yanıtını (v1 listesi ya da <c>[]</c>) taklit eder. <see cref="Hold"/> bir ucu bırakılana kadar
/// bekletir, <see cref="DuringPush"/> istek "işlenirken" çalışır (uçuştaki yerel değişiklik), <see cref="Offline"/>
/// her isteği ağ hatasına çevirir; <see cref="MaxInFlight"/> eşzamanlı istek sayısının tepesini verir.</para>
/// </summary>
internal sealed class FakeSalesServer : HttpMessageHandler
{
    public const string Hello = "hello";
    public const string State = "state";
    public const string SessionsSync = "sessions/sync";
    public const string OrdersSync = "orders/sync";
    public const string ShipmentsSync = "shipments/sync";

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
                    if (current is not null && verdict.Status == "ignored")
                        _orders[order.Id] = current = current with { Version = current.Version + 1, UpdatedAt = now };
                    results.Add(new OrderResultBody(order.Id, current?.Version, current?.Row.ShipmentId, null,
                        verdict.Status, verdict.Units, verdict.Reason, current?.ToItem()));
                    if (current is not null) echoed.Add(current);
                    continue;
                }

                var row = current is null
                    ? new ServerOrder(v2 ? order : WithoutV2Fields(order), 0, v2 ? envelope!.ReplicaId : null, now)
                    : current with
                    {
                        Row = v2 ? order : order with
                        {
                            ParentOrderId = current.Row.ParentOrderId, PrintBatchId = current.Row.PrintBatchId,
                            PrintSeq = current.Row.PrintSeq, PriceCorrectedAt = current.Row.PriceCorrectedAt,
                            PaidPrice = current.Row.PaidPrice, ShipmentId = current.Row.ShipmentId,
                        },
                        Version = current.Version + 1,
                        UpdatedAt = now,
                    };
                _orders[order.Id] = row;
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

    /// <summary><c>sessions/sync</c> — W1-06'da yalnız kayıt ve bugünkü (v1) devir yolu: EndedAt tekdüze, EndedAt
    /// dolu gelince devredilir.</summary>
    private HttpResponseMessage HandleSessions(Guid licenseId, string raw)
    {
        var body = JsonSerializer.Deserialize<SessionsBody>(raw, Web);
        if (body?.Sessions is null) return Invalid("sessions/sync gövdesi yok");
        if (!TryReadEnvelope(body.ReplicaId, body.Protocol, body.SeenMode, out var envelope, out var error))
            return Invalid(error!);

        var now = DateTimeOffset.UtcNow;
        var results = new List<SessionResultBody>();
        lock (_gate)
        {
            if (envelope is { } e) Touch(licenseId, e.ReplicaId, e.SeenMode);
            foreach (var s in body.Sessions)
            {
                _serverSessions.TryGetValue(s.Id, out var current);
                var endedAt = current?.Row.EndedAt ?? s.EndedAt;
                var releasedAt = current?.ReleasedAt ?? (endedAt is not null ? now : null);
                _serverSessions[s.Id] = new ServerSession(s with { EndedAt = endedAt }, releasedAt);
                results.Add(new SessionResultBody(s.Id, s.Title, s.StartedAt, endedAt, s.Platforms, s.Notes, now,
                    null, releasedAt, 2, "applied", null));
            }
        }
        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(results, Web));
    }
    private readonly Dictionary<Guid, ServerSession> _serverSessions = new();

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
                _shipments[s.Id] = row;
                rows.Add(row);
            }
        }

        var v2 = envelope is not null && ShipmentsResponse != PushResponseKind.V1List;
        return FakeHttpMessageHandler.Json(200, v2
            ? JsonSerializer.Serialize(rows.Select(r => r.ToItem()).ToList(), Web)
            : JsonSerializer.Serialize(rows.Select(r => r.ToV1()).ToList(), Web));
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
    }

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
    internal sealed record OrderVerdict(string Status, string? Reason, string[] Units);

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
        string? PrintBatchId, int? PrintSeq, DateTimeOffset? PriceCorrectedAt, decimal? PaidPrice, Guid? ShipmentId);

    /// <summary>Sunucudaki sipariş: son yazılan öğe, sürüm (<c>Order.SyncVersion</c>) ve kaynak kopya.</summary>
    internal sealed record ServerOrder(OrderBody Row, int Version, Guid? OriginReplicaId, DateTimeOffset UpdatedAt)
    {
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

    internal sealed record ServerSession(SessionBody Row, DateTimeOffset? ReleasedAt);

    /// <summary><c>LicensesSessionsSyncController.SyncedSessionDto</c>.</summary>
    internal sealed record SessionResultBody(
        Guid Id, string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Platforms, string? Notes,
        DateTimeOffset UpdatedAt, Guid? OwnerReplicaId, DateTimeOffset? ReleasedAt, int Protocol, string Status,
        string? Reason);

    /// <summary><c>LicensesShipmentsSyncController.SyncRequest</c>.</summary>
    internal sealed record ShipmentsBody(List<ShipmentBody>? Shipments, Guid? ReplicaId, int? Protocol, int? SeenMode);

    /// <summary><c>LicensesShipmentsSyncController.SyncShipmentItem</c>.</summary>
    internal sealed record ShipmentBody(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount, DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt);

    internal sealed record ServerShipment(ShipmentBody Row, int Version, Guid? OriginReplicaId, DateTimeOffset UpdatedAt)
    {
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
