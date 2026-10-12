using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>"Fiyatı düzelt" çekmecesi — akışın tamamı <see cref="PriceCorrectionViewModel"/>'de; görünüm
/// yalnız bağlar. Açılınca odak yeni fiyat kutusunda.</summary>
public partial class PriceCorrectionDrawer : UserControl
{
    private readonly PriceCorrectionViewModel _vm;

    private PriceCorrectionDrawer(PriceCorrectionViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) => NewPriceBox.Focus();
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    public static PriceCorrectionDrawer Create(PriceCorrectionViewModel vm) => new(vm);

    /// <summary>"Yeniden basılsın mı?" açıldı (varsayılan EVET, K5): odak "Evet"e geçer — Enter Evet demek.
    ///
    /// <para>IsDefault DEĞİL: çekmece modal değil ve IsDefault Enter'ı bütün pencereye kaydeder; soru açıkken
    /// sohbet aramasında ya da kod kutusunda basılan Enter etiketleri istenmeden "DÜZELTME" işaretiyle
    /// yeniden basardı. Odak yalnız çekmecedeyse (ya da hiçbir yerde değilse) taşınır — soru, baskı
    /// hatasından sonra saniyeler içinde yeniden açılabilir; operatör o sırada başka bir kutuda yazıyorsa
    /// odak çalınmaz. Karar anda verilir: onay düğmesi gizlenince klavye odağı sonradan yeniden
    /// değerlendirilir, o zaman "çekmecede" görünmeyebilir.</para></summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PriceCorrectionViewModel.IsReprintAsked) || !_vm.IsReprintAsked) return;
        if (!IsKeyboardFocusWithin && Keyboard.FocusedElement is not null) return;
        // Soru kutusu bağ güncellenince görünür olur; odak ondan sonra.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_vm.IsReprintAsked) ReprintYesButton.Focus();
        });
    }
}
