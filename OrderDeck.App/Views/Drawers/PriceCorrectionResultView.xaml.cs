using System.Windows;
using System.Windows.Controls;

namespace OrderDeck.App.Views.Drawers;

/// <summary>Fiyat düzeltmesinin sonuç görünümü — "Fiyatı düzelt" çekmecesinde ve FİYAT değişikliğinin satır içi
/// sonucunda (F2, <see cref="Shell.CodePriceResultPanel"/>) aynı. Akış <see cref="ViewModels.PriceCorrectionViewModel"/>'de;
/// görünüm yalnız bağlar. "Yeniden basılsın mı?" sorusunun odak kuralı barındıranın işi (çekmece:
/// <see cref="PriceCorrectionDrawer"/>; satır içi sonuçta odak hiç taşınmaz — operatör kod/fiyat kutusunda yazıyor
/// olabilir).</summary>
public partial class PriceCorrectionResultView : UserControl
{
    public static readonly DependencyProperty ShowsDoneButtonProperty = DependencyProperty.Register(
        nameof(ShowsDoneButton), typeof(bool), typeof(PriceCorrectionResultView),
        new PropertyMetadata(true, (d, e) => ((PriceCorrectionResultView)d).DoneButton.Visibility =
            (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public PriceCorrectionResultView() => InitializeComponent();

    /// <summary>Alttaki "Kapat" görünür mü (varsayılan evet). Satır içi sonuç kendi "Kapat"ını kaydırılan alanın
    /// dışında, başlık satırında taşıyor — listenin altındaki düğme sınırlı yükseklikte görünmez kalırdı.</summary>
    public bool ShowsDoneButton
    {
        get => (bool)GetValue(ShowsDoneButtonProperty);
        set => SetValue(ShowsDoneButtonProperty, value);
    }
}
