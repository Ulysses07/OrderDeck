using System;
using System.Windows;
using System.Windows.Controls;

namespace OrderDeck.App.Views.Shell;

/// <summary>FİYAT değişikliğinin satır içi sonucu (F2). Kabuk (<see cref="MainShellView"/>) sonuca kuyruğun payı
/// düşülmüş yeri verir (<see cref="Room"/>); sonuç ondan uzun olamaz. Başlık satırı ("Kapat") her zaman görünür,
/// yer azsa yalnız gövde — kaydırılan alan — küçülür, gerekirse sıfıra.</summary>
public partial class CodePriceResultPanel : UserControl
{
    private double _room = double.PositiveInfinity;

    /// <summary>Gövde dışındaki yükseklik (çerçeve, başlık, kenar boşluğu) — sonuç son görünürken ölçüldü.
    /// Gizliyken ölçülemez; yeniden görünür olunca ilk geçiş bununla sınırlanır, gerekirse SizeChanged düzeltir.</summary>
    private double _chrome;

    public CodePriceResultPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
    }

    /// <summary>Sonucun kaplayabileceği en büyük yükseklik, kenar boşluğu dahil.</summary>
    public double Room
    {
        get => _room;
        set
        {
            _room = value;
            Fit();
        }
    }

    private void Fit()
    {
        if (Frame.Visibility == Visibility.Visible && ActualHeight > 0)
            _chrome = Math.Max(0, ActualHeight - Body.ActualHeight);
        var max = Math.Max(0, _room - _chrome);
        if (!max.Equals(Body.MaxHeight)) Body.MaxHeight = max;
    }
}
