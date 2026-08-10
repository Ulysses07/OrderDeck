using System.Windows.Controls;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// CustomerDetailDialog'un çekmece hâli. DataContext = CustomerDetailViewModel.
///
/// Pencere sürümünün <c>Open(customerId)</c> metodu (Load + "bulunamadı"
/// uyarısı + ShowDialog) buraya taşınmadı: Faz 3'ün ortak kuralı gereği view
/// kendi verisini yüklemiyor. Çağıran <c>vm.Load(id)</c>'yi yapıp false
/// dönerse çekmeceyi hiç açmıyor.
/// </summary>
public partial class CustomerDetailDrawer : UserControl
{
    private CustomerDetailDrawer(CustomerDetailViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Çekmece fabrikası. <see cref="Services.Drawers.Drawer"/>
    /// parametresi almıyor: bu çekmecenin kendini kapatan bir düğmesi yok,
    /// kapatma şeritte.</summary>
    public static CustomerDetailDrawer Create(CustomerDetailViewModel vm) => new(vm);

    /// <summary>ListBox.SelectedItems bağlanabilir değil (DependencyProperty
    /// değil), o yüzden ViewModel'in koleksiyonuna elle yansıtılıyor. Pencere
    /// sürümünde aynı işi DataGrid yapıyordu.</summary>
    private void OnLabelsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not CustomerDetailViewModel vm) return;
        vm.SelectedLabels.Clear();
        foreach (var item in LabelsList.SelectedItems)
            if (item is CustomerLabelRow row)
                vm.SelectedLabels.Add(row);
    }
}
