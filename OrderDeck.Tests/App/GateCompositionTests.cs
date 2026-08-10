using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.Views;
using OrderDeck.App.Views.Gates;

namespace OrderDeck.Tests.App;

/// <summary>
/// Gate katmanı gerçekten çiziliyor mu? (Faz 3'teki
/// MainShellViewCompositionTests kalıbı.)
///
/// NEDEN TEK [Fact]: her Fact kendi STA thread'ini açıyor. Hepsini tek
/// thread'de kurmak hem hızlı hem de "process başına tek Application"
/// kuralını en az zorlayan yol.
/// </summary>
public class GateCompositionTests
{
    [Fact]
    public void Gate_layer_composes()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var gates = new AppGateStack();
            var root = new AppRootView(gates);

            // Binding'ler DataBind önceliğinde dispatcher kuyruğuna giriyor;
            // Pump() ile boşaltıyoruz ki ilk değerlendirme tamamlansın.
            ThemeTestHost.Pump();

            // Gate yokken katman Collapsed ve shell yuvası boş.
            Assert.Equal(Visibility.Collapsed, root.GateHost.Visibility);
            Assert.False(root.IsShellMounted);

            // Shell yuvası doldurulabiliyor.
            root.MountShell(new Border());
            Assert.True(root.IsShellMounted);

            // Gate açılınca: katman görünür, içerik doğru nesne,
            // ShellHost Tab navigasyonuna kapalı.
            var content = new Border();
            _ = gates.ShowAsync(_ => content);

            // ShowAsync → PropertyChanged → binding kuyruğa girdi; Pump() boşaltır.
            ThemeTestHost.Pump();

            Assert.Equal(Visibility.Visible, root.GateHost.Visibility);
            Assert.Same(content, root.GateContent.Content);
            Assert.False(root.ShellHost.IsEnabled);

            // Açık Border gate'ini kapat; yığın temizlendikten sonra
            // BootGate bloğu boş bir yığından başlasın.
            gates.Top!.Close(false);
            ThemeTestHost.Pump();
            Assert.False(gates.IsOpen);
            Assert.Equal(Visibility.Collapsed, root.GateHost.Visibility);
            Assert.True(root.ShellHost.IsEnabled);

            // BootGate gate katmanına gerçekten oturuyor mu?
            var pending = gates.ShowAsync(_ => new BootGate());
            Assert.IsType<BootGate>(gates.Top!.Content);
            Assert.True(gates.IsOpen);

            gates.Top.Close(false);
            Assert.False(gates.IsOpen);
            Assert.True(pending.IsCompleted);
        });
        Assert.Null(error);
    }
}
