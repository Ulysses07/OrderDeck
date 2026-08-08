using CommunityToolkit.Mvvm.ComponentModel;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Kenar çubuğu alt bilgisindeki tek bağlantı satırı.
///
/// DİKKAT: burada "kısaltma + marka rengi" rozeti KULLANILMAZ (bkz.
/// Themes/PlatformIcons.xaml dosya başı — Google itirazı). View, resmi
/// ikonu OD.PlatformIcon.* üzerinden bağlar.
/// </summary>
public sealed partial class PlatformConnectionViewModel : ObservableObject
{
    public PlatformConnectionViewModel(string platform)
    {
        Platform = platform;
        DisplayName = platform switch
        {
            "youtube"   => "YouTube",
            "instagram" => "Instagram",
            "tiktok"    => "TikTok",
            "facebook"  => "Facebook",
            _           => platform
        };
    }

    public string Platform { get; }
    public string DisplayName { get; }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _viewerCount;
}
