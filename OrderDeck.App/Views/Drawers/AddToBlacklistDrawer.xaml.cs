using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;

namespace OrderDeck.App.Views.Drawers;

public partial class AddToBlacklistDrawer : UserControl
{
    /// <summary>Prefilled = kullanıcı sohbetten/kuyruktan geldi, kimliği
    /// değiştirilemez; Manual = operatör elle yazıyor.</summary>
    public enum DrawerMode { Prefilled, Manual }

    private readonly Drawer _drawer;

    /// <summary>Onaylandıysa okunacak sonuçlar. Çekmece kapandıktan sonra
    /// çağıran bu view örneğinden alır (Drawer sonuç taşımıyor).</summary>
    public string PlatformText { get; private set; } = "instagram";
    public string UsernameText { get; private set; } = "";
    public string? ReasonText { get; private set; }

    private AddToBlacklistDrawer(
        Drawer drawer, DrawerMode mode, string? platform, string? username)
    {
        InitializeComponent();
        _drawer = drawer;

        UsernameBox.Text = username ?? "";
        var target = platform ?? "instagram";
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

        UsernameBox.IsReadOnly = mode == DrawerMode.Prefilled;
        PlatformBox.IsEnabled = mode == DrawerMode.Manual;

        Loaded += (_, _) =>
        {
            if (mode == DrawerMode.Manual) UsernameBox.Focus();
            else ReasonBox.Focus();
        };
    }

    public static AddToBlacklistDrawer Create(
        Drawer drawer, DrawerMode mode, string? platform = null, string? username = null)
        => new(drawer, mode, platform, username);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            WarningText.Text = "Kullanıcı adı boş olamaz.";
            WarningText.Visibility = Visibility.Visible;
            UsernameBox.Focus();
            return;
        }

        UsernameText = username;
        ReasonText = string.IsNullOrWhiteSpace(ReasonBox.Text) ? null : ReasonBox.Text.Trim();
        PlatformText = (PlatformBox.SelectedItem as ComboBoxItem)?.Content as string ?? "instagram";

        _drawer.Close(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
