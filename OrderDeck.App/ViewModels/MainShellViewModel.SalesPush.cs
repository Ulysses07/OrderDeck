using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services.Sync;

namespace OrderDeck.App.ViewModels;

// ─── Etiket gönderiminin sonucu (spec 2026-10-13 §5.2, §5.6-5, §5.7 "Reddedilen push"; W1-08) ─────────────
//
// Gönderim turu (SessionOrderSyncService, arka plan iş parçacığı) sunucunun yok saydığı ya da reddettiği satırlara
// sunucunun değerini uyguladıktan sonra PushOutcome yayar. Operatör iki durumda uyarılır, açılır pencere OLMADAN
// (kabuk şeridi, "Tamam" ile kapanır): reddedilen değişiklikler ("N değişiklik uygulanamadı: …") ve başka
// bilgisayarda iptal edilmiş olduğu anlaşılan basılı kâğıtlar ("… kâğıtları imha et"). Metin yalnız sayı taşır —
// ad, kullanıcı adı ya da mesaj yok (§9). Şerit kapatılana kadar gelen sonuçlar birikir; aynı etiket iki kez
// sayılmaz (uçuşta değişip sonraki turda yeniden reddedilen satır).
public sealed partial class MainShellViewModel
{
    private readonly SessionOrderSyncService? _orderSync;

    /// <summary>Şerit kapatılana kadar biriken reddedilen etiketler: kimlik → neden (boş neden "").</summary>
    private readonly Dictionary<string, string> _pendingRejected = new(System.StringComparer.Ordinal);

    /// <summary>Şerit kapatılana kadar biriken imha edilecek kâğıtların etiketleri.</summary>
    private readonly HashSet<string> _pendingDiscard = new(System.StringComparer.Ordinal);

    /// <summary>Gönderim sonucunun uyarı şeridi; null = şerit kapalı.</summary>
    [ObservableProperty] private string? _salesPushWarning;

    /// <summary>Arka plan iş parçacığından gelir: dispatcher'a taşınır, gönderim turunu bekletmez.</summary>
    private void OnSalesPushOutcome(object? sender, SalesPushOutcome outcome)
    {
        if (!outcome.NeedsAttention) return;
        if (_dispatcher.CheckAccess()) ShowSalesPushOutcome(outcome);
        else _dispatcher.BeginInvoke(() => ShowSalesPushOutcome(outcome));
    }

    /// <summary>Sonucu şeride ekler (UI iş parçacığında).</summary>
    internal void ShowSalesPushOutcome(SalesPushOutcome outcome)
    {
        foreach (var r in outcome.Rejected) _pendingRejected[r.LabelId] = r.Reason ?? "";
        foreach (var id in outcome.PrintDiscardLabelIds) _pendingDiscard.Add(id);
        var byReason = _pendingRejected.Values
            .GroupBy(r => r, System.StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), System.StringComparer.Ordinal);
        SalesPushWarning = SalesPushWarningText(byReason, _pendingDiscard.Count);
    }

    [RelayCommand]
    private void DismissSalesPushWarning()
    {
        _pendingRejected.Clear();
        _pendingDiscard.Clear();
        SalesPushWarning = null;
    }

    /// <summary>Şeridin metni; uyarılacak bir şey yoksa null.</summary>
    /// <param name="rejectedByReason">Sunucunun nedeni → reddedilen etiket sayısı.</param>
    /// <param name="discard">İmha edilecek kâğıt sayısı.</param>
    internal static string? SalesPushWarningText(IReadOnlyDictionary<string, int> rejectedByReason, int discard)
    {
        var parts = new List<string>(2);
        var rejected = rejectedByReason.Values.Sum();
        if (rejected > 0)
        {
            var reasons = rejectedByReason
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, System.StringComparer.Ordinal)
                .Select(kv => rejectedByReason.Count(x => x.Value > 0) == 1
                    ? RejectReasonText(kv.Key)
                    : $"{RejectReasonText(kv.Key)} ({kv.Value})");
            parts.Add($"{rejected} değişiklik uygulanamadı: {string.Join(", ", reasons)}.");
        }
        if (discard > 0)
            parts.Add($"{discard} etiket başka bilgisayarda iptal edilmişti; kâğıtları imha et.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Sunucunun ret nedeninin operatör metni (§5.2).</summary>
    internal static string RejectReasonText(string? reason) => reason switch
    {
        "session-released" => "yayın sunucuya devredilmişti",
        "shipment-shipped" => "kargoya verilmiş dosya",
        "session-owned-elsewhere" => "yayın başka bilgisayarda açık",
        _ => "sunucu kabul etmedi",
    };
}
