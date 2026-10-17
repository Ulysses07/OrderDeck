using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Bir operatör niyetinin komutu (spec 2026-10-13 §2 "Komut", §4.6): kimlik, tür, yükün teldeki JSON'u ve beklenen
/// sürümler. <see cref="CommandId"/> niyet başına BİR KEZ üretilir; her yeniden denemede kimlik, yük ve beklenen
/// sürümler AYNEN gider — sunucu aynı kimliği içerik özetiyle tanır, yanıtı kaybolan komut ikinci kez uygulanmaz
/// (yeniden oynatma). Yeniden denemenin girdisi her zaman son sonucun <see cref="SalesCommandOutcome.Intent"/>'idir:
/// <c>stale</c>'den sonraki kendiliğinden deneme beklenen sürümleri tazeler (kimlik aynı).
/// </summary>
/// <param name="PayloadJson">Yükün teldeki JSON'u (<see cref="SalesCommandPayload.ToWireJson"/>). Bekleyen komut
/// kuyruğu (§5.5) bunu saklar.</param>
/// <param name="Expected">Beklenen sürümler, sipariş Id'sine göre sıralı.</param>
public sealed record SalesCommandIntent(
    Guid CommandId,
    string Kind,
    string PayloadJson,
    IReadOnlyList<SalesCommandExpected> Expected);

/// <summary>Bir komut gönderiminin sonucu (§5.3'ün 409 listesi, §5.7, §5.8).</summary>
public enum SalesCommandStatus
{
    /// <summary>200: komut uygulandı (ya da zaten uygulanmıştı / etkisizdi); yanıtın satırları yerele yazıldı.</summary>
    Applied,

    /// <summary>409 <c>stale</c>: satırlar başka bilgisayarda değişti. Güncel satırlar yerele yazıldı; iptal, geri alma
    /// ve ilk baskı bir kez taze sürümlerle denendi, yine bayattı. Fiyat, yedek ve kargo hiç kendiliğinden denenmez.</summary>
    Stale,

    /// <summary>409 <c>plan-stale</c> (<c>set-code-price</c>): güncel satırlar yazıldı, plan yeniden gösterilir.</summary>
    PlanStale,

    /// <summary>409 <c>session-live</c>: yayın başka bilgisayarda canlı ya da devrediliyor.</summary>
    SessionLive,

    /// <summary>409 <c>owner-active</c> (force-end): sahip hâlâ sinyal veriyor.</summary>
    OwnerActive,

    /// <summary>409 <c>handing-over</c> (force-end): yayın bitti, sahipte gönderilmemiş etiket var.</summary>
    HandingOver,

    /// <summary>409 <c>shipment-shipped</c>: kargoya verilmiş dosya; güncel satırlar yazıldı.</summary>
    ShipmentShipped,

    /// <summary>409 <c>client-update-required</c>: operatörün işleminde güncelleme sorusu açıldı (süreç başına bir
    /// kez; arka plan gönderiminde açılmaz).</summary>
    ClientUpdateRequired,

    /// <summary>409 <c>too-many-candidates</c>: kalıcı; <c>SalesConflictLog</c>'a yazıldı.</summary>
    TooManyCandidates,

    /// <summary>409 <c>content-conflict</c>: istemci hatası; <c>SalesConflictLog</c>'a yazıldı, yeniden denenmez.</summary>
    ContentConflict,

    /// <summary>409 <c>mode-off</c>: sunucuda mod kapalı; <see cref="SalesCommandClient.ModeOffDetected"/> yükseldi.</summary>
    ModeOff,

    /// <summary>404: komut ucu yok.</summary>
    EndpointMissing,

    /// <summary>Ağ hatası ya da zaman aşımı: komut uygulanmış olabilir; aynı niyet aynen yeniden gönderilir.</summary>
    Offline,

    /// <summary>5xx ya da çözümlenemeyen yanıt: komut uygulanmış olabilir; aynı niyet aynen yeniden gönderilir.</summary>
    Failed,

    /// <summary>400: istemci hatası (geçersiz zarf, yük ya da bilinmeyen satır); yeniden denenmez.</summary>
    Invalid,

    /// <summary>Hiç istek gitmedi: yerel kip Off, zarf hazır değil (geri yüklemeden sonra kip tazelenmedi, kimlik yok)
    /// ya da lisans yok.</summary>
    NotReady,
}

/// <summary><see cref="SalesCommandClient.SendAsync"/>'in sonucu.</summary>
/// <param name="Intent">Son gönderilen (ya da gönderilecek) niyet: yeniden deneme bununla, aynen yapılır.</param>
/// <param name="Message">Operatöre görünen Türkçe metin (<see cref="SalesCommandMessages"/>); başarıda null.</param>
/// <param name="ServerStatus">200'ün durumu: <c>applied</c> ya da <c>unchanged</c>.</param>
/// <param name="Result">200'ün türe özgü sonucu (marked, alreadyPrinted, cancelled, reprintIds, …).</param>
/// <param name="Problem">Son ret (ek üyeleriyle).</param>
/// <param name="LabelIds">Yerelde değeri değişen etiketler (yanıt ya da ret satırlarından); arayüz bunları tazeler.</param>
public sealed record SalesCommandOutcome(
    SalesCommandStatus Status,
    SalesCommandIntent Intent,
    string? Message,
    string? ServerStatus,
    SalesCommandResult? Result,
    SalesCommandProblem? Problem,
    IReadOnlyList<string> LabelIds,
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<string> ShipmentIds)
{
    public bool IsApplied => Status == SalesCommandStatus.Applied;

    /// <summary>Aynı niyet aynen yeniden gönderilebilir (sonuç bilinmiyor ya da istek hiç gitmedi).</summary>
    public bool CanResend => Status is SalesCommandStatus.Offline or SalesCommandStatus.Failed
        or SalesCommandStatus.EndpointMissing or SalesCommandStatus.NotReady;
}

/// <summary>
/// Satış komutlarının istemcisi (spec 2026-10-13 §4.6, §5.3, §5.7; W2-04): devredilmiş yayının etiketini ve kargo
/// dosyasını sunucuda değiştiren on komutun tel sözleşmesi. Yönlendirici (<c>ILabelEditor</c>, §5.3) yalnız mod On'da
/// ve <c>Authority = 'server'</c> satırda bunu çağırır; mod Off'ta hiçbir istek gitmez.
///
/// <para><b>Niyet</b> (<see cref="Prepare"/>, <see cref="Cancel"/>, …): yeni <c>CommandId</c>, yükün JSON'u ve beklenen
/// sürümler (<see cref="LabelRepository.GetServerVersions"/>; kargo kararında <see cref="Shipment.ServerVersion"/>
/// yükte). Sürümü bilinmeyen satır 0 ile gider: sunucu <c>stale</c> ve güncel satırı döner.</para>
///
/// <para><b>Gönderim</b> (<see cref="SendAsync"/>): yerel <c>SalesSyncState.Mode</c> 1 değilse, zarf
/// (<see cref="SalesSyncEnvelope.ForPush"/>) yoksa ya da v1 ise HTTP yok (<see cref="SalesCommandStatus.NotReady"/>).
/// 200'ün ve 409'un güncel satırları (<c>orders</c>/<c>current</c>, <c>shipments</c>/<c>currentShipments</c>,
/// <c>sessions</c>) akışla AYNI fonksiyonla yazılır (<see cref="SalesFeedRepository.ApplyItem"/>, dosyalar dahil):
/// gönderim kuyruğuna dokunulmaz (<c>SyncedAt</c>, <c>Revision</c> aynı; DM8). Yazılamayan satır (U5, ödeme akışı,
/// eksik müşteri) akışa kalır — komutun etkisi sunucuda, akış onu getirir.</para>
///
/// <para><b>409'lar</b> (§5.3): <c>stale</c>/<c>plan-stale</c> satırları uygular; yalnız iptal, geri alma ve ilk
/// baskı (niyeti idempotent) taze sürümlerle, aynı kimlikle BİR KEZ kendiliğinden yeniden denenir. Fiyat, yedek ve
/// kargo sessizce denenmez. <c>client-update-required</c> güncelleme sorusunu açar
/// (<see cref="IAppUpdatePrompt"/>) — yalnız operatörün işleminde ve süreç başına bir kez, arka plan gönderiminde hiç;
/// <c>too-many-candidates</c> ve <c>content-conflict</c> birer
/// <c>SalesConflictLog</c> satırı yazar; <c>mode-off</c> <see cref="ModeOffDetected"/>'ı yükseltir (Off turu,
/// §8.5).</para>
///
/// <para>Ağ ve veritabanı işi arayüz iş parçacığında koşmaz (<see cref="SendAsync"/> arka plana geçer). Günlük yalnız
/// kimlik, tür ve sayı taşır.</para>
/// </summary>
public sealed class SalesCommandClient
{
    /// <summary>Komut yanıtı dosyaları da yazar (§4.6 adım 7); ilk yetişme izi yalnız akışın işidir.</summary>
    private static readonly SalesFeedApplyOptions ResponseOptions = new(ApplyShipments: true, CountCatchUpOverwrite: false);

    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly SalesSyncStateRepository _state;
    private readonly LabelRepository _labels;
    private readonly ShipmentRepository _shipments;
    private readonly SalesFeedRepository _feed;
    private readonly SalesConflictLogRepository _conflicts;
    private readonly IAppUpdatePrompt _updatePrompt;
    private readonly ICurrentLicenseProvider _license;
    private readonly IClock _clock;
    private readonly ILogger<SalesCommandClient> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    /// <summary>Bu süreçte güncelleme sorusu komut reddinden açıldı (<see cref="OpenUpdatePromptOnce"/>).</summary>
    private int _updatePromptOpened;

    public SalesCommandClient(
        LicenseApiClient api,
        SalesSyncEnvelope envelope,
        SalesSyncStateRepository state,
        LabelRepository labels,
        ShipmentRepository shipments,
        SalesFeedRepository feed,
        SalesConflictLogRepository conflicts,
        IAppUpdatePrompt updatePrompt,
        ICurrentLicenseProvider license,
        IClock clock,
        ILogger<SalesCommandClient> log)
    {
        _api = api;
        _envelope = envelope;
        _state = state;
        _labels = labels;
        _shipments = shipments;
        _feed = feed;
        _conflicts = conflicts;
        _updatePrompt = updatePrompt;
        _license = license;
        _clock = clock;
        _log = log;
    }

    /// <summary>Sunucu 409 <c>mode-off</c> döndü: istemci modu yanlış biliyor; Off geçişi (§8.5) başlamalı. İstemcinin
    /// iş parçacığında yükselir; dinleyicinin hatası gönderimi düşürmez.</summary>
    public event EventHandler? ModeOffDetected;

    // ── niyet ──────────────────────────────────────────────────────────────

    /// <summary><c>cancel</c>: etiketler ve neden; beklenen sürümler etiketlerin.</summary>
    public SalesCommandIntent Cancel(IReadOnlyCollection<string> labelIds, string reason) =>
        Prepare(new SalesCancelPayload(SortedIds(labelIds), reason));

    /// <summary><c>uncancel</c>: etiketler; beklenen sürümler etiketlerin.</summary>
    public SalesCommandIntent Uncancel(IReadOnlyCollection<string> labelIds) =>
        Prepare(new SalesUncancelPayload(SortedIds(labelIds)));

    /// <summary><c>mark-printed</c>: basış kimliği ve etiketlerin kâğıttaki sırası (1'den).</summary>
    public SalesCommandIntent MarkPrinted(string batchId, IReadOnlyList<(string LabelId, int Seq)> items) =>
        Prepare(new SalesMarkPrintedPayload(batchId,
            items.Select(i => new SalesMarkPrintedItem(ToGuid(i.LabelId), i.Seq)).OrderBy(i => i.OrderId).ToList()));

    /// <summary><c>reprint</c>: etiketler kâğıt sırasıyla; yazmaz, beklenen sürüm yok.</summary>
    public SalesCommandIntent Reprint(IReadOnlyList<string> labelIds) =>
        Prepare(new SalesReprintPayload(labelIds.Select(ToGuid).Distinct().ToList()));

    /// <summary><c>confirm-backup</c>: geçici yedek ve (verildiyse) yeni fiyat.</summary>
    public SalesCommandIntent ConfirmBackup(string backupLabelId, decimal? newPrice) =>
        Prepare(new SalesConfirmBackupPayload(ToGuid(backupLabelId), newPrice));

    /// <summary><c>shipment-decision</c>: dosyanın sürümü yükte, yerel aynanın <see cref="Shipment.ServerVersion"/>'ından
    /// (bilinmiyorsa 0: sunucu <c>stale</c> ve güncel dosyayı döner).</summary>
    public SalesCommandIntent ShipmentDecision(string shipmentId, string decision)
    {
        var version = _shipments.GetById(shipmentId)?.ServerVersion ?? 0;
        return Prepare(new SalesShipmentDecisionPayload(ToGuid(shipmentId), decision, checked((int)version)));
    }

    /// <summary>
    /// Bir niyet kurar: yeni <c>CommandId</c>, yükün JSON'u ve türün beklenen sürüm kümesi — <c>cancel</c>,
    /// <c>uncancel</c>, <c>mark-printed</c> ve <c>confirm-backup</c> için yükün etiketleri; <c>set-code-price</c>
    /// (plan) ve <c>attach-shipment</c> (aday kümesi) için <paramref name="expectedLabelIds"/>; öbür türlerde boş.
    /// </summary>
    /// <param name="expectedLabelIds">Yalnız plan/aday kümesi taşıyan türlerde (zorunlu); öbürlerinde verilmez.</param>
    public SalesCommandIntent Prepare(SalesCommandPayload payload, IReadOnlyCollection<string>? expectedLabelIds = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var setKind = payload is SalesSetCodePricePayload or SalesAttachShipmentPayload;
        if (setKind && expectedLabelIds is null)
            throw new ArgumentException($"{payload.Kind} beklenen kümeyi (plan ya da adaylar) ister.", nameof(expectedLabelIds));
        if (!setKind && expectedLabelIds is not null)
            throw new ArgumentException($"{payload.Kind} beklenen kümeyi yükten kurar.", nameof(expectedLabelIds));

        IReadOnlyList<Guid> expectedIds = payload switch
        {
            SalesCancelPayload p => p.Items,
            SalesUncancelPayload p => p.Items,
            SalesMarkPrintedPayload p => p.Items.Select(i => i.OrderId).ToList(),
            SalesConfirmBackupPayload p => [p.BackupOrderId],
            _ when setKind => expectedLabelIds!.Select(ToGuid).Distinct().ToList(),
            _ => [],
        };
        return new SalesCommandIntent(Guid.NewGuid(), payload.Kind, payload.ToWireJson(), ReadExpected(expectedIds));
    }

    // ── gönderim ───────────────────────────────────────────────────────────

    /// <summary>
    /// Niyeti gönderir ve sonucu uygular. Arka plan iş parçacığında koşar; yalnız <paramref name="ct"/> iptali
    /// fırlatır. Yeniden deneme her zaman dönen <see cref="SalesCommandOutcome.Intent"/> ile yapılır.
    /// </summary>
    /// <param name="interactive">Operatörün o anki işlemi (varsayılan). <c>false</c>: arka plan gönderimi (bekleyen
    /// komutların turu, §5.5) — <c>client-update-required</c> güncelleme sorusunu AÇMAZ; satır içi metin yine döner.</param>
    public Task<SalesCommandOutcome> SendAsync(
        SalesCommandIntent intent, CancellationToken ct = default, bool interactive = true)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Task.Run(() => SendCoreAsync(intent, interactive, ct), ct);
    }

    private async Task<SalesCommandOutcome> SendCoreAsync(SalesCommandIntent intent, bool interactive, CancellationToken ct)
    {
        var rows = new AppliedRows();

        // Mod Off (bugün her lisans): hiçbir istek gitmez — kalp atışı bile. Yönlendirici zaten yerel yolu seçer.
        int mode;
        try { mode = _state.GetMode(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış komutu {Kind} {CommandId}: yerel kip okunamadı; gönderilmedi", intent.Kind, intent.CommandId);
            return Outcome(SalesCommandStatus.NotReady, intent, SalesCommandMessages.NotReady, rows);
        }
        if (mode != 1)
        {
            _log.LogDebug("Satış komutu {Kind} {CommandId}: yerel kip Off; gönderilmedi", intent.Kind, intent.CommandId);
            return Outcome(SalesCommandStatus.NotReady, intent, SalesCommandMessages.NotReady, rows);
        }
        if (_envelope.ForPush() is not { IsV2: true } envelope)
        {
            _log.LogInformation("Satış komutu {Kind} {CommandId}: v2 zarfı hazır değil; gönderilmedi", intent.Kind, intent.CommandId);
            return Outcome(SalesCommandStatus.NotReady, intent, SalesCommandMessages.NotReady, rows);
        }
        var licenseKey = _license.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey))
            return Outcome(SalesCommandStatus.NotReady, intent, SalesCommandMessages.NotReady, rows);
        var (licenseId, offline) = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
        if (licenseId is null)
            return offline
                ? Outcome(SalesCommandStatus.Offline, intent, SalesCommandMessages.Offline, rows)
                : Outcome(SalesCommandStatus.NotReady, intent, SalesCommandMessages.NotReady, rows);

        var current = intent;
        var retried = false;
        while (true)
        {
            var request = new SalesCommandRequest(current.CommandId, envelope.ReplicaId!.Value, SalesSyncEnvelope.Protocol,
                envelope.SeenMode, current.Kind, ParsePayload(current.PayloadJson), current.Expected);
            var http = await _api.PostSalesCommandAsync(licenseId.Value, request, ct).ConfigureAwait(false);
            switch (http.Transport)
            {
                case SalesCommandTransport.Ok:
                {
                    var response = http.Response!;
                    ApplyRows(rows, response.Sessions, response.Orders, response.Shipments, current);
                    _log.LogInformation(
                        "Satış komutu {Kind} {CommandId}: {Status} — {Labels} etiket, {Sessions} oturum, {Shipments} dosya yerele yazıldı, {Deferred} satır akışa kaldı",
                        current.Kind, current.CommandId, response.Status, rows.Labels.Count, rows.Sessions.Count,
                        rows.Shipments.Count, rows.Deferred);
                    return Outcome(SalesCommandStatus.Applied, current, null, rows, response.Status, response.Result);
                }
                case SalesCommandTransport.EndpointMissing:
                    _log.LogWarning("Satış komutu {Kind} {CommandId}: komut ucu yok (404)", current.Kind, current.CommandId);
                    return Outcome(SalesCommandStatus.EndpointMissing, current, SalesCommandMessages.EndpointMissing, rows);
                case SalesCommandTransport.Offline:
                    _log.LogInformation("Satış komutu {Kind} {CommandId}: sunucuya ulaşılamadı", current.Kind, current.CommandId);
                    return Outcome(SalesCommandStatus.Offline, current, SalesCommandMessages.Offline, rows);
                case SalesCommandTransport.Failed:
                    _log.LogWarning("Satış komutu {Kind} {CommandId}: sunucu hatası (HTTP {Status}): {Error}",
                        current.Kind, current.CommandId, http.StatusCode, http.Error);
                    return Outcome(SalesCommandStatus.Failed, current, SalesCommandMessages.Failed, rows);
            }

            var problem = http.Problem!;
            if (problem.Code is SalesCommandProblemCodes.Stale or SalesCommandProblemCodes.PlanStale)
            {
                ApplyRows(rows, null, problem.Current, problem.CurrentShipments, current);
                if (!retried && IsIdempotent(current.Kind))
                {
                    var fresh = current with { Expected = ReadExpected(current.Expected.Select(e => e.OrderId).ToList()) };
                    if (!fresh.Expected.SequenceEqual(current.Expected))
                    {
                        _log.LogInformation(
                            "Satış komutu {Kind} {CommandId}: {Code}; güncel satırlar uygulandı, taze sürümlerle bir kez yeniden deneniyor",
                            current.Kind, current.CommandId, problem.Code);
                        retried = true;
                        current = fresh;
                        continue;
                    }
                }
                _log.LogInformation("Satış komutu {Kind} {CommandId}: {Code}; güncel satırlar uygulandı ({Labels} etiket)",
                    current.Kind, current.CommandId, problem.Code, rows.Labels.Count);
                var status = problem.Code == SalesCommandProblemCodes.PlanStale
                    ? SalesCommandStatus.PlanStale
                    : SalesCommandStatus.Stale;
                return Outcome(status, current, SalesCommandMessages.Stale, rows, problem: problem);
            }
            return Rejected(current, problem, rows, interactive);
        }
    }

    /// <summary>Stale dışındaki retler (§5.3).</summary>
    private SalesCommandOutcome Rejected(
        SalesCommandIntent intent, SalesCommandProblem problem, AppliedRows rows, bool interactive)
    {
        _log.LogInformation("Satış komutu {Kind} {CommandId} reddedildi: {Code}", intent.Kind, intent.CommandId, problem.Code);
        switch (problem.Code)
        {
            case SalesCommandProblemCodes.SessionLive:
                return Outcome(SalesCommandStatus.SessionLive, intent,
                    SalesCommandMessages.SessionLive(problem.Machine, problem.OwnerLastSeenAt), rows, problem: problem);
            case SalesCommandProblemCodes.OwnerActive:
                return Outcome(SalesCommandStatus.OwnerActive, intent,
                    SalesCommandMessages.OwnerActive(problem.Machine, problem.LastSeenAt), rows, problem: problem);
            case SalesCommandProblemCodes.HandingOver:
                return Outcome(SalesCommandStatus.HandingOver, intent,
                    SalesCommandMessages.HandingOver(problem.Machine, problem.UnsentLabelCount), rows, problem: problem);
            case SalesCommandProblemCodes.ShipmentShipped:
                ApplyRows(rows, null, problem.Current, problem.CurrentShipments, intent);
                return Outcome(SalesCommandStatus.ShipmentShipped, intent,
                    problem.OrderId is not null
                        ? SalesCommandMessages.ShipmentShippedPrice
                        : SalesCommandMessages.ShipmentShippedDecision,
                    rows, problem: problem);
            case SalesCommandProblemCodes.ClientUpdateRequired:
                OpenUpdatePromptOnce(intent, interactive);
                return Outcome(SalesCommandStatus.ClientUpdateRequired, intent, SalesCommandMessages.ClientUpdateRequired,
                    rows, problem: problem);
            case SalesCommandProblemCodes.TooManyCandidates:
                RecordConflict(intent, SalesConflictLog.CommandTooManyCandidates, problem.Count, problem.Max);
                return Outcome(SalesCommandStatus.TooManyCandidates, intent,
                    SalesCommandMessages.TooManyCandidates(problem.Count), rows, problem: problem);
            case SalesCommandProblemCodes.ContentConflict:
                RecordConflict(intent, SalesConflictLog.CommandContentConflict, null, null);
                return Outcome(SalesCommandStatus.ContentConflict, intent, SalesCommandMessages.ContentConflict, rows,
                    problem: problem);
            case SalesCommandProblemCodes.ModeOff:
                RaiseModeOffDetected();
                return Outcome(SalesCommandStatus.ModeOff, intent, SalesCommandMessages.ModeOff, rows, problem: problem);
            default:
                _log.LogWarning("Satış komutu {Kind} {CommandId}: istemci hatası sayılan ret {Code}", intent.Kind,
                    intent.CommandId, problem.Code);
                return Outcome(SalesCommandStatus.Invalid, intent, SalesCommandMessages.ContentConflict, rows, problem: problem);
        }
    }

    /// <summary>
    /// <c>client-update-required</c>'ın güncelleme sorusu (§5.3) — yalnız operatörün işleminde ve süreç başına BİR
    /// KEZ. Soru modaldır: arka plandaki bekleyen komut her 30 sn'lik turda yeniden reddedilir, her seferinde açılsaydı
    /// yayın sırasında ana pencereyi kilitlerdi (sahibin #508 tercihi: akışlarda açılır pencere yok). "Hayır" Velopack'te
    /// sonraki açılışa bırakır; aynı süreçte yeniden sorulmaz. Satır içi metin her retle döner.
    /// </summary>
    private void OpenUpdatePromptOnce(SalesCommandIntent intent, bool interactive)
    {
        if (!interactive || Interlocked.Exchange(ref _updatePromptOpened, 1) == 1)
        {
            _log.LogDebug("Satış komutu {Kind} {CommandId}: güncelleme gerekli; soru açılmadı ({Reason})", intent.Kind,
                intent.CommandId, interactive ? "bu süreçte zaten soruldu" : "arka plan gönderimi");
            return;
        }
        try { _updatePrompt.CheckAndPrompt(); }
        catch (Exception ex) { _log.LogWarning(ex, "Güncelleme sorusu açılamadı"); }
    }

    /// <summary>Niyeti idempotent türler (§5.3): taze sürümlerle kendiliğinden bir kez yeniden denenir.</summary>
    private static bool IsIdempotent(string kind) =>
        kind is SalesCommandKinds.Cancel or SalesCommandKinds.Uncancel or SalesCommandKinds.MarkPrinted;

    private void RecordConflict(SalesCommandIntent intent, string kind, int? count, int? max)
    {
        try
        {
            _conflicts.Add(_clock.UnixNow(), null, kind, SalesConflictLog.CommandReason(intent.Kind, intent.CommandId, count, max));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış komutu {Kind} {CommandId}: SalesConflictLog yazılamadı", intent.Kind, intent.CommandId);
        }
    }

    private void RaiseModeOffDetected()
    {
        try { ModeOffDetected?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "Satış komutu: mod kapandı bildirimi iletilemedi"); }
    }

    // ── satırların uygulanması ─────────────────────────────────────────────

    /// <summary>Yanıtın ya da retin değişen satırları (bütün denemeler boyunca).</summary>
    private sealed class AppliedRows
    {
        public readonly HashSet<string> Labels = new(StringComparer.Ordinal);
        public readonly HashSet<string> Sessions = new(StringComparer.Ordinal);
        public readonly HashSet<string> Shipments = new(StringComparer.Ordinal);
        public int Deferred;
    }

    /// <summary>
    /// Satırları akış öğesi gibi yazar: önce oturumlar, sonra siparişler (oturumları yan liste olarak), sonra dosyalar.
    /// Müşteri taslağı komut yanıtında yok: yerelde olmayan siparişin müşterisi de yoksa öğe akışa kalır. Öğe başına
    /// hata yutulur ve sayılır — sunucunun değeri akışla yine iner.
    /// </summary>
    private void ApplyRows(
        AppliedRows rows,
        IReadOnlyList<SalesSessionItem>? sessions,
        IReadOnlyList<SalesOrderItem>? orders,
        IReadOnlyList<SalesShipmentItem>? shipments,
        SalesCommandIntent intent)
    {
        var now = _clock.UnixNow();
        var side = new List<SalesFeedSession>();
        foreach (var s in sessions ?? [])
        {
            try { side.Add(SalesFeedMapper.ToCore(s)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Defer(rows, intent, "session", s.Id, ex); }
        }
        var drafts = Array.Empty<SalesFeedCustomerDraft>();

        foreach (var s in sessions ?? [])
            Apply(rows, intent, "session", s.Id,
                () => new SalesFeedItem(SalesFeedItemKind.Session, s.ChangeSeq, SalesFeedMapper.ToCore(s), null, null),
                side, drafts, now);
        foreach (var o in orders ?? [])
            Apply(rows, intent, "order", o.Id,
                () => new SalesFeedItem(SalesFeedItemKind.Order, o.ChangeSeq, null, SalesFeedMapper.ToCore(o), null),
                side, drafts, now);
        foreach (var s in shipments ?? [])
            Apply(rows, intent, "shipment", s.Id,
                () => new SalesFeedItem(SalesFeedItemKind.Shipment, s.ChangeSeq, null, null, SalesFeedMapper.ToCore(s)),
                side, drafts, now);
    }

    private void Apply(
        AppliedRows rows, SalesCommandIntent intent, string kind, Guid id, Func<SalesFeedItem> build,
        IReadOnlyCollection<SalesFeedSession> side, IReadOnlyCollection<SalesFeedCustomerDraft> drafts, long now)
    {
        try
        {
            var result = _feed.ApplyItem(build(), side, drafts, ResponseOptions, now);
            if (result.Status is SalesFeedApplyStatus.Stalled or SalesFeedApplyStatus.PendingCustomer
                or SalesFeedApplyStatus.Busy)
            {
                rows.Deferred++;
                _log.LogDebug("Satış komutu {Kind} {CommandId}: {ItemKind} {ItemId} yazılamadı ({Status}); akışa kaldı",
                    intent.Kind, intent.CommandId, kind, id, result.Status);
                return;
            }
            rows.Labels.UnionWith(result.LabelIds);
            rows.Sessions.UnionWith(result.SessionIds);
            rows.Shipments.UnionWith(result.ShipmentIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Defer(rows, intent, kind, id, ex);
        }
    }

    private void Defer(AppliedRows rows, SalesCommandIntent intent, string kind, Guid id, Exception ex)
    {
        rows.Deferred++;
        _log.LogWarning("Satış komutu {Kind} {CommandId}: {ItemKind} {ItemId} yazılamadı ({Error}); akışa kaldı",
            intent.Kind, intent.CommandId, kind, id, ex.GetType().Name);
    }

    // ── yardımcılar ────────────────────────────────────────────────────────

    /// <summary>Beklenen sürümler, sipariş Id'sine göre sıralı; sürümü bilinmeyen 0.</summary>
    private IReadOnlyList<SalesCommandExpected> ReadExpected(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return [];
        var versions = _labels.GetServerVersions(ids.Select(id => id.ToString("N")).ToList());
        return ids.Distinct().Order()
            .Select(id => new SalesCommandExpected(id, versions.GetValueOrDefault(id.ToString("N")) ?? 0))
            .ToList();
    }

    private static IReadOnlyList<Guid> SortedIds(IEnumerable<string> ids) => ids.Select(ToGuid).Distinct().Order().ToList();

    private static Guid ToGuid(string id) =>
        Guid.TryParse(id, out var g) ? g : throw new ArgumentException($"Satış komutunun kimliği GUID değil: {id}.");

    private static JsonElement ParsePayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static SalesCommandOutcome Outcome(
        SalesCommandStatus status, SalesCommandIntent intent, string? message, AppliedRows rows,
        string? serverStatus = null, SalesCommandResult? result = null, SalesCommandProblem? problem = null) =>
        new(status, intent, message, serverStatus, result, problem,
            rows.Labels.ToList(), rows.Sessions.ToList(), rows.Shipments.ToList());

    /// <returns>LicenseId; çözülemezse null ve sebebin ağ olup olmadığı.</returns>
    private async Task<(Guid? Id, bool Offline)> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is { } cached && _cachedLicenseKey == licenseKey) return (cached, false);
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return (null, false);
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return (match.Id, false);
        }
        catch (LicenseApiNetworkException)
        {
            return (null, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Satış komutu: LicenseId çözülemedi");
            return (null, false);
        }
    }
}
