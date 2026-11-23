using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// "Instagram hesabı ekle" çekmecesi. Kurucusu private + statik <c>Create</c> (<c>VariantPickerDrawer</c> kalıbı):
/// denetim yalnız bir <see cref="Drawer"/> bağlamında var olabilir. Seçim modelde (<see cref="InstagramAccountPickerViewModel.Selected"/>)
/// tutulur; çağıran onu çekmece kapandıktan sonra modelden okur.
/// </summary>
public partial class InstagramAccountAddDrawer : UserControl
{
    private readonly Drawer _drawer;
    private readonly InstagramAccountPickerViewModel _picker;

    private InstagramAccountAddDrawer(Drawer drawer, InstagramAccountPickerViewModel picker)
    {
        InitializeComponent();
        _drawer = drawer;
        _picker = picker;
        DataContext = picker;
    }

    public static InstagramAccountAddDrawer Create(Drawer drawer, InstagramAccountPickerViewModel picker)
        => new(drawer, picker);

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        // Seçim yoksa (ya da pasif bir seçenekse) kapatma: "Ekle" ekleyecek bir hesap olmadan çekmeceyi kapatırsa
        // kullanıcı işlemin yapıldığını sanardı.
        if (_picker.Selected is not { CanSelect: true }) return;
        _drawer.Close(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
