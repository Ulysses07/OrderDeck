using System.Windows;
using System.Windows.Controls;

namespace OrderDeck.App.Views;

public partial class AddToBlacklistDialog : Window
{
    public enum DialogMode { Prefilled, Manual }

    public string? PlatformText { get; set; }
    public string? UsernameText { get; set; }
    public string? ReasonText   { get; set; }
    public DialogMode Mode { get; set; } = DialogMode.Manual;

    public AddToBlacklistDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UsernameBox.Text = UsernameText ?? "";
        ReasonBox.Text   = ReasonText ?? "";

        var target = PlatformText ?? "instagram";
        foreach (ComboBoxItem item in PlatformBox.Items)
        {
            if ((item.Content as string) == target)
            {
                PlatformBox.SelectedItem = item;
                break;
            }
        }
        if (PlatformBox.SelectedItem is null && PlatformBox.Items.Count > 0)
            PlatformBox.SelectedIndex = 0;

        UsernameBox.IsReadOnly = (Mode == DialogMode.Prefilled);
        PlatformBox.IsEnabled  = (Mode == DialogMode.Manual);
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show("Kullanıcı adı boş olamaz.",
                "Eksik bilgi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UsernameText = username;
        ReasonText   = string.IsNullOrWhiteSpace(ReasonBox.Text) ? null : ReasonBox.Text.Trim();
        PlatformText = (PlatformBox.SelectedItem as ComboBoxItem)?.Content as string ?? "instagram";

        DialogResult = true;
    }
}
