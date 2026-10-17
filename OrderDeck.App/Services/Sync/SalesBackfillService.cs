using Microsoft.Extensions.Logging;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bir <see cref="SalesBackfillService"/> turunun sonucu.</summary>
public enum SalesBackfillTourOutcome
{
    /// <summary>Lisans yok ya da LicenseId çözülemedi — istek gitmedi.</summary>
    NoLicense,
    /// <summary>Yerel kopya kimliği eksik ya da bozuk: geri doldurma yalnız v2 konuşur.</summary>
    NoReplica,
    /// <summary>Bu (lisans, kopya) için <c>sales/hello</c> henüz başarılı değil — istek gitmedi.</summary>
    WaitingForHello,
    /// <summary>Bu lisans ve kopya için geri doldurma önceden tamamlandı — istek gitmedi.</summary>
    AlreadyCompleted,
    /// <summary>Partiler gitti; tur bütçesi doldu ya da bir parti sonraki tura kaldı.</summary>
    InProgress,
    /// <summary>İmleçten sonraki ilk yerel satır gönderilmeyi bekliyor: imleç onu geçmez, olağan push onaylayınca
    /// sonraki tur kaldığı yerden sürer (§8.2-3).</summary>
    AwaitingPush,
    /// <summary>Üç aşama bitti; yeniden kuyruğa alınan satırların <c>applied</c> onayı bekleniyor (§8.2-5).</summary>
    AwaitingRepair,
    /// <summary>Bu turda <c>hello {backfillCompleted: true}</c> gitti ve tamamlanma işaretlendi.</summary>
    Completed,
    /// <summary>Ağ, sunucu ya da yerel hata; imleç ilerlemedi, sonraki turda yeniden denenir.</summary>
    Failed,
}

/// <summary>
/// Ws'nin ilk açılışındaki geri doldurma (spec 2026-10-13 §8.2 adım 2–5, §4.3; W1-09): bu bilgisayarın yerel geçmişini
/// — oturumlar, etiketler, kargo bağları — <c>sales/backfill</c> ile sunucuda tamamlar, F05 öncesinin kayıp
/// güncellemelerini onarır ve bitince <c>hello {backfillCompleted: true}</c> gönderir. Mod kapısı bu bayrağı her
/// kopyada ister (§4.10-2); mod, onarım bitmeden açılamaz (M20).
///
/// <list type="number">
/// <item><b>Bekleme:</b> bu (lisans, kopya) için W1-02'nin hello'su başarılı olana kadar hiçbir istek yok — sunucu
/// hello'suz doğan kopyada yerel kazanmayı kapatır, geri yüklenmiş kopya önce önceki kimliğini bildirmeli (§8.7).</item>
/// <item><b>Boşaltma (§8.2-2):</b> süreç başına bir kez, oturum ve etiket kuyruğu gönderim kapısından boşaltılır
/// (<see cref="SyncFlushService.Drain"/>; tur dolu parti gönderdikçe yinelenir, canlı yayının yeni etiketlerini
/// kovalamaz), ardından bir kargo turu. Bu gönderimler olağan push'tur; tur bütçesine girmez.</item>
/// <item><b>Geri doldurma (§8.2-3):</b> imleç <c>SyncCursor('sales-backfill-out', LicenseKey)</c> — kodlaması
/// <see cref="SalesBackfillCursor"/>'da. Sırayla oturumlar, etiketler ve dosya bağları, rowid'e göre; listede en çok
/// <see cref="MaxBatch"/> öğe, turda en çok <see cref="MaxRequestsPerTour"/> istek (hız sınırı 100 istek/dk/IP,
/// §12 risk 9), tur 60 sn'de bir. İmleç her partinin yanıtı uygulandıktan sonra yazılır: kesilen tur kaldığı
/// partiden sürer, tekrarlanan parti zararsızdır. Yalnız onaylı satırlar gider ve imleç gönderilmeyi bekleyen satırı
/// geçmez (<see cref="BackfillWait"/>, <see cref="SalesBackfillTourOutcome.AwaitingPush"/>): v1 döneminden sunucuda
/// olan bekleyen satırı push güncelleme dalında yazar ve kaynak kopyasını ya da sahibini doldurmaz; onaylanınca geri
/// doldurma onu kaldığı yerden götürür. Gönderim kapısı partiler boyunca TUTULMAZ: canlı yayının ≈5 sn'lik
/// push'u sürer. Bitmiş, yerel ve devri bilinmeyen oturumun <c>Release</c>'i ve <c>UnsentLabelCount</c>'u
/// <see cref="HandoverGate"/> altında, §5.2'deki gibi hesaplanır; kilit yanıt uygulanana kadar tutulur.</item>
/// <item><b>Yanıt:</b> oturumda yalnız <c>ReleasedAt</c> saklanır (mod Off'ta <c>SyncedAt</c>, <c>Revision</c> ve
/// <c>Authority</c> değişmez); devri gerçekleşmeyen kendi oturumu olağan kuyruğa verilir (kapanış gönderimi kuyruk
/// boşalınca devreder). Siparişte <c>ServerVersion</c> yalnız satır okunduğundan beri değişmediyse ve ayrışmadıysa.
/// <c>foreign-license</c> son durumdur: sayı günlüğe yazılır, imleç ilerler, yeniden gönderilmez. Bütün partiyi düşüren
/// 400 <c>unknown-session</c> imleci ilerletmeden sonraki tura kalır.</item>
/// <item><b>Onarım (§8.2-4):</b> <c>diverged</c>'da yerel kazanırsa satır tek işlemde olağan kuyruğa yeniden alınır ve
/// <c>backfill-requeued</c> izi düşer; sunucu kazanırsa yalnız <c>backfill-diverged</c> izi. Olağan v2 push'u yeniden
/// alınan satırları götürür.</item>
/// <item><b>Tamamlanma (§8.2-5):</b> üç aşama bitti ∧ yeniden alınan her etiketin sonraki gönderimi <c>applied</c>
/// (<see cref="SalesBackfillRepository.GetCompletionState"/>). Sonra hello ve imleçte tamamlanma işareti; yeniden
/// koşmak zararsız. Hiç uygulanmayan satır tamamlanmayı bekletir ve günlük bunu söyler (mod Off'ta olmaz).</item>
/// </list>
///
/// <para>Lisans değişince imleç yeni lisansın anahtarındadır: geri doldurma yeni lisansa baştan gider. Geri yüklemeden
/// sonra kopya kimliği döner ve imleç yeni kopya için baştan başlar. Ödeme geçmişi (§8.2-6) W3'ün işidir.</para>
///
/// <para><b>Canlı yol beklemez:</b> tur arka plan iş parçacığında koşar, her <c>await</c> <c>ConfigureAwait(false)</c>;
/// ağ isteği uçarken açık SQLite bağlantısı ya da yazma kilidi yok. Günlük yalnız kimlik ve sayı taşır: müşteri,
/// makine adı ve lisans anahtarı yazılmaz.</para>
/// </summary>
public sealed class SalesBackfillService
{
    /// <summary>Bir listedeki en çok öğe (sunucunun <c>MaxBackfillBatch</c>'i).</summary>
    public const int MaxBatch = 200;

    /// <summary>Bir turdaki en çok istek: lisans çözümü, geri doldurma partileri ve tamamlanma hello'su.</summary>
    public const int MaxRequestsPerTour = 10;

    /// <summary>Sunucunun bütün partiyi düşüren oturum hatası (orders/sync F01 ile aynı).</summary>
    internal const string UnknownSessionCode = "unknown-session";

    /// <summary>Süren bir hata serisinde iki uyarı arasındaki en kısa süre.</summary>
    internal static readonly TimeSpan FailureWarningInterval = TimeSpan.FromMinutes(10);

    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly SalesSyncStateService _state;
    private readonly SessionOrderSyncService _push;
    private readonly ShipmentSyncService _shipments;
    private readonly SalesBackfillRepository _repo;
    private readonly SyncCursorRepository _cursors;
    private readonly HandoverGate _handover;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<SalesBackfillService> _log;

    private readonly SemaphoreSlim _tourGate = new(1, 1);

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private (string LicenseKey, Guid ReplicaId)? _drainedFor;
    private (string LicenseKey, Guid ReplicaId)? _restartLoggedFor;
    private BackfillCompletionState? _lastLoggedRepair;
    private (SalesBackfillPhase Phase, string? PendingId)? _lastLoggedWait;
    private int _failures;
    private DateTimeOffset? _lastFailureWarnAt;

    public SalesBackfillService(
        LicenseApiClient api,
        SalesSyncEnvelope envelope,
        SalesSyncStateService state,
        SessionOrderSyncService push,
        ShipmentSyncService shipments,
        SalesBackfillRepository repo,
        SyncCursorRepository cursors,
        HandoverGate handover,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<SalesBackfillService> log)
    {
        _api = api;
        _envelope = envelope;
        _state = state;
        _push = push;
        _shipments = shipments;
        _repo = repo;
        _cursors = cursors;
        _handover = handover;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
    }

    /// <summary>Bir tur. İptal belirteci <see cref="OperationCanceledException"/> ile çıkar; imleç son uygulanan
    /// partide kalır.</summary>
    public async Task<SalesBackfillTourOutcome> TourOnceAsync(CancellationToken ct)
    {
        await _tourGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TourCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn(ex, "yerel hata");
            return SalesBackfillTourOutcome.Failed;
        }
        finally
        {
            _tourGate.Release();
        }
    }

    private async Task<SalesBackfillTourOutcome> TourCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return SalesBackfillTourOutcome.NoLicense;
        if (_envelope.ReplicaId is not { } replicaId) return SalesBackfillTourOutcome.NoReplica;

        var cursor = ReadCursor(licenseKey, replicaId);
        if (cursor.Phase == SalesBackfillPhase.Completed) return SalesBackfillTourOutcome.AlreadyCompleted;
        // Geri yükleme kimliği bu süreçte yazılamadıysa hello da gitmez (zarf kapalı); ayrıca denetlenir: sunucu kopyayı
        // özgün sanıp yereli kazandırırdı (§4.3, M20).
        if (!_envelope.HasHelloSucceeded(licenseKey, replicaId) || _envelope.IsHeldUntilRestart)
            return SalesBackfillTourOutcome.WaitingForHello;

        var budget = MaxRequestsPerTour;
        var licenseId = await ResolveLicenseIdAsync(licenseKey, () => budget--, ct).ConfigureAwait(false);
        if (licenseId is null) return SalesBackfillTourOutcome.NoLicense;

        if (cursor.Phase < SalesBackfillPhase.Done && _drainedFor != (licenseKey, replicaId))
        {
            await DrainOutgoingAsync(ct).ConfigureAwait(false);
            _drainedFor = (licenseKey, replicaId);
        }

        while (cursor.Phase < SalesBackfillPhase.Done)
        {
            var read = cursor.Phase switch
            {
                SalesBackfillPhase.Sessions => ReadSessions(cursor),
                SalesBackfillPhase.Labels => ReadLabels(cursor),
                _ => ReadShipments(cursor),
            };
            if (read.Wait is { } wait)
            {
                // İmleç bekleyen satırı geçmez (§8.2-3): push onaylayınca sonraki tur kaldığı yerden sürer.
                LogWaiting(cursor.Phase, wait, replicaId);
                return SalesBackfillTourOutcome.AwaitingPush;
            }
            if (read.Send is not { } batch)
            {
                // Aşama bitti: istek yok, sonraki aşama.
                cursor = cursor.NextPhase();
                SaveCursor(licenseKey, replicaId, cursor);
                if (cursor.Phase == SalesBackfillPhase.Done)
                    _log.LogInformation("Geri doldurma: oturum, etiket ve dosya bağı aşamaları gönderildi (replica={ReplicaId})",
                        replicaId);
                continue;
            }
            if (budget <= 0) return SalesBackfillTourOutcome.InProgress;
            budget--;

            var next = await batch(licenseId.Value, replicaId, ct).ConfigureAwait(false);
            if (next is null) return SalesBackfillTourOutcome.Failed;
            cursor = next.Value;
            SaveCursor(licenseKey, replicaId, cursor);
            _failures = 0;
            _lastFailureWarnAt = null;
        }

        var repair = _repo.GetCompletionState();
        if (!repair.IsComplete)
        {
            LogRepairPending(repair, replicaId);
            return SalesBackfillTourOutcome.AwaitingRepair;
        }
        if (budget <= 0) return SalesBackfillTourOutcome.InProgress;

        if (!await _state.ReportBackfillCompletedAsync(licenseKey, licenseId.Value, replicaId, ct).ConfigureAwait(false))
        {
            Warn(null, "sales/hello (backfillCompleted) gönderilemedi");
            return SalesBackfillTourOutcome.Failed;
        }
        SaveCursor(licenseKey, replicaId, SalesBackfillCursor.Completed);
        _log.LogInformation(
            "Geri doldurma tamamlandı; sunucuya bildirildi (replica={ReplicaId}, yerelde olmayan yeniden kuyruk etiketi={Missing})",
            replicaId, repair.Missing);
        return SalesBackfillTourOutcome.Completed;
    }

    /// <summary>Bir partinin gönderimi: yanıt uygulanınca imlecin yeni değeri, sonraki tura kalınca <c>null</c>.</summary>
    private delegate Task<SalesBackfillCursor?> Batch(Guid licenseId, Guid replicaId, CancellationToken ct);

    /// <summary>Bir aşamanın okuması: gönderilecek parti (<see cref="Send"/>), imleci tutan bekleyen satır
    /// (<see cref="Wait"/>) ya da ikisi de yoksa aşamanın sonu.</summary>
    private readonly record struct BatchRead(Batch? Send, BackfillWait? Wait)
    {
        public static BatchRead Of(Batch send) => new(send, null);

        public static BatchRead Empty(BackfillWait? wait) => new(null, wait);
    }

    // ── boşaltma (§8.2-2) ────────────────────────────────────────────────

    /// <summary>
    /// Açılıştaki kuyruğun boşaltılması (§8.2-2). Tur yalnız dolu parti gönderdikçe yinelenir: o an canlı yayın
    /// sürüyorsa her tur yeni etiket bulur; boşaltma onları turlar arasında beklemeden kovalasaydı birkaç saniyede
    /// onlarca istek müşteri ve durum servisleriyle paylaşılan hız sınırını (§12 risk 9) tüketirdi. Yeni etiketleri
    /// ≈5 sn'lik gönderim götürür; bekletilen oturum canlı ritimdeki gibi seyreltilir.
    /// </summary>
    private async Task DrainOutgoingAsync(CancellationToken ct)
    {
        await SyncFlushService.Drain(async c =>
        {
            var r = await _push.SyncOnceAsync(liveCadence: true, c).ConfigureAwait(false);
            return r.OrdersPushed >= SessionOrderSyncService.OrderBatchSize
                || r.SessionsPushed >= SessionOrderSyncService.SessionBatchSize ? 1 : 0;
        })(ct).ConfigureAwait(false);
        await _shipments.SyncOnceAsync(ct).ConfigureAwait(false);
    }

    // ── oturumlar ────────────────────────────────────────────────────────

    /// <summary>Devir adayı (§5.2): bitmiş, bu bilgisayarın yazdığı ve devri henüz görülmemiş oturum.</summary>
    private static bool IsHandoverCandidate(StreamSession s) =>
        s.EndedAt is not null && s.Authority == StreamSessionAuthority.Local && s.ReleasedAt is null;

    private BatchRead ReadSessions(SalesBackfillCursor cursor)
    {
        var rows = _repo.GetSessionsAfter(cursor.RowId, MaxBatch);
        if (rows.Count == 0) return BatchRead.Empty(_repo.GetSessionWait(cursor.RowId));
        return BatchRead.Of((licenseId, replicaId, ct) => SendSessionsAsync(licenseId, replicaId, cursor, rows, ct));
    }

    private async Task<SalesBackfillCursor?> SendSessionsAsync(
        Guid licenseId, Guid replicaId, SalesBackfillCursor cursor, IReadOnlyList<BackfillSessionRow> rows,
        CancellationToken ct)
    {
        var candidates = rows.Select(r => r.Session).Where(IsHandoverCandidate).Select(s => s.Id)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        // Kilitler kimlik sırasıyla (gönderim de aynı sırayla alır). Gönderim kapısı tutulmaz.
        var leases = new List<IDisposable>(candidates.Count);
        try
        {
            foreach (var id in candidates) leases.Add(await _handover.EnterAsync(id, ct).ConfigureAwait(false));

            var unsent = candidates.ToDictionary(id => id, id => _repo.CountUnsyncedBySession(id), StringComparer.Ordinal);
            var machineName = string.IsNullOrEmpty(_envelope.MachineName) ? null : _envelope.MachineName;
            var items = rows
                .Select(r => SessionOrderSyncService.ToSyncSessionItem(r.Session, v2: true, machineName, unsent))
                .ToList();

            var response = await PostAsync(licenseId, replicaId, items, [], [], "oturum", ct).ConfigureAwait(false);
            if (response is null) return null;

            ApplySessions(rows, response.Sessions ?? [], unsent, replicaId);
            return cursor.After(rows[^1].RowId);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    /// <summary>
    /// Oturum yanıtı (§4.3, §5.2): devir anı geldiyse yalnız <c>ReleasedAt</c> saklanır. Devir adayının devri
    /// gerçekleşmediyse (gönderilmemiş etiketi var ya da sunucu devri yazmadı) ve sahip bu kopyaysa oturum olağan
    /// kuyruğa verilir: kapanış gönderimi onu kuyruk boşalınca devreder. Başka kopyanın oturumu ve başka lisansın
    /// Id'si yalnız sayılır.
    /// </summary>
    private void ApplySessions(
        IReadOnlyList<BackfillSessionRow> rows, IReadOnlyList<SalesBackfillSessionResult> results,
        IReadOnlyDictionary<string, int> unsent, Guid replicaId)
    {
        var byId = new Dictionary<Guid, SalesBackfillSessionResult>(results.Count);
        foreach (var r in results) byId[r.Id] = r;

        int released = 0, handed = 0, ownedElsewhere = 0, foreign = 0;
        foreach (var row in rows)
        {
            var s = row.Session;
            if (!byId.TryGetValue(Guid.Parse(s.Id), out var result)) continue;
            if (result.Reason == SalesBackfillOrderResult.ReasonForeignLicense)
            {
                foreign++;
                continue;
            }
            if (result.ReleasedAt is { } releasedAt)
            {
                if (_repo.StoreReleasedAt(s.Id, releasedAt.ToUnixTimeSeconds())) released++;
                continue;
            }
            if (!unsent.ContainsKey(s.Id)) continue;                      // devir adayı değil
            if (result.OwnerReplicaId == replicaId)
            {
                if (_repo.QueueSessionForHandover(s.Id, s.Revision)) handed++;
            }
            else
            {
                ownedElsewhere++;
            }
        }

        if (released + handed + ownedElsewhere + foreign > 0)
            _log.LogInformation(
                "Geri doldurma oturum partisi: {Sent} oturum; {Released} devir saklandı, {Handed} oturum devir için "
              + "gönderim kuyruğuna verildi, {OwnedElsewhere} oturum başka kopyanın, {Foreign} oturum başka lisansın "
              + "(son durum, yeniden gönderilmez)",
                rows.Count, released, handed, ownedElsewhere, foreign);
    }

    // ── etiketler ────────────────────────────────────────────────────────

    private BatchRead ReadLabels(SalesBackfillCursor cursor)
    {
        var rows = _repo.GetLabelsAfter(cursor.RowId, MaxBatch);
        if (rows.Count == 0) return BatchRead.Empty(_repo.GetLabelWait(cursor.RowId));
        return BatchRead.Of((licenseId, replicaId, ct) => SendLabelsAsync(licenseId, replicaId, cursor, rows, ct));
    }

    private async Task<SalesBackfillCursor?> SendLabelsAsync(
        Guid licenseId, Guid replicaId, SalesBackfillCursor cursor, IReadOnlyList<BackfillLabelRow> rows,
        CancellationToken ct)
    {
        // Gönderimle aynı tel öğesi; baseVersion geri doldurmada okunmaz.
        var items = rows.Select(r => SessionOrderSyncService.ToSyncOrderItem(r.Label) with { BaseVersion = null })
            .ToList();
        var response = await PostAsync(licenseId, replicaId, [], items, [], "etiket", ct).ConfigureAwait(false);
        if (response is null) return null;

        ApplyLabels(rows, response.Orders ?? [], response.Diverged ?? []);
        return cursor.After(rows[^1].RowId);
    }

    /// <summary>
    /// Sipariş yanıtı (§4.3, §8.2-4): ayrışmayan ve reddedilmeyen satırda <c>ServerVersion</c> (Revision
    /// karşılaştırmalı); ayrışan satırda yerel kazanırsa yeniden kuyruk, kazanmazsa iz. Reddedilen satır son durum.
    /// </summary>
    private void ApplyLabels(
        IReadOnlyList<BackfillLabelRow> rows, IReadOnlyList<SalesBackfillOrderResult> results,
        IReadOnlyList<SalesBackfillDiverged> diverged)
    {
        var byGuid = new Dictionary<Guid, LabelPushRow>(rows.Count);
        foreach (var r in rows) byGuid[Guid.Parse(r.Label.Id)] = r.Label;
        var divergedIds = diverged.Select(d => d.Id).ToHashSet();

        int foreign = 0, rejected = 0;
        var versions = new List<BackfillVersion>(results.Count);
        foreach (var result in results)
        {
            if (!byGuid.TryGetValue(result.Id, out var label)) continue;
            if (result.Status == SalesBackfillOrderResult.StatusRejected)
            {
                if (result.Reason == SalesBackfillOrderResult.ReasonForeignLicense) foreign++;
                else rejected++;
                continue;
            }
            if (result.Version is { } version && !divergedIds.Contains(result.Id))
                versions.Add(new BackfillVersion(label.Id, label.Revision, version));
        }
        var versioned = _repo.WriteServerVersions(versions);

        var now = _clock.UnixNow();
        int requeued = 0, tracked = 0, alreadyQueued = 0, serverWins = 0, missing = 0;
        foreach (var d in diverged)
        {
            if (!byGuid.TryGetValue(d.Id, out var label)) continue;
            var units = d.Units ?? Array.Empty<string>();
            if (!d.LocalWins)
            {
                _repo.LogDiverged(label.Id, units.ToList(), now);
                serverWins++;
                continue;
            }
            switch (_repo.Requeue(label.Id, units.ToList(), now))
            {
                case BackfillRequeueOutcome.Requeued: requeued++; break;
                case BackfillRequeueOutcome.Tracked: tracked++; break;
                case BackfillRequeueOutcome.AlreadyQueued: alreadyQueued++; break;
                default: missing++; break;
            }
        }

        if (diverged.Count + foreign + rejected > 0)
            _log.LogInformation(
                "Geri doldurma etiket partisi: {Sent} etiket, {Versioned} sürüm yazıldı; birim ayrışması: {Requeued} "
              + "etiket yeniden kuyrukta, {Tracked} zaten bekliyordu, {AlreadyQueued} önceden alınmıştı, {ServerWins} "
              + "sunucu kazanır (yalnız iz), {Missing} yerelde yok; {Foreign} etiket başka lisansın (son durum, yeniden "
              + "gönderilmez), {Rejected} ekleme reddedildi",
                rows.Count, versioned, requeued, tracked, alreadyQueued, serverWins, missing, foreign, rejected);
    }

    // ── dosya bağları ────────────────────────────────────────────────────

    private BatchRead ReadShipments(SalesBackfillCursor cursor)
    {
        var rows = _repo.GetShipmentLinksAfter(cursor.RowId, MaxBatch);
        if (rows.Count == 0) return BatchRead.Empty(null);
        return BatchRead.Of(async (licenseId, replicaId, ct) =>
        {
            var items = rows
                .Where(r => Guid.TryParse(r.ShipmentId, out _))
                .Select(r => new SalesBackfillShipment(
                    Guid.Parse(r.ShipmentId),
                    r.LabelIds.Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
                        .OfType<Guid>().ToList()))
                .ToList();
            var response = await PostAsync(licenseId, replicaId, [], [], items, "dosya bağı", ct).ConfigureAwait(false);
            return response is null ? null : cursor.After(rows[^1].RowId);
        });
    }

    // ── istek ────────────────────────────────────────────────────────────

    private async Task<SalesBackfillResponse?> PostAsync(
        Guid licenseId, Guid replicaId, IReadOnlyList<SyncSessionItem> sessions, IReadOnlyList<SyncOrderItem> orders,
        IReadOnlyList<SalesBackfillShipment> shipments, string phase, CancellationToken ct)
    {
        var request = new SalesBackfillRequest(
            sessions, orders, shipments, CatalogAware: true,
            ReplicaId: replicaId, Protocol: SalesSyncEnvelope.Protocol, SeenMode: _envelope.SeenMode);
        try
        {
            return await _api.PostSalesBackfillAsync(licenseId, request, ct).ConfigureAwait(false);
        }
        catch (LicenseApiException ex) when (ex.Code == UnknownSessionCode)
        {
            // Siparişin oturumu sunucuda yok (henüz gönderilmemiş yeni oturum): parti düştü, hiçbir şey yazılmadı.
            // İmleç ilerlemez; olağan push oturumu götürünce sonraki tur aynı partiyi yeniden dener.
            _log.LogInformation(
                "Geri doldurma {Phase} partisi sunucuda bilinmeyen oturum yüzünden düştü; sonraki turda yeniden "
              + "denenecek ({Count} öğe)", phase, sessions.Count + orders.Count + shipments.Count);
            return null;
        }
        catch (LicenseApiException ex)
        {
            Warn(ex, $"sales/backfill {phase} partisi gönderilemedi ({ex.Code})");
            return null;
        }
    }

    // ── imleç ────────────────────────────────────────────────────────────

    /// <summary>İmleç (<see cref="SalesBackfillCursor"/>): satır yoksa, çözülemiyorsa ya da başka kopyanınsa baştan.</summary>
    private SalesBackfillCursor ReadCursor(string licenseKey, Guid replicaId)
    {
        var row = _cursors.Get(SalesBackfillCursor.Name, licenseKey);
        if (row is null) return SalesBackfillCursor.Start;
        if (row.LastId != replicaId)
        {
            if (_restartLoggedFor != (licenseKey, replicaId))
            {
                _restartLoggedFor = (licenseKey, replicaId);
                _log.LogInformation(
                    "Geri doldurma imleci başka bir kopya kimliğine ait (geri yükleme); yeni kopya için baştan başlıyor "
                  + "(replica={ReplicaId})", replicaId);
            }
            return SalesBackfillCursor.Start;
        }
        if (row.Seq is not { } seq || SalesBackfillCursor.Decode(seq) is not { } cursor)
        {
            _log.LogWarning("Geri doldurma imleci çözülemedi ({Seq}); baştan başlıyor", row.Seq);
            return SalesBackfillCursor.Start;
        }
        return cursor;
    }

    private void SaveCursor(string licenseKey, Guid replicaId, SalesBackfillCursor cursor) =>
        _cursors.Upsert(SalesBackfillCursor.Name, licenseKey,
            seq: cursor.Encode(), updatedAt: DateTimeOffset.UtcNow, lastId: replicaId);

    // ── yardımcılar ──────────────────────────────────────────────────────

    /// <summary>İmleci tutan bekleyen satır: bekleyen satır değiştikçe bir kez yazılır (60 sn'lik tur her seferinde
    /// yinelemesin). Yalnız kimlik ve sayı.</summary>
    private void LogWaiting(SalesBackfillPhase phase, BackfillWait wait, Guid replicaId)
    {
        if (_lastLoggedWait == (phase, wait.PendingId)) return;
        _lastLoggedWait = (phase, wait.PendingId);
        _log.LogInformation(
            "Geri doldurma: imleçten sonraki ilk {Phase} gönderilmeyi bekliyor; olağan gönderim onaylayınca kaldığı "
          + "yerden sürecek (ilk={PendingId}, bekleyen={Pending}, replica={ReplicaId})",
            phase == SalesBackfillPhase.Sessions ? "oturum" : "etiket", wait.PendingId, wait.Pending, replicaId);
    }

    /// <summary>Tamamlanmayı bekleten satırlar: durum değiştikçe bir kez yazılır (60 sn'lik tur her seferinde
    /// yinelemesin).</summary>
    private void LogRepairPending(BackfillCompletionState state, Guid replicaId)
    {
        if (_lastLoggedRepair == state) return;
        _lastLoggedRepair = state;
        if (state.NotApplied > 0)
            _log.LogWarning(
                "Geri doldurma tamamlanamıyor: yeniden kuyruğa alınan {NotApplied} etiketin gönderimi sunucuda uygulanmadı "
              + "(yok sayıldı ya da reddedildi; iz SalesConflictLog'da), {AwaitingPush} etiket gönderim bekliyor — "
              + "backfillCompleted gönderilmeyecek (replica={ReplicaId})",
                state.NotApplied, state.AwaitingPush, replicaId);
        else
            _log.LogInformation(
                "Geri doldurma: yeniden kuyruğa alınan {AwaitingPush} etiket gönderim bekliyor; uygulanınca "
              + "tamamlanacak (replica={ReplicaId})",
                state.AwaitingPush, replicaId);
    }

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, Action spend, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey) return _cachedLicenseId;
        spend();
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null)
            {
                _log.LogDebug("Geri doldurma: geçerli lisans anahtarının LicenseId'si bulunamadı");
                return null;
            }
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (LicenseApiException ex)
        {
            Warn(ex, $"LicenseId çözülemedi ({ex.Code})");
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
            _log.LogDebug(ex, "Geri doldurma: {What} (art arda {Failures}. hata)", what, _failures);
            return;
        }
        _lastFailureWarnAt = now;
        _log.LogWarning(ex, "Geri doldurma: {What} (art arda {Failures}. hata); sonraki turda yeniden denenecek",
            what, _failures);
    }
}
