using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// CustomerSearchDialog'un çekmece hâli. DataContext = CustomerSearchViewModel.
///
/// Pencere sürümü ctor'unda <c>vm.RefreshSearch()</c> çağırıyordu; o yükleme
/// çekmeceyi AÇAN tarafa taşındı (MainShellViewModel). Sebep Faz 3'ün ortak
/// kuralı: view kendi verisini yüklemez, yoksa aynı view'ı farklı ön
/// filtrelerle açan çağrılar (kayıt formu bildirimi vs. nav) birbirini ezer.
/// </summary>
public partial class CustomerSearchDrawer : UserControl
{
    private CustomerSearchDrawer(CustomerSearchViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Çekmece fabrikası. <see cref="Drawer"/> parametresi almıyor:
    /// bu çekmecenin kendini kapatan bir düğmesi yok, kapatma şeritte.</summary>
    public static CustomerSearchDrawer Create(CustomerSearchViewModel vm) => new(vm);

    /// <summary>Karta çift tıklama müşteri detayını AYNI yığında üstüne açar
    /// (spec §6.2: arama → detay iki seviyelik zincir). Yükleme burada, detay
    /// view'ının içinde değil — Faz 3 kuralı.</summary>
    private async void ResultsList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not CustomerSearchViewModel.CustomerCard selected) return;

        var services = App.Host.Services;
        var vm = services.GetRequiredService<CustomerDetailViewModel>();
        if (!vm.Load(selected.Primary.Id)) return;

        await services.GetRequiredService<IDrawerService>()
            .ShowAsync("Müşteri Detayı", _ => CustomerDetailDrawer.Create(vm));
    }

    /// <summary>ListBox.SelectedItems bağlanabilir değil (DependencyProperty
    /// değil), o yüzden ViewModel'in koleksiyonuna elle yansıtılıyor.</summary>
    private void ResultsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not CustomerSearchViewModel vm) return;
        vm.SelectedCards.Clear();
        foreach (var item in ResultsList.SelectedItems)
            if (item is CustomerSearchViewModel.CustomerCard card)
                vm.SelectedCards.Add(card);
    }
}
