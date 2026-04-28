using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

public sealed partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;

    [ObservableProperty] private string _query = "";

    public ObservableCollection<Customer> Results { get; } = new();

    public CustomerSearchViewModel(CustomerRepository customers)
    {
        _customers = customers;
    }

    partial void OnQueryChanged(string value)
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var c in _customers.Search(value.Trim(), limit: 50))
            Results.Add(c);
    }
}
