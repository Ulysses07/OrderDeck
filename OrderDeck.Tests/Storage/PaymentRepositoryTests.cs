using FluentAssertions;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class PaymentRepositoryTests
{
    private static PaymentRepository CreateRepository()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new PaymentRepository(db);
    }

    private static Payment NewPayment(string id = "p1", string? refNo = null) => new(
        Id: id,
        PayerName: "Ahmet Yıldız",
        Amount: 250.75m,
        PaidAt: 1714521600L,
        ReferansNo: refNo ?? $"REF-{id}",
        PdfHash: null,
        Status: PaymentStatus.Pending,
        CreatedAt: 1714521600L,
        UpdatedAt: 1714521600L,
        SyncedAt: null,
        ApprovedAt: null,
        RejectedAt: null,
        RejectReason: null);

    [Fact]
    public void Insert_then_FindById_returns_payment()
    {
        var repo = CreateRepository();
        var p = NewPayment();
        repo.Insert(p);

        var found = repo.FindById("p1");
        found.Should().NotBeNull();
        found!.PayerName.Should().Be("Ahmet Yıldız");
        found.Amount.Should().Be(250.75m);
        found.ReferansNo.Should().Be("REF-p1");
        found.Status.Should().Be(PaymentStatus.Pending);
    }

    [Fact]
    public void Amount_decimal_precision_roundtrips_exactly()
    {
        var repo = CreateRepository();
        // SQLite REAL would lose precision; we store as TEXT to avoid that.
        var p = NewPayment() with { Amount = 1234567.89m };
        repo.Insert(p);

        var found = repo.FindById(p.Id);
        found!.Amount.Should().Be(1234567.89m);
    }

    [Fact]
    public void FindByReferansNo_finds_record()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment(refNo: "REF-XYZ-123"));

        var found = repo.FindByReferansNo("REF-XYZ-123");
        found.Should().NotBeNull();
        found!.Id.Should().Be("p1");
    }

    [Fact]
    public void Duplicate_referans_no_throws()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment("p1", refNo: "REF-DUPE"));

        Action act = () => repo.Insert(NewPayment("p2", refNo: "REF-DUPE"));
        act.Should().Throw<Exception>();   // Sqlite constraint violation
    }

    [Fact]
    public void GetUnsynced_returns_only_null_SyncedAt_oldest_first()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment("p1", refNo: "R1") with { CreatedAt = 1000 });
        repo.Insert(NewPayment("p2", refNo: "R2") with { CreatedAt = 2000 });
        repo.Insert(NewPayment("p3", refNo: "R3") with { CreatedAt = 1500, SyncedAt = 9999 });

        var batch = repo.GetUnsynced(10);
        batch.Should().HaveCount(2);
        batch[0].Id.Should().Be("p1");
        batch[1].Id.Should().Be("p2");
    }

    [Fact]
    public void GetUnsynced_respects_limit()
    {
        var repo = CreateRepository();
        for (int i = 0; i < 5; i++)
            repo.Insert(NewPayment($"p{i}", refNo: $"R{i}") with { CreatedAt = i });

        repo.GetUnsynced(3).Should().HaveCount(3);
    }

    [Fact]
    public void MarkSynced_updates_SyncedAt_and_UpdatedAt()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment());

        repo.MarkSynced("p1", 99999L);
        var found = repo.FindById("p1");
        found!.SyncedAt.Should().Be(99999L);
        found.UpdatedAt.Should().Be(99999L);
    }

    [Fact]
    public void ApplyServerStatus_transitions_to_approved()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment());

        repo.ApplyServerStatus("p1", PaymentStatus.Approved,
            approvedAt: 8888L, rejectedAt: null, rejectReason: null, updatedAt: 8888L);

        var found = repo.FindById("p1");
        found!.Status.Should().Be(PaymentStatus.Approved);
        found.ApprovedAt.Should().Be(8888L);
        found.RejectedAt.Should().BeNull();
    }

    [Fact]
    public void ApplyServerStatus_transitions_to_rejected_with_reason()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment());

        repo.ApplyServerStatus("p1", PaymentStatus.Rejected,
            approvedAt: null, rejectedAt: 7777L, rejectReason: "tutar uyusmuyor",
            updatedAt: 7777L);

        var found = repo.FindById("p1");
        found!.Status.Should().Be(PaymentStatus.Rejected);
        found.RejectedAt.Should().Be(7777L);
        found.RejectReason.Should().Be("tutar uyusmuyor");
    }

    [Fact]
    public void ListByStatus_filters_correctly()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment("p1", refNo: "R1"));
        repo.Insert(NewPayment("p2", refNo: "R2") with { Status = PaymentStatus.Approved });
        repo.Insert(NewPayment("p3", refNo: "R3") with { Status = PaymentStatus.Rejected });

        repo.ListByStatus(PaymentStatus.Pending).Should().HaveCount(1);
        repo.ListByStatus(PaymentStatus.Approved).Should().HaveCount(1);
        repo.ListByStatus(PaymentStatus.Rejected).Should().HaveCount(1);
    }

    [Fact]
    public void CountUnsynced_returns_correct_count()
    {
        var repo = CreateRepository();
        repo.Insert(NewPayment("p1", refNo: "R1"));
        repo.Insert(NewPayment("p2", refNo: "R2") with { SyncedAt = 9999 });
        repo.Insert(NewPayment("p3", refNo: "R3"));

        repo.CountUnsynced().Should().Be(2);
    }
}
