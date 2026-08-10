using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Page chooser used by <c>SettingsViewModel.ConnectFacebookCommand</c> when
/// the operator's account has more than one manageable Page. For the common
/// one-Page install <c>FacebookOAuthService</c> auto-picks and this drawer
/// never opens.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class FacebookPagePickerDrawer : UserControl
{
    private readonly Drawer _drawer;

    public FacebookPageCandidate? Selected { get; private set; }

    private FacebookPagePickerDrawer(
        Drawer drawer, IReadOnlyList<FacebookPageCandidate> candidates)
    {
        InitializeComponent();
        _drawer = drawer;
        PagesList.ItemsSource = candidates;
        if (candidates.Count > 0) PagesList.SelectedIndex = 0;
    }

    public static FacebookPagePickerDrawer Create(
        Drawer drawer, IReadOnlyList<FacebookPageCandidate> candidates)
        => new(drawer, candidates);

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Selected = PagesList.SelectedItem as FacebookPageCandidate;
        // Seçim yoksa kapatma: boş sonuçla dönmek OAuth akışını sessizce
        // iptal etmiş gibi gösterirdi, eski pencere de öyle davranıyordu.
        if (Selected is null) return;
        _drawer.Close(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Selected = null;
        _drawer.Close(false);
    }
}
