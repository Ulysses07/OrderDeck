using FluentAssertions;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// Backup buyer flow (rev 2 — see migration 011): backups are first-class
/// Labels with ParentLabelId + IsTentativeBackup. Tentative rows are
/// physically printed (Y stamp) but excluded from revenue + customer
/// aggregates until <see cref="LabelService.ConfirmBackup"/> is called.
/// </summary>
public class LabelServiceBackupTests
{
    private static (LabelService Svc, LabelRepository Labels, CustomerRepository Customers,
                    Mock<IClock> Clock, InMemorySqlite Db, string SessionId, Label Parent) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var labelRepo    = new LabelRepository(db);
        var customerSvc  = new CustomerService(customerRepo, new SessionRepository(db), labelRepo, clock.Object);
        var svc          = new LabelService(labelRepo, customerSvc, clock.Object);

        var parentMsg = new ChatMessage(System.Guid.NewGuid().ToString("N"),
            "instagram", null, "@buyer", "Buyer", null, "MAVI XL aldım", 1000,
            System.Array.Empty<string>());
        var parent = svc.Add("s1", parentMsg, price: 199m, code: "MAVI");

        return (svc, labelRepo, customerRepo, clock, db, "s1", parent);
    }

    [Fact]
    public void AddBackup_creates_tentative_label_with_parents_price_and_Y_stamp()
    {
        var (svc, _, _, _, db, _, parent) = Fx();
        using var _disposer = db;

        var backup = svc.AddBackup(parent.Id, "tiktok", "@yedek1", "Yedek 1", "ben de aldım");

        backup.IsTentativeBackup.Should().BeTrue("the spare sticker isn't a real sale yet");
        backup.IsBackupPromoted.Should().BeTrue("Y stamp prints on the physical sticker");
        backup.ParentLabelId.Should().Be(parent.Id);
        backup.Price.Should().Be(parent.Price, "default mirrors the original sale's price");
        backup.Code.Should().Be(parent.Code);
        backup.SessionId.Should().Be(parent.SessionId);
    }

    [Fact]
    public void AddBackup_throws_for_unknown_parent()
    {
        var (svc, _, _, _, db, _, _) = Fx();
        using var _disposer = db;

        var act = () => svc.AddBackup("missing-parent", "instagram", "@x", "X", null);
        act.Should().Throw<System.InvalidOperationException>();
    }

    [Fact]
    public void Tentative_backup_appears_in_print_queue()
    {
        var (svc, _, _, _, db, sid, parent) = Fx();
        using var _disposer = db;

        svc.AddBackup(parent.Id, "instagram", "@y", "Y", null);

        // Operator should see two queue rows: the original sale + the spare sticker.
        var queue = svc.GetQueue(sid);
        queue.Should().HaveCount(2);
        queue.Should().ContainSingle(l => l.IsTentativeBackup);
    }

    [Fact]
    public void Printing_a_tentative_backup_does_not_credit_customer_aggregates()
    {
        // The single most important invariant of this feature: physically
        // printing the spare sticker must not inflate revenue or the backup
        // buyer's lifetime totals — the sale isn't real yet.
        var (svc, labels, customers, _, db, sid, parent) = Fx();
        using var _disposer = db;

        var backup = svc.AddBackup(parent.Id, "instagram", "@y", "Y", null);
        svc.MarkPrintedAndRecord(new[] { parent.Id, backup.Id });

        var totals = labels.GetSessionTotals(sid);
        totals.PrintedCount.Should().Be(1, "only the parent sale counts");
        totals.TotalAmount.Should().Be(parent.Price);

        var backupCustomer = customers.FindByPlatformAndUsername("instagram", "@y");
        backupCustomer!.TotalLabelsPrinted.Should().Be(0,
            "backup buyer has no real sale yet, so lifetime aggregate stays at zero");
        backupCustomer.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public void ConfirmBackup_credits_aggregates_when_already_printed()
    {
        // Common path: spare sticker printed live, original cancelled next-day,
        // operator confirms the backup. The retroactive credit must happen.
        var (svc, labels, customers, _, db, sid, parent) = Fx();
        using var _disposer = db;

        var backup = svc.AddBackup(parent.Id, "instagram", "@y", "Y", null);
        svc.MarkPrintedAndRecord(new[] { parent.Id, backup.Id });

        // Sanity: pre-confirm, backup is not in revenue.
        labels.GetSessionTotals(sid).PrintedCount.Should().Be(1);

        // Original cancels.
        svc.Cancel(new[] { parent.Id }, CancelReasonCodes.Customer);

        // Confirm the backup → it becomes a real sale.
        var confirmed = svc.ConfirmBackup(backup.Id);
        confirmed.IsTentativeBackup.Should().BeFalse();

        // Now revenue reflects the confirmed backup, not the cancelled original.
        var totals = labels.GetSessionTotals(sid);
        totals.PrintedCount.Should().Be(1);
        totals.TotalAmount.Should().Be(parent.Price);

        var backupCustomer = customers.FindByPlatformAndUsername("instagram", "@y");
        backupCustomer!.TotalLabelsPrinted.Should().Be(1);
        backupCustomer.TotalAmount.Should().Be(parent.Price);
    }

    [Fact]
    public void ConfirmBackup_with_new_price_overrides_recorded_amount()
    {
        var (svc, labels, customers, _, db, _, parent) = Fx();
        using var _disposer = db;

        var backup = svc.AddBackup(parent.Id, "instagram", "@y", "Y", null);
        svc.MarkPrintedAndRecord(new[] { backup.Id });

        // Operator agrees a discounted ₺150 with the spare buyer at promotion.
        svc.ConfirmBackup(backup.Id, newPrice: 150m);

        labels.GetById(backup.Id)!.Price.Should().Be(150m);

        var c = customers.FindByPlatformAndUsername("instagram", "@y");
        c!.TotalAmount.Should().Be(150m);
    }

    [Fact]
    public void Confirming_unprinted_backup_credits_aggregates_only_when_printed_later()
    {
        // Edge case: operator forgot to print during the live, then confirms
        // next-day. ConfirmBackup must NOT credit aggregates yet (the sticker
        // hasn't even existed); MarkPrintedAndRecord on a confirmed (non-
        // tentative) label is the path that records the sale.
        var (svc, labels, customers, _, db, _, parent) = Fx();
        using var _disposer = db;

        var backup = svc.AddBackup(parent.Id, "instagram", "@y", "Y", null);

        svc.ConfirmBackup(backup.Id);
        customers.FindByPlatformAndUsername("instagram", "@y")!
            .TotalLabelsPrinted.Should().Be(0);

        svc.MarkPrintedAndRecord(new[] { backup.Id });
        var c = customers.FindByPlatformAndUsername("instagram", "@y");
        c!.TotalLabelsPrinted.Should().Be(1);
        c.TotalAmount.Should().Be(parent.Price);
    }

    [Fact]
    public void GetBackupCounts_only_counts_tentative_active_backups()
    {
        var (svc, _, _, _, db, _, parent) = Fx();
        using var _disposer = db;

        svc.AddBackup(parent.Id, "instagram", "@y1", "Y1", null);
        svc.AddBackup(parent.Id, "tiktok",    "@y2", "Y2", null);
        var third = svc.AddBackup(parent.Id, "youtube", "@y3", "Y3", null);

        // Confirming one removes it from the tentative count.
        svc.ConfirmBackup(third.Id);

        var counts = svc.GetBackupCounts(new[] { parent.Id });
        counts[parent.Id].Should().Be(2);
    }

    [Fact]
    public void RemoveBackup_only_deletes_tentative_rows()
    {
        var (svc, labels, _, _, db, _, parent) = Fx();
        using var _disposer = db;

        var b = svc.AddBackup(parent.Id, "instagram", "@y", "Y", null);
        svc.RemoveBackup(b.Id);
        labels.GetById(b.Id).Should().BeNull();

        // After confirm, RemoveBackup is a no-op — confirmed labels are real
        // sales and must go through the standard Cancel() flow instead.
        var b2 = svc.AddBackup(parent.Id, "instagram", "@z", "Z", null);
        svc.ConfirmBackup(b2.Id);
        svc.RemoveBackup(b2.Id);
        labels.GetById(b2.Id).Should().NotBeNull("confirmed backups can't be silently removed");
    }

    [Fact]
    public void Cancelling_tentative_backup_does_not_alter_aggregates()
    {
        // Regression guard: cancelling a tentative-backup row should not
        // produce a phantom negative-delta against the customer (the aggregate
        // never got credited in the first place).
        var (svc, _, customers, _, db, _, parent) = Fx();
        using var _disposer = db;

        var b = svc.AddBackup(parent.Id, "instagram", "@y", "Y", null);
        svc.MarkPrintedAndRecord(new[] { b.Id });

        var beforeCancel = customers.FindByPlatformAndUsername("instagram", "@y");
        beforeCancel!.TotalLabelsPrinted.Should().Be(0);
        beforeCancel.TotalAmount.Should().Be(0m);

        svc.Cancel(new[] { b.Id }, CancelReasonCodes.Customer);

        var afterCancel = customers.FindByPlatformAndUsername("instagram", "@y");
        afterCancel!.TotalLabelsPrinted.Should().Be(0);
        afterCancel.TotalAmount.Should().Be(0m);
    }
}
