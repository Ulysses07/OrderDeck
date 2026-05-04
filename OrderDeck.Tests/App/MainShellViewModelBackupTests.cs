using System.Linq;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Tests for the backup-buyer flow on <see cref="MainShellViewModel"/> —
/// chip on every queued label, "begin backup mode → pick chat user → label
/// joins the queue as tentative" path, and the cancellation routes that
/// keep the queue + counts consistent.
///
/// All tests run against an in-memory SQLite + a started session; uses
/// <see cref="MainShellTestHarness"/> to skip the WPF / license boilerplate.
/// </summary>
public class MainShellViewModelBackupTests
{
    [Fact]
    public void Begin_backup_mode_sets_target_label_and_banner()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);
        var label = h.Vm.PrintQueue[0];

        h.Vm.BeginAddBackupCommand.Execute(label);

        h.Vm.IsInBackupSelectionMode.Should().BeTrue();
        h.Vm.BackupTargetLabel.Should().Be(label);
        h.Vm.BackupModeBanner.Should().NotBeNull();
        h.Vm.BackupModeBanner!.Should().Contain("@buyer");
    }

    [Fact]
    public void Cancel_backup_selection_clears_state()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);
        var label = h.Vm.PrintQueue[0];

        h.Vm.BeginAddBackupCommand.Execute(label);
        h.Vm.CancelBackupSelectionCommand.Execute(null);

        h.Vm.IsInBackupSelectionMode.Should().BeFalse();
        h.Vm.BackupTargetLabel.Should().BeNull();
        h.Vm.BackupModeBanner.Should().BeNull();
    }

    [Fact]
    public void TryAssignChatAsBackup_outside_mode_returns_false()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);

        var consumed = h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "ben de"));

        consumed.Should().BeFalse(
            "no backup target set → caller falls through to the normal queue flow");
        h.Vm.PrintQueue.Should().HaveCount(1, "no new label was added");
    }

    [Fact]
    public void TryAssignChatAsBackup_in_mode_creates_tentative_label_and_clears_mode()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);
        var parentVm = h.Vm.PrintQueue[0];
        h.Vm.BeginAddBackupCommand.Execute(parentVm);

        var consumed = h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "ben de aldım"));

        consumed.Should().BeTrue();
        h.Vm.IsInBackupSelectionMode.Should().BeFalse("backup-mode auto-clears after assignment");
        h.Vm.PrintQueue.Should().HaveCount(2, "the tentative backup is now queued alongside the parent");

        var backupVm = h.Vm.PrintQueue.Single(l => l.Label.IsTentativeBackup);
        backupVm.Label.ParentLabelId.Should().Be(parentVm.Id);
        backupVm.Label.Price.Should().Be(parentVm.Price, "default mirrors the parent's price");
        backupVm.Label.IsBackupPromoted.Should().BeTrue("Y stamp prints on the spare sticker");
    }

    [Fact]
    public void Adding_backup_increments_parent_chip_count_in_place()
    {
        // The chip on the parent row should reflect backup count without a
        // full queue requery — we assert the in-place bump that
        // TryAssignChatAsBackup does on the LabelViewModel.
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);
        var parentVm = h.Vm.PrintQueue[0];
        h.Vm.BeginAddBackupCommand.Execute(parentVm);

        parentVm.BackupCount.Should().Be(0);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek1", "ben de"));

        parentVm.BackupCount.Should().Be(1);
    }

    [Fact]
    public void Begin_backup_mode_with_null_parent_is_a_noop()
    {
        // Defensive: the chip click can theoretically fire with no item if
        // a stale binding catches it — should silently ignore, not throw.
        var h = MainShellTestHarness.Build();

        h.Vm.BeginAddBackupCommand.Execute(null);

        h.Vm.IsInBackupSelectionMode.Should().BeFalse();
    }

    [Fact]
    public void Tentative_backup_in_queue_has_parent_pointer_and_zero_chip_count()
    {
        // The tentative-backup row gets its own LabelViewModel; its own
        // BackupCount stays at 0 (it's a leaf, not a parent).
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);
        h.Vm.BeginAddBackupCommand.Execute(h.Vm.PrintQueue[0]);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "+1"));

        var backup = h.Vm.PrintQueue.Single(l => l.Label.IsTentativeBackup);
        backup.BackupCount.Should().Be(0);
        backup.Label.ParentLabelId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Multiple_backups_for_same_parent_create_distinct_queue_rows()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);
        var parent = h.Vm.PrintQueue[0];

        h.Vm.BeginAddBackupCommand.Execute(parent);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek1", "+1"));

        h.Vm.BeginAddBackupCommand.Execute(parent);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek2", "+1"));

        h.Vm.BeginAddBackupCommand.Execute(parent);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek3", "+1"));

        h.Vm.PrintQueue.Should().HaveCount(4, "1 parent + 3 tentative backups");
        parent.BackupCount.Should().Be(3);
        h.Vm.PrintQueue.Where(l => l.Label.IsTentativeBackup).Should().HaveCount(3);
    }

    [Fact]
    public void Tentative_backup_persists_via_label_service()
    {
        // Round-trips through the persistence layer — a second call to
        // GetBackups (the Service equivalent of what the cancel flow does)
        // should see the tentative row.
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);
        var parent = h.Vm.PrintQueue[0];
        h.Vm.BeginAddBackupCommand.Execute(parent);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "+1"));

        var backups = h.Labels.GetBackups(parent.Id);

        backups.Should().HaveCount(1);
        backups[0].Username.Should().Be("@yedek");
        backups[0].IsTentativeBackup.Should().BeTrue();
    }
}
