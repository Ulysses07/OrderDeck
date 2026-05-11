using System.Windows;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views;

public partial class DekontEkleDialog : Window
{
    private readonly DekontEkleViewModel _vm;

    public DekontEkleDialog(DekontEkleViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var error = _vm.TrySave();
        if (error is null) DialogResult = true;
        // else hata mesajı ErrorMessage binding'i ile UI'da görünür
    }
}
