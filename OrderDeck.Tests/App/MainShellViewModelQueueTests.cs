using System.Linq;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sales;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Queue-management behaviour on <see cref="MainShellViewModel"/>:
/// AddChatToQueue + RemoveSelectedFromQueue + ClearQueue + the dynamic
/// label labels (PrintButtonLabel, DeleteButtonLabel) that change shape
/// based on what's selected.
///
/// These complement <c>MainShellPrintTests</c>, which covers the
/// printing path itself. Together they describe the full chat → queue
/// → print pipeline.
/// </summary>
public class MainShellViewModelQueueTests
{
    [Fact]
    public void AddChatToQueue_appends_a_label_with_provided_price()
    {
        var h = MainShellTestHarness.Build();

        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 250m);

        h.Vm.PrintQueue.Should().HaveCount(1);
        h.Vm.PrintQueue[0].Label.Username.Should().Be("@buyer");
        h.Vm.PrintQueue[0].Label.Price.Should().Be(250m);
        h.Vm.PrintQueue[0].Label.PrintedAt.Should().BeNull();
    }

    [Fact]
    public void AddChatToQueue_with_invalid_price_does_not_add()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "abc";

        h.Vm.AddChatToQueue(MainShellTestHarness.ChatVm("@buyer", "alıyorum"));

        h.Vm.PrintQueue.Should().BeEmpty(
            "an unparseable price should keep the chat message out of the queue");
    }

    [Fact]
    public void AddChatToQueue_attaches_active_code_when_set()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ActiveCode = "MAVI";
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);

        h.Vm.PrintQueue[0].Label.Code.Should().Be("MAVI");
    }

    [Fact]
    public void AddChatToQueue_with_blank_code_stores_null()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ActiveCode = "   ";
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);

        h.Vm.PrintQueue[0].Label.Code.Should().BeNull(
            "whitespace-only codes should never reach the database");
    }

    [Fact]
    public void PrintButtonLabel_reflects_selection_state()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.PrintButtonLabel.Should().Be("Yazdır");

        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 10m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 20m);
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[0]);

        h.Vm.PrintButtonLabel.Should().Be("Yazdır (1)");

        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[1]);

        h.Vm.PrintButtonLabel.Should().Be("Yazdır (2)");
    }

    [Fact]
    public void DeleteButtonLabel_reflects_selection_state()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.DeleteButtonLabel.Should().Be("Seçileni Sil");

        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 10m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 20m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@c", 30m);
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[0]);

        h.Vm.DeleteButtonLabel.Should().Be("Seçileni Sil");

        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[1]);
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[2]);

        h.Vm.DeleteButtonLabel.Should().Be("Seçilenleri Sil (3)");
    }

    [Fact]
    public void RemoveSelectedFromQueue_drops_only_selected_rows()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 10m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 20m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@c", 30m);

        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[1]); // @b

        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Vm.PrintQueue.Should().HaveCount(2);
        h.Vm.PrintQueue.Should().NotContain(l => l.Username == "@b");
        h.Vm.SelectedQueueItems.Should().BeEmpty(
            "selection clears after removal so the next click rebuilds it");
    }

    [Fact]
    public void RemoveSelectedFromQueue_with_empty_selection_is_a_noop()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 10m);

        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Vm.PrintQueue.Should().HaveCount(1);
    }

    [Fact]
    public void Tentative_backup_in_queue_survives_RemoveSelected_for_unrelated_rows()
    {
        // Regression guard: the backup tentative row shouldn't get caught
        // up in selection-based removals targeting other rows.
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@parent", 199m);
        var parent = h.Vm.PrintQueue[0];

        h.Vm.BeginAddBackupCommand.Execute(parent);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "+1"));

        // Select the parent and remove
        h.Vm.SelectedQueueItems.Add(parent);
        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        // The tentative backup row should still be there — only the
        // explicitly selected parent was removed.
        h.Vm.PrintQueue.Should().ContainSingle(l => l.Label.IsTentativeBackup);
    }
}
