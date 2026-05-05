using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Coverage for <see cref="CustomerDetailViewModel"/> — the dialog viewmodel
/// behind the customer-info popup. Focuses on the surface that doesn't require
/// a live WPF dispatcher: <c>Load</c>, the session-scoped vs lifetime label
/// switch, notes persistence, and the CanCancel/CanUncancel selection
/// predicates that drive the cancel/uncancel buttons.
///
/// The Cancel/Uncancel commands themselves open <c>Views.CancelLabelDialog</c>
/// / <c>Views.BackupTransferDialog</c> synchronously and need
/// <c>Application.Current</c>; those paths stay covered by manual smoke +
/// the integration of <see cref="LabelService.Cancel"/> /
/// <see cref="LabelService.Uncancel"/> which are exercised in
/// <see cref="LabelServiceTests"/>-style core tests.
/// </summary>
public class CustomerDetailViewModelTests
{
    private sealed record Harness(
        InMemorySqlite Db,
        CustomerRepository Customers,
        LabelRepository Labels,
        LabelService LabelService,
        GiveawayRepository Giveaways,
        SessionRepository Sessions,
        StreamSessionService SessionService,
        Mock<IClock> Clock,
        CustomerDetailViewModel Vm);

    private static Harness Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(2_000L);

        var sessionRepo  = new SessionRepository(db);
        var customerRepo = new CustomerRepository(db);
        var labelRepo    = new LabelRepository(db);
        var giveawayRepo = new GiveawayRepository(db);

        var customerSvc  = new CustomerService(customerRepo, sessionRepo, labelRepo, clock.Object);
        var sessionSvc   = new StreamSessionService(sessionRepo, clock.Object);
        var labelSvc     = new LabelService(labelRepo, customerSvc, clock.Object);

        var vm = new CustomerDetailViewModel(customerRepo, labelRepo, labelSvc, giveawayRepo, sessionSvc);
        return new Harness(db, customerRepo, labelRepo, labelSvc, giveawayRepo,
                           sessionRepo, sessionSvc, clock, vm);
    }

    private static Customer SeedCustomer(Harness h, string id = "c1",
        string username = "alice", string platform = "instagram",
        string? displayName = "Alice", string? notes = null,
        bool blacklisted = false, string? blacklistReason = null,
        int totalLabels = 0, decimal totalAmount = 0m,
        long firstSeen = 100, long lastSeen = 200, long? blacklistedAt = null)
    {
        var c = new Customer(id, platform, username, displayName, null,
                             firstSeen, lastSeen, blacklisted, blacklistReason, notes,
                             totalLabels, totalAmount, blacklistedAt, null, null);
        h.Customers.Insert(c);
        return c;
    }

    private static StreamSession SeedActiveSession(Harness h, string sessionId = "s-active")
    {
        var s = new StreamSession(sessionId, "Live", 50, null, new[] { "instagram" }, null);
        h.Sessions.Insert(s);
        return s;
    }

    private static void SeedLabel(Harness h, string id, string customerId, string sessionId,
        decimal price = 100m, long addedAt = 110, long? printedAt = 115,
        long? cancelledAt = null, string? cancelReason = null)
    {
        var lbl = new Label(id, sessionId, customerId, "instagram", "alice",
                            "alıyorum", null, price, addedAt, printedAt,
                            cancelledAt, cancelReason);
        h.Labels.Insert(lbl);
        if (cancelledAt is long ca)
            h.Labels.MarkCancelled(new[] { id }, ca, cancelReason ?? "test");
    }

    // ─── Load ────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_returns_false_for_unknown_customer()
    {
        var h = Build();

        var ok = h.Vm.Load("does-not-exist");

        ok.Should().BeFalse();
        h.Vm.Username.Should().Be("",
            "VM properties stay at their initial defaults when load fails");
    }

    [Fact]
    public void Load_populates_summary_fields_from_repository()
    {
        var h = Build();
        SeedCustomer(h, id: "c1", username: "alice", displayName: "Alice Y.",
                     notes: "VIP", totalLabels: 4, totalAmount: 480m,
                     firstSeen: 1_700_000_000, lastSeen: 1_700_300_000);

        var ok = h.Vm.Load("c1");

        ok.Should().BeTrue();
        h.Vm.Username.Should().Be("alice");
        h.Vm.Platform.Should().Be("instagram");
        h.Vm.DisplayName.Should().Be("Alice Y.");
        h.Vm.NotesEdit.Should().Be("VIP");
        h.Vm.TotalLabelsPrinted.Should().Be(4);
        h.Vm.TotalAmount.Should().Be(480m);
        h.Vm.IsBlacklisted.Should().BeFalse();
        h.Vm.FirstSeenLabel.Should().NotBeNullOrEmpty();
        h.Vm.LastSeenLabel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Load_blacklisted_customer_sets_blacklist_fields()
    {
        var h = Build();
        SeedCustomer(h, blacklisted: true, blacklistReason: "spam",
                     blacklistedAt: 1_700_000_500);

        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.IsBlacklisted.Should().BeTrue();
        h.Vm.BlacklistReason.Should().Be("spam");
        h.Vm.BlacklistedAtLabel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Load_with_null_notes_sets_NotesEdit_to_empty_string()
    {
        var h = Build();
        SeedCustomer(h, notes: null);

        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.NotesEdit.Should().Be("",
            "the bound TextBox cannot show a null — VM normalizes to empty string");
    }

    // ─── Section title (active session vs lifetime) ──────────────────────────

    [Fact]
    public void Load_with_active_session_scopes_labels_and_sets_session_title()
    {
        var h = Build();
        SeedCustomer(h);
        SeedActiveSession(h, "s-active");
        var s2 = new StreamSession("s-old", "Old", 10, 20, Array.Empty<string>(), null);
        h.Sessions.Insert(s2);

        SeedLabel(h, "l-active", "c1", "s-active");
        SeedLabel(h, "l-old",    "c1", "s-old");

        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.LabelsSectionTitle.Should().Be("Bu yayındaki etiketler");
        h.Vm.Labels.Should().ContainSingle()
            .Which.Id.Should().Be("l-active",
                "the active-session view must hide labels from prior sessions");
    }

    [Fact]
    public void Load_with_no_active_session_uses_lifetime_view_and_sets_lifetime_title()
    {
        var h = Build();
        SeedCustomer(h);
        // Session ended → no active session.
        var s = new StreamSession("s-old", "Old", 10, 20, Array.Empty<string>(), null);
        h.Sessions.Insert(s);
        SeedLabel(h, "l-old", "c1", "s-old");

        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.LabelsSectionTitle.Should().Be("Tüm etiketler");
        h.Vm.Labels.Should().ContainSingle().Which.Id.Should().Be("l-old");
    }

    // ─── Giveaway participation history ──────────────────────────────────────

    [Fact]
    public void Load_includes_giveaway_participations()
    {
        var h = Build();
        SeedCustomer(h);
        SeedActiveSession(h, "s-active");

        var giveaway = new Giveaway(
            "g1", "s-active", "kazan", 60, 1, null, true, "seed",
            StartedAt: 110, EndedAt: 200, CancelledAt: null, AnimationId: "wheel");
        h.Giveaways.Insert(giveaway);
        h.Giveaways.AddParticipant(new GiveawayParticipant(
            "p1", "g1", "c1", "instagram", "alice", 120, IsWinner: true));

        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.Giveaways.Should().ContainSingle()
            .Which.IsWinner.Should().BeTrue();
        h.Vm.Giveaways[0].GiveawayId.Should().Be("g1");
        h.Vm.Giveaways[0].Keyword.Should().Be("kazan");
    }

    [Fact]
    public void Reloading_a_different_customer_replaces_label_and_giveaway_collections()
    {
        var h = Build();
        SeedCustomer(h, id: "c1", username: "alice");
        SeedCustomer(h, id: "c2", username: "bob");
        SeedActiveSession(h, "s-active");
        SeedLabel(h, "l-alice", "c1", "s-active");
        SeedLabel(h, "l-bob",   "c2", "s-active");

        h.Vm.Load("c1").Should().BeTrue();
        h.Vm.Labels.Should().ContainSingle().Which.Id.Should().Be("l-alice");

        h.Vm.Load("c2").Should().BeTrue();
        h.Vm.Labels.Should().ContainSingle().Which.Id.Should().Be("l-bob");
        h.Vm.Username.Should().Be("bob");
    }

    // ─── SaveNotes ───────────────────────────────────────────────────────────

    [Fact]
    public void SaveNotes_persists_NotesEdit_to_the_repository()
    {
        var h = Build();
        SeedCustomer(h, notes: "old");

        h.Vm.Load("c1").Should().BeTrue();
        h.Vm.NotesEdit = "yeni not";
        h.Vm.SaveNotesCommand.Execute(null);

        h.Customers.GetById("c1")!.Notes.Should().Be("yeni not");
    }

    [Fact]
    public void SaveNotes_with_whitespace_only_input_normalizes_to_null()
    {
        // Repo normalization is a property of CustomerRepository.UpdateNotes;
        // we assert end-to-end here so future VM-level trimming changes don't
        // silently drift away from the backend contract.
        var h = Build();
        SeedCustomer(h, notes: "old");

        h.Vm.Load("c1").Should().BeTrue();
        h.Vm.NotesEdit = "   ";
        h.Vm.SaveNotesCommand.Execute(null);

        h.Customers.GetById("c1")!.Notes.Should().BeNull();
    }

    [Fact]
    public void SaveNotes_before_Load_is_a_noop()
    {
        var h = Build();
        SeedCustomer(h, notes: "untouched");

        // No Load() call yet → _customerId is null.
        h.Vm.NotesEdit = "should not persist";
        h.Vm.SaveNotesCommand.Execute(null);

        h.Customers.GetById("c1")!.Notes.Should().Be("untouched");
    }

    // ─── CanCancelSelected / CanUncancelSelected ─────────────────────────────

    [Fact]
    public void Cancel_and_Uncancel_can_execute_default_to_false_with_no_selection()
    {
        var h = Build();
        SeedCustomer(h);

        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.CancelSelectedCommand.CanExecute(null).Should().BeFalse();
        h.Vm.UncancelSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Selecting_an_active_label_enables_Cancel_only()
    {
        var h = Build();
        SeedCustomer(h);
        SeedActiveSession(h, "s-active");
        SeedLabel(h, "l-active", "c1", "s-active");
        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.SelectedLabels.Add(h.Vm.Labels.Single());

        h.Vm.CancelSelectedCommand.CanExecute(null).Should().BeTrue();
        h.Vm.UncancelSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Selecting_a_cancelled_label_enables_Uncancel_only()
    {
        var h = Build();
        SeedCustomer(h);
        SeedActiveSession(h, "s-active");
        SeedLabel(h, "l-cancelled", "c1", "s-active",
                  cancelledAt: 150, cancelReason: "wrong-price");
        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.Labels.Single().IsCancelled.Should().BeTrue();
        h.Vm.SelectedLabels.Add(h.Vm.Labels.Single());

        h.Vm.CancelSelectedCommand.CanExecute(null).Should().BeFalse();
        h.Vm.UncancelSelectedCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Mixed_selection_enables_both_commands()
    {
        // Two rows: one active, one cancelled. The cancel button targets the
        // active subset; the uncancel button targets the cancelled subset.
        // Both should report CanExecute=true so the operator can pick.
        var h = Build();
        SeedCustomer(h);
        SeedActiveSession(h, "s-active");
        SeedLabel(h, "l-active",    "c1", "s-active");
        SeedLabel(h, "l-cancelled", "c1", "s-active",
                  cancelledAt: 150, cancelReason: "wrong-price");
        h.Vm.Load("c1").Should().BeTrue();

        foreach (var row in h.Vm.Labels) h.Vm.SelectedLabels.Add(row);

        h.Vm.CancelSelectedCommand.CanExecute(null).Should().BeTrue();
        h.Vm.UncancelSelectedCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Clearing_selection_drops_both_predicates_back_to_false()
    {
        var h = Build();
        SeedCustomer(h);
        SeedActiveSession(h, "s-active");
        SeedLabel(h, "l-active", "c1", "s-active");
        h.Vm.Load("c1").Should().BeTrue();

        h.Vm.SelectedLabels.Add(h.Vm.Labels.Single());
        h.Vm.CancelSelectedCommand.CanExecute(null).Should().BeTrue();

        h.Vm.SelectedLabels.Clear();
        h.Vm.CancelSelectedCommand.CanExecute(null).Should().BeFalse();
        h.Vm.UncancelSelectedCommand.CanExecute(null).Should().BeFalse();
    }
}
