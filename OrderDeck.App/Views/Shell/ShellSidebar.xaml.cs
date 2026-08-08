using System.Windows;
using System.Windows.Controls;

namespace OrderDeck.App.Views.Shell;

public partial class ShellSidebar : UserControl
{
    public ShellSidebar() => InitializeComponent();

    /// <summary>
    /// MainShellView'dan taşındı — gövde birebir korundu. ContextMenu butonun
    /// Button.ContextMenu'sü olarak tanımlı, yani mantıksal ağaçta butonun
    /// altında; DataContext (MainShellViewModel) menü öğelerine kendiliğinden
    /// akıyor, elle atamaya gerek yok.
    /// </summary>
    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuButton.ContextMenu is { } cm)
        {
            cm.PlacementTarget = MenuButton;
            cm.IsOpen = true;
        }
    }
}
