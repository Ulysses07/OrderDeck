using System;
using System.Windows;
using LiveDeck.App.ViewModels;

namespace LiveDeck.App.Views;

public partial class AccountDialog : Window
{
    public AccountDialog(AccountDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += (_, _) => { DialogResult = true; Close(); };
    }
}
