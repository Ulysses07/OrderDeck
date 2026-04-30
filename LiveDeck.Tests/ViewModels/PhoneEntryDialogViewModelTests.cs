using FluentAssertions;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.ViewModels;

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
}
