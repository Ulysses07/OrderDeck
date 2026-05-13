using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class ShipmentRepositoryTests
{
    private static (InMemorySqlite Db, ShipmentRepository Repo, LabelRepository Labels, string SessionId, string CustomerId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

        return (db, new ShipmentRepository(db), new LabelRepository(db), "s1", "c1");
    }

    private static Shipment NewShipment(string id = "sh1", string customerId = "c1",
        ShipmentStatus status = ShipmentStatus.Pending, decimal cumulative = 0m,
        long createdAt = 1000L) =>
        new(id, customerId, status, createdAt, HeldAt: null, ShippedAt: null,
            CumulativeAmount: cumulative);

    [Fact]
    public void Insert_then_GetById_roundtrips_all_fields()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment(cumulative: 1234.56m));

        var found = repo.GetById("sh1");
        found.Should().NotBeNull();
        found!.CustomerId.Should().Be("c1");
        found.Status.Should().Be(ShipmentStatus.Pending);
        found.CumulativeAmount.Should().Be(1234.56m);
        found.HeldAt.Should().BeNull();
        found.ShippedAt.Should().BeNull();
    }

    [Fact]
    public void GetOpenByCustomer_returns_pending_shipment()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment("sh1", status: ShipmentStatus.Pending));

        var open = repo.GetOpenByCustomer("c1");
        open.Should().NotBeNull();
        open!.Id.Should().Be("sh1");
    }

    [Fact]
    public void GetOpenByCustomer_returns_held_shipment()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment("sh1", status: ShipmentStatus.Held));

        var open = repo.GetOpenByCustomer("c1");
        open.Should().NotBeNull();
        open!.Status.Should().Be(ShipmentStatus.Held);
    }

    [Fact]
    public void GetOpenByCustomer_skips_shipped_shipment()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment("sh1", status: ShipmentStatus.Shipped) with { ShippedAt = 2000L });

        var open = repo.GetOpenByCustomer("c1");
        open.Should().BeNull();
    }

    [Fact]
    public void GetOpenByCustomer_skips_recipient_pays_shipment()
    {
        // RecipientPays terminal akış — sticky kargo türü, yeni alım yeni Shipment açar.
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment("sh1", status: ShipmentStatus.RecipientPays));

        var open = repo.GetOpenByCustomer("c1");
        open.Should().BeNull();
    }

    [Fact]
    public void Update_transitions_status_and_timestamps()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment());
        var s = repo.GetById("sh1")!;

        // Pending → Held
        repo.Update(s with { Status = ShipmentStatus.Held, HeldAt = 1500L, CumulativeAmount = 2000m });

        var held = repo.GetById("sh1")!;
        held.Status.Should().Be(ShipmentStatus.Held);
        held.HeldAt.Should().Be(1500L);
        held.CumulativeAmount.Should().Be(2000m);

        // Held → Shipped
        repo.Update(held with { Status = ShipmentStatus.Shipped, ShippedAt = 2000L });

        var shipped = repo.GetById("sh1")!;
        shipped.Status.Should().Be(ShipmentStatus.Shipped);
        shipped.ShippedAt.Should().Be(2000L);
        shipped.HeldAt.Should().Be(1500L); // preserved
    }

    [Fact]
    public void GetByStatus_returns_only_matching_and_orders_FIFO()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        // İkinci müşteri eklemeden iki Held Shipment oluşturmak için
        // farklı CustomerId ile bypass etmek istemiyorum — aynı c1 ile yeterli.
        repo.Insert(NewShipment("sh1", status: ShipmentStatus.Held, createdAt: 1000L));
        repo.Insert(NewShipment("sh2", status: ShipmentStatus.Held, createdAt: 2000L));
        repo.Insert(NewShipment("sh3", status: ShipmentStatus.Shipped, createdAt: 1500L));

        var held = repo.GetByStatus(ShipmentStatus.Held);
        held.Should().HaveCount(2);
        held[0].Id.Should().Be("sh1"); // FIFO: en eski önce
        held[1].Id.Should().Be("sh2");
    }

    [Fact]
    public void AttachLabel_sets_ShipmentId_on_label()
    {
        var (db, repo, labels, sid, cid) = Fx();
        using var _d = db;

        repo.Insert(NewShipment());
        labels.Insert(new Label("l1", sid, cid, "instagram", "@a", "msg", null,
            150m, AddedAt: 200, PrintedAt: null));

        repo.AttachLabel("sh1", "l1");

        var label = labels.GetById("l1");
        label!.ShipmentId.Should().Be("sh1");
    }

    [Fact]
    public void GetLabelIds_returns_attached_labels_only_ordered_by_AddedAt()
    {
        var (db, repo, labels, sid, cid) = Fx();
        using var _d = db;

        repo.Insert(NewShipment());
        labels.Insert(new Label("l1", sid, cid, "instagram", "@a", "msg1", null, 100m, AddedAt: 300, PrintedAt: null));
        labels.Insert(new Label("l2", sid, cid, "instagram", "@a", "msg2", null, 200m, AddedAt: 200, PrintedAt: null));
        labels.Insert(new Label("l3", sid, cid, "instagram", "@a", "msg3", null, 300m, AddedAt: 400, PrintedAt: null));

        repo.AttachLabel("sh1", "l1");
        repo.AttachLabel("sh1", "l2");
        // l3 attach edilmedi

        var ids = repo.GetLabelIds("sh1");
        ids.Should().HaveCount(2);
        ids[0].Should().Be("l2"); // AddedAt=200 önce
        ids[1].Should().Be("l1"); // AddedAt=300 sonra
    }

    [Fact]
    public void Label_inserted_directly_with_ShipmentId_roundtrips()
    {
        var (db, repo, labels, sid, cid) = Fx();
        using var _d = db;

        repo.Insert(NewShipment());
        labels.Insert(new Label("l1", sid, cid, "instagram", "@a", "msg", null,
            150m, AddedAt: 200, PrintedAt: null, ShipmentId: "sh1"));

        var label = labels.GetById("l1");
        label!.ShipmentId.Should().Be("sh1");
    }

    // ── PR-D outbox / sync tests ──────────────────────────────────────────

    [Fact]
    public void GetUnsynced_returns_only_null_SyncedAt_rows()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment("sh1"));
        repo.Insert(NewShipment("sh2") with { SyncedAt = 5000L });
        repo.Insert(NewShipment("sh3"));

        var unsynced = repo.GetUnsynced();
        unsynced.Should().HaveCount(2);
        unsynced.Select(x => x.Id).Should().BeEquivalentTo(new[] { "sh1", "sh3" });
    }

    [Fact]
    public void MarkSynced_sets_SyncedAt_timestamp()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment());
        repo.MarkSynced("sh1", syncedAt: 7777L);

        var found = repo.GetById("sh1");
        found!.SyncedAt.Should().Be(7777L);
    }

    [Fact]
    public void Update_resets_SyncedAt_so_next_tick_pushes_again()
    {
        // Lokal state değişti → outbox'a tekrar düşmeli (server stale data göstermemeli).
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        repo.Insert(NewShipment());
        repo.MarkSynced("sh1", syncedAt: 1000L);

        var current = repo.GetById("sh1")!;
        repo.Update(current with { Status = ShipmentStatus.Held, HeldAt = 2000L });

        var afterUpdate = repo.GetById("sh1")!;
        afterUpdate.SyncedAt.Should().BeNull("Update SyncedAt'i NULL'a düşürmeli");
    }

    [Fact]
    public void GetUnsynced_honors_limit_param()
    {
        var (db, repo, _, _, _) = Fx();
        using var _d = db;

        for (int i = 0; i < 10; i++)
            repo.Insert(NewShipment($"sh{i}"));

        repo.GetUnsynced(limit: 3).Should().HaveCount(3);
        repo.GetUnsynced(limit: 100).Should().HaveCount(10);
    }
}
