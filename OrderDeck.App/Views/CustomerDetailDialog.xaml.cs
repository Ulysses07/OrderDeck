using System.Windows;
using OrderDeck.App.ViewModels;

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

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
