using System.Windows.Controls;

namespace OrderDeck.App.Views.Drawers;

/// <summary>Fiyat düzeltmesinin sonuç görünümü — "Fiyatı düzelt" çekmecesinde ve FİYAT kutusunun altında (F2)
/// aynı. Akış <see cref="ViewModels.PriceCorrectionViewModel"/>'de; görünüm yalnız bağlar. "Yeniden basılsın
/// mı?" sorusunun odak kuralı barındıranın işi (çekmece: <see cref="PriceCorrectionDrawer"/>; FİYAT kutusunun
/// altında odak hiç taşınmaz — operatör kod/fiyat kutusunda yazıyor olabilir).</summary>
public partial class PriceCorrectionResultView : UserControl
{
    public PriceCorrectionResultView() => InitializeComponent();
}
