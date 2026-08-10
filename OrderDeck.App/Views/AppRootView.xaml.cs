using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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

        // Klavye modalliği: gate açıkken ShellHost'u devre dışı bırak.
        // Border'ın opak zemini fare tıklamalarını zaten engelliyor; ama
        // IsEnabled=true kalan bir alt ağaç Tab ile gezilebilir olmaya devam
        // eder. Disabled alt ağaç Tab navigasyonuna kapalıdır, bu açığı kapar.
        gates.PropertyChanged += OnGatesPropertyChanged;
    }

    private void OnGatesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(AppGateStack.IsOpen)) return;

        var gates = (AppGateStack)sender!;
        ShellHost.IsEnabled = !gates.IsOpen;

        if (gates.IsOpen)
        {
            // Gate içeriğinin şablonu PropertyChanged anında henüz
            // gerçekleştirilmemiş olabilir; BeginInvoke ile Input
            // önceliğine erteleyerek şablon realize olduktan sonra
            // odak taşıyoruz.
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                () => GateHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        }
    }

    /// <summary>Shell kuruldu mu? MainWindow.OnClosing bunu soruyor: gate
    /// aşamasındayken MainShellViewModel'i DI'dan çekmek onu KURAR ve
    /// veritabanı henüz yokken patlar.</summary>
    public bool IsShellMounted => ShellHost.Content is not null;

    /// <summary>Gate'ler geçildikten sonra bir kez çağrılır.</summary>
    public void MountShell(object shell) => ShellHost.Content = shell;
}
