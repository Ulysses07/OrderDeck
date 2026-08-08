using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Views.Drawers;
using OrderDeck.App.Views.Shell;

namespace OrderDeck.Tests.App;

/// <summary>
/// Çekmece katmanının YERLEŞİMİ. Kompozisyon testi yalnız "XAML çözülüyor mu"
/// diyor; buradaki risk bir adım ötesi: katman çözülür ama görünmez, ölçüsüz
/// ya da yanlış yerde çizilir. Hiçbiri istisna atmaz — ancak ekranda fark
/// edilir.
///
/// Katman sağ sütunun hücresine yerleşiyor (MainShellView), o yüzden ölçüler
/// sağ sütunun genişliğinden veriliyor.
/// </summary>
public class DrawerHostLayoutTests
{
    private const double HostWidth = 400;
    private const double HostHeight = 700;

    /// <summary>Ölçüm + yerleşimi gerçekten çalıştırır; görsel ağaç ancak
    /// bundan sonra koordinat verir.</summary>
    private static DrawerHost Arrange(DrawerStack stack)
    {
        var host = new DrawerHost { DataContext = stack };
        host.Measure(new Size(HostWidth, HostHeight));
        host.Arrange(new Rect(0, 0, HostWidth, HostHeight));
        host.UpdateLayout();
        return host;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deep) return deep;
        }
        return null;
    }

    [Fact]
    public void Layer_takes_no_space_while_no_drawer_is_open()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var host = Arrange(new DrawerStack());

            // Collapsed olmalı: aksi hâlde boş bir katman sağ sütunun üstünde
            // durur ve ürün kartına giden tıklamaları yutar.
            var items = FindChild<ItemsControl>(host)!;
            Assert.Equal(Visibility.Collapsed, items.Visibility);
            Assert.Equal(0, items.ActualHeight);
        });

        Assert.Null(error);
    }

    [Fact]
    public void Open_drawer_fills_the_right_column_cell()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var stack = new DrawerStack();
            stack.ShowAsync("Onay", d => MessageDrawer.ForConfirm(d, "Emin misin?"));
            var host = Arrange(stack);

            var width = (double)Application.Current!.Resources["OD.Layout.DrawerWidth"];
            var items = FindChild<ItemsControl>(host)!;

            // Sağ panelle aynı genişlik: çekmece onu tam örtsün, kenarından
            // bir şerit sızmasın.
            Assert.Equal(width, items.ActualWidth);
            Assert.Equal(HostHeight, items.ActualHeight, 1);

            var origin = items.TransformToAncestor(host).Transform(new Point(0, 0));
            Assert.Equal(0, origin.X, 1);
            Assert.Equal(0, origin.Y, 1);
        });

        Assert.Null(error);
    }

    [Fact]
    public void Nested_drawer_covers_the_one_below_it()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var stack = new DrawerStack();
            stack.ShowAsync("Dekont", d => MessageDrawer.ForNotice(
                d, "Alt çekmece", OrderDeck.App.Services.DialogSeverity.Info));
            stack.ShowAsync("Kargo", d => MessageDrawer.ForConfirm(d, "Üst çekmece"));
            var host = Arrange(stack);

            // İkisi de AYNI hücrede, aynı ölçüde: ItemsPanel Grid olduğu için
            // üstteki alttakini tam örtüyor. Varsayılan StackPanel'e düşerse
            // ikisi alt alta dizilir ve yarısı ekran dışında kalır.
            var items = FindChild<ItemsControl>(host)!;
            Assert.Equal(2, items.Items.Count);

            for (var i = 0; i < items.Items.Count; i++)
            {
                var container = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(i)!;
                var origin = container.TransformToAncestor(host).Transform(new Point(0, 0));

                Assert.Equal(0, origin.X, 1);
                Assert.Equal(0, origin.Y, 1);
                Assert.Equal(HostHeight, container.ActualHeight, 1);
            }
        });

        Assert.Null(error);
    }
}
