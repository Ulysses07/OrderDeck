using System.Linq;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
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
        using var h = MainShellTestHarness.Build();

        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 250m);

        h.Vm.PrintQueue.Should().HaveCount(1);
        h.Vm.PrintQueue[0].Label.Username.Should().Be("@buyer");
        h.Vm.PrintQueue[0].Label.Price.Should().Be(250m);
        h.Vm.PrintQueue[0].Label.PrintedAt.Should().BeNull();
    }

    [Fact]
    public async Task AddChatToQueue_with_invalid_price_does_not_add()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "abc";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@buyer", "alıyorum"));

        h.Vm.PrintQueue.Should().BeEmpty(
            "an unparseable price should keep the chat message out of the queue");

        // Uyarı IDialogService'ten geçmeli. Doğrudan MessageBox.Show olsaydı
        // burada gerçek bir modal pencere açılır, koşu kilitlenir ve kutunun
        // kendi mesaj döngüsü alakasız dispatcher işlerini pompalayarak
        // rastgele hatalar üretirdi — bu test tam olarak öyle çakıyordu.
        h.Dialogs.Shown.Should().ContainSingle()
            .Which.Title.Should().Be("Geçersiz fiyat");
    }

    [Fact]
    public void AddChatToQueue_attaches_active_code_when_set()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActiveCode = "MAVI";
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);

        h.Vm.PrintQueue[0].Label.Code.Should().Be("MAVI");
    }

    [Fact]
    public void AddChatToQueue_with_blank_code_stores_null()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActiveCode = "   ";
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 199m);

        h.Vm.PrintQueue[0].Label.Code.Should().BeNull(
            "whitespace-only codes should never reach the database");
    }

    [Fact]
    public void PrintButtonLabel_reflects_selection_state()
    {
        using var h = MainShellTestHarness.Build();

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
        using var h = MainShellTestHarness.Build();

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
        using var h = MainShellTestHarness.Build();
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
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 10m);

        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Vm.PrintQueue.Should().HaveCount(1);
    }

    [Fact]
    public void Tentative_backup_in_queue_survives_RemoveSelected_for_unrelated_rows()
    {
        // Regression guard: the backup tentative row shouldn't get caught
        // up in selection-based removals targeting other rows.
        using var h = MainShellTestHarness.Build();
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

    // ── F06 (denetim 2026-09-09): kuyruktan silme = soft-cancel ──────────────
    //
    // Eski davranış fiziksel DELETE idi. Satır sunucuya bir kez push
    // edildiyse DELETE mezar taşını da yok eder: sunucu kopyası ömür boyu
    // aktif satış kalır, stok geri gelmez. Aşağıdaki testler yeni sözleşmeyi
    // sabitliyor: satır YERİNDE kalır, CancelledAt + queue-removed sebebi
    // yazılır, SyncedAt düşer ki iptal sunucuya gitsin.

    [Fact]
    public void RemoveSelectedFromQueue_soft_cancels_row_instead_of_deleting()
    {
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 250m);
        var label = h.Vm.PrintQueue[0].Label;

        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[0]);
        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        var repo = new LabelRepository(h.Db);
        var row = repo.GetById(label.Id);
        row.Should().NotBeNull("satır silinmemeli, iptal edilmeli — mezar taşı sunucuya gidecek");
        row!.CancelledAt.Should().Be(1000L);
        row.CancelReason.Should().Be(CancelReasonCodes.QueueRemoved);
        row.SyncedAt.Should().BeNull("iptal outbox'a düşmeli ki sunucu stok iade etsin");

        // Kuyruk sorgusu iptalli satırı zaten dışlıyor — restart sonrası da
        // kuyruğa geri gelmez.
        h.Labels.GetQueue(label.SessionId).Should().BeEmpty();
    }

    [Fact]
    public void RemoveSelectedFromQueue_resets_sync_stamp_on_already_synced_row()
    {
        // Asıl hata senaryosu: satır sunucuya PUSH EDİLMİŞKEN kuyruktan
        // çıkarılıyor. DELETE olsaydı sunucu iptali hiç öğrenmezdi.
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@buyer", 250m);
        var label = h.Vm.PrintQueue[0].Label;

        var repo = new LabelRepository(h.Db);
        // F05 (#380) sonrası MarkSynced compare-and-set: taze satırın
        // Revision'ı 0, ack o yüzden revision:0 ile geçer.
        repo.MarkSynced(label.Id, 2000, revision: 0);

        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[0]);
        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        var row = repo.GetById(label.Id)!;
        row.CancelledAt.Should().NotBeNull();
        row.SyncedAt.Should().BeNull("sonraki sync tick'i iptali sunucuya taşımalı");
        repo.GetUnsynced().Should().ContainSingle(l => l.Id == label.Id);
    }

    [Fact]
    public void ClearQueue_soft_cancels_every_row()
    {
        using var h = MainShellTestHarness.Build();
        h.Dialogs.ConfirmResult = _ => true;
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 10m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 20m);
        var ids = h.Vm.PrintQueue.Select(l => l.Id).ToList();

        h.Vm.ClearQueueCommand.Execute(null);

        h.Vm.PrintQueue.Should().BeEmpty();
        var repo = new LabelRepository(h.Db);
        foreach (var id in ids)
        {
            var row = repo.GetById(id);
            row.Should().NotBeNull();
            row!.CancelledAt.Should().NotBeNull();
            row.CancelReason.Should().Be(CancelReasonCodes.QueueRemoved);
        }
    }
}
