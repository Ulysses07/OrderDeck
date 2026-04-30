using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.ViewModels;

public sealed partial class BlacklistViewModel : ViewModelBase
{
    private readonly CustomerRepository _repo;
    private readonly CustomerService _customers;

    public ObservableCollection<Customer> Items { get; } = new();

    [ObservableProperty] private Customer? _selected;
    [ObservableProperty] private int _totalCount;

    public BlacklistViewModel(CustomerRepository repo, CustomerService customers)
    {
        _repo = repo;
        _customers = customers;
        Reload();
    }

    public void Reload()
    {
        Items.Clear();
        foreach (var c in _repo.GetBlacklisted()) Items.Add(c);
        TotalCount = Items.Count;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is null) return;
        var confirm = MessageBox.Show(
            $"{Selected.Username} kara listeden çıkarılacak. Emin misin?",
            "Onayla", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _customers.RemoveFromBlacklist(Selected.Id);
        Reload();
    }

    [RelayCommand]
    private void AddManual()
    {
        var dialog = new Views.AddToBlacklistDialog
        {
            Mode = Views.AddToBlacklistDialog.DialogMode.Manual
        };
        dialog.Owner = Application.Current?.Windows.Count > 0
            ? Application.Current?.Windows[Application.Current.Windows.Count - 1]
            : null;
        if (dialog.ShowDialog() != true) return;

        _customers.EnsureBlacklistedManual(
            dialog.PlatformText ?? "instagram",
            dialog.UsernameText ?? "",
            dialog.ReasonText);
        Reload();
    }

    [RelayCommand]
    private void OpenCustomerDetail(string? customerId)
    {
        if (string.IsNullOrEmpty(customerId)) return;
        var dlg = App.Host.Services.GetRequiredService<Views.CustomerDetailDialog>();
        dlg.Owner = Application.Current?.Windows.Count > 0
            ? Application.Current?.Windows[Application.Current.Windows.Count - 1]
            : null;
        dlg.Open(customerId);
    }
}
