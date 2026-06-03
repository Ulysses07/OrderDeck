using System.Windows;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views;

/// <summary>
/// Yayıncı paneli — shopper destek talepleri (forgot-password fallback).
/// Mobil DestekTalepleriScreen'in WPF karşılığı. DataContext =
/// SupportRequestsViewModel (DI). Açılışta listeyi yükler.
/// </summary>
public partial class SupportRequestsDialog : Window
{
    private readonly SupportRequestsViewModel _vm;

    public SupportRequestsDialog(SupportRequestsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>Listeyi yükler ve dialog'u modal açar.</summary>
    public void Open()
    {
        _ = _vm.LoadAsync();
        ShowDialog();
    }

    private async void OnIncludeResolvedChanged(object sender, RoutedEventArgs e)
    {
        // CheckBox kullanıcı değişimi → VM'i güncelle + reload.
        if (ChkResolved.IsChecked is bool b && b != _vm.IncludeResolved)
        {
            _vm.IncludeResolved = b;
            await _vm.LoadAsync();
        }
    }

    private void OnCopyPassword(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe &&
            fe.DataContext is SupportRequestsViewModel.SupportRequestRow row &&
            !string.IsNullOrEmpty(row.TempPassword))
        {
            try { Clipboard.SetText(row.TempPassword); }
            catch { /* clipboard erişilemezse kullanıcı elle kopyalar */ }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
