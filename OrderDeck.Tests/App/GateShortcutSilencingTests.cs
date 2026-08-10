using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.Startup;

namespace OrderDeck.Tests.App;

/// <summary>
/// Gate açıkken Window'a bağlı kısayollar gerçekten susuyor mu?
///
/// NEDEN ÖLÇÜLÜYOR: gate katmanı yalnız <c>ShellHost.IsEnabled</c>'ı düşürüyor,
/// bu da <see cref="Window.InputBindings"/>'e ULAŞMIYOR — KeyBinding'ler
/// pencerenin üzerinde duruyor. Susturma yapılmazsa çalışma anında hesap
/// değiştirmek için açılan LoginGate'in üstünden Ctrl+Shift+E yayını bitirir.
/// Ekranda görünmediği için gözle fark edilmez; tek kanıt sayım.
///
/// Gerçek <see cref="OrderDeck.App.Shortcuts.ShortcutBinder"/> kullanılmıyor:
/// o MainShellViewModel istiyor (22 servis). Ölçülen şey binder'ın içeriği
/// değil, koleksiyonun boşaltılıp yeniden doldurulması.
/// </summary>
public class GateShortcutSilencingTests
{
    [Fact]
    public void Window_shortcuts_are_cleared_while_a_gate_is_open()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var gates = new AppGateStack();
            var window = new Window();
            var applyCount = 0;

            // ShortcutBinder.Apply'ın sözleşmesi: temizle + registry'den
            // yeniden inşa et. Sahte de aynısını yapıyor.
            void Apply(Window w)
            {
                applyCount++;
                w.InputBindings.Clear();
                w.InputBindings.Add(
                    new KeyBinding(ApplicationCommands.NotACommand, Key.F1, ModifierKeys.None));
                w.InputBindings.Add(new KeyBinding(ApplicationCommands.NotACommand, Key.E,
                    ModifierKeys.Control | ModifierKeys.Shift));
            }

            Apply(window);
            WpfStartupEnvironment.SilenceShortcutsWhileGateIsOpen(window, gates, Apply);
            Assert.Equal(2, window.InputBindings.Count);

            // Gate açıldı → koleksiyon boş.
            var pending = gates.ShowAsync(_ => new Border());
            Assert.True(gates.IsOpen);
            Assert.Empty(window.InputBindings);

            // Kapandı → eski sayıya döndü.
            gates.Top!.Close(false);
            Assert.False(gates.IsOpen);
            Assert.True(pending.IsCompleted);
            Assert.Equal(2, window.InputBindings.Count);
            Assert.Equal(2, applyCount);

            // Yığın: alttaki gate hâlâ açıkken üstteki kapanırsa kısayollar
            // AÇILMAMALI. AppGateStack her ekle/çıkarda IsOpen için
            // PropertyChanged yükseltiyor (değer değişmese bile), yani handler
            // yeniden koşuyor — doğru davranış değerin kendisinden geliyor.
            // applyCount sabit kalması bunu ölçüyor.
            var outer = gates.ShowAsync(_ => new Border());
            Assert.Empty(window.InputBindings);
            var inner = gates.ShowAsync(_ => new Border());
            Assert.Empty(window.InputBindings);

            gates.Top!.Close(false);
            Assert.True(gates.IsOpen);
            Assert.Empty(window.InputBindings);
            Assert.Equal(2, applyCount);

            gates.Top!.Close(false);
            Assert.False(gates.IsOpen);
            Assert.Equal(2, window.InputBindings.Count);
            Assert.Equal(3, applyCount);
            Assert.True(outer.IsCompleted);
            Assert.True(inner.IsCompleted);

            window.Close();
        });
        Assert.Null(error);
    }
}
