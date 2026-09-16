using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class PhoneEntryDialogViewModelTests
{
    private static CustomerRepository CreateRepoWithCustomer(string id = "c1")
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);
        customers.Insert(new Customer(id, "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        return customers;
    }

    [Fact]
    public void Save_InvalidPhone_SetsValidationErrorAndDoesNotClose()
    {
        var customers = CreateRepoWithCustomer();
        var closed = false;
        var sut = new PhoneEntryDialogViewModel(customers, "c1", () => closed = true);
        sut.PhoneInput = "abc";

        sut.SaveCommand.Execute(null);

        sut.ValidationError.Should().NotBeNullOrEmpty();
        closed.Should().BeFalse();
        customers.GetById("c1")!.Phone.Should().BeNull();
    }

    [Fact]
    public void Save_ValidPhone_PersistsE164AndCloses()
    {
        var customers = CreateRepoWithCustomer();
        var closed = false;
        var sut = new PhoneEntryDialogViewModel(customers, "c1", () => closed = true);
        sut.PhoneInput = "5551234567";

        sut.SaveCommand.Execute(null);

        sut.ValidationError.Should().BeNull();
        closed.Should().BeTrue();
        customers.GetById("c1")!.Phone.Should().Be("+905551234567");
    }

    [Fact]
    public void Save_EmptyInput_SetsValidationError()
    {
        var customers = CreateRepoWithCustomer();
        var closed = false;
        var sut = new PhoneEntryDialogViewModel(customers, "c1", () => closed = true);
        sut.PhoneInput = "";

        sut.SaveCommand.Execute(null);

        sut.ValidationError.Should().NotBeNullOrEmpty();
        closed.Should().BeFalse();
    }

    // ── R12-D02 (2026-09-16 denetimi): pencere açıkken inen silme ──────────
    //
    // Telefon toplamak için çekmece açıldıktan SONRA ingest bir KVKK
    // tombstone'u uyguluyor. Operatör o sırada Kaydet'e basarsa eski Save,
    // boşaltılmış satıra telefonu geri yazıyordu — üstelik çekmece "kaydedildi"
    // diye kapanıp operatöre yanlış bilgi veriyordu.

    [Fact]
    public void Save_silinmis_musteriye_telefon_yazmaz_ve_kapanmaz()
    {
        var customers = CreateRepoWithCustomer();
        var closed = false;
        var sut = new PhoneEntryDialogViewModel(customers, "c1", () => closed = true);
        sut.PhoneInput = "5551234567";

        // Çekmece açıkken silme kararı iniyor.
        customers.RecordPurge("twitch", "alice", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        sut.SaveCommand.Execute(null);

        customers.GetById("c1")!.Phone.Should().BeNull(
            "silme kararı satırda yaşıyor; geç bir Save onu aşamamalı");
        closed.Should().BeFalse(
            "hiç satır değişmediyse çekmece 'kaydedildi' diye kapanmamalı");
        sut.ValidationError.Should().NotBeNullOrEmpty(
            "operatör numaranın neden kaydedilmediğini görmeli");
    }
}
