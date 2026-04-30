using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

public sealed partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _platformFilter;

    public ObservableCollection<Customer> Results { get; } = new();

    public CustomerSearchViewModel(CustomerRepository customers)
    {
        _customers = customers;
    }

    partial void OnQueryChanged(string value)
    {
        ApplySearch(value);
    }

    partial void OnPlatformFilterChanged(string? value)
    {
        RefreshSearch();
    }

    /// <summary>Phase 4f: external trigger to re-run search after PlatformFilter changes.</summary>
    public void RefreshSearch()
    {
        ApplySearch(Query);
    }

    private void ApplySearch(string value)
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        var raw = _customers.Search(value.Trim(), limit: 50);
        var filtered = string.IsNullOrEmpty(PlatformFilter)
            ? raw
            : raw.Where(c => c.Platform == PlatformFilter);
        foreach (var c in filtered)
            Results.Add(c);
    }
}
