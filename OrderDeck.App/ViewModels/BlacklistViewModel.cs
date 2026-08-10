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
            $"{Selected.Display} kara listeden çıkarılacak. Emin misin?",
            "Onayla", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _customers.RemoveFromBlacklist(Selected.Id);
        Reload();
    }

    [RelayCommand]
    private async Task AddManualAsync()
    {
        var drawers = App.Host.Services.GetRequiredService<Services.Drawers.IDrawerService>();

        Views.Drawers.AddToBlacklistDrawer? view = null;
        var ok = await drawers.ShowAsync("Kara Listeye Al",
            d => view = Views.Drawers.AddToBlacklistDrawer.Create(
                d, Views.Drawers.AddToBlacklistDrawer.DrawerMode.Manual));
        if (!ok) return;

        _customers.EnsureBlacklistedManual(
            view!.PlatformText, view.UsernameText, view.ReasonText);
        Reload();
    }

    /// <summary>Müşteri detay çekmecesini açar. Yükleme burada, view'da değil
    /// (Faz 3 kuralı). Servisler App.Host'tan çözülüyor — bu ViewModel'in
    /// ctor'u sayfa listesi için kurulmuş, çekmece bağımlılığı taşımıyor.</summary>
    [RelayCommand]
    private async Task OpenCustomerDetailAsync(string? customerId)
    {
        if (string.IsNullOrEmpty(customerId)) return;

        var services = App.Host.Services;
        var vm = services.GetRequiredService<CustomerDetailViewModel>();
        if (!vm.Load(customerId))
        {
            MessageBox.Show("Müşteri kaydı bulunamadı.", "Müşteri Detayı",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await services.GetRequiredService<Services.Drawers.IDrawerService>()
            .ShowAsync("Müşteri Detayı",
                _ => Views.Drawers.CustomerDetailDrawer.Create(vm));
    }
}
