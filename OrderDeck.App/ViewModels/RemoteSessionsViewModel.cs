using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sales;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.ViewModels;

/// <summary>Üst çubuktaki bir uzak yayın çipi (§5.7): <c>RemoteLiveSession</c>'ın bir satırı, o anki saate göre.</summary>
/// <param name="SessionId">Yayının Id'si ("N").</param>
/// <param name="Text">"PC-A'da canlı · son sinyal 21:14" ya da "PC-A'da yayın bitti, N etiket gönderilmeyi bekliyor ·
/// son sinyal SS:DD".</param>
/// <param name="SilentNote">Devrediliyor yayında sahip eşik kadar sessizken ek satır; yoksa null.</param>
/// <param name="CanForceEnd">"Yayını bitir…" eylemi: yalnız canlı yayın ∧ sahip eşik kadar sessiz ∧ çevrimiçi.</param>
/// <param name="SilentMinutes">Sahibin sessiz kaldığı tam dakika (son sinyal bilinmiyorsa null).</param>
/// <param name="MachineName">Sahibin makine adı (onay metni için; günlüğe yazılmaz).</param>
/// <param name="IsHandingOver">Devrediliyor yayın (çipin durum noktası).</param>
public sealed record RemoteSessionChipViewModel(
    string SessionId,
    string Text,
    string? SilentNote,
    bool CanForceEnd,
    int? SilentMinutes,
    string? MachineName,
    bool IsHandingOver);

/// <summary>
/// Başka bilgisayarların canlı ve devrediliyor yayınlarının çipleri, "Yayını bitir…" akışı ve bu bilgisayarın uzaktan
/// bitirilmiş canlı yayınının bandı (spec 2026-10-13 §5.7, §4.6 <c>force-end</c>, §5.3, Y7, Y8, M18; W2-09).
///
/// <para><b>Yalnız mod On'da:</b> yerel <c>SalesSyncState.Mode</c> 1 değilse (bugün her lisans) çip, band ve şerit
/// yoktur; hiçbir istek gitmez. Kaynak yerel aynadır (<see cref="RemoteLiveSessionRepository"/>; satış akışı turu her
/// turda <c>sales/state</c>'ten yazar) — başka bilgisayarın yayını <c>StreamSession</c>'a hiç girmez (M5).</para>
///
/// <para><b>Çip:</b> satır başına bir tane. Canlı: "PC-A'da canlı · son sinyal SS:DD". Devrediliyor: "PC-A'da yayın
/// bitti, N etiket gönderilmeyi bekliyor · son sinyal SS:DD"; sahip eşik kadar sessizse ek satır, ama hiçbir zaman
/// eylem yok (Y8). Eşik sunucunun <c>sales/state.forceEndSilenceMinutes</c>'ıdır (bu lisansın son state'i); bilinmiyorsa
/// eylem sunulmaz — 15 sabit yazılmaz. Eylem ayrıca çevrimiçi ister (<see cref="SalesConnectivity.IsOnline"/>): bayat
/// aynada "sessiz" görünen sahip aslında bu bilgisayarın bağlantısızlığıdır.</para>
///
/// <para><b>"Yayını bitir…":</b> açılır pencere yok; kabuk şeridinde §5.7'nin onay metni (çipteki dakika ve makine
/// adıyla). Onay <c>force-end {sessionId, confirm: true}</c> gönderir (<see cref="SalesCommandClient"/>, arka planda).
/// Başarı (etkisiz başarı dahil) çipi ve aynadaki satırı kaldırır ve bir satış akışı turu başlatır (beklemeden).
/// <c>owner-active</c> ve <c>handing-over</c> §5.3 metinleriyle şeritte kalır; sonucu bilinmeyen gönderimde "Tekrar
/// dene" aynı komutu (aynı CommandId) gönderir.</para>
///
/// <para><b>Band:</b> bu bilgisayarın açık (aktif) yayınında <c>ReleasedAt</c> doluysa (başka bilgisayardan force-end;
/// W1 kapanış yanıtı ya da akış, §5.4) yayın sürer, band bilgi verir. Sohbetten etiket, FİYAT ve baskı etkilenmez.</para>
///
/// <para><see cref="Refresh"/> UI iş parçacığında çağrılır (her akış turundan sonra ve dakikada bir); yalnız yerel
/// veritabanını okur. Günlük yalnız kimlik ve durum taşır.</para>
/// </summary>
public sealed partial class RemoteSessionsViewModel : ViewModelBase
{
    private readonly RemoteLiveSessionRepository _liveSessions;
    private readonly SalesSyncStateRepository _state;
    private readonly SessionRepository _sessions;
    private readonly SalesCommandClient _commands;
    private readonly SalesConnectivity _connectivity;
    private readonly Func<SalesStateSnapshot?> _lastState;
    private readonly Func<string?> _licenseKey;
    private readonly Func<CancellationToken, Task> _feedTour;
    private readonly IClock _clock;
    private readonly ILogger<RemoteSessionsViewModel> _log;
    private readonly TimeZoneInfo _zone;

    private RemoteSessionChipViewModel? _confirming;
    private SalesCommandIntent? _retryIntent;
    private string? _retrySessionId;
    private bool _refreshFailing;

    /// <param name="feedTour">Başarılı force-end'den sonra koşan satış akışı turu
    /// (<see cref="SalesChangesPullService.TourOnceAsync"/>); beklenmez.</param>
    /// <param name="zone">SS:DD saat dilimi; verilmezse yerel.</param>
    public RemoteSessionsViewModel(
        RemoteLiveSessionRepository liveSessions,
        SalesSyncStateRepository state,
        SessionRepository sessions,
        SalesCommandClient commands,
        SalesConnectivity connectivity,
        Func<SalesStateSnapshot?> lastState,
        Func<string?> licenseKey,
        Func<CancellationToken, Task> feedTour,
        IClock clock,
        ILogger<RemoteSessionsViewModel>? log = null,
        TimeZoneInfo? zone = null)
    {
        _liveSessions = liveSessions;
        _state = state;
        _sessions = sessions;
        _commands = commands;
        _connectivity = connectivity;
        _lastState = lastState;
        _licenseKey = licenseKey;
        _feedTour = feedTour;
        _clock = clock;
        _log = log ?? NullLogger<RemoteSessionsViewModel>.Instance;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>Uzak yayın çipleri, başlama sırasıyla.</summary>
    public ObservableCollection<RemoteSessionChipViewModel> Chips { get; } = new();

    [ObservableProperty] private bool _hasChips;

    /// <summary>Bu bilgisayarın uzaktan bitirilmiş açık yayınının bandı; null = yok.</summary>
    [ObservableProperty] private string? _ownSessionBand;

    /// <summary>"Yayını bitir…" onay metni; null = şerit kapalı.</summary>
    [ObservableProperty] private string? _forceEndPrompt;

    /// <summary>Onaylanan komut sunucuda: "Vazgeç" pasif (onay komutu kendi yürürken zaten pasif).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelForceEndCommand))]
    private bool _isForceEndBusy;

    /// <summary>Gönderilen force-end'in sorunu (§5.3 metni); null = şerit kapalı.</summary>
    [ObservableProperty] private string? _forceEndProblem;

    /// <summary>Sonucu bilinmeyen gönderim: "Tekrar dene" aynı komutu gönderir.</summary>
    [ObservableProperty] private bool _canRetryForceEnd;

    /// <summary>Son başarılı force-end'in başlattığı akış turu (testler bekler; arayüz beklemez).</summary>
    internal Task? LastFeedTour { get; private set; }

    /// <summary>Çipleri ve bandı yerel aynadan ve yayından yeniden kurar. Hata kabuğu düşürmez.</summary>
    public void Refresh()
    {
        try
        {
            if (_state.GetMode() != 1)
            {
                ClearAll();
                return;
            }

            var now = _clock.UnixNow();
            var threshold = SilenceThresholdMinutes();
            var online = _connectivity.IsOnline;
            var chips = _liveSessions.GetAll().Select(r => BuildChip(r, now, threshold, online)).ToList();
            if (!chips.SequenceEqual(Chips))
            {
                Chips.Clear();
                foreach (var chip in chips) Chips.Add(chip);
            }
            HasChips = Chips.Count > 0;

            OwnSessionBand = _sessions.GetActive() is { ReleasedAt: { } releasedAt }
                ? SalesCommandMessages.SessionEndedRemotely(DateTimeOffset.FromUnixTimeSeconds(releasedAt), _zone)
                : null;
            _refreshFailing = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_refreshFailing) _log.LogWarning(ex, "Uzak yayın çipleri okunamadı; sonraki tazelemede yeniden");
            _refreshFailing = true;
        }
    }

    /// <summary>Bu lisansın son <c>sales/state</c>'indeki sessizlik eşiği; bilinmiyorsa null (eylem sunulmaz).</summary>
    private int? SilenceThresholdMinutes()
    {
        var state = _lastState();
        var key = _licenseKey();
        if (state is null || string.IsNullOrWhiteSpace(key) || state.LicenseKey != key) return null;
        return state.ForceEndSilenceMinutes > 0 ? state.ForceEndSilenceMinutes : null;
    }

    private RemoteSessionChipViewModel BuildChip(RemoteLiveSessionRow row, long now, int? threshold, bool online)
    {
        var lastSeen = row.OwnerLastSeenAt is { } seen ? DateTimeOffset.FromUnixTimeSeconds(seen) : (DateTimeOffset?)null;
        var silentSeconds = row.OwnerLastSeenAt is { } s ? Math.Max(0L, now - s) : (long?)null;
        var silentEnough = threshold is { } t && silentSeconds is { } secs && secs >= t * 60L;
        var minutes = silentSeconds is { } m ? (int)Math.Min(int.MaxValue, m / 60) : (int?)null;

        if (row.State == RemoteLiveSessionState.HandingOver)
            return new RemoteSessionChipViewModel(row.Id,
                SalesCommandMessages.RemoteHandingOverChip(row.OwnerMachineName, row.UnsentLabelCount, lastSeen, _zone),
                silentEnough ? SalesCommandMessages.RemoteHandingOverSilent(row.OwnerMachineName) : null,
                CanForceEnd: false, minutes, row.OwnerMachineName, IsHandingOver: true);

        return new RemoteSessionChipViewModel(row.Id,
            SalesCommandMessages.RemoteLiveChip(row.OwnerMachineName, lastSeen, _zone),
            SilentNote: null, CanForceEnd: silentEnough && online, minutes, row.OwnerMachineName, IsHandingOver: false);
    }

    private void ClearAll()
    {
        if (Chips.Count > 0) Chips.Clear();
        HasChips = false;
        OwnSessionBand = null;
        if (IsForceEndBusy) return;                     // gönderim sürüyor: sonucu yine gösterilsin
        ForceEndPrompt = null;
        ForceEndProblem = null;
        CanRetryForceEnd = false;
        _confirming = null;
        _retryIntent = null;
        _retrySessionId = null;
    }

    // ── "Yayını bitir…" ────────────────────────────────────────────────────

    /// <summary>Çipin eylemi: onay şeridini açar (§5.7 metni, çipteki dakika ve makine adıyla). İstek gitmez.</summary>
    [RelayCommand]
    private void BeginForceEnd(RemoteSessionChipViewModel? chip)
    {
        if (chip is not { CanForceEnd: true, SilentMinutes: { } minutes } || IsForceEndBusy) return;
        _confirming = chip;
        ForceEndProblem = null;
        CanRetryForceEnd = false;
        _retryIntent = null;
        _retrySessionId = null;
        ForceEndPrompt = SalesCommandMessages.ForceEndConfirm(chip.MachineName, minutes);
    }

    /// <summary>"Vazgeç": şerit kapanır, istek gitmez. Gönderim sürerken pasif.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelForceEnd))]
    private void CancelForceEnd()
    {
        if (IsForceEndBusy) return;
        ForceEndPrompt = null;
        _confirming = null;
    }

    private bool CanCancelForceEnd() => !IsForceEndBusy;

    /// <summary>Onay: <c>force-end {sessionId, confirm: true}</c> (yeni CommandId).</summary>
    [RelayCommand]
    private async Task ConfirmForceEndAsync()
    {
        if (_confirming is not { } chip || IsForceEndBusy) return;
        SalesCommandIntent intent;
        try
        {
            intent = _commands.Prepare(new SalesForceEndPayload(Guid.ParseExact(chip.SessionId, "N")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Yayını bitir: oturum {SessionId} için komut kurulamadı", chip.SessionId);
            ShowProblem(SalesCommandMessages.ContentConflict, retry: null, sessionId: null);
            return;
        }
        await SendAsync(chip.SessionId, intent);
    }

    /// <summary>"Tekrar dene": sonucu bilinmeyen komut AYNEN (aynı CommandId) yeniden gider.</summary>
    [RelayCommand]
    private async Task RetryForceEndAsync()
    {
        if (_retryIntent is not { } intent || _retrySessionId is not { } sessionId || IsForceEndBusy) return;
        await SendAsync(sessionId, intent);
    }

    /// <summary>"Tamam": sorun şeridi kapanır.</summary>
    [RelayCommand]
    private void DismissForceEndProblem()
    {
        if (IsForceEndBusy) return;
        ForceEndProblem = null;
        CanRetryForceEnd = false;
        _retryIntent = null;
        _retrySessionId = null;
    }

    private async Task SendAsync(string sessionId, SalesCommandIntent intent)
    {
        IsForceEndBusy = true;
        SalesCommandOutcome outcome;
        try
        {
            outcome = await _commands.SendAsync(intent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Yayını bitir: oturum {SessionId} komutu {CommandId} gönderilemedi", sessionId, intent.CommandId);
            IsForceEndBusy = false;
            ForceEndPrompt = null;
            _confirming = null;
            ShowProblem(SalesCommandMessages.Failed, intent, sessionId);
            return;
        }
        IsForceEndBusy = false;
        ForceEndPrompt = null;
        _confirming = null;
        _connectivity.Report(outcome.Status);

        if (outcome.IsApplied)
        {
            _log.LogInformation(
                "Yayını bitir: oturum {SessionId} sunucuda bitirildi (komut {CommandId}, {Status}, zaten devredilmiş: {AlreadyReleased})",
                sessionId, outcome.Intent.CommandId, outcome.ServerStatus, outcome.Result?.AlreadyReleased == true);
            ForceEndProblem = null;
            CanRetryForceEnd = false;
            _retryIntent = null;
            _retrySessionId = null;
            RemoveChip(sessionId);
            LastFeedTour = RunFeedTourAsync();
            return;
        }

        _log.LogInformation("Yayını bitir: oturum {SessionId} komutu {CommandId} sonuçlanmadı: {Status}",
            sessionId, outcome.Intent.CommandId, outcome.Status);
        ShowProblem(outcome.Message ?? SalesCommandMessages.Failed, outcome.CanResend ? outcome.Intent : null, sessionId);
    }

    private void ShowProblem(string message, SalesCommandIntent? retry, string? sessionId)
    {
        ForceEndProblem = message;
        _retryIntent = retry;
        _retrySessionId = retry is null ? null : sessionId;
        CanRetryForceEnd = retry is not null;
    }

    /// <summary>Çip ve aynadaki satır kalkar; sonraki state onu zaten listelemez.</summary>
    private void RemoveChip(string sessionId)
    {
        try { _liveSessions.Remove(sessionId); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Yayını bitir: oturum {SessionId} aynadan silinemedi; sonraki turda düşer", sessionId);
        }
        foreach (var chip in Chips.Where(c => c.SessionId == sessionId).ToList()) Chips.Remove(chip);
        HasChips = Chips.Count > 0;
    }

    /// <summary>Akış turu arka planda: yayının siparişleri ve devri iner. Beklenmez; hatası yalnız günlüğe.</summary>
    private Task RunFeedTourAsync() => Task.Run(async () =>
    {
        try
        {
            await _feedTour(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Yayını bitir: sonraki satış akışı turu düştü; 30 sn'lik tur yeniden dener");
        }
    });
}
