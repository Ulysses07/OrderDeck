using System;
using System.ComponentModel;
using System.Windows;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views;

public partial class LoginDialog : Window
{
    private readonly LoginDialogViewModel _vm;

    public LoginDialog(LoginDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Visibility convenience properties on VM are derived from Mode; we just need to refresh.
    }

    private void OnLoginPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb) _vm.Password = pb.Password;
    }

    private void OnRegisterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb) _vm.Password = pb.Password;
    }

    private void OnRegisterPasswordConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb) _vm.PasswordConfirm = pb.Password;
    }
}
