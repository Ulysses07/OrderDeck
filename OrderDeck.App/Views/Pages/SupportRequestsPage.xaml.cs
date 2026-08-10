using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Yayıncı paneli — shopper destek talepleri (forgot-password fallback).
/// Mobil DestekTalepleriScreen'in WPF karşılığı. DataContext =
/// SupportRequestsViewModel.
///
/// Pencere sürümündeki <c>Open()</c> metodu DÜŞTÜ: listeyi yükleyip
/// <c>ShowDialog()</c> çağırıyordu, yani açma sorumluluğu görünümdeydi.
/// Sayfayı açan artık <c>MainShellViewModel</c>; yükleme de orada, sayfa
/// kurulmadan önce başlıyor.
/// </summary>
public partial class SupportRequestsPage : UserControl
{
    private readonly SupportRequestsViewModel _vm;

    private SupportRequestsPage(SupportRequestsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    public static SupportRequestsPage Create(SupportRequestsViewModel vm) => new(vm);

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
}
