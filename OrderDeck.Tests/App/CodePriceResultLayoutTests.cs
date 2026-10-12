using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using OrderDeck.App.Views;
using OrderDeck.App.Views.Shell;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Tests.App;

/// <summary>
/// FİYAT değişikliğinin satır içi sonucu (F2) kabuğun GERÇEK ağacında: sonuç kuyruğu yemez, "Kapat" her boyutta
/// tıklanabilir. Sonuç eskiden kod/fiyat kutusunun altında, Auto satırda ve sınırsızdı: 1440×900'de dört kişilik
/// "eski tutarla istek gitmiş" listesi ürün çubuğunu 280'den 849 piksele çıkarıyor, kuyruğu 0'a indiriyor,
/// "Kapat"ı yazdır yuvasının altına itiyordu (hit-test boş) — kuyruk yayının geri kalanında kullanılamıyordu.
/// ActiveProductBarLayoutTests çubuğu tek başına ölçtüğü için bunu göremez.
/// </summary>
public class CodePriceResultLayoutTests
{
    private static T Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
        => TryFind(root, match) ?? throw new InvalidOperationException($"{typeof(T).Name} bulunamadı");

    private static T? TryFind<T>(DependencyObject root, Func<T, bool>? match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && (match is null || match(hit))) return hit;
            if (TryFind(child, match) is { } deep) return deep;
        }
        return null;
    }

    private static Rect Bounds(FrameworkElement element, Visual root)
        => new(element.TransformToAncestor(root).Transform(new Point(0, 0)),
            new Size(element.ActualWidth, element.ActualHeight));

    /// <summary>Fare yolunun hit-test'i (InputHitTest'in süzgeci: görünmeyen ya da hit-test dışı öğe atlanır;
    /// kırpılmış öğe kırpma geometrisiyle elenir) <paramref name="element"/>'in ortasında onu buluyor mu.</summary>
    private static bool IsClickable(Visual root, FrameworkElement element)
    {
        var r = Bounds(element, root);
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(root,
            d => d is UIElement ui && (ui.Visibility != Visibility.Visible || !ui.IsHitTestVisible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            res => { hit = res.VisualHit; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(new Point(r.X + r.Width / 2, r.Y + r.Height / 2)));
        for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, element)) return true;
        return false;
    }

    private static void Click(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);
        ThemeTestHost.Pump();
    }

    /// <summary>Kabuk: A1'in <paramref name="people"/> etiketi 250 TL'den basılmış, her birine 250 TL'lik yayın
    /// isteği teslim edilmiş; kuyrukta üç basılmamış etiket. Sayfa ve çekmece katmanları testte App.Host'suz —
    /// boş yığınlarla kapalı (gerçekte de kapalılar).</summary>
    private static (MainShellTestHarness.Harness H, MainShellView Shell) Build(int people)
    {
        var h = MainShellTestHarness.Build(paymentFollowUp: (db, customers, labels) =>
            new OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests(customers, labels,
                new PaymentJobRepository(db), () => new OrderDeck.Core.Settings.AppSettings(),
                (_, _, _, _, _) => Task.FromResult(new OrderDeck.App.Services.PaymentRequestOutcome(
                    OrderDeck.App.Services.PaymentRequestResult.Sent))));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "250";
        for (var i = 0; i < people; i++)
            h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm($"@alici{i}", "A1 aldım")).GetAwaiter().GetResult();
        var printed = h.Vm.PrintQueue.ToList();
        h.Vm.PrintCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var jobs = new PaymentJobRepository(h.Db);
        var scope = OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests.SessionScope(
            h.Sessions.GetActive()!.Id);
        foreach (var row in printed)
        {
            var job = jobs.FindOrCreate(row.Label.CustomerId, scope, 250m);
            jobs.MarkNoBalance(job.Id, null, job.Revision).Should().BeTrue();
            jobs.MarkDelivered(job.Id, 250m);
        }
        for (var i = 0; i < 3; i++)
            h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm($"@sira{i}", "A1 aldım")).GetAwaiter().GetResult();

        var shell = new MainShellView { DataContext = h.Vm };
        shell.Measure(new Size(1000, 800));
        shell.Arrange(new Rect(0, 0, 1000, 800));
        shell.UpdateLayout();
        Find<PageHost>(shell).DataContext = new OrderDeck.App.Services.Pages.PageStack();
        Find<DrawerHost>(shell).DataContext = new OrderDeck.App.Services.Drawers.DrawerStack();
        return (h, shell);
    }

    /// <summary>Bağ güncellemeleri kuyruğa giriyor, sonucun yeri (SizeChanged) bir sonraki geçişte oturuyor:
    /// ölç-yerleştir-pompala iki tur.</summary>
    private static void Layout(MainShellView shell, double width, double height)
    {
        for (var i = 0; i < 3; i++)
        {
            shell.Measure(new Size(width, height));
            shell.Arrange(new Rect(0, 0, width, height));
            shell.UpdateLayout();
            ThemeTestHost.Pump();
        }
    }

    [Theory]
    [InlineData(1440, 900)]    // açılış boyu
    [InlineData(1920, 1040)]   // tam ekran
    public void Result_leaves_the_queue_a_row_and_keeps_close_reachable(double width, double height)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var (h, shell) = Build(people: 4);
            using var _ = h;
            Layout(shell, width, height);
            var queue = Find<PrintQueuePanel>(shell);
            var list = Find<ListBox>(queue, l => l.Name == "QueueList");
            var queueBefore = queue.ActualHeight;

            h.Vm.ActivePriceText = "200";
            h.Vm.ConfirmCodePriceCommand.Execute(null);
            Layout(shell, width, height);

            var panel = Find<CodePriceResultPanel>(shell);
            var close = Find<Button>(panel, b => b.Name == "CloseButton");
            var body = Find<ScrollViewer>(panel, s => s.Name == "Body");
            h.Vm.CodePriceCorrection!.ResendRows.Should().HaveCount(4, "liste uzun — sonucun hepsi sığmaz");
            panel.ActualHeight.Should().BeGreaterThan(0, "sonuç görünüyor");
            IsClickable(shell, close).Should().BeTrue("\"Kapat\" kaydırılan alanın dışında, başlıkta");
            var firstRow = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
            Bounds(firstRow, shell).Bottom.Should().BeLessThanOrEqualTo(Bounds(queue, shell).Bottom,
                "kuyruğun en az bir satırı görünür kalır");
            panel.ActualHeight.Should().BeLessThanOrEqualTo((panel.ActualHeight + queue.ActualHeight) / 2 + 0.5,
                "sonuç kuyrukla paylaştığı yerin en çok yarısını alır");
            body.ExtentHeight.Should().BeGreaterThan(body.ViewportHeight, "sığmayan içerik kaydırılır, kesilmez");
            IsClickable(shell, Find<RadioButton>(queue)).Should().BeTrue("Kuyruk sekmesi tıklanabilir");

            Click(close);
            Layout(shell, width, height);

            panel.ActualHeight.Should().Be(0);
            queue.ActualHeight.Should().BeApproximately(queueBefore, 0.5, "Kapat'la kuyruk eski boyuna döner");
        });

        error.Should().BeNull();
    }

    [Fact]
    public void At_the_minimum_size_only_the_results_header_takes_room()
    {
        // 960×720'de kuyruğun ancak bir satırlık yeri var (sekme başlığı + QueueMinHeight). Sonucun gövdesi hiç
        // yer almaz; yalnız "Kapat"lı başlık satırı görünür (pencere büyütülünce gövde açılır).
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var (h, shell) = Build(people: 4);
            using var _ = h;
            Layout(shell, 960, 720);
            var queue = Find<PrintQueuePanel>(shell);

            h.Vm.ActivePriceText = "200";
            h.Vm.ConfirmCodePriceCommand.Execute(null);
            Layout(shell, 960, 720);

            var panel = Find<CodePriceResultPanel>(shell);
            var close = Find<Button>(panel, b => b.Name == "CloseButton");
            var header = Find<DockPanel>(panel, d => d.Name == "Header");
            IsClickable(shell, close).Should().BeTrue();
            Find<ScrollViewer>(panel, s => s.Name == "Body").ActualHeight.Should().Be(0);
            panel.ActualHeight.Should().BeLessThan(header.ActualHeight + 16, "yalnız başlık satırı");
            IsClickable(shell, Find<RadioButton>(queue)).Should().BeTrue("Kuyruk sekmesi tıklanabilir");

            Layout(shell, 1440, 900);
            Find<ScrollViewer>(panel, s => s.Name == "Body").ActualHeight.Should().BeGreaterThan(0,
                "pencere büyüyünce gövde açılır");
        });

        error.Should().BeNull();
    }
}
