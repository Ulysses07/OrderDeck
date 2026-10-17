using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
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
    private readonly SalesConnectivity _connectivity;
    private readonly Func<DateTimeOffset?> _lastFeedSyncAt;
    private readonly ILogger<SalesEditRouter> _log;

    /// <param name="lastFeedSyncAt">Satış akışının bu süreçte son yetiştiği an (çevrimdışı yeniden baskının notu).</param>
    public SalesEditRouter(
        LabelService labels,
        LabelRepository labelRepo,
        SessionRepository sessions,
        SalesSyncStateRepository state,
        HandoverGate handover,
        SalesCommandClient commands,
        SalesConnectivity connectivity,
        Func<DateTimeOffset?> lastFeedSyncAt,
        ILogger<SalesEditRouter> log)
    {
        _labels = labels;
        _labelRepo = labelRepo;
        _sessions = sessions;
        _state = state;
        _handover = handover;
        _commands = commands;
        _connectivity = connectivity;
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
    /// etiketler TEK yerel yazımda (kapılı yayınların kilitleri yazım boyunca); sunucu etiketleri TEK komutta.
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
        if (server.Count > 0)
        {
            var outcome = await _commands.SendAsync(prepareCommand(server), ct).ConfigureAwait(false);
            _connectivity.Report(outcome.Status);
            result.Changed(outcome.LabelIds);
            if (outcome.IsApplied) result.Applied(server);
            else result.Problem(outcome.Message ?? SalesCommandMessages.Failed, server);
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

        if (server.Count > 0)
        {
            var outcome = await _commands.SendAsync(_commands.Reprint(server), ct).ConfigureAwait(false);
            _connectivity.Report(outcome.Status);
            if (outcome.IsApplied)
            {
                printable.UnionWith((outcome.Result?.ReprintIds ?? []).Select(id => id.ToString("N")));
                corrections.UnionWith((outcome.Result?.CorrectionIds ?? []).Select(id => id.ToString("N")));
            }
            else if (outcome.CanResend)
            {
                cached.AddRange(server);
                fromCache = true;
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
        if (resend is not null) return SendBackupAsync(resend, ct);
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
    /// (fiyat ve kod ebeveynin, ürün yok — §12); yeniden deneme aynı niyeti gönderir.
    /// </summary>
    private Task<SalesBackupResult> AddBackupByCommandAsync(SalesBackupRequest request, CancellationToken ct)
    {
        if (_labelRepo.GetById(request.ParentLabelId) is not { } parent)
            return Task.FromResult(SalesBackupResult.Failed(SalesCommandMessages.Failed));
        var customer = _labels.EnsureBackupCustomer(request.Platform, request.Username, request.DisplayName);
        var intent = _commands.Prepare(new SalesAddBackupPayload(
            Guid.NewGuid(),
            ToGuid(parent.Id),
            new SalesAddBackupCustomer(customer.Id, request.Platform, request.Username, request.DisplayName),
            request.MessageText ?? "",
            parent.Price,
            parent.Code));
        return SendBackupAsync(intent, ct);
    }

    /// <summary>Niyeti gönderir; uygulandıysa yanıtın yerele yazılmış satırını döner. Sonucu bilinmeyen niyet
    /// (<see cref="SalesCommandOutcome.CanResend"/>) "Tekrar dene" için döner.</summary>
    private async Task<SalesBackupResult> SendBackupAsync(SalesCommandIntent intent, CancellationToken ct)
    {
        var outcome = await _commands.SendAsync(intent, ct).ConfigureAwait(false);
        _connectivity.Report(outcome.Status);
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
        private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
        private readonly List<(string Message, List<string> Ids)> _problems = new();

        public int ProblemCount => _problems.Sum(p => p.Ids.Count);

        public void Applied(IEnumerable<string> ids) => _applied.AddRange(ids);

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
            _changed.ToList());
    }
}
