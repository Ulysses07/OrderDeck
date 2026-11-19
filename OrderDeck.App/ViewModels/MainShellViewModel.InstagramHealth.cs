using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services.Instagram;
using OrderDeck.Chat.Ingestors.Instagram;

namespace OrderDeck.App.ViewModels;

// ─── Instagram hesap sağlığı (çoklu hesap tasarımı 2026-11-16 §8.6.3, §8.6.4) ─────────────────────────────────────
//
// Hosted service'in yazdığı tekil InstagramHealthMonitor 5 sn'lik sohbet nabzında (UpdateChatHealth) bir kez
// değerlendirilir; sonuç üç yüzeye gider: sohbet panelinin sağlık şeridi (InstagramAccounts, ShowInstagramStatus),
// üst bardaki sohbet noktası (Instagram sarı durumu) ve kenar çubuğunun Instagram satırı (en az bir hesap canlı).
// İzleyici verilmezse (testler) hiçbiri koşmaz: şerit boş, nokta ve kenar çubuğu bugünkü gibi.
public sealed partial class MainShellViewModel
{
    private readonly InstagramHealthMonitor? _instagramHealth;

    /// <summary>Son nabzın değerlendirmesi; izleyici yoksa ya da henüz nabız koşmadıysa null.</summary>
    private InstagramHealthView? _instagramView;

    /// <summary>Sohbet panelindeki Instagram sağlık şeridinin satırları.</summary>
    public ObservableCollection<InstagramAccountStatusViewModel> InstagramAccounts { get; } = new();

    /// <summary>Sağlık şeridi görünsün mü.</summary>
    [ObservableProperty] private bool _showInstagramStatus;

    /// <summary>Değerlendirme anı (test dikişi). Yerel saatli an: metinlerdeki saat bu ofsetle yazılır.</summary>
    internal Func<DateTimeOffset> InstagramNow { get; set; } = () => DateTimeOffset.Now;

    /// <summary>
    /// İzleyiciyi değerlendirir, sonucu saklar ve şeride yazar. Satırlar öncekiyle sırayla eşitse koleksiyona
    /// dokunulmaz: değişmeyen satırda açık ipucu her nabızda kapanmasın.
    /// </summary>
    private void EvaluateInstagramHealth(bool sessionActive)
    {
        if (_instagramHealth is null) return;

        var view = InstagramHealthPresenter.Evaluate(_instagramHealth.Snapshot(), InstagramNow(), sessionActive);
        _instagramView = view;
        if (!InstagramAccounts.SequenceEqual(view.Rows))
        {
            InstagramAccounts.Clear();
            foreach (var row in view.Rows) InstagramAccounts.Add(row);
        }
        ShowInstagramStatus = view.ShowStrip;
    }
}
