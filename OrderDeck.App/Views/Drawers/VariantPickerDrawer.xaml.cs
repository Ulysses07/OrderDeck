using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Varyant seçici. Kurucusu private + statik <c>Create</c>:
/// <c>BackupTransferDrawer</c> ile aynı kalıp — denetim yalnız bir
/// <see cref="Drawer"/> bağlamında var olabilir.
/// </summary>
public partial class VariantPickerDrawer : UserControl
{
    private readonly Drawer _drawer;

    private VariantPickerDrawer(Drawer drawer, VariantPickerViewModel vm)
    {
        InitializeComponent();
        _drawer = drawer;
        DataContext = vm;
    }

    public static VariantPickerDrawer Create(Drawer drawer, VariantPickerViewModel vm)
        => new(drawer, vm);

    private void Confirm_OnClick(object sender, RoutedEventArgs e) => _drawer.Close(true);

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
