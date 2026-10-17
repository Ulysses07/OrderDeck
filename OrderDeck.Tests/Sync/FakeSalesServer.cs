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
/// </summary>
internal sealed class FakeSalesServer : HttpMessageHandler
{
    public const string Hello = "hello";
    public const string State = "state";

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
        try
        {
            if (path == "/api/v1/me/licenses" && req.Method == HttpMethod.Get)
                return FakeHttpMessageHandler.Json(200,
                    $"[{{\"id\":\"{LicenseId}\",\"licenseKey\":\"{Lisans}\"}},"
                  + $"{{\"id\":\"{SecondLicenseId}\",\"licenseKey\":\"{SecondLisans}\"}}]");

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

    /// <summary><c>SalesEnvelope.TryRead</c>'in aynası (yalnız v2 dalı; protokolsüz uçlar W1-06'da).</summary>
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
}
