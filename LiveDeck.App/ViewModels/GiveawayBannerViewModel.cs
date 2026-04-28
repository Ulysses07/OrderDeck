using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.App.ViewModels;

/// <summary>
/// While a giveaway is active, drives the live banner shown in MainShell instead of the
/// usual stream status label. Computes countdown and refreshes participant count every
/// second via DispatcherTimer. Fires <see cref="AutoDrawRequested"/> when the timer hits 0.
/// </summary>
public sealed partial class GiveawayBannerViewModel : ViewModelBase, IDisposable
{
    private readonly GiveawayRepository _giveaways;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;
    private Giveaway? _active;

    /// <summary>True when a giveaway is being tracked.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private int _participantCount;
    [ObservableProperty] private string _countdownText = "";    // "0:32" or "(süre limitsiz)"
    [ObservableProperty] private bool _isManualEnd;             // true when DurationSeconds = 0

    /// <summary>Raised when the countdown reaches 0. MainShell handles auto-draw.</summary>
    public event Action? AutoDrawRequested;

    public GiveawayBannerViewModel(GiveawayRepository giveaways, IClock clock)
    {
        _giveaways = giveaways;
        _clock = clock;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    public void StartTracking(Giveaway g)
    {
        _active = g;
        Keyword = g.Keyword;
        IsManualEnd = g.DurationSeconds == 0;
        IsActive = true;
        UpdateState();
        _timer.Start();
    }

    public void StopTracking()
    {
        _timer.Stop();
        _active = null;
        IsActive = false;
    }

    private void OnTick(object? sender, EventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (_active is null) return;

        ParticipantCount = _giveaways.GetParticipants(_active.Id).Count;

        if (IsManualEnd)
        {
            CountdownText = "(süre limitsiz)";
            return;
        }

        long now = _clock.UnixNow();
        long endsAt = _active.StartedAt + _active.DurationSeconds;
        long remaining = endsAt - now;

        if (remaining <= 0)
        {
            CountdownText = "0:00";
            _timer.Stop();
            AutoDrawRequested?.Invoke();
            return;
        }

        long mm = remaining / 60;
        long ss = remaining % 60;
        CountdownText = $"{mm}:{ss:D2}";
    }

    public void Dispose() => _timer.Stop();
}
