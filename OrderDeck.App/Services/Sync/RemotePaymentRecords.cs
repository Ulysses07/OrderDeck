using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Uzak kayıt okumasının anahtarı: kişi × kapsam. Önbellekte <c>(ScopeKey, Person.MemberKey)</c> ile tutulur; istek
/// kişinin üyeleriyle gider.</summary>
public readonly record struct RemotePaymentKey(string ScopeKey, PaymentPerson Person);

/// <summary>
/// Bir kişi × kapsamın başka bilgisayarlardaki ödeme kayıtları, yerel işle birleştirilmeye hazır (spec 2026-10-13 §6.4
/// "Teslim kayıtlarının okunması"; W3-10). İki bilgisayarın saati karşılaştırılmaz: sıra sunucunun kapanış sırasıdır ve yerel
/// işin son teslimi listede bu kopyanın en yeni kaydının yerindedir.
/// </summary>
/// <param name="NewestOther">Başka kopyaların en yeni teslim edilmiş ya da sonucu bilinmeyen kaydı — kişi × kapsamın yerel işi
/// yoksa son istek budur.</param>
/// <param name="NewestOtherAfterLocal">Yerel işin son tesliminden SONRA kapanmış en yeni başka kopya kaydı; null = yerel iş en
/// yeni (listede bu kopyanın kaydı yok ya da yerel kaydı henüz gönderilmedi).</param>
public sealed record RemotePaymentLast(PaymentRequestListItem? NewestOther, PaymentRequestListItem? NewestOtherAfterLocal)
{
    /// <summary>Birleşik son istek uzak kayıtsa o kayıt; yerel işse null.</summary>
    /// <param name="hasLocal">Kişi × kapsamın teslim edilmiş (ya da gitmiş olabilecek) yerel işi var mı.</param>
    public PaymentRequestListItem? LastWith(bool hasLocal) => hasLocal ? NewestOtherAfterLocal : NewestOther;
}

/// <summary>
/// Önbelleğin bir okumalık görüntüsü (<see cref="RemotePaymentRecords.Snapshot"/>): kayıtlar, bu kopyanın kimlikleri ve
/// kuyrukta bekleyen yerel kayıtlar BİR KEZ okunur — rozetler yüzlerce kişi için aynı görüntüyü kullanır.
/// </summary>
public sealed class RemotePaymentSnapshot
{
    private readonly IReadOnlyDictionary<(string Scope, string MemberKey), IReadOnlyList<PaymentRequestListItem>> _entries;
    private readonly IReadOnlySet<Guid> _own;
    private readonly bool _anyQueuedFinish;
    private readonly IReadOnlyList<(string Scope, IReadOnlySet<string> Members)> _queuedRecords;

    internal RemotePaymentSnapshot(
        IReadOnlyDictionary<(string Scope, string MemberKey), IReadOnlyList<PaymentRequestListItem>> entries,
        IReadOnlySet<Guid> own, bool anyQueuedFinish, IReadOnlyList<(string Scope, IReadOnlySet<string> Members)> queuedRecords)
    {
        _entries = entries;
        _own = own;
        _anyQueuedFinish = anyQueuedFinish;
        _queuedRecords = queuedRecords;
    }

    /// <summary>
    /// Kişi × kapsamın uzak kayıtları; okunmamışsa (bütçe bitti, okuma düştü, hiç istenmedi) null — çağıran yalnız yerel
    /// işi kullanır.
    /// <list type="bullet">
    ///   <item>Birleştirmeye yalnız BAŞKA kopyaların teslim edilmiş ya da sonucu bilinmeyen kayıtları girer; bu kopyanın (ve
    ///   bekleyen önceki kimliğinin) kayıtları yerel işle zaten temsil edilir. Canlı kira (sonucu henüz yok) girmez — "zaten
    ///   istendi" sorusunun (e) adımı gibi.</item>
    ///   <item>Yerel işin son teslimi, listede bu kopyanın en yeni kaydının yerindedir. Listede bu kopyanın kaydı yoksa ya da
    ///   yerel kaydı henüz gönderilmediyse (kuyrukta bekleyen kirasız kayıt ya da teslim/belirsiz <c>finish</c>) yerel iş en
    ///   yenidir.</item>
    /// </list>
    /// </summary>
    public RemotePaymentLast? For(string scopeKey, PaymentPerson person)
    {
        if (!_entries.TryGetValue((scopeKey, person.MemberKey), out var records)) return null;

        var ownIndex = -1;
        for (var i = 0; i < records.Count; i++)
            if (_own.Contains(records[i].ReplicaId)) ownIndex = i;
        var localQueued = _anyQueuedFinish
            || _queuedRecords.Any(q => string.Equals(q.Scope, scopeKey, StringComparison.Ordinal)
                                       && person.MemberIds.Any(q.Members.Contains));
        var localAt = ownIndex < 0 || localQueued ? records.Count : ownIndex;

        PaymentRequestListItem? newest = null;
        PaymentRequestListItem? afterLocal = null;
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (_own.Contains(record.ReplicaId) || !IsPrompt(record)) continue;
            newest = record;
            if (i > localAt) afterLocal = record;
        }
        return new RemotePaymentLast(newest, afterLocal);
    }

    /// <summary>"Zaten istendi" sorusuna giren kayıt: teslim edilmiş ya da sonucu bilinmeyen (gitmiş olabilir).</summary>
    private static bool IsPrompt(PaymentRequestListItem record) =>
        record.State is PaymentRequestStates.Delivered or PaymentRequestStates.Unconfirmed;
}

/// <summary>
/// Başka bilgisayarların ödeme isteği kayıtlarının tekil önbelleği (spec 2026-10-13 §6.4 "Teslim kayıtlarının okunması", §6.7,
/// M17, §15-2; W3-10): <c>GET payment-requests?scopeKey&amp;memberIds</c>. "Basılanlar" rozetleri, fiyat düzeltmesinin
/// <c>BalancePaidLabels</c>'ı ve #508'in "ödendi, fark" planı (<c>CorrectedPaymentRequests</c>) yerel işleri bununla
/// birleştirir: PC-A'nın isteği PC-B'nin rozetlerinde ve planında da görünür.
///
/// <para><b>Yalnız mod On.</b> Yerel kip 0'da hiç istek gitmez ve görüntü yoktur (bugünkü davranış); kip okunamazsa da öyle.
/// Kip kapanınca önbellek boşalır.</para>
///
/// <para><b>Okuma rozeti ve planı ASLA engellemez.</b> Hesaplar zaman uyumlu ve yalnız önbellekten okur; çağıranlar önce
/// zaman uyumsuz ön okuma (<see cref="RefreshAsync"/>) yapar, sonra yeniden hesaplar. Okuma düşerse, bütçe biterse ya da süre
/// dolarsa okunamayan kişi × kapsam yalnız yerel kalır. Para güvenliği <c>begin</c>'in "zaten istendi" sorusundadır; bu
/// görüntü bilgi içindir.</para>
///
/// <para><b>Hız sınırı</b> (§12 risk 9): aynı NAT arkasındaki bilgisayarlar 100 istek/dk/IP'yi müşteri ve satış akışıyla
/// paylaşır. Bir ön okuma lisans çözümü dahil en çok <see cref="RequestBudget"/> istek atar ve en çok
/// <see cref="RefreshBudget"/> sürer; ön okumalar sırayla koşar (aynı anahtar iki kez istenmez). Okunmuş anahtar, zorlanmadıkça
/// yeniden istenmez.</para>
///
/// <para><b>Geçersizleşme:</b> satış akışından değişiklik inince (<see cref="SalesChangesPullService.SalesChanged"/>) ve bir
/// ödeme kaydı sunucuya geçince (<see cref="PaymentRecordSender.RecordsSent"/>; <c>finish</c> dahil) bütün önbellek boşalır.
/// Geçersizleşmeden önce başlamış okumanın yanıtı yazılmaz.</para>
///
/// <para>Ağ işi arayüz iş parçacığında koşmaz. Günlük yalnız sayı ve kod taşır (ad, telefon, tutar yok).</para>
/// </summary>
public sealed class RemotePaymentRecords
{
    /// <summary>Bir ön okumanın en çok isteği, lisans çözümü dahil (<see cref="PaymentRecordSender.MaxRequestsPerFlush"/>
    /// ile aynı ölçü).</summary>
    public const int RequestBudget = 10;

    /// <summary>Bir ön okumanın en uzun süresi (sırasını beklemek dahil); dolarsa okunabilenle yetinilir.</summary>
    public static readonly TimeSpan RefreshBudget = TimeSpan.FromSeconds(3);

    private readonly LicenseApiClient _api;
    private readonly ICurrentLicenseProvider _license;
    private readonly SalesSyncStateRepository _state;
    private readonly ReplicaIdentityRepository _identity;
    private readonly PendingPaymentRecordRepository _pending;
    private readonly ILogger<RemotePaymentRecords> _log;

    private readonly object _gate = new();
    private readonly Dictionary<(string Scope, string MemberKey), IReadOnlyList<PaymentRequestListItem>> _entries = new();

    /// <summary>Geçersizleşme sayacı: okuma başladığındaki değer değiştiyse yanıt yazılmaz.</summary>
    private long _generation;

    /// <summary>Ön okumalar sırayla: aynı anahtar aynı anda iki kez istenmez.</summary>
    private readonly SemaphoreSlim _refresh = new(1, 1);

    /// <summary>Uyarısı düşülmüş ret kodları (400): her yüklemede yinelenmesin.</summary>
    private readonly HashSet<string> _warnedCodes = new(StringComparer.Ordinal);

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    /// <param name="pull">Verilirse <see cref="SalesChangesPullService.SalesChanged"/> önbelleği boşaltır.</param>
    /// <param name="records">Verilirse <see cref="PaymentRecordSender.RecordsSent"/> önbelleği boşaltır.</param>
    public RemotePaymentRecords(
        LicenseApiClient api,
        ICurrentLicenseProvider license,
        SalesSyncStateRepository state,
        ReplicaIdentityRepository identity,
        PendingPaymentRecordRepository pending,
        ILogger<RemotePaymentRecords> log,
        SalesChangesPullService? pull = null,
        PaymentRecordSender? records = null)
    {
        _api = api;
        _license = license;
        _state = state;
        _identity = identity;
        _pending = pending;
        _log = log;
        if (pull is not null) pull.SalesChanged += (_, _) => Invalidate();
        if (records is not null) records.RecordsSent += (_, _) => Invalidate();
    }

    /// <summary>Bir ön okumanın süresi; testler değiştirir.</summary>
    internal TimeSpan RefreshTimeout { get; set; } = RefreshBudget;

    /// <summary>Yerel kip On mu (okunamazsa false). Kip Off'ta ön okumanın kişi araması bile yapılmaz.</summary>
    public bool IsModeOn
    {
        get
        {
            try { return _state.GetMode() == 1; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Uzak ödeme kayıtları: yerel kip okunamadı; yalnız yerel");
                return false;
            }
        }
    }

    /// <summary>Bütün önbelleği boşaltır; sürmekte olan okumanın yanıtı yazılmaz.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _entries.Clear();
        }
    }

    /// <summary>
    /// Hesap için görüntü (zaman uyumlu, ağ yok). Yerel kip On değilse null (ve önbellek boşalır); yerel bir okuma düşerse de
    /// null — çağıran yalnız yerel işleri kullanır.
    /// </summary>
    public RemotePaymentSnapshot? Snapshot()
    {
        if (!IsModeOn)
        {
            Invalidate();
            return null;
        }
        try
        {
            Dictionary<(string Scope, string MemberKey), IReadOnlyList<PaymentRequestListItem>> entries;
            lock (_gate) entries = new(_entries);

            var own = new HashSet<Guid>();
            if (_identity.GetReplicaId() is { } replica) own.Add(replica);
            if (_identity.GetPendingPreviousReplicaId() is { } previous) own.Add(previous);

            var anyQueuedFinish = false;
            var queuedRecords = new List<(string Scope, IReadOnlySet<string> Members)>();
            foreach (var row in _pending.List())
            {
                try
                {
                    if (row.Kind == PendingPaymentRecordKinds.Finish)
                    {
                        // Finish'in yükü kişiyi ve kapsamı taşımaz: teslim ya da belirsiz finish beklerken her kişi × kapsamda
                        // yerel iş en yeni sayılır (kısa pencere; bağlantı yokken liste de okunamaz).
                        if (PendingPaymentRecordCodec.DecodeFinish(row).Outcome is PaymentRequestStates.Delivered
                            or PaymentRequestStates.Unconfirmed)
                            anyQueuedFinish = true;
                    }
                    else if (row.Kind == PendingPaymentRecordKinds.Record)
                    {
                        var item = PendingPaymentRecordCodec.DecodeRecord(row);
                        queuedRecords.Add((item.ScopeKey, item.MemberIds.ToHashSet(StringComparer.Ordinal)));
                    }
                }
                catch (JsonException)
                {
                    // Çözülemeyen satırı gönderici düşürür; burada sayılmaz.
                }
            }
            return new RemotePaymentSnapshot(entries, own, anyQueuedFinish, queuedRecords);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Uzak ödeme kayıtlarının görüntüsü kurulamadı; yalnız yerel işler kullanılıyor");
            return null;
        }
    }

    /// <summary>
    /// Verilen kişi × kapsamların uzak kayıtlarını okur (arka plan iş parçacığında): <paramref name="keys"/> sırayla, zorlanmadıkça
    /// yalnız önbellekte olmayanlar; lisans çözümü dahil en çok <see cref="RequestBudget"/> istek, en çok
    /// <see cref="RefreshTimeout"/>. Anahtarlar tembel numaralanır (kişi araması da arka planda ve bütçeyle durur).
    /// <para>Yerel kip On değilse istek gitmez. Ağ hatası, sunucu hatası ve uç yok okumayı durdurur; 400 o anahtarı atlar.
    /// Fırlatmaz (iptal hariç): okuma düşerse okunamayanlar yalnız yerel kalır.</para>
    /// </summary>
    /// <param name="force">Önbellekte olsa da yeniden oku — fiyat düzeltme çekmecesinin açılışı (planın tutarı).</param>
    /// <returns>Önbellekte bir kişi × kapsam yeni geldi ya da değişti: çağıran yeniden hesaplamalı.</returns>
    public Task<bool> RefreshAsync(IEnumerable<RemotePaymentKey> keys, bool force = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!IsModeOn) return Task.FromResult(false);
        return Task.Run(() => RefreshCoreAsync(keys, force, ct), CancellationToken.None);
    }

    private async Task<bool> RefreshCoreAsync(IEnumerable<RemotePaymentKey> keys, bool force, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RefreshTimeout);
        try
        {
            await _refresh.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("Uzak ödeme kayıtları: önceki okuma {Budget} sn içinde bitmedi; bu yükleme yalnız yerel",
                RefreshTimeout.TotalSeconds);
            return false;
        }

        var tally = new Tally();
        try
        {
            return await FetchAsync(keys, force, tally, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("Uzak ödeme kayıtları: okuma {Budget} sn içinde bitmedi ({Fetched} kişi × kapsam okundu)",
                RefreshTimeout.TotalSeconds, tally.Fetched);
            return tally.Changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Uzak ödeme kayıtları okunamadı ({Fetched} kişi × kapsam okundu); okunamayanlar yalnız yerel",
                tally.Fetched);
            return tally.Changed;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Bir ön okumanın sayaçları.</summary>
    private sealed class Tally
    {
        public int Budget = RequestBudget;
        public int Fetched;
        public bool Changed;
    }

    private async Task<bool> FetchAsync(IEnumerable<RemotePaymentKey> keys, bool force, Tally tally, CancellationToken ct)
    {
        var licenseKey = _license.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return false;

        long generation;
        lock (_gate) generation = _generation;

        Guid? licenseId = null;
        var seen = new HashSet<(string Scope, string MemberKey)>();
        var exhausted = false;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!PaymentHistoryRecords.IsLeaseScope(key.ScopeKey)) continue;
            var cacheKey = (key.ScopeKey, key.Person.MemberKey);
            if (!seen.Add(cacheKey)) continue;
            if (!force)
                lock (_gate)
                    if (_entries.ContainsKey(cacheKey)) continue;
            var members = WireMembers(key.Person);
            if (members.Count == 0) continue;

            licenseId ??= await ResolveLicenseIdAsync(licenseKey, tally, ct).ConfigureAwait(false);
            if (licenseId is null) return tally.Changed;
            if (tally.Budget <= 0)
            {
                // Bütçe bitti: kalan anahtarlar (ve kişi aramaları) sonraki yüklemeye.
                exhausted = true;
                break;
            }

            tally.Budget--;
            var http = await _api.GetPaymentRequestsAsync(licenseId.Value, key.ScopeKey, members, ct).ConfigureAwait(false);
            switch (http.Transport)
            {
                case PaymentRequestTransport.Ok:
                    tally.Fetched++;
                    if (Store(cacheKey, http.Response!.Records, generation)) tally.Changed = true;
                    break;
                case PaymentRequestTransport.Problem:
                {
                    var code = http.Problem!.Code ?? "";
                    bool first;
                    lock (_gate) first = _warnedCodes.Add(code);
                    if (first)
                        _log.LogWarning("Uzak ödeme kayıtları: liste isteği reddedildi (HTTP {Status}, {Code}); o kişi × kapsam "
                                      + "yalnız yerel", http.StatusCode, code);
                    break;
                }
                default:
                    _log.LogDebug("Uzak ödeme kayıtları: liste okunamadı ({Transport}, HTTP {Status}); okuma durdu, {Fetched} "
                                + "kişi × kapsam okundu", http.Transport, http.StatusCode, tally.Fetched);
                    return tally.Changed;
            }
        }

        if (tally.Fetched > 0 || exhausted)
            _log.LogDebug("Uzak ödeme kayıtları: {Fetched} kişi × kapsam okundu (bütçe bitti: {Exhausted}, değişti: {Changed})",
                tally.Fetched, exhausted, tally.Changed);
        return tally.Changed;
    }

    /// <summary>Yanıtı yazar — okuma başladıktan sonra önbellek geçersizleştiyse yazmaz. Yeni ya da değişmişse true.</summary>
    private bool Store(
        (string Scope, string MemberKey) cacheKey, IReadOnlyList<PaymentRequestListItem> records, long generation)
    {
        lock (_gate)
        {
            if (_generation != generation) return false;
            var changed = !_entries.TryGetValue(cacheKey, out var old) || !Same(old, records);
            _entries[cacheKey] = records;
            return changed;
        }
    }

    private static bool Same(IReadOnlyList<PaymentRequestListItem> a, IReadOnlyList<PaymentRequestListItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Id != y.Id || x.ReplicaId != y.ReplicaId || x.State != y.State || x.NetAmount != y.NetAmount
                || x.AppliedAmount != y.AppliedAmount || x.FullyFromBalance != y.FullyFromBalance
                || x.TotalBeforeBalance != y.TotalBeforeBalance || x.FinishedAt != y.FinishedAt
                || !x.OrderIds.SequenceEqual(y.OrderIds))
                return false;
        }
        return true;
    }

    /// <summary>Teldeki üye listesi: boş ya da <see cref="PaymentRequestLimits.MaxMemberIdLength"/>'ten uzun Id atlanır, tekil,
    /// sahip kuralı sırasıyla en çok <see cref="PaymentRequestLimits.MaxMembers"/>.</summary>
    private static IReadOnlyList<string> WireMembers(PaymentPerson person) =>
        person.MemberIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= PaymentRequestLimits.MaxMemberIdLength)
            .Distinct(StringComparer.Ordinal)
            .Take(PaymentRequestLimits.MaxMembers)
            .ToList();

    /// <summary>LicenseId; çözülemezse null. Ağ isteği bütçeden düşer.</summary>
    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, Tally tally, CancellationToken ct)
    {
        if (_cachedLicenseId is { } cached && _cachedLicenseKey == licenseKey) return cached;
        tally.Budget--;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return null;
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return match.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Uzak ödeme kayıtları: LicenseId çözülemedi");
            return null;
        }
    }
}
