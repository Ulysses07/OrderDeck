using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Drawers;

namespace OrderDeck.App.Views.Drawers;

public partial class MessageDrawer : UserControl
{
    private readonly Drawer _drawer;

    private MessageDrawer(Drawer drawer, string message, DialogSeverity severity, bool asQuestion, string confirmText)
    {
        InitializeComponent();
        _drawer = drawer;

        MessageText.Text = message;
        Stripe.Background = (Brush)FindResource(severity switch
        {
            DialogSeverity.Error   => "OD.Brush.Accent",
            DialogSeverity.Warning => "OD.Brush.Amber",
            _                      => "OD.Brush.Info",
        });

        ConfirmButton.Content = asQuestion ? confirmText : "Tamam";
        // Bildirimde iptal düğmesi yok: kapatmanın tek anlamı var.
        CancelButton.Content = "Vazgeç";
        CancelButton.Visibility = asQuestion ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Onay/Vazgeç sorusu. <c>MessageBoxButton.YesNo</c>'nun yerine. Onay düğmesinin yazısı
    /// <paramref name="confirmText"/> (varsayılan "Evet"; Ayır onayı "Ayır", spec 2026-10-11 §6.1-e).</summary>
    public static MessageDrawer ForConfirm(Drawer drawer, string message, string confirmText = "Evet")
        => new(drawer, message, DialogSeverity.Info, asQuestion: true, confirmText);

    /// <summary>Tek düğmeli bildirim. <c>MessageBoxButton.OK</c>'un yerine.</summary>
    public static MessageDrawer ForNotice(Drawer drawer, string message, DialogSeverity severity)
        => new(drawer, message, severity, asQuestion: false, confirmText: "Tamam");

    private void OnConfirm(object sender, RoutedEventArgs e) => _drawer.Close(true);

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
