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
/// kodun bütün etiketlerini yeni fiyata geçirir, sonuç satır içi (çekmece değil — kuyruğun üstünde,
/// <see cref="CodePriceResultPanel"/>) ve "Kapat"la kalkar.</summary>
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
            var panel = new CodePriceResultPanel { DataContext = h.Vm };
            panel.Measure(new Size(360, 900));
            panel.Arrange(new Rect(0, 0, 360, 900));
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
            var frame = (FrameworkElement)panel.FindName("Frame");
            frame.Visibility.Should().Be(Visibility.Visible, "sonuç satır içi");
            Descendants(frame).OfType<TextBlock>().Select(t => t.Text)
                .Should().Contain(t => t.StartsWith("A1: 2 etiket 200,00 TL'ye düzeltildi"));
            Descendants((DependencyObject)panel.FindName("Result")).OfType<Button>()
                .Where(b => Equals(b.Content, "Kapat")).Should().ContainSingle().Which.Visibility.Should()
                .Be(Visibility.Collapsed,
                    "görünümün alttaki \"Kapat\"ı gizli — sınırlı yükseklikte kaydırılan alanın dibinde kalırdı");

            Click((Button)panel.FindName("CloseButton"));
            ThemeTestHost.Pump();

            frame.Visibility.Should().Be(Visibility.Collapsed);
        });

        error.Should().BeNull();
    }

    /// <summary>Bağın görünür hâli: <paramref name="text"/> metinli TextBlock, kendisi ve ebeveynleri görünür
    /// (pencere yok — IsVisible kullanılamaz, Visibility zinciri denetlenir).</summary>
    private static bool Shows(FrameworkElement root, string text)
    {
        var block = Descendants(root).OfType<TextBlock>().SingleOrDefault(t => t.Text == text);
        for (DependencyObject? d = block; d is not null && !ReferenceEquals(d, root); d = LogicalTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
        return block is not null;
    }

    [Fact]
    public void Carried_over_hint_says_why_the_box_is_suspect()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var h = MainShellTestHarness.Build();
            h.Vm.ActiveCode = "A1";
            MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 250m);
            var bar = new ActiveProductBar { DataContext = h.Vm };
            bar.Measure(new Size(360, 900));
            bar.Arrange(new Rect(0, 0, 360, 900));
            ThemeTestHost.Pump();

            h.Vm.ActiveCode = "B7";
            ThemeTestHost.Pump();
            Shows(bar, "önceki üründen kaldı — bu ürünün fiyatı mı?").Should().BeTrue();

            h.Vm.ActiveCode = "A1";
            h.Vm.ClearQueueCommand.CanExecute(null).Should().BeTrue();
            h.Dialogs.ConfirmResult = _ => true;
            h.Vm.ClearQueueCommand.Execute(null);
            ThemeTestHost.Pump();
            Shows(bar, "bu yayında artık etiketi yok — bu fiyat doğru mu?").Should().BeTrue();

            h.Vm.ActiveCode = "B7";
            ThemeTestHost.Pump();
            Shows(bar, "önceki üründen kaldı — bu ürünün fiyatı mı?").Should().BeTrue("metin nedene göre değişir");
        });

        error.Should().BeNull();
    }

    [Fact]
    public void Source_blocked_names_and_strip_error_are_shown_by_the_bar()
    {
        // Kutunun altındaki kaynak ("bu yayındaki fiyatı"), şerit beklerken yazılmayan yorumlar ve şeridin hata
        // satırı VM'de doğru olsa da bağ koparsa operatör görmez: etiket sessizce yazılmamış olur.
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var h = MainShellTestHarness.Build();
            h.Vm.ActiveCode = "A1";
            MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 250m);
            var bar = new ActiveProductBar { DataContext = h.Vm };
            bar.Measure(new Size(360, 900));
            bar.Arrange(new Rect(0, 0, 360, 900));
            ThemeTestHost.Pump();
            Shows(bar, "bu yayındaki fiyatı").Should().BeTrue("kutunun kaynağı");

            h.Vm.ActivePriceText = "200";
            h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@gec", "A1 aldım")).GetAwaiter().GetResult();
            ThemeTestHost.Pump();
            Shows(bar, "Önce fiyat değişikliğini onayla — yazılmadı: @gec").Should().BeTrue();

            // Şerit açıkken kodun etiketi başka yoldan iptal edildi: "Evet"in uygulanacağı etiket yok.
            h.Labels.Cancel(new[] { h.Vm.PrintQueue[0].Id }, OrderDeck.Core.Sales.CancelReasonCodes.Customer);
            Click(ButtonIn((Border)bar.FindName("CodePriceStrip"), "Evet"));
            ThemeTestHost.Pump();
            h.Vm.CodePriceError.Should().NotBeNull();
            Shows(bar, h.Vm.CodePriceError!).Should().BeTrue("şeridin hata satırı");
        });

        error.Should().BeNull();
    }
}
