using System.Windows.Controls;
using OrderDeck.App.Services.Gates;

namespace OrderDeck.App.Views;

/// <summary>
/// MainWindow'un tek çocuğu. Gate katmanını ve shell yuvasını taşır.
/// </summary>
public partial class AppRootView : UserControl
{
    public AppRootView(AppGateStack gates)
    {
        InitializeComponent();
        DataContext = gates;
    }

    /// <summary>Shell kuruldu mu? MainWindow.OnClosing bunu soruyor: gate
    /// aşamasındayken MainShellViewModel'i DI'dan çekmek onu KURAR ve
    /// veritabanı henüz yokken patlar.</summary>
    public bool IsShellMounted => ShellHost.Content is not null;

    /// <summary>Gate'ler geçildikten sonra bir kez çağrılır.</summary>
    public void MountShell(object shell) => ShellHost.Content = shell;
}
