using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public sealed class DekontEkleViewModelTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    private static (DekontEkleViewModel vm, PaymentRepository repo) Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new PaymentRepository(db);
        var vm = new DekontEkleViewModel(repo, new FakeClock(),
            NullLogger<DekontEkleViewModel>.Instance);
        return (vm, repo);
    }

    private static void FillValid(DekontEkleViewModel vm)
    {
        vm.PayerName = "Ahmet Yıldız";
        vm.AmountText = "250,50";
        vm.ReferansNo = "REF-001";
        vm.PaidAt = DateTime.Today;
    }

    [Fact]
    public void CanSave_is_false_when_form_is_empty()
    {
        var (vm, _) = Build();
        vm.CanSave.Should().BeFalse();
    }

    [Fact]
    public void CanSave_is_true_when_all_required_fields_are_set()
    {
        var (vm, _) = Build();
        FillValid(vm);
        vm.CanSave.Should().BeTrue();
    }

    [Theory]
    [InlineData("250,50")]
    [InlineData("250.50")]
    [InlineData("1000")]
    [InlineData("0.01")]
    public void Amount_accepts_comma_and_dot_decimal(string raw)
    {
        var (vm, repo) = Build();
        FillValid(vm);
        vm.AmountText = raw;
        vm.ReferansNo = $"REF-{Guid.NewGuid():N}";

        var error = vm.TrySave();
        error.Should().BeNull();

        var stored = repo.ListByStatus(PaymentStatus.Pending).First();
        stored.Amount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TrySave_persists_payment_with_pending_status_and_null_synced_at()
    {
        var (vm, repo) = Build();
        FillValid(vm);

        var error = vm.TrySave();

        error.Should().BeNull();
        var stored = repo.FindByReferansNo("REF-001");
        stored.Should().NotBeNull();
        stored!.PayerName.Should().Be("Ahmet Yıldız");
        stored.Amount.Should().Be(250.50m);
        stored.Status.Should().Be(PaymentStatus.Pending);
        stored.SyncedAt.Should().BeNull("outbox pickup yapacak");
        stored.CreatedAt.Should().Be(1714521600L);
    }

    [Fact]
    public void TrySave_rejects_empty_payer_name()
    {
        var (vm, _) = Build();
        FillValid(vm);
        vm.PayerName = "";

        var error = vm.TrySave();
        error.Should().NotBeNull();
        error.Should().Contain("Ödeyen");
        vm.ErrorMessage.Should().NotBeNull();
    }

    [Fact]
    public void TrySave_rejects_empty_referans_no()
    {
        var (vm, _) = Build();
        FillValid(vm);
        vm.ReferansNo = "";

        var error = vm.TrySave();
        error.Should().NotBeNull();
        error.Should().Contain("Referans");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-50")]
    public void TrySave_rejects_invalid_amount(string raw)
    {
        var (vm, _) = Build();
        FillValid(vm);
        vm.AmountText = raw;

        var error = vm.TrySave();
        error.Should().NotBeNull();
    }

    [Fact]
    public void TrySave_rejects_future_paid_date()
    {
        var (vm, _) = Build();
        FillValid(vm);
        vm.PaidAt = DateTime.Today.AddDays(2);

        var error = vm.TrySave();
        error.Should().NotBeNull();
        error.Should().Contain("gelecek");
    }

    [Fact]
    public void TrySave_rejects_duplicate_referans_no()
    {
        var (vm, repo) = Build();
        FillValid(vm);
        vm.TrySave().Should().BeNull();

        // Same form, same ref no
        var (vm2, _) = (new DekontEkleViewModel(repo, new FakeClock(),
            NullLogger<DekontEkleViewModel>.Instance), repo);
        FillValid(vm2);

        var error = vm2.TrySave();
        error.Should().NotBeNull();
        error.Should().Contain("zaten kayıtlı");
    }

    [Fact]
    public void Editing_a_field_clears_error_message()
    {
        var (vm, _) = Build();
        vm.PayerName = "";
        vm.ReferansNo = "REF";
        vm.AmountText = "100";
        vm.TrySave();
        vm.ErrorMessage.Should().NotBeNull();

        vm.PayerName = "Ali";   // typing should clear stale error
        vm.ErrorMessage.Should().BeNull();
    }
}
