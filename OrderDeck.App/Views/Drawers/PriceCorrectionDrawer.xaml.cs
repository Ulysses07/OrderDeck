using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>"Fiyatı düzelt" çekmecesi — akışın tamamı <see cref="PriceCorrectionViewModel"/>'de; görünüm
/// yalnız bağlar. Açılınca odak yeni fiyat kutusunda.</summary>
public partial class PriceCorrectionDrawer : UserControl
{
    private PriceCorrectionDrawer(PriceCorrectionViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => NewPriceBox.Focus();
    }

    public static PriceCorrectionDrawer Create(PriceCorrectionViewModel vm) => new(vm);
}
