using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.Views.Shell;

namespace OrderDeck.Tests.App;

/// <summary>
/// Sayfa katmanının YERLEŞİMİ — <see cref="DrawerHostLayoutTests"/>'in
/// kardeşi, aynı gerekçeyle: katman çözülür ama görünmez ya da ölçüsüz
/// çizilirse hiçbir istisna atılmaz, yalnız ekranda fark edilir.
///
/// Ölçüler içerik sütunundan veriliyor: sayfa üst bar HARİÇ satır 1-2'yi
/// kaplıyor (MainShellView'deki Grid.Row=1 + RowSpan=2).
/// </summary>
public class PageHostLayoutTests
{
    private const double HostWidth = 1200;
    private const double HostHeight = 800;

    private static PageHost Arrange(PageStack stack)
    {
        var host = new PageHost { DataContext = stack };
        host.Measure(new Size(HostWidth, HostHeight));
        host.Arrange(new Rect(0, 0, HostWidth, HostHeight));
        host.UpdateLayout();
        return host;
    }

    /// <summary>
    /// Tip araması DEĞİL ad araması: PageHost'un ağacında hem UserControl'ün
    /// kendi şablon Border'ı hem de geri düğmesi (bir ContentControl) var,
    /// ikisi de aranan öğeden önce gelir.
    /// </summary>
    private static T Named<T>(PageHost host, string name) where T : DependencyObject
        => (T)host.FindName(name);

    [Fact]
    public void Layer_takes_no_space_while_no_page_is_open()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var host = Arrange(new PageStack());

            // Collapsed olmalı: açık sayfa yokken kabuğun üstünde duran boş
            // bir katman tüm tıklamaları yutar (çekmeceden farkı: bu katman
            // kenar çubuğunu da örtüyor).
            var root = Named<Border>(host, "Root");
            Assert.Equal(Visibility.Collapsed, root.Visibility);
            Assert.Equal(0, root.ActualHeight);
        });

        Assert.Null(error);
    }

    [Fact]
    public void Open_page_fills_the_content_area_and_shows_its_title()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var stack = new PageStack();
            stack.ShowAsync("blacklist", "Kara Liste", _ => new TextBlock());
            var host = Arrange(stack);

            var root = Named<Border>(host, "Root");
            Assert.Equal(Visibility.Visible, root.Visibility);
            Assert.Equal(HostWidth, root.ActualWidth, 1);
            Assert.Equal(HostHeight, root.ActualHeight, 1);

            // İçerik yuvası gerçekten yığının tepesini gösteriyor mu?
            var content = Named<ContentControl>(host, "PageContent");
            Assert.Same(stack.Top!.Content, content.Content);
        });

        Assert.Null(error);
    }

    [Fact]
    public void Top_page_replaces_the_one_below_it()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            // Çekmeceden ayrıştığı yer: alttaki sayfa ÇİZİLMİYOR. Yığında
            // duruyor ama görsel ağaçta yalnız tepedeki var.
            var stack = new PageStack();
            var history = new TextBlock();
            var report = new TextBlock();
            stack.ShowAsync("history", "Yayın Geçmişi", _ => history);
            stack.ShowAsync("stream-report", "Yayın Raporu", _ => report);
            var host = Arrange(stack);

            var content = Named<ContentControl>(host, "PageContent");
            Assert.Same(report, content.Content);

            stack.Back();
            host.UpdateLayout();

            Assert.Same(history, content.Content);
        });

        Assert.Null(error);
    }
}
