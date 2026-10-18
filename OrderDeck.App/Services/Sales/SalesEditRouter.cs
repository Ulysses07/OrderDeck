using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sales;

/// <summary>
/// Düzenleme yönlendiricisi (spec 2026-10-13 §5.3; W2-05): müşteri kartı, Basılanlar'ın toplu iptali ve yeniden baskısı,
/// yedek çekmecesi, "Fiyatı düzelt" (<c>set-code-price</c>) ve yedek seçimi (<c>add-backup</c>; W2-06) bunun üzerinden
/// yazar.
///
/// <para><b>Mod Off</b> (<c>SalesSyncState.Mode = 0</c>, bugün her lisans; kip HER çağrıda okunur): bugünkü çağrının
/// aynısı — <see cref="LabelService"/>'e bütün liste, çağıranın iş parçacığında, beklemeden. Oturum okunmaz, devir
/// kapısına uğranmaz, ağ yok. Kip okunamazsa da bu yol (yerel yazım aynı veritabanına gider).</para>
///
/// <para><b>Mod On</b>, etiket başına (<see cref="Route"/>):
/// <list type="bullet">
///   <item>Yayın <c>Authority = 'server'</c> (devredilmiş; uzak yayınların hepsi): sunucu kullanılabiliyorsa
///   <see cref="SalesEditRoute.Command"/>, değilse <see cref="SalesEditRoute.Unavailable"/> (çevrimdışı ya da komut ucu
///   yok, <see cref="SalesConnectivity"/>).</item>
///   <item>Kendi açık (canlı) yayını: <see cref="SalesEditRoute.Local"/> — canlı yol kapıya da ağa da uğramaz (§5.1).</item>
///   <item>Kendi bitmiş, yerel otoriteli yayını: devri yerelde bilinmiyor ve ilk yetişme bitmedi
///   (<c>FirstCatchUpAt</c> boş) ise <see cref="SalesEditRoute.Blocked"/> (§8.4-2: sunucuda devredilmiş olabilir); aksi
///   hâlde <see cref="SalesEditRoute.LocalGated"/>: devir kapısı (<see cref="HandoverGate.TryEnter"/>, sıfır bekleme)
///   yerel yazım boyunca tutulur, kapanış gönderimi tutuyorsa
///   <see cref="SalesCommandMessages.HandoverInProgress"/>. (Devri görülmüş ama hâlâ yerel otoriteli bitmiş yayın
///   yalnız force-end sonrası yerelde yeni bitmiş yayındır; sahibin push'u K2 ile yargılanır — §5.4, K5 istisnası.)</item>
/// </list>
/// Karışık seçim bölünür: önce tek bir yerel yazım (kapılı yayınların kilitleri birlikte tutulur), sonra tek bir
/// komut; sonuç tek iletide (<see cref="SalesEditResult.ProblemText"/>). Komutun sonucu bağlantı ölçüsüne işlenir
/// (<see cref="SalesConnectivity.Report"/>).</para>
///
/// <para><b>Kargo</b> (W2-08, §5.3 "Dekont kargo adımı" ve "Eşik çekmecesi kararı", §7.1): mod Off'ta bugünkü yerel
/// sıra (<see cref="LocalShipmentEditor"/>); mod On'da dosya sunucunun, her zaman komut — <c>attach-shipment</c>
/// (beklenen küme yerel bağlanmamış etiketler; <c>stale</c>'de sunucunun aday kümesiyle bir kez daha) ve
/// <c>shipment-decision</c>. Yerel <c>Shipment</c>'a hiç yazılmaz; yanıtın satırları kilit altında aynaya iner.</para>
///
/// <para>Yerel yazım çağıranın iş parçacığında (bugünkü gibi); komut <see cref="SalesCommandClient.SendAsync"/> ile
/// arka planda. Günlük yalnız kimlik ve sayı taşır.</para>
/// </summary>
public sealed class SalesEditRouter : ILabelEditor
{
    private readonly LabelService _labels;
    private readonly LabelRepository _labelRepo;
    private readonly SessionRepository _sessions;
    private readonly SalesSyncStateRepository _state;
    private readonly HandoverGate _handover;
    private readonly SalesCommandClient _commands;
    private readonly PendingSalesCommandSender _pending;
    private readonly SalesConnectivity _connectivity;
    private readonly ShipmentService _shipments;
    private readonly ShipmentRepository _shipmentRepo;
    private readonly CustomerRepository _customers;
    private readonly LocalShipmentEditor _localShipments;
    private readonly Func<DateTimeOffset?> _lastFeedSyncAt;
    private readonly ILogger<SalesEditRouter> _log;

    /// <summary>Bekleyen komutun anlık gönderiminin (kuyruk iptali, kâğıdın <c>mark-printed</c>'i) en çok beklenme süresi
    /// (§5.6-2'nin baskı öncesi turuyla aynı). Süre dolunca beklenmez; gönderim arka planda biter, sonucu
    /// <see cref="PendingSalesCommandSender.Settled"/> ile gelir, komut zaten kalıcı kuyrukta.</summary>
    internal static readonly TimeSpan ImmediateSendBudget = TimeSpan.FromSeconds(3);

    /// <summary>Kâğıdın kapılı satırları (kendi bitmiş, devredilmemiş yayını) için devir kilidinin en çok beklenme süresi
    /// (§5.6-2'nin baskı öncesi beklemesiyle aynı). Süre dolunca baskı beklemez; kapılı satırlar kilit açılınca arka
    /// planda, kilit altında işaretlenir. Testler kısaltır.</summary>
    internal TimeSpan HandoverWaitBudget { get; set; } = TimeSpan.FromSeconds(3);

    /// <param name="lastFeedSyncAt">Satış akışının bu süreçte son yetiştiği an (çevrimdışı yeniden baskının notu).</param>
    public SalesEditRouter(
        LabelService labels,
        LabelRepository labelRepo,
        SessionRepository sessions,
        SalesSyncStateRepository state,
        HandoverGate handover,
        SalesCommandClient commands,
        PendingSalesCommandSender pending,
        SalesConnectivity connectivity,
        ShipmentService shipments,
        ShipmentRepository shipmentRepo,
        CustomerRepository customers,
        Func<DateTimeOffset?> lastFeedSyncAt,
        ILogger<SalesEditRouter> log)
    {
        _labels = labels;
        _labelRepo = labelRepo;
        _sessions = sessions;
        _state = state;
        _handover = handover;
        _commands = commands;
        _pending = pending;
        _connectivity = connectivity;
        _shipments = shipments;
        _shipmentRepo = shipmentRepo;
        _customers = customers;
        _localShipments = new LocalShipmentEditor(shipments);
        _lastFeedSyncAt = lastFeedSyncAt;
        _log = log;
    }

    // ── karar ──────────────────────────────────────────────────────────────

    public IReadOnlyList<SalesEditDecision> Route(IReadOnlyCollection<string> labelIds) =>
        Decide(labelIds) ?? AllLocal(labelIds);

    public string? ServerUnavailableReason => _connectivity.UnavailableReason;

    public string? EditBlockedReason(IReadOnlyCollection<string> labelIds)
    {
        if (labelIds.Count == 0) return null;
        var decisions = Decide(labelIds);
        if (decisions is null || decisions.Any(d => d.Route != SalesEditRoute.Unavailable)) return null;
        return decisions[0].Reason;
    }

    /// <summary>Mod On'da etiket başına karar; mod Off'ta (ya da kip okunamazsa) null — çağıran bugünkü yolu seçer.</summary>
    private IReadOnlyList<SalesEditDecision>? Decide(IReadOnlyCollection<string> labelIds)
    {
        if (!IsModeOn()) return null;

        var ids = labelIds.Distinct(StringComparer.Ordinal).ToList();
        var sessionOf = _labelRepo.GetByIds(ids).ToDictionary(l => l.Id, l => l.SessionId, StringComparer.Ordinal);
        var sessions = new Dictionary<string, StreamSession?>(StringComparer.Ordinal);
        long? firstCatchUpAt = null;
        var firstCatchUpRead = false;
        string? serverReason = null;
        var serverReasonRead = false;

        var decisions = new List<SalesEditDecision>(ids.Count);
        foreach (var id in ids)
        {
            // Yerelde olmayan etiket: yerel servis onu zaten atlar (bugünkü gibi).
            if (!sessionOf.TryGetValue(id, out var sessionId))
            {
                decisions.Add(new SalesEditDecision(id, null, SalesEditRoute.Local, null));
                continue;
            }
            if (!sessions.TryGetValue(sessionId, out var session))
                sessions[sessionId] = session = _sessions.GetById(sessionId);

            if (session is null)
            {
                decisions.Add(new SalesEditDecision(id, sessionId, SalesEditRoute.Local, null));
            }
            else if (session.Authority == StreamSessionAuthority.Server)
            {
                if (!serverReasonRead)
                {
                    serverReason = _connectivity.UnavailableReason;
                    serverReasonRead = true;
                }
                decisions.Add(serverReason is null
                    ? new SalesEditDecision(id, sessionId, SalesEditRoute.Command, null)
                    : new SalesEditDecision(id, sessionId, SalesEditRoute.Unavailable, serverReason));
            }
            else if (session.EndedAt is null)
            {
                decisions.Add(new SalesEditDecision(id, sessionId, SalesEditRoute.Local, null));
            }
            else
            {
                if (!firstCatchUpRead)
                {
                    firstCatchUpAt = _state.Get().FirstCatchUpAt;
                    firstCatchUpRead = true;
                }
                decisions.Add(session.ReleasedAt is null && firstCatchUpAt is null
                    ? new SalesEditDecision(id, sessionId, SalesEditRoute.Blocked, SalesCommandMessages.NotReady)
                    : new SalesEditDecision(id, sessionId, SalesEditRoute.LocalGated, null));
            }
        }
        return decisions;
    }

    private bool IsModeOn()
    {
        try
        {
            return _state.GetMode() == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış düzenlemesi: yerel kip okunamadı; bugünkü yerel yol");
            return false;
        }
    }

    private static List<SalesEditDecision> AllLocal(IEnumerable<string> labelIds) =>
        labelIds.Distinct(StringComparer.Ordinal).Select(id => new SalesEditDecision(id, null, SalesEditRoute.Local, null)).ToList();

    // ── düzenlemeler ───────────────────────────────────────────────────────

    public Task<SalesEditResult> CancelAsync(IReadOnlyList<string> labelIds, string reason, CancellationToken ct = default)
    {
        if (labelIds.Count == 0) return Task.FromResult(SalesEditResult.Empty);
        if (Decide(labelIds) is not { } decisions)
        {
            _labels.Cancel(labelIds, reason);
            return Task.FromResult(SalesEditResult.AllLocal(labelIds));
        }
        return RunAsync("cancel", decisions, ids => _labels.Cancel(ids, reason), ids => _commands.Cancel(ids, reason), ct);
    }

    public Task<SalesEditResult> UncancelAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default)
    {
        if (labelIds.Count == 0) return Task.FromResult(SalesEditResult.Empty);
        if (Decide(labelIds) is not { } decisions)
        {
            _labels.Uncancel(labelIds);
            return Task.FromResult(SalesEditResult.AllLocal(labelIds));
        }
        return RunAsync("uncancel", decisions, ids => _labels.Uncancel(ids), ids => _commands.Uncancel(ids), ct);
    }

    public Task<SalesEditResult> ConfirmBackupAsync(string backupLabelId, decimal? newPrice, CancellationToken ct = default)
    {
        if (Decide([backupLabelId]) is not { } decisions)
        {
            _labels.ConfirmBackup(backupLabelId, newPrice);
            return Task.FromResult(SalesEditResult.AllLocal([backupLabelId]));
        }
        return RunAsync("confirm-backup", decisions, ids => _labels.ConfirmBackup(ids[0], newPrice),
            ids => _commands.ConfirmBackup(ids[0], newPrice), ct);
    }

    /// <summary>
    /// Mod On'un bölünmüş düzenlemesi: kapalı ve kullanılamayan etiketler sorun olarak toplanır; yerel ve kapılı
    /// etiketler TEK yerel yazımda (kapılı yayınların kilitleri yazım boyunca); sunucu etiketleri komut başına en çok
    /// <see cref="MaxCommandOrders"/> (sunucunun tavanı).
    /// </summary>
    private async Task<SalesEditResult> RunAsync(
        string kind,
        IReadOnlyList<SalesEditDecision> decisions,
        Action<IReadOnlyList<string>> writeLocal,
        Func<IReadOnlyList<string>, SalesCommandIntent> prepareCommand,
        CancellationToken ct)
    {
        var result = new ResultBuilder();
        foreach (var d in decisions.Where(d => d.Route is SalesEditRoute.Blocked or SalesEditRoute.Unavailable))
            result.Problem(d.Reason ?? SalesCommandMessages.Failed, d.LabelId);

        var local = decisions.Where(d => d.Route is SalesEditRoute.Local or SalesEditRoute.LocalGated).ToList();
        var written = WriteLocal(local, writeLocal, result);

        var server = decisions.Where(d => d.Route == SalesEditRoute.Command).Select(d => d.LabelId).ToList();
        string? unreachable = null;
        foreach (var chunk in server.Chunk(MaxCommandOrders))
        {
            // Sunucunun tek komut tavanı (SalesCommandService.MaxOrders): büyük seçim parça parça, her parça kendi
            // komutuyla. Sonucu bilinmeyen parçadan sonrası gönderilmez (aynı sebeple sorun).
            if (unreachable is not null)
            {
                result.Problem(unreachable, chunk);
                continue;
            }
            var outcome = await _commands.SendAsync(prepareCommand(chunk), ct).ConfigureAwait(false);
            _connectivity.Report(outcome.Status);
            result.Changed(outcome.LabelIds);
            if (outcome.IsApplied) result.Applied(chunk);
            else
            {
                result.Problem(outcome.Message ?? SalesCommandMessages.Failed, chunk);
                if (outcome.CanResend) unreachable = outcome.Message ?? SalesCommandMessages.Failed;
            }
        }

        _log.LogInformation(
            "Satış düzenlemesi {Kind}: {Local} yerel ({Gated} kapılı), {Command} komut, {Blocked} kapalı, "
          + "{Unavailable} bağlantısız; {Problems} etiket uygulanamadı",
            kind, written, local.Count(d => d.Route == SalesEditRoute.LocalGated), server.Count,
            decisions.Count(d => d.Route == SalesEditRoute.Blocked),
            decisions.Count(d => d.Route == SalesEditRoute.Unavailable), result.ProblemCount);
        return result.Build();
    }

    /// <summary>Yerel ve kapılı etiketleri tek yazımda yazar. Kapılı yayının kilidi alınamazsa (kapanış gönderimi
    /// tutuyor) o yayının etiketleri sorun olur; kilitler yazımdan sonra bırakılır.</summary>
    /// <returns>Yazılan etiket sayısı.</returns>
    private int WriteLocal(
        IReadOnlyList<SalesEditDecision> local, Action<IReadOnlyList<string>> write, ResultBuilder result)
    {
        if (local.Count == 0) return 0;
        var leases = new List<IDisposable>();
        try
        {
            var refused = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sessionId in local.Where(d => d.Route == SalesEditRoute.LocalGated)
                         .Select(d => d.SessionId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (_handover.TryEnter(sessionId, out var lease)) leases.Add(lease);
                else refused.Add(sessionId);
            }

            var writable = new List<string>(local.Count);
            foreach (var d in local)
            {
                if (d.Route == SalesEditRoute.LocalGated && refused.Contains(d.SessionId!))
                    result.Problem(SalesCommandMessages.HandoverInProgress, d.LabelId);
                else
                    writable.Add(d.LabelId);
            }
            if (writable.Count == 0) return 0;

            write(writable);
            result.Applied(writable);
            result.Changed(writable);
            return writable.Count;
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    // ── kuyruk: kalıntı etiketler (§5.5) ───────────────────────────────────

    public Task<SalesEditResult> CancelQueueAsync(IReadOnlyList<string> labelIds, string reason, CancellationToken ct = default)
    {
        if (labelIds.Count == 0) return Task.FromResult(SalesEditResult.Empty);
        // Mod Off: bugünkü çağrı, beklemeden.
        if (Decide(labelIds) is not { } decisions)
        {
            _labels.Cancel(labelIds, reason);
            return Task.FromResult(SalesEditResult.AllLocal(labelIds));
        }
        return CancelQueueModeOnAsync(decisions, reason, ct);
    }

    /// <summary>
    /// Mod On'un kuyruk iptali: kapalı etiketler sorun; yerel ve kapılı etiketler TEK yerel yazımda; devredilmiş KENDİ
    /// yayınının kalıntıları sabit kimlikli <c>cancel</c> ile — önce bekleyen komut (kalıcı, örtülü), çevrimiçiyse hemen
    /// gönderilir (en çok <see cref="ImmediateSendBudget"/> beklenir). Başka bilgisayarın yayını bekleyen komuta girmez:
    /// bugünkü komut yolu (çevrimdışıyken sorun).
    /// </summary>
    private async Task<SalesEditResult> CancelQueueModeOnAsync(
        IReadOnlyList<SalesEditDecision> decisions, string reason, CancellationToken ct)
    {
        var result = new ResultBuilder();
        foreach (var d in decisions.Where(d => d.Route == SalesEditRoute.Blocked))
            result.Problem(d.Reason ?? SalesCommandMessages.Failed, d.LabelId);

        var local = decisions.Where(d => d.Route is SalesEditRoute.Local or SalesEditRoute.LocalGated).ToList();
        var written = WriteLocal(local, ids => _labels.Cancel(ids, reason), result);

        var (own, remote) = SplitServerLabels(decisions);
        var online = decisions.Any(d => d.Route == SalesEditRoute.Command);

        if (remote.Count > 0)
        {
            if (!online)
                result.Problem(decisions.First(d => d.Route == SalesEditRoute.Unavailable).Reason ?? SalesCommandMessages.Offline,
                    remote);
            else
            {
                string? unreachable = null;
                foreach (var chunk in remote.Chunk(MaxCommandOrders))        // sunucunun tek komut tavanı
                {
                    // Sonucu bilinmeyen parçadan sonrası gönderilmez (aynı sebeple sorun).
                    if (unreachable is not null)
                    {
                        result.Problem(unreachable, chunk);
                        continue;
                    }
                    var outcome = await _commands.SendAsync(_commands.Cancel(chunk, reason), ct).ConfigureAwait(false);
                    _connectivity.Report(outcome.Status);
                    result.Changed(outcome.LabelIds);
                    if (outcome.IsApplied) result.Applied(chunk);
                    else
                    {
                        result.Problem(outcome.Message ?? SalesCommandMessages.Failed, chunk);
                        if (outcome.CanResend) unreachable = outcome.Message ?? SalesCommandMessages.Failed;
                    }
                }
            }
        }

        if (own.Count > 0)
        {
            var parts = await QueueAndSendAsync(own, ids => _commands.Cancel(ids, reason), online, ct)
                .ConfigureAwait(false);
            foreach (var (chunk, settled) in parts)
            {
                switch (settled.State)
                {
                    case PendingState.Applied:
                        result.Applied(chunk);
                        break;
                    case PendingState.Pending:
                        result.Pending(chunk);
                        break;
                    case PendingState.Dropped:
                        result.Notice(settled.Flush!.Settled.Warnings);
                        break;
                    default:
                        result.Problem(SalesCommandMessages.ModeOff, chunk);
                        break;
                }
                if (settled.Flush is { } flush) result.Changed(flush.Settled.ChangedLabelIds);
            }
        }

        _log.LogInformation(
            "Kuyruk iptali: {Local} yerel, {Own} kalıntı (komut; {Pending} bekliyor), {Remote} başka bilgisayarın; {Problems} etiket uygulanamadı",
            written, own.Count, result.PendingCount, remote.Count, result.ProblemCount);
        return result.Build();
    }

    public IReadOnlySet<string> PrintHeldRows(IReadOnlyCollection<string> labelIds)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (labelIds.Count == 0 || Decide(labelIds) is not { } decisions) return held;   // mod Off: hiçbiri
        var unavailable = decisions.Where(d => d.Route == SalesEditRoute.Unavailable).ToList();
        if (unavailable.Count == 0) return held;
        held.UnionWith(SplitServerLabels(unavailable).Remote);
        return held;
    }

    public Task<SalesPrintMarkResult> MarkPrintedAsync(IReadOnlyList<string> paperOrder, CancellationToken ct = default)
    {
        if (paperOrder.Count == 0) return Task.FromResult(SalesPrintMarkResult.Empty);
        // Mod Off: bugünkü çağrı, beklemeden.
        if (Decide(paperOrder) is not { } decisions)
        {
            _labels.MarkPrintedAndRecord(paperOrder);
            return Task.FromResult(SalesPrintMarkResult.AllLocal(paperOrder));
        }
        return MarkPrintedModeOnAsync(paperOrder, decisions, ct);
    }

    /// <summary>
    /// Mod On'un kâğıt işaretlemesi (§5.6-4): TEK basış kimliği, kâğıt sırası kesintisiz; parçalar ayrı işlemlerde.
    /// <list type="number">
    ///   <item>Canlı ve yerel satırlar (<see cref="SalesEditRoute.Local"/>) hemen, devir kilitlerinden ÖNCE ve kendi
    ///   işleminde (§5.1, §5.2: canlı yol kapıya uğramaz). Kendi bitmiş yayınının kapanış gönderimi kilidi sessions/sync
    ///   boyunca tutsa da canlı etiketin işareti onu beklemez.</item>
    ///   <item>Kendi bitmiş ama devredilmemiş yayınlarının satırları (<see cref="SalesEditRoute.LocalGated"/>,
    ///   <see cref="SalesEditRoute.Blocked"/>): <see cref="MarkGatedPaperAsync"/> — kilit beklenir, karar kilit altında
    ///   yeniden verilir. Kilit en çok <see cref="HandoverWaitBudget"/> beklenir: süre dolunca çağıran beklemez, bu
    ///   satırlar <see cref="SalesPrintMarkResult.Pending"/>'de döner ve kilit açılınca arka planda aynı kuralla
    ///   işaretlenir. Kilitsiz yazılmaz (M2: kapanış "kuyruk boş" okuduysa devir kuyruk boşken olur), işaret de
    ///   düşmez.</item>
    ///   <item>Sunucu satırları (<see cref="MarkServerPaperAsync"/>).</item>
    /// </list>
    /// </summary>
    private async Task<SalesPrintMarkResult> MarkPrintedModeOnAsync(
        IReadOnlyList<string> paperOrder, IReadOnlyList<SalesEditDecision> first, CancellationToken ct)
    {
        var batchId = LabelService.NewPrintBatchId();
        var seqOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < paperOrder.Count; i++) seqOf.TryAdd(paperOrder[i], i + 1);
        var result = new PrintMarkBuilder();

        var decisions = first;
        var live = decisions.Where(d => d.Route == SalesEditRoute.Local).Select(d => d.LabelId).ToList();
        if (live.Count > 0 && !TryMarkPaper(live, batchId, seqOf))
        {
            // Beklenmeyen: canlı satırın yayını bu arada sunucu otoritesine geçti ve TR_Label_ServerAuthority_U yazımı
            // geri çevirdi — hiçbir satır yazılmadı (tek işlem). Kâğıt basıldı: karar yeniden verilir.
            _log.LogInformation("Kuyruk baskısı {BatchId}: canlı satırın yayını bu arada sunucuya geçti; karar yeniden veriliyor",
                batchId);
            decisions = Decide(paperOrder) ?? AllLocal(paperOrder);
            live = decisions.Where(d => d.Route == SalesEditRoute.Local).Select(d => d.LabelId).ToList();
            if (live.Count > 0)
                _labels.MarkPrintedAndRecord(live, batchId, live.Select(id => seqOf[id]).ToList());
        }
        result.Local(live);

        var gated = decisions.Where(IsGated).Select(d => d.LabelId).ToList();
        var deferred = 0;
        if (gated.Count > 0)
        {
            var sessions = decisions.Where(IsGated).Select(d => d.SessionId!).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
            var part = MarkGatedPaperAsync(gated, sessions, batchId, seqOf);
            if (await Task.WhenAny(part, Task.Delay(HandoverWaitBudget, ct)).ConfigureAwait(false) == part)
            {
                // Kilit altında verilen karar kapılı satırların eski kararının yerine geçer: devri görülenler aşağıda
                // kâğıdın öbür sunucu satırlarıyla aynı komuta girer.
                var (gatedResult, gatedDecisions) = await part.ConfigureAwait(false);
                result.Merge(gatedResult);
                decisions = decisions.Where(d => !IsGated(d)).Concat(gatedDecisions).ToList();
            }
            else
            {
                // Kapanış gönderimi kilidi hâlâ tutuyor (sessions/sync sürüyor). Baskı komutu ve "Yayını bitir" daha
                // fazla beklemez; iş kilit açılınca arka planda biter (kapılı kararlar aşağıdaki sunucu yolunda yok
                // sayılır: Local/LocalGated/Blocked).
                deferred = gated.Count;
                result.Pending.AddRange(gated);
                _log.LogInformation(
                    "Kuyruk baskısı {BatchId}: {Count} satırın devir kilidi süresinde alınamadı (kapanış gönderimi sürüyor); kilit açılınca işaretlenecek",
                    batchId, gated.Count);
                _ = FinishDeferredGatedAsync(part, batchId, seqOf, gated.Count);
            }
        }

        await MarkServerPaperAsync(decisions, result, batchId, seqOf, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Kuyruk baskısı {BatchId}: {Local} yerel, {Own} kalıntı (komut; {Pending} bekliyor), {Remote} başka bilgisayarın, {Deferred} kilit bekliyor; {Discard} kâğıt imha edilecek",
            batchId, result.LocalCount, result.OwnCount, result.Pending.Count - deferred, result.RemoteCount, deferred,
            result.Discard.Count);
        return result.Build();
    }

    private static bool IsGated(SalesEditDecision d) => d.Route is SalesEditRoute.LocalGated or SalesEditRoute.Blocked;

    /// <summary>
    /// Kâğıdın kapılı parçası (kendi bitmiş ama devredilmemiş yayınları): kilitler kimlik sırasıyla beklenir (kapanış
    /// gönderimi o an "kuyruk boş mu" kararındaysa) ve karar kilit altında yeniden verilir. Hâlâ yerel yoldaki satırlar
    /// kilit altında damgalanır; kâğıt zaten basıldığı için kapalı (ilk yetişme) satır da — sunucu devredilmiş yayına
    /// baskı doldurmayı kabul eder (K3). Devri arada görülen satırlar dönen kararda sunucu yolundadır; komutları kilit
    /// bırakıldıktan sonra çağıranın işidir. Kâğıt basıldı: çağıranın beklemekten vazgeçmesi (süre, belirteç) bu işi
    /// kesmez.
    /// </summary>
    /// <param name="sessionIds">Kapılı yayınlar, kimlik sırasıyla (kilitlenme olmasın).</param>
    /// <returns>Yerelde damgalananlar ve kapılı satırların kilit altındaki kararı.</returns>
    private async Task<(PrintMarkBuilder Result, IReadOnlyList<SalesEditDecision> Decisions)> MarkGatedPaperAsync(
        IReadOnlyList<string> gated, IReadOnlyList<string> sessionIds, string batchId,
        IReadOnlyDictionary<string, int> seqOf)
    {
        var result = new PrintMarkBuilder();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var sessionId in sessionIds)
                leases.Add(await _handover.EnterAsync(sessionId, CancellationToken.None).ConfigureAwait(false));
            var decisions = Decide(gated) ?? AllLocal(gated);
            var local = LocalPaper(decisions);
            if (local.Count > 0 && !TryMarkPaper(local, batchId, seqOf))
            {
                // Akış bu arada kendi bitmiş yayınının devrini yazdı (Authority = 'server'; akış devir kapısını almaz)
                // ve TR_Label_ServerAuthority_U yerel yazımı geri çevirdi — hiçbir satır yazılmadı (tek işlem). Kâğıt
                // basıldı: karar yeniden verilir, devredilen satırlar bekleyen komut yoluna geçer.
                _log.LogInformation("Kuyruk baskısı {BatchId}: yayın bu arada sunucuya devredildi; karar yeniden veriliyor",
                    batchId);
                decisions = Decide(gated) ?? AllLocal(gated);
                local = LocalPaper(decisions);
                if (local.Count > 0)
                    _labels.MarkPrintedAndRecord(local, batchId, local.Select(id => seqOf[id]).ToList());
            }
            result.Local(local);
            return (result, decisions);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    /// <summary>
    /// Süresinde beklenemeyen kapılı parçanın arka planda bitişi: kilit açılınca yerel damga (<see cref="MarkGatedPaperAsync"/>),
    /// devri görülen satırlar için bekleyen <c>mark-printed</c> (kalıcı önce; çevrimiçiyse hemen gönderilir — sonucu
    /// arayüze <see cref="PendingSalesCommandSender.Settled"/> ile gider). Günlük yalnız kimlik ve sayı taşır.
    /// </summary>
    private async Task FinishDeferredGatedAsync(
        Task<(PrintMarkBuilder Result, IReadOnlyList<SalesEditDecision> Decisions)> part, string batchId,
        IReadOnlyDictionary<string, int> seqOf, int count)
    {
        try
        {
            var (result, decisions) = await part.ConfigureAwait(false);
            await MarkServerPaperAsync(decisions, result, batchId, seqOf, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation(
                "Kuyruk baskısı {BatchId}: kilit açıldı; {Count} satırdan {Local} yerelde işaretlendi, {Own} kalıntı komuta girdi ({Pending} bekliyor); {Notices} uyarı",
                batchId, count, result.LocalCount, result.OwnCount, result.Pending.Count, result.Notices.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Kuyruk baskısı {BatchId}: kâğıdı basılmış {Count} satırın işareti kilit açıldıktan sonra yazılamadı",
                batchId, count);
        }
    }

    /// <summary>
    /// Kâğıdın sunucu yolundaki satırları (§5.5, §5.6-4): kendi devredilmiş yayınınınkiler bekleyen <c>mark-printed</c>
    /// (kalıcı önce; çevrimiçiyse hemen gönderilir), başka bilgisayarınkiler doğrudan komut. Basış kimliği ve kâğıt
    /// sıraları kâğıdınkiyle aynı.
    /// </summary>
    private async Task MarkServerPaperAsync(IReadOnlyList<SalesEditDecision> decisions, PrintMarkBuilder result,
        string batchId, IReadOnlyDictionary<string, int> seqOf, CancellationToken ct)
    {
        var (own, remote) = SplitServerLabels(decisions);
        result.OwnCount += own.Count;
        result.RemoteCount += remote.Count;
        var online = decisions.Any(d => d.Route == SalesEditRoute.Command);

        if (own.Count > 0)
        {
            // Sunucunun tek komut tavanı: parça başına ayrı bekleyen komut; basış kimliği ve kâğıt sıraları aynı.
            var parts = await QueueAndSendAsync(own,
                ids => _commands.MarkPrinted(batchId, ids.Select(id => (id, seqOf[id])).ToList()), online, ct)
                .ConfigureAwait(false);
            foreach (var (chunk, settled) in parts)
            {
                switch (settled.State)
                {
                    case PendingState.Applied:
                        result.Marked.AddRange(chunk);
                        break;
                    case PendingState.Pending:
                        result.Pending.AddRange(chunk);
                        break;
                    case PendingState.Dropped:
                        result.Notices.AddRange(settled.Flush!.Settled.Warnings);
                        break;
                    default:
                        // Kararla bekleyen komut arasında yerel kip Off'a döndü (disable turunun 2. adımı): kendi
                        // yayınları artık yerel otoritede (aynı işlemde). Kâğıt basıldı — işaret aynı basış ve sırayla
                        // yerelde yazılır; yoksa etiket basılmamış görünür, sonraki yayında yeniden teklif edilirdi.
                        if (TryMarkLocally(chunk, batchId, seqOf)) result.Marked.AddRange(chunk);
                        else result.Notices.Add(SalesCommandMessages.PrintNotRecorded(chunk.Length, SalesCommandMessages.ModeOff));
                        break;
                }
                if (settled.Flush is { } flush)
                {
                    result.Discard.UnionWith(flush.Settled.DiscardLabelIds);
                    result.AlreadyPrinted.UnionWith(flush.Settled.AlreadyPrintedLabelIds);
                    result.Changed.UnionWith(flush.Settled.ChangedLabelIds);
                }
            }
        }

        if (remote.Count > 0)
        {
            if (!online)
                result.Notices.Add(SalesCommandMessages.PrintNotRecorded(remote.Count,
                    decisions.First(d => d.Route == SalesEditRoute.Unavailable).Reason));
            else
            {
                foreach (var chunk in remote.Chunk(MaxCommandOrders))        // sunucunun tek komut tavanı
                {
                    var outcome = await _commands.SendAsync(
                            _commands.MarkPrinted(batchId, chunk.Select(id => (id, seqOf[id])).ToList()), ct)
                        .ConfigureAwait(false);
                    _connectivity.Report(outcome.Status);
                    result.Changed.UnionWith(outcome.LabelIds);
                    if (outcome.IsApplied)
                    {
                        result.Marked.AddRange(chunk);
                        var asked = chunk.ToHashSet(StringComparer.Ordinal);
                        result.Discard.UnionWith((outcome.Result?.Cancelled ?? []).Select(id => id.ToString("N"))
                            .Where(asked.Contains));
                        foreach (var id in (outcome.Result?.AlreadyPrinted ?? []).Select(id => id.ToString("N"))
                                     .Where(asked.Contains))
                            if (result.Discard.Add(id)) result.AlreadyPrinted.Add(id);
                    }
                    else result.Notices.Add(SalesCommandMessages.PrintNotRecorded(chunk.Length, outcome.Message));
                }
            }
        }
    }

    /// <summary>Kâğıt işaretlemesinin (ya da bir parçasının) birikimi. Parçalar ayrı etiketleri taşır; kapılı parça
    /// süresinde biterse ana parçaya katılır, bitmezse yalnız arka planda kullanılır (eşzamanlı erişim yok).</summary>
    private sealed class PrintMarkBuilder
    {
        public readonly List<string> Marked = new();
        public readonly List<string> Pending = new();
        public readonly HashSet<string> Discard = new(StringComparer.Ordinal);
        public readonly HashSet<string> AlreadyPrinted = new(StringComparer.Ordinal);
        public readonly List<string> Notices = new();
        public readonly HashSet<string> Changed = new(StringComparer.Ordinal);
        public int LocalCount;
        public int OwnCount;
        public int RemoteCount;

        /// <summary>Yerelde damgalanan satırlar.</summary>
        public void Local(IReadOnlyCollection<string> ids)
        {
            Marked.AddRange(ids);
            Changed.UnionWith(ids);
            LocalCount += ids.Count;
        }

        public void Merge(PrintMarkBuilder part)
        {
            Marked.AddRange(part.Marked);
            Pending.AddRange(part.Pending);
            Discard.UnionWith(part.Discard);
            AlreadyPrinted.UnionWith(part.AlreadyPrinted);
            Notices.AddRange(part.Notices);
            Changed.UnionWith(part.Changed);
            LocalCount += part.LocalCount;
            OwnCount += part.OwnCount;
            RemoteCount += part.RemoteCount;
        }

        public SalesPrintMarkResult Build() =>
            new(Marked, Pending, Discard.ToList(), Notices, Changed.ToList(), AlreadyPrinted.ToList());
    }

    /// <summary>Kâğıdın yerel yoldaki satırları.</summary>
    private static List<string> LocalPaper(IReadOnlyList<SalesEditDecision> decisions) => decisions
        .Where(d => d.Route is SalesEditRoute.Local or SalesEditRoute.LocalGated or SalesEditRoute.Blocked)
        .Select(d => d.LabelId).ToList();

    /// <summary>Kâğıdın yerel işareti; satırlardan biri bu arada sunucu otoritesine geçtiyse (049'un
    /// <c>sales-server-authority</c> ABORT'u, tek işlem: hiçbir şey yazılmadı) false. Başka hata fırlar.</summary>
    private bool TryMarkPaper(IReadOnlyList<string> labelIds, string batchId, IReadOnlyDictionary<string, int> seqOf)
    {
        try
        {
            _labels.MarkPrintedAndRecord(labelIds, batchId, labelIds.Select(id => seqOf[id]).ToList());
            return true;
        }
        catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) == 19
                                         && ex.Message.Contains("sales-server-authority", StringComparison.Ordinal))
        {
            return false;
        }
    }

    /// <summary>Bekleyen komuta giremeyen (yerel kip Off'a döndü) kâğıt satırlarının yerel işareti. Yazım düşerse
    /// (beklenmeyen: Off'un 2. adımı yayını aynı işlemde yerele döndürür) false; çağıran operatörü uyarır.</summary>
    private bool TryMarkLocally(IReadOnlyList<string> labelIds, string batchId, IReadOnlyDictionary<string, int> seqOf)
    {
        try
        {
            _labels.MarkPrintedAndRecord(labelIds, batchId, labelIds.Select(id => seqOf[id]).ToList());
            _log.LogInformation("Kuyruk baskısı {BatchId}: yerel kip Off'a döndü; {Count} kalıntı yerelde işaretlendi",
                batchId, labelIds.Count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Kuyruk baskısı {BatchId}: {Count} kalıntının işareti yerelde de yazılamadı", batchId,
                labelIds.Count);
            return false;
        }
    }

    private enum PendingState { Applied, Pending, Dropped, NotQueued }

    private readonly record struct PendingSettlement(PendingState State, PendingSalesCommandFlush? Flush);

    /// <summary>
    /// Kalıntının komutları, sunucunun tek komut tavanına (<see cref="MaxCommandOrders"/>) göre parça parça: her parça
    /// kendi sabit kimliği ve örtüsüyle ayrı bekleyen komut (<paramref name="prepare"/> parçanın niyetini kurar; kâğıtta
    /// basış kimliği ve sıralar parçalar arasında aynı kalır). Önce HEPSİ kalıcı yazılır (örtü — kâğıt ya da iptal artık
    /// yeniden teklif edilmez; ağdan önce), çevrimiçiyse sırayla hemen gönderilir. Sonucu bilinmeyen parçadan sonrası
    /// gönderilmez: kuyrukta kalır, satış akışı turu sırayla götürür.
    /// </summary>
    private async Task<IReadOnlyList<(string[] Labels, PendingSettlement Settlement)>> QueueAndSendAsync(
        IReadOnlyList<string> labelIds, Func<IReadOnlyList<string>, SalesCommandIntent> prepare, bool online,
        CancellationToken ct)
    {
        var parts = new List<(string[] Labels, SalesCommandIntent? Intent)>();
        foreach (var chunk in labelIds.Chunk(MaxCommandOrders))
        {
            var intent = prepare(chunk);
            parts.Add((chunk, _pending.Enqueue(intent, chunk) ? intent : null));
        }

        var results = new List<(string[] Labels, PendingSettlement Settlement)>(parts.Count);
        var send = online;
        foreach (var (chunk, intent) in parts)
        {
            if (intent is null)
            {
                results.Add((chunk, new(PendingState.NotQueued, null)));
                continue;
            }
            if (!send)
            {
                results.Add((chunk, new(PendingState.Pending, null)));
                continue;
            }
            var settled = await SendQueuedAsync(intent, ct).ConfigureAwait(false);
            if (settled.State == PendingState.Pending) send = false;
            results.Add((chunk, settled));
        }
        return results;
    }

    /// <summary>
    /// Kuyruktaki tek komutun anlık gönderimi. <see cref="ImmediateSendBudget"/> içinde bitmezse beklenmez: sonucu arka
    /// planda işlenir (<see cref="PendingSalesCommandSender.Settled"/>), sonucu bilinmeyen komut zaten kuyrukta.
    /// </summary>
    private async Task<PendingSettlement> SendQueuedAsync(SalesCommandIntent intent, CancellationToken ct)
    {
        var send = _pending.SendNowAsync(intent.CommandId, CancellationToken.None);
        _ = send.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        if (await Task.WhenAny(send, Task.Delay(ImmediateSendBudget, ct)).ConfigureAwait(false) != send)
        {
            _log.LogInformation("Bekleyen satış komutu {Kind} {CommandId}: anlık gönderim süresinde bitmedi; arka planda sürüyor",
                intent.Kind, intent.CommandId);
            return new(PendingState.Pending, null);
        }
        PendingSalesCommandFlush flush;
        try { flush = await send.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Bekleyen satış komutu {Kind} {CommandId}: anlık gönderim düştü; kuyrukta kaldı",
                intent.Kind, intent.CommandId);
            return new(PendingState.Pending, null);
        }
        var state = flush.Sent > 0 ? PendingState.Applied
            : flush.Dropped > 0 ? PendingState.Dropped
            : PendingState.Pending;
        return new(state, flush);
    }

    /// <summary>Sunucu yolundaki etiketler (komut ya da bağlantısız): bu bilgisayarın yayınları (kalıntı; bekleyen
    /// komuta girebilir) ve başka bilgisayarınkiler.</summary>
    private (List<string> Own, List<string> Remote) SplitServerLabels(IReadOnlyList<SalesEditDecision> decisions)
    {
        var own = new List<string>();
        var remote = new List<string>();
        var origin = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var d in decisions.Where(d => d.Route is SalesEditRoute.Command or SalesEditRoute.Unavailable))
        {
            if (!origin.TryGetValue(d.SessionId!, out var o))
                origin[d.SessionId!] = o = _sessions.GetById(d.SessionId!)?.Origin;
            (o == StreamSessionOrigin.Local ? own : remote).Add(d.LabelId);
        }
        return (own, remote);
    }

    // ── yeniden baskı ──────────────────────────────────────────────────────

    public Task<SalesReprintPlan> PrepareReprintAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default)
    {
        if (Decide(labelIds) is not { } decisions || decisions.All(d => d.Route != SalesEditRoute.Command
                && d.Route != SalesEditRoute.Unavailable))
            return Task.FromResult(SalesReprintPlan.From(_labels.PrepareReprint(labelIds)));
        return PrepareReprintModeOnAsync(labelIds, decisions, ct);
    }

    /// <summary>
    /// Sunucu satırlı yeniden baskı (§5.6): yerel satırlar önbellekten (yazma yok, kapı gerekmez); sunucu satırları
    /// çevrimiçiyken <c>reprint</c> komutu — yanıtın güncel satırları yerele yazılır, sunucunun listesindekiler basılır
    /// (arada iptal edilen düşer, düzeltilmiş fiyat basılır). Çevrimdışıyken, uç yokken ya da sonucu bilinmeyen
    /// komutta önbellekten, notla. Başka bir ret: hiçbir şey basılmaz, metin döner. Sıra istenen sıradır.
    /// </summary>
    private async Task<SalesReprintPlan> PrepareReprintModeOnAsync(
        IReadOnlyList<string> labelIds, IReadOnlyList<SalesEditDecision> decisions, CancellationToken ct)
    {
        var cached = decisions.Where(d => d.Route != SalesEditRoute.Command).Select(d => d.LabelId).ToList();
        var server = decisions.Where(d => d.Route == SalesEditRoute.Command).Select(d => d.LabelId).ToList();
        var fromCache = decisions.Any(d => d.Route == SalesEditRoute.Unavailable);
        var printable = new HashSet<string>(StringComparer.Ordinal);
        var corrections = new HashSet<string>(StringComparer.Ordinal);

        var offline = false;
        foreach (var chunk in server.Chunk(MaxCommandOrders))                // sunucunun tek komut tavanı
        {
            if (offline)
            {
                cached.AddRange(chunk);
                continue;
            }
            var outcome = await _commands.SendAsync(_commands.Reprint(chunk), ct).ConfigureAwait(false);
            _connectivity.Report(outcome.Status);
            if (outcome.IsApplied)
            {
                printable.UnionWith((outcome.Result?.ReprintIds ?? []).Select(id => id.ToString("N")));
                corrections.UnionWith((outcome.Result?.CorrectionIds ?? []).Select(id => id.ToString("N")));
            }
            else if (outcome.CanResend)
            {
                cached.AddRange(chunk);
                fromCache = offline = true;
            }
            else
            {
                _log.LogInformation("Yeniden baskı: reprint komutu reddedildi ({Status}); {Count} etiket basılmadı",
                    outcome.Status, labelIds.Count);
                return new SalesReprintPlan([], new HashSet<string>(), outcome.Message ?? SalesCommandMessages.Failed);
            }
        }

        if (cached.Count > 0)
        {
            var local = _labels.PrepareReprint(cached);
            printable.UnionWith(local.Labels.Select(l => l.Id));
            corrections.UnionWith(local.CorrectionIds);
        }

        var labels = _labelRepo.GetByIds(labelIds.Where(printable.Contains).ToList());
        corrections.IntersectWith(labels.Select(l => l.Id));
        _log.LogInformation(
            "Yeniden baskı: {Server} sunucu satırı, {Cached} önbellek satırı; {Printed} etiket basılacak (önbellekten={FromCache})",
            server.Count, cached.Count, labels.Count, fromCache);
        return new SalesReprintPlan(labels, corrections, null, fromCache, fromCache ? _lastFeedSyncAt() : null);
    }

    // ── fiyat düzeltmesi (set-code-price) ──────────────────────────────────

    /// <summary>Komutun <c>alsoSelectedIds</c> ve <c>paidFromBalanceIds</c> listelerinin tavanı
    /// (<c>SalesCommandService.MaxOrders</c>, sunucunun <c>TryReadOptionalIds</c>'i).</summary>
    private const int MaxListedOrders = 500;

    /// <summary>Etiket listesi taşıyan tek komutun (<c>cancel</c>, <c>uncancel</c>, <c>reprint</c>,
    /// <c>mark-printed</c>) öğe ve beklenen sürüm tavanı (<c>SalesCommandService.MaxOrders</c>): üstündeki seçim
    /// 400 alır, yeniden denemek hep aynı sonucu verirdi — operatörün toplu işlemi parça parça gider.</summary>
    private const int MaxCommandOrders = 500;

    public Task<SalesCodePriceResult> CorrectCodePricesAsync(
        SalesCodePriceRequest request,
        Func<IReadOnlyList<string>, PriceCorrectionResult> writeLocal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeLocal);
        // Mod Off ve canlı yayın: bugünkü çağrı, aynı etiketlerle, beklemeden.
        if (request.LabelIds.Count == 0 || Decide(request.LabelIds) is not { } decisions
            || decisions.All(d => d.Route == SalesEditRoute.Local))
            return Task.FromResult(SalesCodePriceResult.AllLocal(writeLocal(request.LabelIds)));

        // Plan tek pakettir: kapalı ya da bağlantısız etiket varsa hiçbir şey yazılmaz.
        if (decisions.FirstOrDefault(d => d.Route is SalesEditRoute.Blocked or SalesEditRoute.Unavailable) is { } refused)
        {
            _log.LogInformation("Fiyat düzeltmesi: {Count} etiketlik plan yazılmadı ({Route})", decisions.Count, refused.Route);
            return Task.FromResult(SalesCodePriceResult.Refused(refused.Reason ?? SalesCommandMessages.Failed));
        }
        return CorrectCodePricesModeOnAsync(request, decisions, writeLocal, ct);
    }

    /// <summary>
    /// Mod On: önce yerel parça (canlı yayın ve kapılı kendi yayını) TEK yerel yazımda, bugünkü paketle — çakışması
    /// çağırana fırlar, henüz komut gitmemiştir. Sonra devredilmiş yayınların etiketleri plan ve yayın başına birer
    /// <c>set-code-price</c> komutuyla, sırayla; ilk başarısızlıkta durulur (fiyatta sessiz yeniden deneme yok, §5.3).
    /// Sonucu bilinmeyen komutun niyeti saklanır: aynı plan ve fiyatla yeniden onay aynı <c>CommandId</c>'yi gönderir,
    /// sunucu kaydı yeniden oynatır.
    /// </summary>
    private async Task<SalesCodePriceResult> CorrectCodePricesModeOnAsync(
        SalesCodePriceRequest request,
        IReadOnlyList<SalesEditDecision> decisions,
        Func<IReadOnlyList<string>, PriceCorrectionResult> writeLocal,
        CancellationToken ct)
    {
        var serverIds = decisions.Where(d => d.Route == SalesEditRoute.Command).Select(d => d.LabelId)
            .ToHashSet(StringComparer.Ordinal);
        var commands = CodePriceCommands(request, serverIds);
        var tooLong = commands.Select(c => Math.Max(c.Payload.AlsoSelectedIds.Count, c.Payload.PaidFromBalanceIds.Count))
            .DefaultIfEmpty(0).Max();
        if (tooLong > MaxListedOrders)
            return SalesCodePriceResult.Refused(SalesCommandMessages.TooManyCandidates(tooLong));

        PriceCorrectionResult? local = null;
        var localIds = request.LabelIds.Where(id => !serverIds.Contains(id)).ToList();
        if (localIds.Count > 0)
        {
            var leases = new List<IDisposable>();
            try
            {
                foreach (var sessionId in decisions.Where(d => d.Route == SalesEditRoute.LocalGated)
                             .Select(d => d.SessionId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    if (!_handover.TryEnter(sessionId, out var lease))
                        return SalesCodePriceResult.Refused(SalesCommandMessages.HandoverInProgress);
                    leases.Add(lease);
                }
                local = writeLocal(localIds);
            }
            finally
            {
                foreach (var lease in leases) lease.Dispose();
            }
        }

        var changed = new List<string>();
        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (payload, expected) in commands)
        {
            var outcome = await _commands.SendAsync(ReuseUnresolved(_commands.Prepare(payload, expected)), ct)
                .ConfigureAwait(false);
            _connectivity.Report(outcome.Status);
            TrackUnresolved(outcome);
            rows.UnionWith(outcome.LabelIds);
            if (!outcome.IsApplied)
            {
                _log.LogInformation(
                    "Fiyat düzeltmesi: set-code-price {CommandId} uygulanmadı ({Status}); {Local} yerel, {Changed} komutla yazılmıştı",
                    outcome.Intent.CommandId, outcome.Status, local?.Corrected.Count ?? 0, changed.Count);
                return new SalesCodePriceResult(local, true, changed, outcome.Message ?? SalesCommandMessages.Failed,
                    outcome.Status, rows.ToList());
            }
            changed.AddRange((outcome.Result?.Changed ?? []).Select(id => id.ToString("N")));
        }

        _log.LogInformation("Fiyat düzeltmesi: {Local} etiket yerel, {Commands} komutla {Changed} etiket",
            local?.Corrected.Count ?? 0, commands.Count, changed.Count);
        return new SalesCodePriceResult(local, serverIds.Count > 0, changed, null, null, rows.ToList());
    }

    /// <summary>
    /// Sunucu etiketlerinin komutları: plan ve yayın başına bir <c>set-code-price</c>. Düzeltmenin yayınında kodlu plan
    /// kodu taşır (sunucu yayın×kod'u kendisi bulur) ve yalnız yayın×kod dışında kalan seçilenleri; başka yayından
    /// seçilenler (kalıntı kuyruk) ve kodsuz seçim kodsuz gider, yalnız seçilenlerle — yerel planlayıcının kümesiyle
    /// aynı. Beklenen küme planın o yayındaki düzeltilebilir etiketleridir; düzeltilebilir etiketi olmayan grup gitmez.
    /// </summary>
    private static List<(SalesSetCodePricePayload Payload, IReadOnlyList<string> Expected)> CodePriceCommands(
        SalesCodePriceRequest request, IReadOnlySet<string> serverIds)
    {
        var commands = new List<(SalesSetCodePricePayload, IReadOnlyList<string>)>();
        foreach (var plan in request.Plans)
        {
            var labels = plan.Plan.Correctable.Concat(plan.Plan.Shipped).Concat(plan.Plan.Elsewhere)
                .Where(l => serverIds.Contains(l.Id))
                .DistinctBy(l => l.Id, StringComparer.Ordinal)
                .ToList();
            foreach (var group in labels.GroupBy(l => l.SessionId, StringComparer.Ordinal))
            {
                var code = string.Equals(group.Key, request.SessionId, StringComparison.Ordinal) ? plan.Code : null;
                var expected = plan.Plan.Correctable
                    .Where(l => serverIds.Contains(l.Id) && string.Equals(l.SessionId, group.Key, StringComparison.Ordinal))
                    .Select(l => l.Id).Distinct(StringComparer.Ordinal).ToList();
                if (expected.Count == 0) continue;
                var also = group
                    .Where(l => request.Selection.Contains(l.Id) && (code is null || !LabelRepository.SameCode(l.Code, code)))
                    .Select(l => ToGuid(l.Id)).Distinct().Order().ToList();
                if (code is null && also.Count == 0) continue;
                var paid = expected.Where(id => request.PaidFromBalance?.Contains(id) == true)
                    .Select(ToGuid).Distinct().Order().ToList();
                commands.Add((new SalesSetCodePricePayload(ToGuid(group.Key), code, also, request.NewPrice, paid),
                    expected));
            }
        }
        return commands;
    }

    // ── yedek seçimi (add-backup) ──────────────────────────────────────────

    public Task<SalesBackupResult> AddBackupAsync(
        SalesBackupRequest request, SalesCommandIntent? resend = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (resend is not null) return SendBackupAsync(request, resend, ct);
        // Mod Off: bugünkü çağrı, beklemeden.
        if (Decide([request.ParentLabelId]) is not { } decisions)
            return Task.FromResult(SalesBackupResult.Added(AddBackupLocal(request)));

        var decision = decisions[0];
        switch (decision.Route)
        {
            case SalesEditRoute.Local:
                return Task.FromResult(SalesBackupResult.Added(AddBackupLocal(request)));
            case SalesEditRoute.LocalGated:
                if (!_handover.TryEnter(decision.SessionId!, out var lease))
                    return Task.FromResult(SalesBackupResult.Failed(SalesCommandMessages.HandoverInProgress));
                using (lease)
                    return Task.FromResult(SalesBackupResult.Added(AddBackupLocal(request)));
            case SalesEditRoute.Command:
                return AddBackupByCommandAsync(request, ct);
            default:
                _log.LogInformation("Yedek ekleme: ebeveyn {LabelId} için {Route}", request.ParentLabelId, decision.Route);
                return Task.FromResult(SalesBackupResult.Failed(decision.Reason ?? SalesCommandMessages.Failed));
        }
    }

    private Label AddBackupLocal(SalesBackupRequest request) => _labels.AddBackup(
        request.ParentLabelId, request.Platform, request.Username, request.DisplayName, request.MessageText);

    /// <summary>
    /// Devredilmiş yayının ebeveynine yedek (§4.6 <c>add-backup</c>): müşteri yerelde bugünkü kuralla bulunur ya da
    /// açılır (<see cref="LabelService.EnsureBackupCustomer"/>); niyet yeni <c>newOrderId</c> ile BİR KEZ kurulur
    /// (ürün yok — §12); yeniden deneme aynı niyeti gönderir. Fiyat ve kod gönderilmez: sunucu ebeveynin GÜNCEL
    /// değerini yazar — yerel ayna akış gecikmesi kadar bayat olabilir (başka bilgisayarın <c>set-code-price</c>'ı),
    /// yedek de Y kâğıdı da yanıtın satırından.
    /// </summary>
    private Task<SalesBackupResult> AddBackupByCommandAsync(SalesBackupRequest request, CancellationToken ct)
    {
        // Aynı kişi aynı ebeveyne yeniden seçildi ve önceki niyetin sonucu bilinmiyor (şerit kapatılmış olsa da): aynı
        // CommandId ve newOrderId gider, sunucu ikinci yedeği eklemez.
        SalesCommandIntent? known;
        lock (_unresolvedGate) _unresolvedBackups.TryGetValue(BackupKey(request), out known);
        if (known is not null) return SendBackupAsync(request, known, ct);

        if (_labelRepo.GetById(request.ParentLabelId) is not { } parent)
            return Task.FromResult(SalesBackupResult.Failed(SalesCommandMessages.Failed));
        var customer = _labels.EnsureBackupCustomer(request.Platform, request.Username, request.DisplayName);
        var intent = _commands.Prepare(new SalesAddBackupPayload(
            Guid.NewGuid(),
            ToGuid(parent.Id),
            new SalesAddBackupCustomer(customer.Id, request.Platform, request.Username, request.DisplayName),
            request.MessageText ?? "",
            Price: null,
            Code: null));
        return SendBackupAsync(request, intent, ct);
    }

    /// <summary>Sonucu bilinmeyen <c>add-backup</c> niyetleri (ebeveyn, platform, kullanıcı adı) anahtarıyla; uygulanınca
    /// ya da kalıcı retle düşer.</summary>
    private readonly Dictionary<string, SalesCommandIntent> _unresolvedBackups = new(StringComparer.Ordinal);

    private static string BackupKey(SalesBackupRequest request) =>
        request.ParentLabelId + "\n" + request.Platform + "\n" + request.Username;

    /// <summary>Niyeti gönderir; uygulandıysa yanıtın yerele yazılmış satırını döner. Sonucu bilinmeyen niyet
    /// (<see cref="SalesCommandOutcome.CanResend"/>) "Tekrar dene" için döner ve aynı kişinin yeniden seçimi için
    /// saklanır.</summary>
    private async Task<SalesBackupResult> SendBackupAsync(
        SalesBackupRequest request, SalesCommandIntent intent, CancellationToken ct)
    {
        var outcome = await _commands.SendAsync(intent, ct).ConfigureAwait(false);
        _connectivity.Report(outcome.Status);
        lock (_unresolvedGate)
        {
            if (outcome.CanResend) _unresolvedBackups[BackupKey(request)] = outcome.Intent;
            else _unresolvedBackups.Remove(BackupKey(request));
        }
        if (!outcome.IsApplied)
        {
            _log.LogInformation("Yedek ekleme: add-backup {CommandId} uygulanmadı ({Status}); yeniden gönderilebilir={CanResend}",
                intent.CommandId, outcome.Status, outcome.CanResend);
            return SalesBackupResult.Failed(outcome.Message ?? SalesCommandMessages.Failed,
                outcome.CanResend ? outcome.Intent : null);
        }

        var newId = NewOrderIdOf(intent);
        var backup = _labelRepo.GetById(newId);
        _log.LogInformation("Yedek ekleme: add-backup {CommandId} uygulandı ({Status}); yeni yedek {LabelId} yerelde={Local}",
            intent.CommandId, outcome.ServerStatus, newId, backup is not null);
        return backup is not null ? SalesBackupResult.Added(backup) : new SalesBackupResult(null, null, AddedRemotely: true);
    }

    private static string NewOrderIdOf(SalesCommandIntent intent)
    {
        using var doc = JsonDocument.Parse(intent.PayloadJson);
        return doc.RootElement.GetProperty("newOrderId").GetGuid().ToString("N");
    }

    // ── kargo (attach-shipment, shipment-decision) ─────────────────────────

    public Task<SalesShipmentStepResult> RunDekontShipmentStepAsync(
        string customerId, ShipmentDirective directive, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        // Mod Off: bugünkü yerel sıra, çağıranın iş parçacığında, beklemeden.
        if (!IsModeOn()) return _localShipments.RunDekontShipmentStepAsync(customerId, directive, ct);
        return DekontShipmentStepModeOnAsync(customerId, directive, ct);
    }

    /// <summary>
    /// Mod On'un dekont kargo adımı (§4.6 <c>attach-shipment</c>, §7.1, §5.8): sunucu kullanılamıyorsa istek gitmez.
    /// Niyet BİR KEZ kurulur — müşteri yerelde asıl Id'sine çözülür, önerilen dosya Id'si yeni, beklenen küme yerel
    /// bağlanmamış etiketler ve sunucu sürümleri. <c>stale</c> aday kümesini taşırsa (adayların güncel satırları yazıldı)
    /// aynı komut ve dosya Id'siyle, sunucunun kümesiyle bir kez daha gönderilir; ikinci ret adımı atlatır. Uygulanınca
    /// yönerge: Hold/RecipientPays <c>shipment-decision</c> (çekmece yok), Normal'de eşik yanıtın havuzuyla.
    /// </summary>
    private async Task<SalesShipmentStepResult> DekontShipmentStepModeOnAsync(
        string customerId, ShipmentDirective directive, CancellationToken ct)
    {
        if (_connectivity.UnavailableReason is not null)
        {
            _log.LogInformation("Dekont kargo adımı: sunucu kullanılamıyor; adım atlandı (dekont kaydedildi)");
            return SalesShipmentStepResult.Skipped(SalesCommandMessages.DekontShipmentDeferred);
        }

        var customer = _customers.ResolveId(customerId);
        var unattached = _labelRepo.GetUnattachedByCustomer(customer).Select(l => l.Id).ToList();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var outcome = await _commands.SendAsync(
            _commands.Prepare(new SalesAttachShipmentPayload(customer, Guid.NewGuid()), unattached), ct).ConfigureAwait(false);
        _connectivity.Report(outcome.Status);
        changed.UnionWith(outcome.LabelIds);
        if (outcome.Status == SalesCommandStatus.Stale && outcome.Problem?.Candidates is { } candidates)
        {
            _log.LogInformation(
                "Dekont kargo adımı: attach-shipment {CommandId} stale ({Local} yerel, {Server} sunucu adayı); sunucunun kümesiyle bir kez yeniden",
                outcome.Intent.CommandId, unattached.Count, candidates.Count);
            var retry = outcome.Intent with { Expected = candidates.OrderBy(c => c.OrderId).ToList() };
            outcome = await _commands.SendAsync(retry, ct).ConfigureAwait(false);
            _connectivity.Report(outcome.Status);
            changed.UnionWith(outcome.LabelIds);
        }
        if (!outcome.IsApplied)
        {
            _log.LogInformation("Dekont kargo adımı: attach-shipment {CommandId} uygulanmadı ({Status}); adım atlandı",
                outcome.Intent.CommandId, outcome.Status);
            return SalesShipmentStepResult.Skipped(
                outcome.CanResend
                    ? SalesCommandMessages.DekontShipmentDeferred
                    : SalesCommandMessages.DekontShipmentFailed(outcome.Message ?? SalesCommandMessages.Failed),
                changed.ToList());
        }

        var result = outcome.Result;
        Guid? target = result?.ShipmentId ?? (result?.PoolShipmentIds is { Count: > 0 } pool ? pool[0] : null);
        _log.LogInformation(
            "Dekont kargo adımı: attach-shipment {CommandId} {Status} — {Attached} etiket, dosya {ShipmentId}, havuz {Pool}",
            outcome.Intent.CommandId, outcome.ServerStatus, result?.Changed?.Count ?? 0, target, result?.Pool);
        if (target is not { } targetId)
            return new SalesShipmentStepResult(null, null, changed.ToList());     // bağlanacak etiket ve açık dosya yok

        var shipmentId = targetId.ToString("N");
        if (directive is ShipmentDirective.Hold or ShipmentDirective.RecipientPays)
        {
            var decision = await DecideModeOnAsync(shipmentId,
                directive == ShipmentDirective.Hold ? ShipmentDecision.Hold : ShipmentDecision.RecipientPays, ct)
                .ConfigureAwait(false);
            changed.UnionWith(decision.ChangedLabelIds);
            return new SalesShipmentStepResult(null, decision.Problem, changed.ToList());
        }

        if (_shipmentRepo.GetById(shipmentId) is not { } mirror)
        {
            _log.LogWarning("Dekont kargo adımı: dosya {ShipmentId} yerel aynada yok; eşik sorulmadı", shipmentId);
            return new SalesShipmentStepResult(null, null, changed.ToList());
        }
        var ctx = _shipments.EvaluatePool(mirror, result?.Pool ?? mirror.CumulativeAmount);
        return new SalesShipmentStepResult(ctx.ShouldPrompt && ctx.ThresholdReached ? ctx : null, null, changed.ToList());
    }

    public Task<SalesShipmentDecisionResult> DecideShipmentAsync(
        string shipmentId, ShipmentDecision decision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shipmentId);
        // Mod Off: bugünkü yerel karar, beklemeden.
        if (!IsModeOn()) return _localShipments.DecideShipmentAsync(shipmentId, decision, ct);
        return DecideModeOnAsync(shipmentId, decision, ct);
    }

    /// <summary>
    /// Mod On'un kargo kararı (§4.6 <c>shipment-decision</c>, §7.3): beklenen sürüm aynanın
    /// <see cref="Shipment.ServerVersion"/>'ı; sessiz yeniden deneme yok. <c>stale</c> ve <c>shipment-shipped</c>'te
    /// dosyanın güncel hâli (<c>currentShipments</c>) yerel aynaya yazılmıştır, sonuç sorunu taşır. Uygulanınca havuz
    /// kararın dosyalarının (U12) yanıttaki tutarlarıdır ("kazandın" mesajı bunu söyler).
    /// </summary>
    private async Task<SalesShipmentDecisionResult> DecideModeOnAsync(
        string shipmentId, ShipmentDecision decision, CancellationToken ct)
    {
        if (_connectivity.UnavailableReason is { } unavailable)
        {
            _log.LogInformation("Kargo kararı {Decision}: dosya {ShipmentId} — sunucu kullanılamıyor", decision, shipmentId);
            return new SalesShipmentDecisionResult(null, 0m, SalesCommandMessages.ShipmentDecisionFailed(unavailable), []);
        }

        var outcome = await _commands.SendAsync(_commands.ShipmentDecision(shipmentId, WireDecision(decision)), ct)
            .ConfigureAwait(false);
        _connectivity.Report(outcome.Status);
        if (!outcome.IsApplied)
        {
            var replan = outcome.Status == SalesCommandStatus.Stale ? Replan(shipmentId) : null;
            _log.LogInformation(
                "Kargo kararı {Decision}: shipment-decision {CommandId} dosya {ShipmentId} uygulanmadı ({Status}); plan yeniden gösterilecek={Replan}",
                decision, outcome.Intent.CommandId, shipmentId, outcome.Status, replan is not null);
            return new SalesShipmentDecisionResult(null, 0m,
                SalesCommandMessages.ShipmentDecisionFailed(outcome.Message ?? SalesCommandMessages.Failed), outcome.LabelIds,
                replan);
        }

        var pooled = outcome.Result?.Decided?.Sum(d => d.Amount) ?? 0m;
        _log.LogInformation(
            "Kargo kararı {Decision}: shipment-decision {CommandId} uygulandı — {Files} dosya, {Unlinked} bağı çözülen etiket",
            decision, outcome.Intent.CommandId, outcome.Result?.Decided?.Count ?? 0, outcome.Result?.Changed?.Count ?? 0);
        return new SalesShipmentDecisionResult(_shipmentRepo.GetById(shipmentId), pooled, null, outcome.LabelIds);
    }

    /// <summary>
    /// <c>stale</c>'den sonra planın güncel hâli (§5.3): yanıtın <c>currentShipments</c>'ı aynaya yazıldı. Dosya hâlâ
    /// açıksa (Pending/Held) eşik kişinin aynadaki bütün açık dosyalarının toplamıyla yeniden değerlendirilir (U12;
    /// dekont adımıyla aynı kural); havuz eşiğin altındaysa ya da dosya kargolandıysa sorulacak karar yok — null.
    /// </summary>
    private ShipmentDecisionContext? Replan(string shipmentId)
    {
        try
        {
            if (_shipmentRepo.GetById(shipmentId) is not { Status: ShipmentStatus.Pending or ShipmentStatus.Held } mirror)
                return null;
            var ctx = _shipments.EvaluateAfterPayment(mirror.CustomerId, allLabelsPaid: true);
            return ctx is { ShouldPrompt: true, ThresholdReached: true, Shipment: not null } ? ctx : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Kargo kararı: dosya {ShipmentId} için plan yeniden değerlendirilemedi", shipmentId);
            return null;
        }
    }

    private static string WireDecision(ShipmentDecision decision) => decision switch
    {
        ShipmentDecision.ShipNow => SalesShipmentDecisionPayload.ShipNow,
        ShipmentDecision.Hold => SalesShipmentDecisionPayload.Hold,
        ShipmentDecision.RecipientPays => SalesShipmentDecisionPayload.RecipientPays,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
    };

    // ── sonucu bilinmeyen niyetler ─────────────────────────────────────────

    private readonly object _unresolvedGate = new();

    /// <summary>Sonucu bilinmeyen (zaman aşımı, sunucu hatası) <c>set-code-price</c> niyetleri, içerikleriyle: aynı
    /// içerikli yeni niyet bunun yerine — aynı <c>CommandId</c> ile — gider (§4.6 adım 2, niyet başına bir kimlik).</summary>
    private readonly Dictionary<string, SalesCommandIntent> _unresolved = new(StringComparer.Ordinal);

    private static string IntentKey(SalesCommandIntent intent) =>
        intent.Kind + "\n" + intent.PayloadJson + "\n"
        + string.Join(",", intent.Expected.Select(e => $"{e.OrderId:N}:{e.Version}"));

    private SalesCommandIntent ReuseUnresolved(SalesCommandIntent fresh)
    {
        lock (_unresolvedGate) return _unresolved.TryGetValue(IntentKey(fresh), out var known) ? known : fresh;
    }

    private void TrackUnresolved(SalesCommandOutcome outcome)
    {
        var key = IntentKey(outcome.Intent);
        lock (_unresolvedGate)
        {
            if (outcome.CanResend) _unresolved[key] = outcome.Intent;
            else _unresolved.Remove(key);
        }
    }

    private static Guid ToGuid(string id) =>
        Guid.TryParse(id, out var g) ? g : throw new ArgumentException($"Satış komutunun kimliği GUID değil: {id}.");

    // ── sonuç ──────────────────────────────────────────────────────────────

    private sealed class ResultBuilder
    {
        private readonly List<string> _applied = new();
        private readonly List<string> _pending = new();
        private readonly List<string> _notices = new();
        private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
        private readonly List<(string Message, List<string> Ids)> _problems = new();

        public int ProblemCount => _problems.Sum(p => p.Ids.Count);

        public int PendingCount => _pending.Count;

        public void Applied(IEnumerable<string> ids) => _applied.AddRange(ids);

        public void Pending(IEnumerable<string> ids) => _pending.AddRange(ids);

        public void Notice(IEnumerable<string> texts)
        {
            foreach (var text in texts)
                if (!_notices.Contains(text)) _notices.Add(text);
        }

        public void Changed(IEnumerable<string> ids) => _changed.UnionWith(ids);

        public void Problem(string message, string id) => Problem(message, [id]);

        public void Problem(string message, IEnumerable<string> ids)
        {
            var group = _problems.FirstOrDefault(p => p.Message == message);
            if (group.Ids is null) _problems.Add((message, ids.ToList()));
            else group.Ids.AddRange(ids);
        }

        public SalesEditResult Build() => new(
            _applied.ToList(),
            _problems.Select(p => new SalesEditProblem(p.Message, p.Ids)).ToList(),
            _changed.ToList(),
            _pending.Count == 0 ? null : _pending.ToList(),
            _notices.Count == 0 ? null : _notices.ToList());
    }
}
