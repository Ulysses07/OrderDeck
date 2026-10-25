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

    /// <summary>Listedeki en çok satır. Gizlenen yayın (<see cref="IsListed"/>) bu sınırdan yer yemez: sorgu sınırsız
    /// okunur, liste dolunca durulur.</summary>
    private const int MaxRows = 365;

    private void Reload()
    {
        Sessions.Clear();
        foreach (var s in _sessions.GetAllEnded(limit: int.MaxValue))
        {
            if (Sessions.Count == MaxRows) break;
            var totals = _labels.GetSessionTotals(s.Id);
            if (!IsListed(totals)) continue;
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

    /// <summary>Basılmış etiketi (iptal edilmemiş, onaylanmamış yedek olmayan) hiç olmayan bitmiş yayın listelenmez
    /// (Burak, 25.10.2026: "ciro 0 olan yayınları yayın geçmişinde göstermeye gerek yok"): satışsız, yalnız iptalli ya
    /// da yalnız basılmamış etiketli yayın gizlenir. 0 TL basılmış etiketi olan yayın listede kalır: 0 TL satış meşrudur
    /// (K8) ve bitmiş yayının raporuna (müşteri listesi, Excel, K6 "Basılan etiketler" ile fiyat düzeltme) giden kalıcı
    /// tek yol bu listedir. "Etiket" ve "Ciro" sütunları aynı etiket kümesinden gelir
    /// (<see cref="LabelRepository.GetSessionTotals"/>): cirosu 0'dan farklı her yayının basılmış etiketi vardır, yani
    /// kural tutar karşılaştırmaz ve yuvarlamaya bağlı değildir. Yerel ve başka bilgisayarın yayınına aynı kural; yayın
    /// silinmez, yalnız listede görünmez.</summary>
    private static bool IsListed(SessionTotals totals) => totals.PrintedCount > 0;

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
