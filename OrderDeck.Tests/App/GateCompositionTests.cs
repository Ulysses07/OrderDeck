using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.Views;

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

            // Gate yokken katman kapalı, shell yuvası boş.
            Assert.False(root.IsShellMounted);

            // Shell yuvası doldurulabiliyor.
            root.MountShell(new Border());
            Assert.True(root.IsShellMounted);
        });
        Assert.Null(error);
    }
}
