using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Drawers;

namespace OrderDeck.App.Views.Drawers;

public partial class MessageDrawer : UserControl
{
    private readonly Drawer _drawer;

    private MessageDrawer(Drawer drawer, string message, DialogSeverity severity, bool asQuestion)
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

        ConfirmButton.Content = asQuestion ? "Evet" : "Tamam";
        // Bildirimde iptal düğmesi yok: kapatmanın tek anlamı var.
        CancelButton.Content = "Vazgeç";
        CancelButton.Visibility = asQuestion ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Evet/Vazgeç sorusu. <c>MessageBoxButton.YesNo</c>'nun yerine.</summary>
    public static MessageDrawer ForConfirm(Drawer drawer, string message)
        => new(drawer, message, DialogSeverity.Info, asQuestion: true);

    /// <summary>Tek düğmeli bildirim. <c>MessageBoxButton.OK</c>'un yerine.</summary>
    public static MessageDrawer ForNotice(Drawer drawer, string message, DialogSeverity severity)
        => new(drawer, message, severity, asQuestion: false);

    private void OnConfirm(object sender, RoutedEventArgs e) => _drawer.Close(true);

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
