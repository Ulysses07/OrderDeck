using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Views;
using OrderDeck.App.Views.Drawers;
using OrderDeck.App.Views.Shell;

namespace OrderDeck.Tests.App;

/// <summary>
/// Yazdır yuvası çekmecenin ALTINDA mı kalıyor? Spec §6.2 bunu şart koşuyor:
/// operatör çekmece açıkken de etiket basabilmeli. Yuva PrintQueuePanel'in
/// içindeyken bu sağlanmıyordu, çünkü çekmece sağ sütunun tamamını örtüyordu.
///
/// Test kabuğun GERÇEK ağacında ölçüyor; DrawerHostLayoutTests ise çekmeceyi
/// tek başına, uydurma bir hücrede ölçüyor. İkisi farklı riskler: oradaki
/// "çekmece kendi hücresini dolduruyor mu", buradaki "hücre doğru yerde mi".
/// </summary>
public class ShellPrintSlotLayoutTests
{
    private const double ShellWidth = 1600;
    private const double ShellHeight = 900;

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

    private static Rect Bounds(FrameworkElement element, Visual root)
    {
        var origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    [Fact]
    public void Print_slot_stays_outside_the_open_drawer()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var shell = new MainShellView();

            // Görsel ağaç ancak ilk ölçüm/yerleşimden SONRA oluşuyor; önce
            // arayan VisualTreeHelper boş döner.
            shell.Measure(new Size(ShellWidth, ShellHeight));
            shell.Arrange(new Rect(0, 0, ShellWidth, ShellHeight));
            shell.UpdateLayout();

            // DrawerHost normalde yığını App.Host'tan alıyor; testte host yok,
            // o yüzden elle veriyoruz. Ağacın geri kalanı gerçek.
            var host = FindChild<DrawerHost>(shell)!;
            var stack = new DrawerStack();
            stack.ShowAsync("Onay", d => MessageDrawer.ForConfirm(d, "Emin misin?"));
            host.DataContext = stack;
            ThemeTestHost.Pump();
            shell.Measure(new Size(ShellWidth, ShellHeight));
            shell.Arrange(new Rect(0, 0, ShellWidth, ShellHeight));
            shell.UpdateLayout();

            // Çekmece gerçekten materyalize oldu mu? Pump olmadan ItemsSource
            // null kalıyor ve aşağıdaki ölçümler "kapalı çekmece" ölçerdi.
            var items = FindChild<ItemsControl>(host)!;
            Assert.Single(items.Items);

            var slot = FindChild<PrintSlot>(shell)!;

            // Ölçüsüz bir yuva testi sessizce geçirirdi: boş dikdörtgen
            // hiçbir şeyle kesişmez.
            Assert.True(slot.ActualHeight > 0, "Yazdır yuvası ölçüsüz.");

            var drawerBounds = Bounds(host, shell);
            var slotBounds = Bounds(slot, shell);

            Assert.False(drawerBounds.IntersectsWith(slotBounds),
                $"Çekmece yuvayı örtüyor. Çekmece: {drawerBounds}, yuva: {slotBounds}");
            Assert.True(slotBounds.Top >= drawerBounds.Bottom,
                $"Yuva çekmecenin altında değil. Çekmece alt: {drawerBounds.Bottom}, " +
                $"yuva üst: {slotBounds.Top}");
        });

        Assert.Null(error);
    }
}
