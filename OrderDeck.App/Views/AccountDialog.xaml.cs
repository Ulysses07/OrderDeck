using System;
using System.Windows;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views;

public partial class AccountDialog : Window
{
    public AccountDialog(AccountDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += (_, _) => { DialogResult = true; Close(); };
    }
}
