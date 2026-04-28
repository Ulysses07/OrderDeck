using System;
using System.Collections.ObjectModel;
using LiveDeck.App.Formatting;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

public sealed partial class StreamHistoryViewModel : ViewModelBase
{
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;

    public ObservableCollection<StreamHistoryRow> Sessions { get; } = new();

    public StreamHistoryViewModel(SessionRepository sessions, LabelRepository labels)
    {
        _sessions = sessions;
        _labels = labels;
        Reload();
    }

    private void Reload()
    {
        Sessions.Clear();
        foreach (var s in _sessions.GetAllEnded(limit: 365))
        {
            var totals = _labels.GetSessionTotals(s.Id);
            var endedAt = s.EndedAt ?? s.StartedAt;
            var seconds = endedAt - s.StartedAt;

            Sessions.Add(new StreamHistoryRow(
                SessionId:    s.Id,
                StartedLabel: TrFormats.DateTime(s.StartedAt),
                Duration:     FormatDuration(seconds),
                LabelCount:   totals.PrintedCount,
                TotalAmount:  totals.TotalAmount,
                Platforms:    string.Join(", ", s.Platforms)));
        }
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}s {ts.Minutes}d"
            : $"{ts.Minutes}d {ts.Seconds}s";
    }
}

/// <summary>Flattened row for the history DataGrid.</summary>
public sealed record StreamHistoryRow(
    string SessionId,
    string StartedLabel,
    string Duration,
    int LabelCount,
    decimal TotalAmount,
    string Platforms);
