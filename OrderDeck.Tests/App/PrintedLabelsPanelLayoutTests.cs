using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Shell;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;

namespace OrderDeck.Tests.App;

/// <summary>"Basılanlar" listesinin gerçek WPF bağları: gruplama görünümde (CollectionViewSource) basışa
/// göre yapılır ve VM'deki seçim listeye yansır ("bu baskıyı seç" programla seçer).</summary>
public class PrintedLabelsPanelLayoutTests
{
    /// <summary>Panelin bağlandığı kabuk yüzeyi — testte yalnız liste.</summary>
    public sealed class Holder(PrintedLabelsViewModel printed)
    {
        public PrintedLabelsViewModel PrintedLabels { get; } = printed;
    }

    [Fact]
    public void Rows_are_grouped_by_print_press_and_vm_selection_shows_in_the_list()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var db = new InMemorySqlite();
            new MigrationRunner(db).Run();
            var clock = new Mock<IClock>();
            clock.Setup(c => c.UnixNow()).Returns(1000L);
            new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
            var labels = new LabelRepository(db);
            var svc = new LabelService(labels,
                new CustomerService(new CustomerRepository(db), new SessionRepository(db), labels, clock.Object),
                db, clock.Object);
            OrderDeck.Core.Sales.Label Add(string user) => svc.Add("s1", new ChatMessage(System.Guid.NewGuid().ToString("N"), "instagram",
                null, user, user, null, "aldım", 1000, System.Array.Empty<string>()), 250m, "A12");
            var a = Add("@a");
            var b = Add("@b");
            var c = Add("@c");
            svc.MarkPrintedAndRecord(new[] { a.Id });
            clock.Setup(x => x.UnixNow()).Returns(2000L);
            svc.MarkPrintedAndRecord(new[] { b.Id, c.Id });

            var vm = new PrintedLabelsViewModel(labels);
            vm.Load("s1");
            var panel = new PrintedLabelsPanel { DataContext = new Holder(vm) };
            panel.Measure(new Size(400, 600));
            panel.Arrange(new Rect(0, 0, 400, 600));
            panel.UpdateLayout();
            ThemeTestHost.Pump();

            var list = (ListBox)panel.FindName("PrintedList");
            list.Items.Count.Should().Be(3);
            list.Items.Groups.Should().HaveCount(2, "iki Yazdır basışı = iki grup");

            vm.SelectBatchCommand.Execute(vm.VisibleRows[0].Batch);
            ThemeTestHost.Pump();
            list.SelectedItems.Cast<PrintedLabelRowViewModel>().Select(r => r.Id)
                .Should().BeEquivalentTo(new[] { b.Id, c.Id });
        });

        error.Should().BeNull();
    }

    [Fact]
    public void Batch_header_button_selects_that_press()
    {
        // Grup başlığındaki "Bu baskıyı seç": bağ (ListBox'ın DataContext'i üzerinden) ve parametre (grubun
        // adı = basış) gerçek görünümde.
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var db = new InMemorySqlite();
            new MigrationRunner(db).Run();
            var clock = new Mock<IClock>();
            clock.Setup(c => c.UnixNow()).Returns(1000L);
            new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
            var labels = new LabelRepository(db);
            var svc = new LabelService(labels,
                new CustomerService(new CustomerRepository(db), new SessionRepository(db), labels, clock.Object),
                db, clock.Object);
            OrderDeck.Core.Sales.Label Add(string user) => svc.Add("s1", new ChatMessage(System.Guid.NewGuid().ToString("N"), "instagram",
                null, user, user, null, "aldım", 1000, System.Array.Empty<string>()), 250m, "A12");
            var a = Add("@a");
            var b = Add("@b");
            var c = Add("@c");
            svc.MarkPrintedAndRecord(new[] { a.Id });
            clock.Setup(x => x.UnixNow()).Returns(2000L);
            svc.MarkPrintedAndRecord(new[] { b.Id, c.Id });

            var vm = new PrintedLabelsViewModel(labels);
            vm.Load("s1");
            var panel = new PrintedLabelsPanel { DataContext = new Holder(vm) };
            var window = new Window
            {
                Content = panel, Width = 400, Height = 600, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            };
            window.Show();
            try
            {
                ThemeTestHost.Pump();
                var buttons = Descendants(panel).OfType<Button>()
                    .Where(x => x.Content as string == "Bu baskıyı seç").ToList();
                buttons.Should().HaveCount(2, "her basışın başlığında bir düğme");
                buttons[0].Command.Should().NotBeNull();

                buttons[0].Command.Execute(buttons[0].CommandParameter);

                vm.SelectedIds.Should().BeEquivalentTo(new[] { b.Id, c.Id }, "en üstteki (en yeni) basış");
            }
            finally
            {
                window.Close();
            }
        });

        error.Should().BeNull();
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    [Fact]
    public void Row_menu_acts_on_the_right_clicked_row_not_the_first_selected()
    {
        // Çoklu seçimde seçili bir satıra sağ tık seçimi değiştirmez; ListBox'ın menüsü SelectedItem'ı (ilk
        // seçilen satır) alıyordu — "Bu kodu seç" başka satırın koduna uygulanırdı.
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var db = new InMemorySqlite();
            new MigrationRunner(db).Run();
            var clock = new Mock<IClock>();
            clock.Setup(c => c.UnixNow()).Returns(1000L);
            new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
            var labels = new LabelRepository(db);
            var svc = new LabelService(labels,
                new CustomerService(new CustomerRepository(db), new SessionRepository(db), labels, clock.Object),
                db, clock.Object);
            OrderDeck.Core.Sales.Label Add(string user, string code) => svc.Add("s1", new ChatMessage(
                System.Guid.NewGuid().ToString("N"), "instagram", null, user, user, null, "aldım", 1000,
                System.Array.Empty<string>()), 250m, code);
            var a = Add("@a", "A12");
            var b = Add("@b", "B7");
            svc.MarkPrintedAndRecord(new[] { a.Id, b.Id });

            var vm = new PrintedLabelsViewModel(labels);
            vm.Load("s1");
            var panel = new PrintedLabelsPanel { DataContext = new Holder(vm) };
            var window = new Window
            {
                Content = panel, Width = 400, Height = 600, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            };
            window.Show();
            try
            {
                ThemeTestHost.Pump();
                var list = (ListBox)panel.FindName("PrintedList");
                var rowA = vm.VisibleRows.Single(r => r.Id == a.Id);
                var rowB = vm.VisibleRows.Single(r => r.Id == b.Id);
                rowA.IsSelected = true;
                rowB.IsSelected = true;
                ThemeTestHost.Pump();
                list.SelectedItem.Should().BeSameAs(rowA, "ilk seçilen satır");

                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rowB);
                var menu = container.ContextMenu;
                menu.Should().NotBeNull("menü satırın kendisinde");
                menu!.PlacementTarget = container;
                menu.IsOpen = true;
                ThemeTestHost.Pump();
                var selectCode = menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == "Bu kodu seç");
                selectCode.CommandParameter.Should().BeSameAs(rowB);
                selectCode.Command.Should().NotBeNull();
                selectCode.Command.Execute(selectCode.CommandParameter);
                menu.IsOpen = false;

                vm.SearchText.Should().Be("B7", "sağ tıklanan satırın kodu");
            }
            finally
            {
                window.Close();
            }
        });

        error.Should().BeNull();
    }

    [Fact]
    public void Tab_buttons_switch_lists_and_a_second_panel_does_not_reset_the_tab()
    {
        // Sekme radyolarında süreç geneli GroupName olsaydı ikinci bir panel (testte ya da ileride ikinci
        // görünüm) kurulurken öbürünün seçili radyosu kapanır, iki yönlü bağ sekmeyi Kuyruk'a geri alırdı.
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var h = MainShellTestHarness.Build();
            var first = new PrintQueuePanel { DataContext = h.Vm };
            first.Measure(new Size(400, 400));
            ((ListBox)first.FindName("QueueList")).Visibility.Should().Be(Visibility.Visible);

            h.Vm.IsPrintedTabActive = true;
            var second = new PrintQueuePanel { DataContext = h.Vm };
            second.Measure(new Size(400, 400));
            ThemeTestHost.Pump();

            h.Vm.IsPrintedTabActive.Should().BeTrue();
            ((ListBox)second.FindName("QueueList")).Visibility.Should().Be(Visibility.Collapsed);
            ((ListBox)first.FindName("QueueList")).Visibility.Should().Be(Visibility.Collapsed);
        });

        error.Should().BeNull();
    }
}
