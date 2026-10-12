using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using FluentAssertions;
using OrderDeck.App.Views.Shell;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Tests.App;

/// <summary>FİYAT kutusunun satır içi şeridi (sahip kararı F2) gerçek WPF bağlarında: soru görünür, "Evet"
/// kodun bütün etiketlerini yeni fiyata geçirir, sonuç kutunun altında satır içi (çekmece değil) ve "Kapat"la
/// kalkar.</summary>
public class ActiveProductBarLayoutTests
{
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static Button ButtonIn(DependencyObject root, string content)
        => Descendants(root).OfType<Button>().Single(b => Equals(b.Content, content));

    /// <summary>Gerçek tıklama yolu (otomasyon): OnClick → bağlı komut. ClickEvent'i elle yükseltmek komutu
    /// çalıştırmaz.</summary>
    private static void Click(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        // Otomasyon tıklaması Input önceliğinde kuyruğa girer (Pump Loaded'da durur): kuyruğu boşalt.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);
        ThemeTestHost.Pump();
    }

    [Fact]
    public void Price_change_strip_answers_inline_and_shows_the_result_under_the_box()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var h = MainShellTestHarness.Build();
            h.Vm.ActiveCode = "A1";
            MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 250m);
            MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 250m);
            var bar = new ActiveProductBar { DataContext = h.Vm };
            bar.Measure(new Size(360, 900));
            bar.Arrange(new Rect(0, 0, 360, 900));
            ThemeTestHost.Pump();
            var strip = (Border)bar.FindName("CodePriceStrip");
            strip.Visibility.Should().Be(Visibility.Collapsed);

            h.Vm.ActivePriceText = "200";
            ThemeTestHost.Pump();

            strip.Visibility.Should().Be(Visibility.Visible);
            Descendants(strip).OfType<TextBlock>().Select(t => t.Text)
                .Should().Contain("A1 kodunun 2 etiketi 250,00 TL. Hepsi 200,00 TL olsun mu?");

            Click(ButtonIn(strip, "Evet"));
            ThemeTestHost.Pump();

            strip.Visibility.Should().Be(Visibility.Collapsed);
            var labels = new LabelRepository(h.Db);
            h.Vm.PrintQueue.Select(q => labels.GetById(q.Id)!.Price).Should().OnlyContain(p => p == 200m);
            var result = (FrameworkElement)bar.FindName("CodePriceResult");
            ((FrameworkElement)result.Parent).Visibility.Should().Be(Visibility.Visible, "sonuç kutunun altında");
            Descendants(result).OfType<TextBlock>().Select(t => t.Text)
                .Should().Contain(t => t.StartsWith("A1: 2 etiket 200,00 TL'ye düzeltildi"));

            Click(ButtonIn(result, "Kapat"));
            ThemeTestHost.Pump();

            ((FrameworkElement)result.Parent).Visibility.Should().Be(Visibility.Collapsed);
        });

        error.Should().BeNull();
    }
}
