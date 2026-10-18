using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bekleyen bir <c>finish</c>'in yükü (spec 2026-10-13 §6.4 "Akışın sonu"): <c>POST payment-requests/{id}/finish</c>
/// gövdesinin zarf dışı kısmı. Kopya kimliği ve görülen kip gönderim anının zarfından gelir.</summary>
/// <param name="Outcome"><see cref="PaymentRequestStates.Delivered"/>, <see cref="PaymentRequestStates.Unconfirmed"/> ya
/// da <see cref="PaymentRequestStates.Abandoned"/>.</param>
/// <param name="AppliedAmount">Bakiyeden düşülen tutar; bilinmiyorsa null.</param>
public sealed record PendingPaymentFinish(
    Guid RecordId,
    string Outcome,
    decimal NetAmount,
    decimal? AppliedAmount,
    bool FullyFromBalance);

/// <summary>
/// <c>PendingPaymentRecord.PayloadJson</c>'ın biçimi (spec 2026-10-13 §3.2): <c>finish</c> → <see cref="PendingPaymentFinish"/>,
/// <c>record</c> → <see cref="PaymentRequestBackfillItem"/>, ikisi de web adlandırmasıyla (camelCase). Satırın anahtarı
/// kaydın Id'sidir ("N"). Zarf (kopya kimliği, protokol, görülen kip) yükte TUTULMAZ: geri yüklemeden sonra bekleyen
/// kaydı yeni kimlik gönderir ve sunucu onu <c>PreviousReplicaId</c> zinciriyle tanır (§8.7).
/// </summary>
public static class PendingPaymentRecordCodec
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Kuyruğun anahtarı: kaydın Id'si, "N" biçimi.</summary>
    public static string Key(Guid recordId) => recordId.ToString("N");

    public static string EncodeFinish(PendingPaymentFinish finish)
    {
        ArgumentNullException.ThrowIfNull(finish);
        return JsonSerializer.Serialize(finish, Web);
    }

    public static string EncodeRecord(PaymentRequestBackfillItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return JsonSerializer.Serialize(item, Web);
    }

    /// <exception cref="JsonException">Yük çözülemiyor, eksik ya da satırın anahtarıyla uyuşmuyor.</exception>
    public static PendingPaymentFinish DecodeFinish(PendingPaymentRecordRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var finish = JsonSerializer.Deserialize<PendingPaymentFinish>(row.PayloadJson, Web)
                     ?? throw new JsonException("Bekleyen finish'in yükü boş.");
        if (!MatchesKey(row, finish.RecordId))
            throw new JsonException("Bekleyen finish'in kayıt Id'si satırın anahtarıyla uyuşmuyor.");
        if (finish.Outcome is not (PaymentRequestStates.Delivered or PaymentRequestStates.Unconfirmed
            or PaymentRequestStates.Abandoned))
            throw new JsonException("Bekleyen finish'in sonucu tanınmıyor.");
        return finish;
    }

    /// <summary>Kirasız kayıt. Sunucunun 400 vereceği ya da istemcinin göndermeden reddedeceği biçim (üyesiz, 100'den
    /// fazla üyeli, kapsamsız, sipariş kümesiz) çözülemez sayılır: hiçbir zaman gönderilemez.</summary>
    /// <exception cref="JsonException">Yük çözülemiyor, eksik ya da satırın anahtarıyla uyuşmuyor.</exception>
    public static PaymentRequestBackfillItem DecodeRecord(PendingPaymentRecordRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var item = JsonSerializer.Deserialize<PaymentRequestBackfillItem>(row.PayloadJson, Web)
                   ?? throw new JsonException("Bekleyen kaydın yükü boş.");
        if (!MatchesKey(row, item.RecordId))
            throw new JsonException("Bekleyen kaydın Id'si satırın anahtarıyla uyuşmuyor.");
        if (string.IsNullOrWhiteSpace(item.OwnerId) || string.IsNullOrWhiteSpace(item.ScopeKey)
            || string.IsNullOrWhiteSpace(item.State) || item.OrderIds is null
            || item.MemberIds is not { Count: > 0 and <= PaymentRequestLimits.MaxMembers } members
            || members.Any(string.IsNullOrWhiteSpace))
            throw new JsonException("Bekleyen kaydın sahibi, üyeleri, kapsamı, durumu ya da sipariş kümesi eksik.");
        return item;
    }

    private static bool MatchesKey(PendingPaymentRecordRow row, Guid recordId) =>
        recordId != Guid.Empty && string.Equals(row.RecordId, Key(recordId), StringComparison.Ordinal);
}

/// <summary>Bir gönderimin sayıları (<see cref="PaymentRecordSender.FlushAsync"/>).</summary>
/// <param name="Sent">Sunucunun aldığı ve kuyruktan düşen kayıtlar (<c>finish</c> 200; geri doldurmada <c>applied</c>
/// ya da <c>unchanged</c>).</param>
/// <param name="Kept">İsteği gidip sonucu alınamayan, kuyrukta kalan kayıtlar (deneme sayıldı).</param>
/// <param name="Dropped">Son durumla kuyruktan düşen kayıtlar: <c>record-not-found</c>, <c>content-conflict</c> ya da
/// çözülemeyen yük.</param>
/// <param name="Remaining">Gönderimden sonra kuyruktaki kayıt sayısı (bu arada yazılanlar dahil).</param>
public sealed record PaymentRecordFlush(int Sent, int Kept, int Dropped, int Remaining)
{
    public static PaymentRecordFlush Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Bekleyen ödeme kayıtlarının gönderimi (spec 2026-10-13 §3.2, §4.7, §6.4 "Akışın sonu" ve "Ödeme geçmişi", §8.4-1;
/// W3-05). Kuyruğa iki tür girer (<see cref="PendingPaymentRecordRepository"/>): gönderilemeyen <c>finish</c>
/// (<see cref="EnqueueFinish"/>) ve mod Off'taki kirasız kayıt (<see cref="EnqueueRecord"/>). İkisi de iki kipte gider;
/// sürücüleri geri doldurma turu (<see cref="SalesBackfillService"/>, 60 sn, geri doldurmanın durumundan bağımsız) ve mod
/// On açılışının 1. adımıdır (<see cref="SalesOpeningFlush"/>, İLK kuyruk). Hepsi TEK kapıdan: aynı kayıt aynı anda iki
/// kez gitmez.
///
/// <para><b>Sıra ve bütçe:</b> kuyruk eskiden yeniye işlenir. <c>finish</c> kayıt başına bir istektir; art arda gelen
/// kirasız kayıtlar tek <c>payment-requests/backfill</c> partisinde, en çok <see cref="PaymentRequestLimits.MaxBackfillBatch"/>
/// kayıtla gider. Bir gönderimde lisans çözümü dahil en çok <see cref="MaxRequestsPerFlush"/> istek (aynı NAT arkasındaki
/// PC'lerin 100 istek/dk/IP bütçesi, §12 risk 9); kalan sonraki gönderime.</para>
///
/// <para><b>Sonuç:</b>
/// <list type="bullet">
///   <item><c>finish</c> 200 ve geri doldurmanın <c>applied</c>/<c>unchanged</c>'i: satır silinir.</item>
///   <item><c>finish</c>'in 404 <c>record-not-found</c> ve 409 <c>content-conflict</c>'i, geri doldurmanın öğe başına
///   <c>content-conflict</c>'i: son durum (§4.7) — satır silinir, sayı ve Id günlüğe yazılır, yeniden gönderilmez.
///   Çözülemeyen yük de böyle düşer: hiçbir zaman gönderilemez.</item>
///   <item>Ağ hatası, sunucu hatası ve uç yok: deneme sayılır (<c>Attempts + 1</c>, <c>LastError</c>), satır kalır,
///   gönderim durur; sonraki gönderim aynı içeriği aynen yineler (sunucu Id ve içerikle tanır).</item>
///   <item>Öbür ret (400 geçersiz istek): istemci hatası — satır SİLİNMEZ (para geçmişi sessizce kaybolmasın;
///   fail-closed), deneme sayılır, kayıt başına bir kez uyarı düşer; gönderim sonraki kayıtla sürer.</item>
/// </list></para>
///
/// <para>v2 zarfı <see cref="SalesSyncEnvelope.ForPush"/>'tan; hazır değilse (geri yüklenmiş süreçte hello, state ve kip
/// adımı henüz yok, süreç <see cref="SalesSyncEnvelope.HoldUntilRestart"/> ile kapalı) ya da kopya kimliği yoksa (uçlar
/// yalnız v2 konuşur) hiç istek gitmez, deneme sayılmaz. Boş kuyrukta istek yok. Ağ işi arayüz iş parçacığında koşmaz;
/// günlük yalnız Id ve sayı taşır (tutar, ad yok).</para>
/// </summary>
public sealed class PaymentRecordSender
{
    /// <summary>Bir gönderimdeki en çok istek: lisans çözümü, <c>finish</c>'ler ve geri doldurma partileri.</summary>
    public const int MaxRequestsPerFlush = 10;

    private readonly LicenseApiClient _api;
    private readonly PendingPaymentRecordRepository _repo;
    private readonly SalesSyncEnvelope _envelope;
    private readonly ICurrentLicenseProvider _license;
    private readonly IClock _clock;
    private readonly ILogger<PaymentRecordSender> _log;

    /// <summary>Tek gönderim kapısı: geri doldurma turu ve açılışın 1. adımı sıraya girer.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>400 alan kayıtlar: kayıt başına bir kez uyarı (60 sn'lik tur her seferinde yinelemesin).</summary>
    private readonly HashSet<string> _rejectionWarned = new(StringComparer.Ordinal);

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public PaymentRecordSender(
        LicenseApiClient api,
        PendingPaymentRecordRepository repo,
        SalesSyncEnvelope envelope,
        ICurrentLicenseProvider license,
        IClock clock,
        ILogger<PaymentRecordSender> log)
    {
        _api = api;
        _repo = repo;
        _envelope = envelope;
        _license = license;
        _clock = clock;
        _log = log;
    }

    /// <summary>Gönderilemeyen <c>finish</c>'i kuyruğa yazar (kayıt Id'sine göre idempotent: zaten kuyruktaysa false).</summary>
    public bool EnqueueFinish(PendingPaymentFinish finish)
    {
        var payload = PendingPaymentRecordCodec.EncodeFinish(finish);
        var queued = _repo.Enqueue(PendingPaymentRecordCodec.Key(finish.RecordId), PendingPaymentRecordKinds.Finish,
            payload, _clock.UnixNow());
        _log.LogInformation("Bekleyen ödeme kaydı (finish) {RecordId}: kuyruğa {Result}", finish.RecordId,
            queued ? "alındı" : "zaten kuyrukta");
        return queued;
    }

    /// <summary>Kirasız kaydı kuyruğa yazar (kayıt Id'sine göre idempotent: zaten kuyruktaysa false).</summary>
    public bool EnqueueRecord(PaymentRequestBackfillItem item)
    {
        var payload = PendingPaymentRecordCodec.EncodeRecord(item);
        var queued = _repo.Enqueue(PendingPaymentRecordCodec.Key(item.RecordId), PendingPaymentRecordKinds.Record,
            payload, _clock.UnixNow());
        _log.LogInformation("Bekleyen ödeme kaydı (record) {RecordId}: kuyruğa {Result}", item.RecordId,
            queued ? "alındı" : "zaten kuyrukta");
        return queued;
    }

    /// <summary>Kuyruğu bütçe içinde gönderir. Kapı başka bir gönderimdeyse sırasını bekler (ardından kalanı görür).
    /// Yerel veritabanı hatası çağırana fırlar; iptal <see cref="OperationCanceledException"/>.</summary>
    public Task<PaymentRecordFlush> FlushAsync(CancellationToken ct) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await ProcessAsync(ct).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }, ct);

    /// <summary>Bir gönderimin sayaçları ve bütçesi.</summary>
    private sealed class Tally(int budget)
    {
        public int Budget = budget;
        public int Sent;
        public int Kept;
        public int Dropped;
        public bool Stopped;
        public string? StopReason;
        public readonly List<string> Conflicts = new();
    }

    private async Task<PaymentRecordFlush> ProcessAsync(CancellationToken ct)
    {
        var rows = _repo.List();
        if (rows.Count == 0) return PaymentRecordFlush.Empty;

        if (_envelope.ForPush() is not { IsV2: true } envelope)
        {
            _log.LogDebug("Bekleyen ödeme kayıtları: v2 zarfı hazır değil, istek gitmedi ({Count} kayıt kuyrukta)",
                rows.Count);
            return new PaymentRecordFlush(0, 0, 0, rows.Count);
        }
        var licenseKey = _license.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return new PaymentRecordFlush(0, 0, 0, rows.Count);

        var tally = new Tally(MaxRequestsPerFlush);
        var licenseId = await ResolveLicenseIdAsync(licenseKey, tally, ct).ConfigureAwait(false);
        if (licenseId is null) return new PaymentRecordFlush(0, 0, 0, rows.Count);

        var replicaId = envelope.ReplicaId!.Value;
        var i = 0;
        while (i < rows.Count && tally.Budget > 0 && !tally.Stopped)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            if (row.Kind == PendingPaymentRecordKinds.Finish)
            {
                i++;
                await SendFinishAsync(licenseId.Value, replicaId, envelope.SeenMode, row, tally, ct)
                    .ConfigureAwait(false);
                continue;
            }
            if (row.Kind != PendingPaymentRecordKinds.Record)
            {
                i++;
                DropUnreadable(row, "tanınmayan tür", tally);
                continue;
            }

            // Art arda gelen kirasız kayıtlar tek partide (≤ 200).
            var batch = new List<(PendingPaymentRecordRow Row, PaymentRequestBackfillItem Item)>();
            while (i < rows.Count && rows[i].Kind == PendingPaymentRecordKinds.Record
                   && batch.Count < PaymentRequestLimits.MaxBackfillBatch)
            {
                var candidate = rows[i++];
                try { batch.Add((candidate, PendingPaymentRecordCodec.DecodeRecord(candidate))); }
                catch (JsonException ex) { DropUnreadable(candidate, ex.Message, tally); }
            }
            if (batch.Count > 0)
                await SendBatchAsync(licenseId.Value, replicaId, envelope.SeenMode, batch, tally, ct)
                    .ConfigureAwait(false);
        }

        if (tally.Conflicts.Count > 0)
            _log.LogWarning(
                "Bekleyen ödeme kayıtları: {Count} kayıt son durumla düştü (record-not-found / content-conflict; yeniden "
              + "gönderilmez): {RecordIds}",
                tally.Conflicts.Count, string.Join(",", tally.Conflicts));

        var remaining = _repo.Count();
        if (tally.Sent + tally.Dropped > 0)
            _log.LogInformation(
                "Bekleyen ödeme kayıtları: {Sent} kayıt sunucuya geçti, {Dropped} son durumla düştü, {Kept} kaldı; "
              + "kuyrukta {Remaining}",
                tally.Sent, tally.Dropped, tally.Kept, remaining);
        else if (tally.Kept > 0)
            _log.LogDebug("Bekleyen ödeme kayıtları gönderilemedi ({Reason}); {Kept} kayıt kaldı, kuyrukta {Remaining}",
                tally.StopReason ?? "ret", tally.Kept, remaining);
        return new PaymentRecordFlush(tally.Sent, tally.Kept, tally.Dropped, remaining);
    }

    // ── finish ───────────────────────────────────────────────────────────

    private async Task SendFinishAsync(
        Guid licenseId, Guid replicaId, int seenMode, PendingPaymentRecordRow row, Tally tally, CancellationToken ct)
    {
        PendingPaymentFinish finish;
        try { finish = PendingPaymentRecordCodec.DecodeFinish(row); }
        catch (JsonException ex)
        {
            DropUnreadable(row, ex.Message, tally);
            return;
        }

        tally.Budget--;
        var request = new PaymentRequestFinishRequest(finish.Outcome, finish.NetAmount, finish.AppliedAmount,
            finish.FullyFromBalance, replicaId, SalesSyncEnvelope.Protocol, seenMode);
        var http = await _api.PostPaymentRequestFinishAsync(licenseId, finish.RecordId, request, ct)
            .ConfigureAwait(false);
        switch (http.Transport)
        {
            case PaymentRequestTransport.Ok:
                _repo.Delete(row.RecordId);
                tally.Sent++;
                return;
            case PaymentRequestTransport.Problem
                when http.Problem!.Code is PaymentRequestProblemCodes.RecordNotFound
                    or PaymentRequestProblemCodes.ContentConflict:
                _repo.Delete(row.RecordId);
                tally.Dropped++;
                tally.Conflicts.Add(row.RecordId);
                return;
            case PaymentRequestTransport.Problem:
                Reject(row.RecordId, http.Problem!.Code!, http.StatusCode, tally, 1);
                _repo.RecordAttempt(row.RecordId, http.Problem.Code);
                return;
            default:
                _repo.RecordAttempt(row.RecordId, Describe(http.Transport, http.StatusCode));
                tally.Kept++;
                Stop(tally, http.Transport, http.StatusCode);
                return;
        }
    }

    // ── kirasız kayıtlar ─────────────────────────────────────────────────

    private async Task SendBatchAsync(
        Guid licenseId, Guid replicaId, int seenMode,
        IReadOnlyList<(PendingPaymentRecordRow Row, PaymentRequestBackfillItem Item)> batch, Tally tally,
        CancellationToken ct)
    {
        tally.Budget--;
        var request = new PaymentRequestBackfillRequest(batch.Select(b => b.Item).ToList(), replicaId,
            SalesSyncEnvelope.Protocol, seenMode);
        var http = await _api.PostPaymentRequestBackfillAsync(licenseId, request, ct).ConfigureAwait(false);
        var ids = batch.Select(b => b.Row.RecordId).ToList();
        switch (http.Transport)
        {
            case PaymentRequestTransport.Ok:
            {
                // Yanıt istekle hizalı (istemci denetledi): öğe başına sonuç.
                var done = new List<string>();
                var conflicts = new List<string>();
                var unknown = new List<string>();
                for (var k = 0; k < batch.Count; k++)
                {
                    var status = http.Response!.Records[k].Status;
                    var target = status switch
                    {
                        PaymentRequestBackfillResult.StatusApplied or PaymentRequestBackfillResult.StatusUnchanged => done,
                        PaymentRequestBackfillResult.StatusContentConflict => conflicts,
                        _ => unknown,
                    };
                    target.Add(batch[k].Row.RecordId);
                }
                _repo.Delete(done.Concat(conflicts).ToList());
                tally.Sent += done.Count;
                tally.Dropped += conflicts.Count;
                tally.Conflicts.AddRange(conflicts);
                if (unknown.Count > 0)
                {
                    // Bu sürümün tanımadığı öğe sonucu: satır kalır, sonraki gönderim yineler (zararsız).
                    _repo.RecordAttempt(unknown, "bilinmeyen öğe sonucu");
                    tally.Kept += unknown.Count;
                    _log.LogWarning("Bekleyen ödeme kayıtları: {Count} kaydın geri doldurma sonucu tanınmadı; kuyrukta kaldı",
                        unknown.Count);
                }
                return;
            }
            case PaymentRequestTransport.Problem:
                Reject(ids[0], http.Problem!.Code!, http.StatusCode, tally, ids.Count);
                _repo.RecordAttempt(ids, http.Problem.Code);
                return;
            default:
                _repo.RecordAttempt(ids, Describe(http.Transport, http.StatusCode));
                tally.Kept += ids.Count;
                Stop(tally, http.Transport, http.StatusCode);
                return;
        }
    }

    // ── yardımcılar ──────────────────────────────────────────────────────

    /// <summary>Hiçbir zaman gönderilemeyecek satır (çözülemeyen ya da eksik yük, tanınmayan tür): son durum.</summary>
    private void DropUnreadable(PendingPaymentRecordRow row, string reason, Tally tally)
    {
        _repo.Delete(row.RecordId);
        tally.Dropped++;
        _log.LogWarning("Bekleyen ödeme kaydı {RecordId} ({Kind}) çözülemedi ({Reason}); kuyruktan düştü",
            row.RecordId, row.Kind, reason);
    }

    /// <summary>İstemci hatası (400 ya da beklenmeyen ret): satır kalır; kayıt başına bir kez uyarı.</summary>
    private void Reject(string firstRecordId, string code, int status, Tally tally, int count)
    {
        tally.Kept += count;
        if (_rejectionWarned.Add(firstRecordId))
            _log.LogWarning(
                "Bekleyen ödeme kaydı reddedildi (HTTP {Status}, {Code}; ilk={RecordId}, {Count} kayıt): istemci hatası, "
              + "kayıtlar kuyrukta kalıyor",
                status, code, firstRecordId, count);
    }

    private static void Stop(Tally tally, PaymentRequestTransport transport, int status)
    {
        tally.Stopped = true;
        tally.StopReason = Describe(transport, status);
    }

    private static string Describe(PaymentRequestTransport transport, int status) =>
        status > 0 ? $"{transport} HTTP {status}" : transport.ToString();

    /// <summary>LicenseId; çözülemezse null. Ağ isteği bütçeden düşer.</summary>
    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, Tally tally, CancellationToken ct)
    {
        if (_cachedLicenseId is { } cached && _cachedLicenseKey == licenseKey) return cached;
        tally.Budget--;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null)
            {
                _log.LogDebug("Bekleyen ödeme kayıtları: geçerli lisans anahtarının LicenseId'si bulunamadı");
                return null;
            }
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return match.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ağ, sunucu ya da çözümlenemeyen yanıt: istek gitmedi, deneme sayılmaz; sonraki gönderim yeniden çözer.
            _log.LogDebug(ex, "Bekleyen ödeme kayıtları: LicenseId çözülemedi");
            return null;
        }
    }
}
