using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Drawers;

namespace OrderDeck.App.Views.Shell;

public partial class DrawerHost : UserControl
{
    public DrawerHost()
    {
        InitializeComponent();
        // Kabuğun DataContext'i MainShellViewModel; çekmece katmanı yığına
        // bağlanmalı, o yüzden kendi DataContext'ini kendi kuruyor.
        // App.Host boş olan tek durum kompozisyon testi (bkz. MainShellView).
        if (App.Host is not null)
            DataContext = App.Host.Services.GetRequiredService<DrawerStack>();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Drawer drawer)
            drawer.Close(false);
    }

    /// <summary>
    /// Sağdan içeri kaydırır. XAML'daki EventTrigger yerine kod: Motion.xaml'ın
    /// notu gereği azaltılmış hareket açıkken animasyon ATLANMALI ve bu koşul
    /// XAML'dan sorulamıyor.
    /// </summary>
    private void OnDrawerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement panel) return;
        if (!SystemParameters.ClientAreaAnimation) return;

        var transform = new TranslateTransform();
        panel.RenderTransform = transform;

        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = (double)FindResource("OD.Layout.DrawerWidth"),
            To = 0,
            Duration = (Duration)FindResource("OD.Dur.Base"),
            EasingFunction = (IEasingFunction)FindResource("OD.Ease.Out"),
        });
    }
}
