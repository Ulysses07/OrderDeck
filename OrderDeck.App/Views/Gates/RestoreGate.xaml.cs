using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// RestoreDialog'un gate karşılığı. İki durum tek view'da: yedek seçimi ve
/// "tamamlandı".
///
/// Yeniden başlatmayı BURASI yapmıyor — gate true ile kapanıyor, kararı
/// StartupFlow veriyor (IStartupEnvironment.RequestRestart). Akışın tamamı
/// böylece headless test edilebiliyor.
///
/// VM'in <c>SkipCommand</c>'ı ve <c>CloseRequested</c>/<c>RestoreCompletedEvent</c>
/// olayları BİLEREK kullanılmıyor: kapanmanın sahibi gate, VM değil — "Atla"
/// doğrudan <see cref="AppGate.Close"/> çağırıyor, "tamamlandı" durumuna da
/// <c>RestoreCompleted</c> özelliğine bağlanarak geçiliyor.
///
/// <paramref name="vm"/> null olabiliyor: kompozisyon testi bu view'ı
/// servissiz çiziyor (ölçtüğü şey kaynak çözümlemesi).
/// </summary>
public partial class RestoreGate : UserControl
{
    private readonly AppGate _gate;

    private RestoreGate(AppGate gate, RestoreDialogViewModel? vm)
    {
        InitializeComponent();
        _gate = gate;
        DataContext = vm;
    }

    /// <summary>Fabrika: içerik gate'in kendisini alır (yığın kalıbı).</summary>
    public static RestoreGate Create(AppGate gate, RestoreDialogViewModel? vm)
        => new(gate, vm);

    private void OnSkip(object sender, RoutedEventArgs e) => _gate.Close(false);

    private void OnRestart(object sender, RoutedEventArgs e) => _gate.Close(true);
}
