using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Windows;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.App.Views;

/// <summary>
/// Modal Page chooser used by <c>SettingsViewModel.ConnectFacebookCommand</c>
/// when the operator's account has more than one manageable Page. For the
/// common one-Page install <c>FacebookOAuthService</c> auto-picks and this
/// dialog never opens.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class FacebookPagePickerDialog : Window
{
    public FacebookPageCandidate? Selected { get; private set; }

    public FacebookPagePickerDialog(IReadOnlyList<FacebookPageCandidate> candidates)
    {
        InitializeComponent();
        PagesList.ItemsSource = candidates;
        if (candidates.Count > 0) PagesList.SelectedIndex = 0;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Selected = PagesList.SelectedItem as FacebookPageCandidate;
        DialogResult = Selected is not null;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Selected = null;
        DialogResult = false;
        Close();
    }
}
