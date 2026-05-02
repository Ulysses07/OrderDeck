using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Views;

public partial class CustomerDetailDialog : Window
{
    private readonly CustomerDetailViewModel _vm;

    public CustomerDetailDialog(CustomerDetailViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>Loads the customer and shows the dialog. Returns false if customer not found.</summary>
    public bool Open(string customerId)
    {
        if (!_vm.Load(customerId))
        {
            MessageBox.Show(
                "Müşteri kaydı bulunamadı.",
                "Müşteri yok",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        ShowDialog();
        return true;
    }

    /// <summary>DataGrid.SelectedItems is not bindable directly (not a DependencyProperty),
    /// so we mirror it into the VM's SelectedLabels collection from the code-behind.
    /// The VM's CancelSelected/UncancelSelected commands re-evaluate CanExecute on
    /// every change.</summary>
    private void OnLabelsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedLabels.Clear();
        foreach (var item in LabelsGrid.SelectedItems)
        {
            if (item is CustomerLabelRow row) _vm.SelectedLabels.Add(row);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
