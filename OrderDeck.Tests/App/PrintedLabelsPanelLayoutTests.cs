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
