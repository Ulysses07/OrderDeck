using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Shell;

public partial class ProductCard : UserControl
{
    public ProductCard() => InitializeComponent();

    /// <summary>
    /// Kartın kökündeki Border DataContext'i ProductCard'a çevirdiği için
    /// UserControl'ün kendi DataContext'i hâlâ kabuk VM'i. VM'i o yüzden
    /// olayın geldiği öğenin DataContext'inden okuyoruz.
    /// </summary>
    private static ProductCardViewModel? VmOf(object sender)
        => (sender as FrameworkElement)?.DataContext as ProductCardViewModel;

    /// <summary>
    /// Beden metnini ızgaraya uygular. Komut değil Click: ApplySizesText bir
    /// dönüşüm, geri alınacak/CanExecute'lu bir eylem değil.
    /// </summary>
    private void ApplySizes_OnClick(object sender, RoutedEventArgs e)
        => VmOf(sender)?.ApplySizesText();

    /// <summary>
    /// Dosya seçme diyaloğu. Bu bir işletim sistemi diyaloğu — spec §6'nın
    /// "pop-up yok" kuralı uygulamanın kendi pencerelerini kapsıyor, dosya
    /// seçiciyi değil (alternatifi sürükle-bırak zorunluluğu olurdu).
    /// </summary>
    private void PickPhoto_OnClick(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;

        var dlg = new OpenFileDialog
        {
            Title = "Ürün fotoğrafı seç",
            Filter = "Görseller|*.jpg;*.jpeg;*.png;*.webp|Tüm dosyalar|*.*"
        };
        if (dlg.ShowDialog() == true) vm.SetPhoto(dlg.FileName);
    }
}
