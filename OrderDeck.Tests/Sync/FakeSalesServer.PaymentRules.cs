using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sahte sunucunun ödeme isteği kuralları (W3-12; spec 2026-10-13 §4.7, §4.8, §6.2, §6.4 a–h, §6.6 ve PR #523'ün onay
/// özeti) — <c>PaymentRequestLeaseService</c>, <c>PaymentRequestRecordService</c>, <c>LicensesShippingSettingsController</c>
/// ve bakiye apply'ının kapsam kuralının BAĞIMSIZ ikizi: istemcinin DTO'larıyla değil JSON'un kendisiyle konuşur, kuralları
/// kendi kopyasıyla uygular (sunucunun kodu çağrılmaz).
///
/// <para><b>Açma:</b> <see cref="LeaseRules"/>. Kapalıyken (varsayılan) W3-05…W3-11'in betikli testlerinin hoşgörülü yolu
/// sürer: <c>begin</c> yalnız (e)'yi uygular, <c>finish</c> sonucu yankılar, geri doldurma her öğeye <c>applied</c> der.
/// Açıkken:</para>
/// <list type="bullet">
///   <item><c>begin</c>: zarf ve alanlar (400), kalp atışı, mod Off 409 <c>mode-off</c>, alt sürüm 409
///   <c>client-update-required</c>, (a) yeniden oynatma / <c>content-conflict</c>, (b) kişi — GUID'li üye asıl kayda (en
///   çok üç adım), PersonKey <c>g:</c>/<c>c:</c>/<c>r:</c>, sunucu grubu (<see cref="SetServerGroup"/> ya da müşteri
///   gönderiminin <c>groupId</c>'si) istemcinin üyelerinde değilse 409 <c>members-stale {members}</c>, (c) süresi dolmuş
///   ve bu kopyanın önceki <c>Sending</c>'i <c>Unconfirmed</c>, (d) başka kopyanın canlı kirası 409
///   <c>in-progress-elsewhere {machine, since, recordId}</c>, (e) onaylanmamış öncül 409
///   <c>already-requested {priors, priorsDigest}</c> (Id listesi "ya da" özet; 500'ü aşan liste 400
///   <c>invalid-confirmed-priors</c>, bozuk özet 400 <c>invalid-priors-digest</c>), (f) sunucu toplamı küme
///   karşılaştırmasıyla 409 <c>stale-total {serverTotal, liveElsewhere}</c>, (g) kargo ayarı 409
///   <c>settings-stale {current}</c>, (h) kira 120 sn (<see cref="PaymentClock"/>).</item>
///   <item><c>finish</c>: kayıt yok 404 <c>record-not-found</c>; kirasız kayıt ve soy dışı kopya 409
///   <c>content-conflict</c>; aynı sonuç idempotent; geç <c>delivered</c> Unconfirmed'ı yükseltir; son hâlde başka sonuç
///   409.</item>
///   <item>Liste: kapsam (ordinal) × üye kesişimi, vazgeçilmiş girmez, süresi dolmuş kira yazılmadan <c>unconfirmed</c>.</item>
///   <item>Geri doldurma: <c>applied</c> / <c>unchanged</c> (yeni üyeler eklenir) / <c>content-conflict</c>.</item>
///   <item>Bakiye: defter (<see cref="BalanceTransactions"/>); apply mod On ∧ kapsam dolu ∧ yeni anahtarda §6.6
///   kapsam kuralı — kapsamda geri alınmamış düşüm 409 <c>scope-has-deduction</c>, betiklenebilir kilit zaman aşımı 409
///   <c>stale</c> (<see cref="ApplyLockTimeouts"/>); <c>reverse</c> ve işlem durumu.</item>
/// </list>
/// <para><c>shipping-settings</c> GET/PUT kipten bağımsız her zaman açıktır (yeni uç): <see cref="Shipping"/> lisans
/// geneli satırdır (boşsa sürüm 0); PUT mod Off'ta 409 <c>mode-off</c>, eski sürümde 409 <c>stale {current}</c>.</para>
/// </summary>
internal sealed partial class FakeSalesServer
{
    /// <summary>Kira süresi (§6.4-h).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);

    /// <summary>Sunucunun <c>begin</c>'deki <c>confirmedPriorIds</c> tavanı.</summary>
    private const int MaxConfirmedPriors = 500;

    private const int MaxPaymentMembers = 100;
    private const int MaxPaymentOrders = 10_000;
    private const int MaxPaymentListRecords = 200;
    private const int MaxPaymentBackfillBatch = 200;
    private const int MaxMemberIdLength = 64;
    private const int MaxScopeKeyLength = 128;
    private const decimal MaxMoney = 9_999_999_999_999_999.99m;

    /// <summary><c>shipping-settings</c> (GET ve PUT): <see cref="Calls"/> ve <see cref="EnqueueStatus"/> için.</summary>
    public const string ShippingSettingsEndpoint = "shipping-settings";

    /// <summary><c>customer-balance/transactions/{id}/reverse</c> ve <c>transactions/{id}</c> (yalnız
    /// <see cref="LeaseRules"/>).</summary>
    public const string BalanceReverseEndpoint = "customer-balance/reverse";
    public const string BalanceTransactionEndpoint = "customer-balance/transaction";

    /// <summary>Ödeme isteği kurallarını uygula (yukarıdaki liste). Kapalıyken betikli testlerin hoşgörülü yolu.</summary>
    public bool LeaseRules { get; set; }

    /// <summary>Kuralların saati (kira başlangıcı, sonu, kapanış anı); testler ileri alır.</summary>
    public Func<DateTimeOffset> PaymentClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Sıradaki bu kadar kapsamlı ve yeni anahtarlı apply, kilit zaman aşımı gibi 409 <c>stale</c> alır (§6.6;
    /// hiçbir şey yazılmaz).</summary>
    public int ApplyLockTimeouts
    {
        get { lock (_gate) return _applyLockTimeouts; }
        set { lock (_gate) _applyLockTimeouts = value; }
    }
    private int _applyLockTimeouts;

    // ── kişi ───────────────────────────────────────────────────────────────

    /// <summary>Sunucudaki müşteri projeksiyonlarının <c>GroupId</c>'si (müşteri gönderiminin <c>groupId</c>'si ya da
    /// <see cref="SetServerGroup"/>); anahtar Id'nin "N" yazımı.</summary>
    private readonly Dictionary<string, string> _customerGroups = new(StringComparer.Ordinal);

    /// <summary>Grup biriminin damgası (<c>GroupIdChangedAt</c>): gönderim birimi yalnız damgası daha yeniyse yazar.</summary>
    private readonly Dictionary<string, DateTimeOffset> _customerGroupStamps = new(StringComparer.Ordinal);

    /// <summary>Sunucuda bu müşterileri aynı gruba koyar (başka bilgisayarın gönderdiği grup; grup senkronu gecikmesi).
    /// <paramref name="groupId"/> boşsa üyelerin grubu kalkar.</summary>
    public void SetServerGroup(string? groupId, params string[] members)
    {
        lock (_gate)
            foreach (var member in members)
            {
                var key = Guid.TryParseExact(member, "N", out var g) ? g.ToString("N") : member;
                _customerGroupStamps[key] = DateTimeOffset.UtcNow;
                if (string.IsNullOrWhiteSpace(groupId)) _customerGroups.Remove(key);
                else _customerGroups[key] = groupId;
            }
    }

    /// <summary>Müşterinin sunucudaki grubu; yoksa null.</summary>
    public string? ServerGroupOf(string customerId)
    {
        lock (_gate)
            return _customerGroups.TryGetValue(
                Guid.TryParseExact(customerId, "N", out var g) ? g.ToString("N") : customerId, out var group)
                ? group
                : null;
    }

    /// <summary>Müşteri gönderiminin grubu (W3-12): biçim 2'nin birim kuralıyla — grup birimi yalnız damgası
    /// (<c>groupIdChangedAt</c>) saklanandan yeniyse yazılır (dolu değer yazılır, boş değer kaldırır); damgasız gönderim
    /// birime dokunmaz. Grubu hiç değiştirmemiş başka bir bilgisayarın boş <c>groupId</c>'si böylece grubu silmez.
    /// <c>_gate</c> altında çağrılır.</summary>
    private void RecordPushedGroup(Guid id, JsonElement customer)
    {
        if (!customer.TryGetProperty("groupIdChangedAt", out var stampElement)
            || stampElement.ValueKind != JsonValueKind.String || !stampElement.TryGetDateTimeOffset(out var stamp))
            return;
        var key = id.ToString("N");
        if (_customerGroupStamps.TryGetValue(key, out var known) && known >= stamp) return;
        _customerGroupStamps[key] = stamp;
        if (customer.TryGetProperty("groupId", out var g) && g.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(g.GetString()))
            _customerGroups[key] = g.GetString()!;
        else
            _customerGroups.Remove(key);
    }

    /// <summary>GUID'li Id'nin asıl kaydı ("N", küçük harf; kopya zinciri en çok üç adım); GUID olmayan Id aynen.</summary>
    private string CanonicalMember(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var g)) return id;
        var current = g;
        for (var hop = 0; hop < 3 && _customers.TryGetValue(current, out var p) && p.MergedInto is { } target; hop++)
            current = target;
        return current.ToString("N");
    }

    /// <summary>Üyeler ve asıl kaydı üyelerden biri olan kopyalar (birleştirme kayıtları yeniden anahtarlamaz).</summary>
    private HashSet<string> WithCopies(IEnumerable<string> members)
    {
        var set = new HashSet<string>(members, StringComparer.Ordinal);
        foreach (var p in _customers.Values)
            if (p.MergedInto is not null && set.Contains(CanonicalMember(p.Id.ToString("N"))))
                set.Add(p.Id.ToString("N"));
        return set;
    }

    /// <summary>Asıl kaydın sunucu grubu (asıl projeksiyonlar, ordinal sıralı); grupsuzsa boş.</summary>
    private List<string> ServerGroupMembers(string? groupId) =>
        string.IsNullOrWhiteSpace(groupId)
            ? []
            : _customerGroups
                .Where(kv => string.Equals(kv.Value, groupId, StringComparison.Ordinal)
                             && string.Equals(CanonicalMember(kv.Key), kv.Key, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .Order(StringComparer.Ordinal)
                .ToList();

    /// <summary>Kişi (§6.4-b): asıl kayda çözülmüş üyeler, PersonKey ve grubun Id'si.</summary>
    private (List<string> Members, string PersonKey, string Owner, string? GroupId) Person(
        string ownerId, IEnumerable<string> memberIds)
    {
        var members = memberIds.Select(CanonicalMember).Distinct(StringComparer.Ordinal).ToList();
        var owner = CanonicalMember(ownerId);
        if (!Guid.TryParseExact(owner, "N", out _)) return (members, "r:" + ownerId, owner, null);
        var group = _customerGroups.TryGetValue(owner, out var g) && !string.IsNullOrWhiteSpace(g) ? g : null;
        return (members, group is null ? "c:" + owner : "g:" + group, owner, group);
    }

    // ── kayıtlar ───────────────────────────────────────────────────────────

    /// <summary>Sunucunun <c>PaymentRequestRecords</c> satırı (üyeler ve siparişler dahil).</summary>
    private sealed class PaymentRecordRow
    {
        public Guid Id;
        public Guid LicenseId;
        public string Kind = "record";
        public string State = "delivered";
        public string ScopeKey = "";
        public string? PersonKey;
        public string? Owner;
        public Guid ReplicaId;
        public string? Machine;
        public List<string> Members = new();
        public List<Guid> OrderIds = new();
        public decimal? ProductTotal;
        public decimal? TotalBeforeBalance;
        public decimal? NetAmount;
        public decimal? AppliedAmount;
        public bool? FullyFromBalance;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? LeaseUntil;
        public DateTimeOffset? FinishedAt;
        public string Hash = "";
        public int Version;

        public bool IsExpiredLease(DateTimeOffset now) => State == "sending" && (LeaseUntil is null || LeaseUntil <= now);

        public PaymentRecordView View() => new(Id, Kind, State, ScopeKey, PersonKey, ReplicaId, Machine,
            Members.ToList(), OrderIds.ToList(), ProductTotal, TotalBeforeBalance, NetAmount, AppliedAmount,
            FullyFromBalance, StartedAt, LeaseUntil, FinishedAt, Version);
    }

    /// <summary>Kaydın test görünümü (<see cref="PaymentRecords"/>).</summary>
    internal sealed record PaymentRecordView(
        Guid Id, string Kind, string State, string ScopeKey, string? PersonKey, Guid ReplicaId, string? Machine,
        IReadOnlyList<string> Members, IReadOnlyList<Guid> OrderIds, decimal? ProductTotal, decimal? TotalBeforeBalance,
        decimal? NetAmount, decimal? AppliedAmount, bool? FullyFromBalance, DateTimeOffset StartedAt,
        DateTimeOffset? LeaseUntil, DateTimeOffset? FinishedAt, int Version);

    private readonly List<PaymentRecordRow> _paymentRecords = new();

    /// <summary>Sunucudaki bütün ödeme isteği kayıtları (yazılmış hâlleriyle; süresi dolmuş kira satırı yazılana kadar
    /// <c>sending</c>).</summary>
    public IReadOnlyList<PaymentRecordView> PaymentRecords()
    {
        lock (_gate) return _paymentRecords.Select(r => r.View()).ToList();
    }

    public PaymentRecordView? PaymentRecord(Guid id)
    {
        lock (_gate) return _paymentRecords.FirstOrDefault(r => r.Id == id)?.View();
    }

    // ── begin (§6.4) ───────────────────────────────────────────────────────

    /// <summary><c>POST payment-requests/begin</c>, <see cref="LeaseRules"/> açıkken. Kayıt ve betik çağıranda.</summary>
    private HttpResponseMessage BeginByRules(Guid licenseId, JsonElement root, string? userAgent)
    {
        if (!TryEnvelope(GuidOf(root, "replicaId"), IntOf(root, "protocol"), IntOf(root, "seenMode"),
                out var replicaId, out var seenMode, out var envError))
            return Invalid($"{PaymentRequestsBegin}: {envError}");
        if (GuidOf(root, "recordId") is not { } recordId || recordId == Guid.Empty)
            return PaymentInvalid("invalid-record", "recordId boş olamaz.");
        if (!TryMembers(root, out var rawMembers, out var membersError))
            return PaymentInvalid("invalid-members", membersError!);
        var ownerId = StringOf(root, "ownerId");
        if (ownerId is null || !rawMembers.Contains(ownerId, StringComparer.Ordinal))
            return PaymentInvalid("invalid-members", "ownerId memberIds'te olmalı.");
        var scopeKey = StringOf(root, "scopeKey");
        if (!IsLeaseScope(scopeKey))
            return PaymentInvalid("invalid-scope", "scopeKey 'cumulative' ya da 'session:{id}' olmalı.");
        if (GuidsOf(root, "orderIds") is not { } rawOrders || rawOrders.Count > MaxPaymentOrders
            || rawOrders.Contains(Guid.Empty))
            return PaymentInvalid("invalid-orders", $"orderIds en çok {MaxPaymentOrders} dolu Id taşır.");
        var productTotal = DecimalOf(root, "productTotal");
        var totalBeforeBalance = DecimalOf(root, "totalBeforeBalance");
        if (!IsMoney(productTotal) || !IsMoney(totalBeforeBalance))
            return PaymentInvalid("invalid-amount", "productTotal ve totalBeforeBalance para biçiminde olmalı.");
        if (BoolOf(root, "recipientPays") is not { } recipientPays
            || IntOf(root, "shippingSettingsVersion") is not { } shippingVersion || shippingVersion < 0)
            return PaymentInvalid("invalid-shipping", "recipientPays ve shippingSettingsVersion gerekli.");

        // Onay alanları (#523): 500'ü aşan Id listesi ve bozuk özet 400; ikisi de beklenen istemci davranışı değil, ama
        // testler kuralı sınar — Faults'a yazılmaz.
        var confirmed = new HashSet<Guid>();
        if (root.TryGetProperty("confirmedPriorIds", out var ids) && ids.ValueKind != JsonValueKind.Null)
        {
            if (GuidsOf(root, "confirmedPriorIds") is not { } list || list.Count > MaxConfirmedPriors
                || list.Contains(Guid.Empty))
                return ProblemResponse(400, "invalid-confirmed-priors",
                    $"confirmedPriorIds en çok {MaxConfirmedPriors} dolu Id taşır.");
            confirmed = list.ToHashSet();
        }
        byte[]? confirmedDigest = null;
        if (root.TryGetProperty("confirmedPriorsDigest", out var digestText) && digestText.ValueKind != JsonValueKind.Null
            && (digestText.ValueKind != JsonValueKind.String
                || !PriorsDigest.TryParse(digestText.GetString(), out confirmedDigest)))
            return ProblemResponse(400, "invalid-priors-digest", "confirmedPriorsDigest biçimi bozuk.");

        var orders = rawOrders.Distinct().ToList();
        var hash = string.Join("|", "lease", replicaId.ToString("N"), ownerId,
            string.Join(",", rawMembers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), scopeKey,
            string.Join(",", orders.Order().Select(o => o.ToString("N"))), Money(productTotal!.Value),
            Money(totalBeforeBalance!.Value), recipientPays ? "1" : "0",
            shippingVersion.ToString(CultureInfo.InvariantCulture));

        lock (_gate)
        {
            var caller = Touch(licenseId, replicaId, seenMode);
            if (Mode != 1)
                return ProblemResponse(409, "mode-off", "Satış senkronu bu lisansta kapalı; ödeme isteği yerel kuralla.");
            if (!ClientAtLeast(userAgent, MinClientVersion))
                return ProblemResponse(409, "client-update-required", "Ödeme isteği için güncel sürüm gerekiyor.");

            var now = PaymentClock();

            // (a) yeniden oynatma.
            if (_paymentRecords.FirstOrDefault(r => r.Id == recordId) is { } existing)
            {
                if (existing.LicenseId != licenseId || existing.Kind != "lease" || existing.Hash != hash)
                    return ProblemResponse(409, "content-conflict", "Bu recordId farklı bir içerikle zaten kullanıldı.");
                return BeginSucceeded(existing.Id, existing.IsExpiredLease(now) ? "unconfirmed" : existing.State,
                    existing.LeaseUntil, existing.Members);
            }

            // (b) kişi ve sunucu grubu.
            var person = Person(ownerId, rawMembers);
            var group = ServerGroupMembers(person.GroupId);
            var clientMembers = person.Members.ToHashSet(StringComparer.Ordinal);
            if (group.Any(m => !clientMembers.Contains(m)))
                return ProblemResponse(409, "members-stale", "Müşteri grupları eşitleniyor; kişi yeniden kurulmalı.",
                    new Dictionary<string, object?> { ["members"] = group });
            var members = person.Members.Union(group, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var lookup = WithCopies(members);
            var related = _paymentRecords
                .Where(r => r.LicenseId == licenseId
                            && string.Equals(r.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)
                            && (string.Equals(r.PersonKey, person.PersonKey, StringComparison.Ordinal)
                                || r.Members.Any(lookup.Contains)))
                .ToList();

            // (c) süresi dolmuş kira ve bu kopyanın önceki kirası Unconfirmed; retlerde de kalır.
            foreach (var stale in related.Where(r => r.State == "sending"
                                                     && (r.LeaseUntil is null || r.LeaseUntil <= now
                                                         || r.ReplicaId == replicaId)))
            {
                stale.FinishedAt = stale.LeaseUntil is null || stale.LeaseUntil <= now
                    ? stale.LeaseUntil ?? stale.StartedAt
                    : now;
                stale.State = "unconfirmed";
                stale.Version++;
            }

            // (d) başka kopyanın canlı kirası.
            if (related.Where(r => r.State == "sending" && r.ReplicaId != replicaId)
                    .OrderBy(r => r.StartedAt).ThenBy(r => r.Id).FirstOrDefault() is { } elsewhere)
                return ProblemResponse(409, "in-progress-elsewhere", "Bu kişiye başka bir bilgisayarda ödeme isteniyor.",
                    new Dictionary<string, object?>
                    {
                        ["machine"] = elsewhere.Machine, ["since"] = elsewhere.StartedAt, ["recordId"] = elsewhere.Id,
                    });

            // (e) onaylanmamış öncül: Id listesi "ya da" bütün kümenin özeti.
            var open = related.Where(r => r.State is "delivered" or "unconfirmed").ToList();
            var priors = open.Where(r => !confirmed.Contains(r.Id))
                .OrderBy(r => r.FinishedAt is null).ThenBy(r => r.FinishedAt).ThenBy(r => r.StartedAt).ThenBy(r => r.Id)
                .ToList();
            if (priors.Count > 0)
            {
                var digest = PriorsDigest.Hash(open.Select(r => r.Id));
                if (confirmedDigest is null || !confirmedDigest.AsSpan().SequenceEqual(digest))
                    return ProblemResponse(409, "already-requested",
                        "Bu kişiden bu kapsamda daha önce ödeme istendi; tekrar gönderilsin mi?",
                        new Dictionary<string, object?>
                        {
                            ["priors"] = priors.Select(p => new
                            {
                                id = p.Id, machine = p.Machine, netAmount = p.NetAmount,
                                totalBeforeBalance = p.TotalBeforeBalance, at = p.StartedAt, finishedAt = p.FinishedAt,
                                unconfirmed = p.State == "unconfirmed",
                            }).ToList(),
                            ["priorsDigest"] = PriorsDigest.Format(digest),
                        });
            }

            // (f) sunucu toplamı: küme ve toplam.
            var counted = CountedOrders(licenseId, scopeKey!, members);
            var serverTotal = counted.Sum(o => o.Row.Price);
            var serverIds = counted.Select(o => o.Row.Id).ToHashSet();
            if (serverTotal != productTotal.Value || !serverIds.SetEquals(orders))
            {
                var clientIds = orders.ToHashSet();
                var serverOnly = counted.Where(o => !clientIds.Contains(o.Row.Id)).ToList();
                return ProblemResponse(409, "stale-total", "Bu bilgisayardaki toplam sunucununkiyle tutmuyor.",
                    new Dictionary<string, object?>
                    {
                        ["serverTotal"] = serverTotal, ["liveElsewhere"] = LiveElsewhere(licenseId, replicaId, serverOnly),
                    });
            }

            // (g) kargo ayarı: sürüm ve tutar.
            var current = Shipping ?? new ShippingBody(0, null, null);
            if (shippingVersion != current.Version
                || totalBeforeBalance.Value != productTotal.Value + ShippingFeeOf(current, productTotal.Value, recipientPays))
                return ProblemResponse(409, "settings-stale", "Kargo ayarı değişti; tutar yeniden hesaplanmalı.",
                    new Dictionary<string, object?> { ["current"] = ShippingSnapshot(current) });

            // (h) kira.
            var record = new PaymentRecordRow
            {
                Id = recordId, LicenseId = licenseId, Kind = "lease", State = "sending", ScopeKey = scopeKey!,
                PersonKey = person.PersonKey, Owner = person.Owner, ReplicaId = replicaId, Machine = caller.MachineName,
                Members = members, OrderIds = orders, ProductTotal = productTotal, TotalBeforeBalance = totalBeforeBalance,
                StartedAt = now, LeaseUntil = now + LeaseDuration, Hash = hash,
            };
            _paymentRecords.Add(record);
            return BeginSucceeded(record.Id, record.State, record.LeaseUntil, record.Members);
        }
    }

    private static HttpResponseMessage BeginSucceeded(Guid id, string state, DateTimeOffset? leaseUntil,
        IReadOnlyList<string> members) =>
        FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
        {
            recordId = id, state, leaseUntil, members = members.Order(StringComparer.Ordinal).ToList(),
        }, Web));

    /// <summary>(f) Kişinin kapsamdaki, toplama giren siparişleri: yayını olan; <c>session:{id}</c> o yayının,
    /// <c>cumulative</c> bütün yayınların; müşterisi (GUID'liyse asıl kaydı) üyelerden; basılmış, iptal edilmemiş, geçici
    /// yedek değil (kargo ücreti satırı dahil).</summary>
    private List<ServerOrder> CountedOrders(Guid licenseId, string scopeKey, IReadOnlyList<string> members)
    {
        var memberSet = members.ToHashSet(StringComparer.Ordinal);
        Guid? sessionId = scopeKey.StartsWith("session:", StringComparison.Ordinal)
            ? Guid.ParseExact(scopeKey["session:".Length..], "N")
            : null;
        return _orders.Values
            .Where(o => o.LicenseId == licenseId && o.Row.SessionId is not null
                        && (sessionId is null || o.Row.SessionId == sessionId)
                        && memberSet.Contains(CanonicalMember(o.Row.CustomerId))
                        && o.Row.PrintedAt is not null && o.Row.CancelledAt is null && !o.Row.IsTentativeBackup)
            .ToList();
    }

    /// <summary><c>liveElsewhere</c>: yalnız sunucuda olan siparişlerin HEPSİ başka kopyanın devredilmemiş yayınındaysa o
    /// yayınlar ve sahip makineleri; değilse null.</summary>
    private List<object>? LiveElsewhere(Guid licenseId, Guid caller, IReadOnlyList<ServerOrder> serverOnly)
    {
        if (serverOnly.Count == 0) return null;
        var sessions = serverOnly.Select(o => o.Row.SessionId!.Value).Distinct()
            .Select(id => _serverSessions.TryGetValue(id, out var s) && s.LicenseId == licenseId ? s : null)
            .ToList();
        if (sessions.Any(s => s is null || s.ReleasedAt is not null || s.OwnerReplicaId == caller)) return null;
        return sessions.OrderBy(s => s!.Id)
            .Select(s => (object)new { sessionId = s!.Id, machine = s.OwnerMachineName })
            .ToList();
    }

    /// <summary>İstenecek kargo: alıcı ödemeli, ayar kapalı (eşik ve ücret ikisi de &gt; 0 değil) ya da toplam eşik ve
    /// üstündeyse 0; değilse ücret (WPF <c>ComputeShipping</c>'in kuralı, bağımsız kopya).</summary>
    private static decimal ShippingFeeOf(ShippingBody settings, decimal productTotal, bool recipientPays)
    {
        if (recipientPays) return 0m;
        if (settings.FreeShippingThreshold is not { } threshold || threshold <= 0 || settings.Fee is not { } fee || fee <= 0)
            return 0m;
        return productTotal >= threshold ? 0m : fee;
    }

    // ── finish ─────────────────────────────────────────────────────────────

    /// <summary><c>POST payment-requests/{id}/finish</c>, <see cref="LeaseRules"/> açıkken.</summary>
    private HttpResponseMessage FinishByRules(Guid licenseId, Guid recordId, JsonElement root)
    {
        if (!TryEnvelope(GuidOf(root, "replicaId"), IntOf(root, "protocol"), IntOf(root, "seenMode"),
                out var replicaId, out var seenMode, out var envError))
            return Invalid($"{PaymentRequestsFinish}: {envError}");
        var outcome = StringOf(root, "outcome");
        if (outcome is not ("delivered" or "unconfirmed" or "abandoned"))
            return PaymentInvalid("invalid-outcome", "outcome 'delivered', 'unconfirmed' ya da 'abandoned' olmalı.");
        var net = DecimalOf(root, "netAmount");
        var applied = DecimalOf(root, "appliedAmount");
        var fullyFromBalance = BoolOf(root, "fullyFromBalance");
        if (!IsMoney(net) || (applied is not null && !IsMoney(applied)) || fullyFromBalance is null)
            return PaymentInvalid("invalid-amount", "netAmount, appliedAmount ve fullyFromBalance biçimi bozuk.");

        lock (_gate)
        {
            Touch(licenseId, replicaId, seenMode);
            var record = _paymentRecords.FirstOrDefault(r => r.Id == recordId && r.LicenseId == licenseId);
            if (record is null)
                return ProblemResponse(404, "record-not-found", "Bu lisansta böyle bir ödeme isteği kaydı yok.");
            if (record.Kind != "lease" || !Lineage(licenseId, replicaId).Contains(record.ReplicaId))
                return ProblemResponse(409, "content-conflict", "Bu kayıt bu sonuçla bitirilemez.");
            if (record.State == outcome && record.NetAmount == net && record.AppliedAmount == applied
                && record.FullyFromBalance == fullyFromBalance)
                return Finished(record);
            if (record.State is "delivered" or "abandoned")
                return ProblemResponse(409, "content-conflict", "Kayıt son hâlde; sonuç ya da tutar farklı.");

            record.State = outcome;
            record.NetAmount = net;
            record.AppliedAmount = applied;
            record.FullyFromBalance = fullyFromBalance;
            record.FinishedAt = PaymentClock();
            record.Version++;
            return Finished(record);
        }

        static HttpResponseMessage Finished(PaymentRecordRow r) =>
            FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
            {
                recordId = r.Id, state = r.State, finishedAt = r.FinishedAt,
            }, Web));
    }

    /// <summary>Çağıran kopya ve onun <c>PreviousReplicaId</c> zinciri (geri yüklenmiş kopyanın öncülleri).</summary>
    private HashSet<Guid> Lineage(Guid licenseId, Guid replicaId)
    {
        var lineage = new HashSet<Guid> { replicaId };
        var current = replicaId;
        for (var hop = 0; hop < 16 && _replicas.TryGetValue((licenseId, current), out var row)
                          && row.PreviousReplicaId is { } previous && lineage.Add(previous); hop++)
            current = previous;
        return lineage;
    }

    // ── liste ──────────────────────────────────────────────────────────────

    /// <summary><c>GET payment-requests</c>, <see cref="LeaseRules"/> açıkken.</summary>
    private HttpResponseMessage ListByRules(Guid licenseId, string? scopeKey, IReadOnlyCollection<string> requested)
    {
        if (!IsRecordScope(scopeKey))
            return PaymentInvalid("invalid-scope", "scopeKey kayıt biçiminde olmalı.");
        if (requested.Count is 0 or > MaxPaymentMembers || requested.Any(m => !IsMemberId(m)))
            return PaymentInvalid("invalid-members", $"memberIds 1–{MaxPaymentMembers} üye taşır.");

        List<object> items;
        lock (_gate)
        {
            var lookup = WithCopies(requested.Select(CanonicalMember));
            var now = PaymentClock();
            items = _paymentRecords
                .Where(r => r.LicenseId == licenseId && r.State != "abandoned"
                            && string.Equals(r.ScopeKey, scopeKey, StringComparison.Ordinal)
                            && r.Members.Any(lookup.Contains))
                .Select(r => (Row: r, State: r.IsExpiredLease(now) ? "unconfirmed" : r.State,
                    FinishedAt: r.IsExpiredLease(now) ? r.LeaseUntil ?? r.StartedAt : r.FinishedAt))
                .OrderBy(x => x.FinishedAt is null).ThenBy(x => x.FinishedAt).ThenBy(x => x.Row.StartedAt)
                .ThenBy(x => x.Row.Id)
                .TakeLast(MaxPaymentListRecords)
                .Select(x => (object)new
                {
                    id = x.Row.Id, replicaId = x.Row.ReplicaId, machine = x.Row.Machine, kind = x.Row.Kind,
                    state = x.State, netAmount = x.Row.NetAmount, appliedAmount = x.Row.AppliedAmount,
                    fullyFromBalance = x.Row.FullyFromBalance, productTotal = x.Row.ProductTotal,
                    totalBeforeBalance = x.Row.TotalBeforeBalance, startedAt = x.Row.StartedAt,
                    finishedAt = x.FinishedAt, orderIds = x.Row.OrderIds.Order().ToList(),
                })
                .ToList();
        }
        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new { records = items }, Web));
    }

    // ── geri doldurma ──────────────────────────────────────────────────────

    /// <summary><c>POST payment-requests/backfill</c>, <see cref="LeaseRules"/> açıkken: kirasız kayıt, kipten bağımsız.</summary>
    private HttpResponseMessage BackfillByRules(Guid licenseId, JsonElement root)
    {
        if (!TryEnvelope(GuidOf(root, "replicaId"), IntOf(root, "protocol"), IntOf(root, "seenMode"),
                out var replicaId, out var seenMode, out var envError))
            return Invalid($"{PaymentRequestsBackfill}: {envError}");
        if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array
            || records.GetArrayLength() is 0 or > MaxPaymentBackfillBatch)
            return PaymentInvalid("invalid-batch", $"records 1–{MaxPaymentBackfillBatch} kayıt taşır.");

        var items = new List<(Guid Id, string Owner, List<string> Members, string Scope, List<Guid> Orders, string State,
            decimal Net, decimal? Applied, bool Fully, DateTimeOffset At)>();
        foreach (var item in records.EnumerateArray())
        {
            if (GuidOf(item, "recordId") is not { } id || id == Guid.Empty)
                return PaymentInvalid("invalid-record", "recordId boş olamaz.");
            if (!TryMembers(item, out var members, out var membersError))
                return PaymentInvalid("invalid-members", membersError!);
            var owner = StringOf(item, "ownerId");
            if (owner is null || !members.Contains(owner, StringComparer.Ordinal))
                return PaymentInvalid("invalid-members", "ownerId memberIds'te olmalı.");
            var scope = StringOf(item, "scopeKey");
            if (!IsRecordScope(scope)) return PaymentInvalid("invalid-scope", "scopeKey kayıt biçiminde olmalı.");
            if (GuidsOf(item, "orderIds") is not { } orders || orders.Count > MaxPaymentOrders || orders.Contains(Guid.Empty))
                return PaymentInvalid("invalid-orders", "orderIds dolu Id taşır.");
            var state = StringOf(item, "state");
            if (state is not ("delivered" or "unconfirmed"))
                return PaymentInvalid("invalid-state", "state 'delivered' ya da 'unconfirmed' olmalı.");
            var net = DecimalOf(item, "netAmount");
            var applied = DecimalOf(item, "appliedAmount");
            var fully = BoolOf(item, "fullyFromBalance");
            if (!IsMoney(net) || (applied is not null && !IsMoney(applied)) || fully is null)
                return PaymentInvalid("invalid-amount", "netAmount, appliedAmount ve fullyFromBalance biçimi bozuk.");
            if (!item.TryGetProperty("at", out var atElement) || atElement.ValueKind != JsonValueKind.String
                || !atElement.TryGetDateTimeOffset(out var at))
                return PaymentInvalid("invalid-at", "at gerekli.");
            items.Add((id, owner, members, scope!, orders.Distinct().ToList(), state, net!.Value, applied, fully.Value, at));
        }

        var results = new List<object>();
        lock (_gate)
        {
            var caller = Touch(licenseId, replicaId, seenMode);
            var now = PaymentClock();
            foreach (var item in items)
            {
                var person = Person(item.Owner, item.Members);
                var members = person.Members.Union(ServerGroupMembers(person.GroupId), StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToList();
                var hash = string.Join("|", "record", item.Scope, item.State, Money(item.Net),
                    item.Applied is { } a ? Money(a) : "null", item.Fully ? "1" : "0",
                    string.Join(",", item.Orders.Order().Select(o => o.ToString("N"))));
                _paymentRecordIds.Add(item.Id);

                if (_paymentRecords.FirstOrDefault(r => r.Id == item.Id) is { } existing)
                {
                    if (existing.LicenseId != licenseId || existing.Kind != "record" || existing.Hash != hash
                        || !string.Equals(CanonicalMember(existing.Owner ?? ""), person.Owner, StringComparison.Ordinal))
                    {
                        results.Add(new { recordId = item.Id, status = "content-conflict" });
                        continue;
                    }
                    foreach (var member in members.Where(m => !existing.Members.Contains(m, StringComparer.Ordinal)))
                        existing.Members.Add(member);
                    results.Add(new { recordId = item.Id, status = "unchanged" });
                    continue;
                }

                _paymentRecords.Add(new PaymentRecordRow
                {
                    Id = item.Id, LicenseId = licenseId, Kind = "record", State = item.State, ScopeKey = item.Scope,
                    PersonKey = person.PersonKey, Owner = person.Owner, ReplicaId = replicaId, Machine = caller.MachineName,
                    Members = members, OrderIds = item.Orders, NetAmount = item.Net, AppliedAmount = item.Applied,
                    FullyFromBalance = item.Fully, StartedAt = item.At > now ? now : item.At, FinishedAt = now, Hash = hash,
                });
                results.Add(new { recordId = item.Id, status = "applied" });
            }
        }
        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new { records = results }, Web));
    }

    // ── kargo ayarı (§4.8) ─────────────────────────────────────────────────

    private DateTimeOffset? _shippingUpdatedAt;
    private string? _shippingUpdatedBy;

    private object ShippingSnapshot(ShippingBody current) => new
    {
        version = current.Version, freeShippingThreshold = current.FreeShippingThreshold, fee = current.Fee,
        updatedAt = _shippingUpdatedAt, updatedBy = _shippingUpdatedBy,
    };

    /// <summary><c>GET/PUT shipping-settings</c> (<c>LicensesShippingSettingsController</c>'ın ikizi): kayıt
    /// (<see cref="Calls"/>, GET'te gövde boş), betik, bekletme.</summary>
    private async Task<HttpResponseMessage> HandleShippingSettingsAsync(HttpRequestMessage req, Guid licenseId,
        CancellationToken ct)
    {
        var raw = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(ShippingSettingsEndpoint, licenseId, raw.Length == 0 ? "{}" : raw));
            if (_scripted.TryGetValue(ShippingSettingsEndpoint, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        await WaitHoldAsync(ShippingSettingsEndpoint, ct);
        if (scripted is not null) return scripted();

        if (req.Method == HttpMethod.Get)
        {
            lock (_gate)
                return FakeHttpMessageHandler.Json(200,
                    JsonSerializer.Serialize(ShippingSnapshot(Shipping ?? new ShippingBody(0, null, null)), Web));
        }
        if (req.Method != HttpMethod.Put)
        {
            _faults.Enqueue($"beklenmeyen istek: {req.Method} {ShippingSettingsEndpoint}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!TryEnvelope(GuidOf(root, "replicaId"), IntOf(root, "protocol"), IntOf(root, "seenMode"),
                out var replicaId, out var seenMode, out var envError))
            return Invalid($"{ShippingSettingsEndpoint}: {envError}");
        if (IntOf(root, "expectedVersion") is not { } expected || expected < 0)
            return PaymentInvalid("invalid-expected-version", "expectedVersion 0 ya da daha büyük olmalı.");
        var threshold = DecimalOf(root, "freeShippingThreshold");
        var fee = DecimalOf(root, "fee");
        var valid = (threshold is null && fee is null)
                    || (threshold is > 0 && fee is > 0 && decimal.Round(threshold.Value, 2) == threshold
                        && decimal.Round(fee.Value, 2) == fee);
        if (!valid) return PaymentInvalid("invalid-shipping", "Eşik ve ücret ikisi de boş ya da ikisi de > 0.");

        lock (_gate)
        {
            Touch(licenseId, replicaId, seenMode);
            if (Mode != 1)
                return ProblemResponse(409, "mode-off", "Mod Off'ta lisans geneli kargo ayarı sunucudan yazılmaz.");
            var current = Shipping ?? new ShippingBody(0, null, null);
            if (expected != current.Version)
                return ProblemResponse(409, "stale", "Kargo ayarı bu arada değişti.",
                    new Dictionary<string, object?> { ["current"] = ShippingSnapshot(current) });
            Shipping = new ShippingBody(current.Version + 1, threshold, fee);
            _shippingUpdatedAt = PaymentClock();
            _shippingUpdatedBy = replicaId.ToString();
            return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(ShippingSnapshot(Shipping), Web));
        }
    }

    // ── bakiye defteri (§6.6) ──────────────────────────────────────────────

    /// <summary>Defterin bir <c>purchase-deduction</c> satırı.</summary>
    private sealed class BalanceTransactionRow
    {
        public Guid Id;
        public string Customer = "";
        public string? SaleScope;
        public decimal Applied;
        public decimal ProductTotal;
        public bool Reversed;
        public DateTimeOffset CreatedAt;
    }

    internal sealed record BalanceTransactionView(Guid Id, string Customer, string? SaleScope, decimal Applied,
        decimal ProductTotal, bool Reversed, DateTimeOffset CreatedAt);

    private readonly List<BalanceTransactionRow> _ledger = new();

    /// <summary>Defter (yalnız <see cref="LeaseRules"/> açıkken yazılır).</summary>
    public IReadOnlyList<BalanceTransactionView> BalanceTransactions()
    {
        lock (_gate)
            return _ledger.Select(t => new BalanceTransactionView(t.Id, t.Customer, t.SaleScope, t.Applied,
                t.ProductTotal, t.Reversed, t.CreatedAt)).ToList();
    }

    /// <summary>Müşterinin bakiyesi: <see cref="BalancePreview"/> eksi geri alınmamış düşümler.</summary>
    private decimal BalanceOf(string customer) =>
        BalancePreview - _ledger.Where(t => !t.Reversed && t.Customer == customer).Sum(t => t.Applied);

    /// <summary>Bakiye uçları, <see cref="LeaseRules"/> açıkken (kayıt ve betik çağıranda).</summary>
    private HttpResponseMessage BalanceByRules(HttpRequestMessage req, string action, string raw)
    {
        lock (_gate)
            switch (action)
            {
                case "preview" when req.Method == HttpMethod.Get:
                {
                    var id = QueryGuid(req.RequestUri!.Query, "wpfCustomerId") ?? Guid.Empty;
                    return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
                    {
                        wpfCustomerId = id, balance = Math.Max(0m, BalanceOf(CanonicalMember(id.ToString("N")))),
                        updatedAt = PaymentClock(),
                    }, Web));
                }
                case "scope" when req.Method == HttpMethod.Get:
                {
                    var query = req.RequestUri!.Query;
                    var id = QueryGuid(query, "wpfCustomerId");
                    var scope = QueryValue(query, "saleScope");
                    if (id is null || string.IsNullOrEmpty(scope)) return FakeHttpMessageHandler.Empty(204);
                    var customer = CanonicalMember(id.Value.ToString("N"));
                    if (ScopeDeductionOf(customer, scope) is { } found)
                        return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
                        {
                            transactionId = found.Id, appliedAmount = found.Applied, productTotal = found.ProductTotal,
                            createdAt = found.CreatedAt,
                        }, Web));
                    return ScopeDeductions.TryGetValue(id.Value, out var seeded)
                        ? FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(seeded, Web))
                        : FakeHttpMessageHandler.Empty(204);
                }
                case "apply" when req.Method == HttpMethod.Post:
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    var id = GuidOf(root, "wpfCustomerId") ?? Guid.Empty;
                    var customer = CanonicalMember(id.ToString("N"));
                    var amount = DecimalOf(root, "amount") ?? 0m;
                    var productTotal = DecimalOf(root, "productTotal") ?? 0m;
                    var key = GuidOf(root, "idempotencyKey");
                    var scope = StringOf(root, "saleScope");
                    if (key is { } k && _ledger.FirstOrDefault(t => t.Id == k) is { } replay)
                        return ApplySucceeded(replay, customer);
                    if (Mode == 1 && !string.IsNullOrEmpty(scope))
                    {
                        if (_applyLockTimeouts > 0)
                        {
                            _applyLockTimeouts--;
                            return ProblemResponse(409, "stale", "Aynı satır ve kapsam için başka bir apply sürüyor.");
                        }
                        if (ScopeDeductionOf(customer, scope) is { } held)
                            return ProblemResponse(409, "scope-has-deduction", "Bu kapsamda geri alınmamış düşüm var.",
                                new Dictionary<string, object?>
                                {
                                    ["transactionId"] = held.Id, ["appliedAmount"] = held.Applied,
                                    ["productTotal"] = held.ProductTotal,
                                });
                    }
                    var row = new BalanceTransactionRow
                    {
                        Id = key ?? Guid.NewGuid(), Customer = customer, SaleScope = scope,
                        Applied = Math.Max(0m, Math.Min(Math.Min(BalanceOf(customer), amount), productTotal)),
                        ProductTotal = productTotal, CreatedAt = PaymentClock(),
                    };
                    _ledger.Add(row);
                    return ApplySucceeded(row, customer);
                }
                default:
                    _faults.Enqueue($"beklenmeyen istek: {req.Method} customer-balance/{action}");
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

        HttpResponseMessage ApplySucceeded(BalanceTransactionRow row, string customer) =>
            FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
            {
                transactionId = row.Id, appliedAmount = row.Applied, remainingBalance = Math.Max(0m, BalanceOf(customer)),
            }, Web));
    }

    /// <summary>Kapsamın geri alınmamış en eski düşümü (aynı asıl kayıt, aynı kapsam).</summary>
    private BalanceTransactionRow? ScopeDeductionOf(string customer, string scope) =>
        _ledger.Where(t => !t.Reversed && t.Customer == customer
                           && string.Equals(t.SaleScope, scope, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.CreatedAt).FirstOrDefault();

    /// <summary><c>customer-balance/transactions/{id}</c> (GET) ve <c>…/reverse</c> (POST), <see cref="LeaseRules"/>
    /// açıkken: kayıt, betik.</summary>
    private async Task<HttpResponseMessage> HandleBalanceTransactionAsync(HttpRequestMessage req, Guid licenseId,
        Guid transactionId, bool reverse, CancellationToken ct)
    {
        var endpoint = reverse ? BalanceReverseEndpoint : BalanceTransactionEndpoint;
        Func<HttpResponseMessage>? scripted = null;
        lock (_gate)
        {
            _pushCalls.Add(new PushCall(endpoint, licenseId, "{}", transactionId));
            if (_scripted.TryGetValue(endpoint, out var q) && q.Count > 0) scripted = q.Dequeue();
        }
        await WaitHoldAsync(endpoint, ct);
        if (scripted is not null) return scripted();

        lock (_gate)
        {
            var row = _ledger.FirstOrDefault(t => t.Id == transactionId);
            if (row is null) return ProblemResponse(404, "transaction-not-found", "İşlem bulunamadı.");
            if (!reverse)
                return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(new
                {
                    transactionId = row.Id, appliedAmount = row.Applied, reversed = row.Reversed,
                }, Web));
            if (row.Reversed) return ProblemResponse(409, "already-reversed", "İşlem zaten geri alındı.");
            row.Reversed = true;
            return FakeHttpMessageHandler.Json(200, "{}");
        }
    }

    // ── ortak ──────────────────────────────────────────────────────────────

    /// <summary>Bu uca bekletme kurulduysa (<see cref="Hold"/>) bırakılana kadar bekler.</summary>
    private async Task WaitHoldAsync(string endpoint, CancellationToken ct)
    {
        PushHold? hold;
        lock (_gate) _holds.TryGetValue(endpoint, out hold);
        if (hold is not null) await hold.WaitAsync(ct);
    }

    /// <summary>Ödeme uçlarının 400'ü: istemci geçersiz gövde göndermemeli — <see cref="Faults"/>'a yazılır.</summary>
    private HttpResponseMessage PaymentInvalid(string code, string detail)
    {
        _faults.Enqueue($"geçersiz ödeme isteği: {code} — {detail}");
        return ProblemResponse(400, code, detail);
    }

    private static bool TryMembers(JsonElement root, out List<string> members, out string? error)
    {
        members = new List<string>();
        error = null;
        if (!root.TryGetProperty("memberIds", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() is 0 or > MaxPaymentMembers)
        {
            error = $"memberIds 1–{MaxPaymentMembers} üye taşır.";
            return false;
        }
        foreach (var m in array.EnumerateArray())
        {
            var id = m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (!IsMemberId(id))
            {
                error = $"Üye Id'si boş olamaz, en çok {MaxMemberIdLength} karakter.";
                return false;
            }
            members.Add(id!);
        }
        members = members.Distinct(StringComparer.Ordinal).ToList();
        return true;
    }

    private static bool IsMemberId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxMemberIdLength;

    private static bool IsLeaseScope(string? scopeKey) =>
        scopeKey is { Length: <= MaxScopeKeyLength }
        && (scopeKey == "cumulative"
            || (scopeKey.StartsWith("session:", StringComparison.Ordinal)
                && Guid.TryParseExact(scopeKey.AsSpan("session:".Length), "N", out _)));

    private static bool IsRecordScope(string? scopeKey) =>
        IsLeaseScope(scopeKey)
        || (scopeKey is { Length: <= MaxScopeKeyLength }
            && (scopeKey == "legacy" || (scopeKey.StartsWith("legacy:", StringComparison.Ordinal) && scopeKey.Length > 7)));

    private static bool IsMoney(decimal? value) =>
        value is { } v && v >= 0 && v <= MaxMoney && decimal.Round(v, 2) == v;

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string? StringOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? DecimalOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static int? IntOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? BoolOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static Guid? GuidOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)
            ? g
            : null;

    /// <summary>Guid dizisi; dizi değilse ya da bir öğe Guid değilse null.</summary>
    private static List<Guid>? GuidsOf(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var g)) return null;
            list.Add(g);
        }
        return list;
    }

    private static Guid? QueryGuid(string query, string name) =>
        Regex.Match(query, $@"[?&]{name}=([0-9a-fA-F-]{{32,36}})") is { Success: true } m
        && Guid.TryParse(m.Groups[1].Value, out var g)
            ? g
            : null;
}
