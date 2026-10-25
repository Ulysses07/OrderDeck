using System;
using System.Collections.ObjectModel;
using System.Linq;
using OrderDeck.App.Formatting;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

public sealed partial class StreamHistoryViewModel : ViewModelBase
{
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;

    public ObservableCollection<StreamHistoryRow> Sessions { get; } = new();

    /// <summary>Listede başka bilgisayarın (satış akışından inmiş) yayını var: "Bilgisayar" sütunu yalnız o zaman
    /// görünür (spec 2026-10-13 §5.7; W2-09). Mod Off'ta (bugün her lisans) uzak yayın yoktur, tablo bugünkü gibi.</summary>
    public bool HasRemoteSessions { get; private set; }

    public StreamHistoryViewModel(SessionRepository sessions, LabelRepository labels)
    {
        _sessions = sessions;
        _labels = labels;
        Reload();
    }

    /// <summary>Listedeki en çok satır. Gizlenen (cirosuz) yayın bu sınırdan yer yemez: sorgu sınırsız okunur, liste
    /// dolunca durulur.</summary>
    private const int MaxRows = 365;

    private void Reload()
    {
        Sessions.Clear();
        foreach (var s in _sessions.GetAllEnded(limit: int.MaxValue))
        {
            if (Sessions.Count == MaxRows) break;
            var totals = _labels.GetSessionTotals(s.Id);
            if (!IsListed(s, totals.TotalAmount)) continue;
            var endedAt = s.EndedAt ?? s.StartedAt;
            var seconds = endedAt - s.StartedAt;

            Sessions.Add(new StreamHistoryRow(
                SessionId:    s.Id,
                StartedLabel: TrFormats.DateTime(s.StartedAt),
                Duration:     FormatDuration(seconds),
                LabelCount:   totals.PrintedCount,
                TotalAmount:  totals.TotalAmount,
                Platforms:    string.Join(", ", s.Platforms),
                Machine:      MachineOf(s)));
        }
        HasRemoteSessions = Sessions.Any(r => r.Machine is not null);
        OnPropertyChanged(nameof(HasRemoteSessions));
    }

    /// <summary>Cirosu 0 olan bitmiş yayın listelenmez (Burak, 25.10.2026). Ciro, "Ciro" sütununun gösterdiği değerdir
    /// (<see cref="LabelRepository.GetSessionTotals"/>'ın <c>TotalAmount</c>'u); yerel ve başka bilgisayarın yayınına
    /// aynı kural. Yayın silinmez, yalnız listede görünmez; raporu ve verisi yerinde. Süren (açık) yayın cirosu 0 olsa
    /// da gizlenmez: bugün <see cref="SessionRepository.GetAllEnded"/> açık yayın döndürmez, koşul sorgu genişlerse
    /// canlı yayının satışsız ilk dakikalarında listeden düşmesin diye.</summary>
    internal static bool IsListed(StreamSession s, decimal totalAmount) =>
        totalAmount != 0m || s.EndedAt is null;

    /// <summary>Başka bilgisayarın yayını için yayını açan makinenin adı (<c>OwnerMachineName</c>); adı bilinmiyorsa
    /// "Başka bilgisayar". Bu bilgisayarın yayınında null.</summary>
    internal static string? MachineOf(StreamSession s) =>
        s.Origin != StreamSessionOrigin.Remote ? null
        : string.IsNullOrWhiteSpace(s.OwnerMachineName) ? "Başka bilgisayar"
        : s.OwnerMachineName;

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
/// <param name="Machine">Başka bilgisayarın yayınında makine adı (W2-09); bu bilgisayarın yayınında null.</param>
public sealed record StreamHistoryRow(
    string SessionId,
    string StartedLabel,
    string Duration,
    int LabelCount,
    decimal TotalAmount,
    string Platforms,
    string? Machine = null);
