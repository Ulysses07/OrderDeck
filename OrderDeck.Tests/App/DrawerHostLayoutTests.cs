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
    public void Close_button_closes_the_price_correction_drawer_while_its_reprint_runs()
    {
        // Fiyat düzeltmede yeniden baskı sürerken çarpı pasifti: askıda kalan yazıcıda çekmece kod/fiyat kutusunu
        // ve kuyruğu yayının geri kalanında örtüyordu. Çekmece her zaman kapanır; iş arka planda sürer.
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var db = new OrderDeck.Tests.TestHelpers.InMemorySqlite();
            new OrderDeck.Core.Storage.MigrationRunner(db).Run();
            var clock = new Moq.Mock<OrderDeck.Core.Time.IClock>();
            clock.Setup(c => c.UnixNow()).Returns(1000L);
            var sessions = new OrderDeck.Core.Storage.Repositories.SessionRepository(db);
            sessions.Insert(new OrderDeck.Core.Sessions.StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
            var labels = new OrderDeck.Core.Storage.Repositories.LabelRepository(db);
            var svc = new OrderDeck.Core.Sales.LabelService(labels,
                new OrderDeck.Core.Customers.CustomerService(
                    new OrderDeck.Core.Storage.Repositories.CustomerRepository(db), sessions, labels, clock.Object),
                db, clock.Object);
            var a = svc.Add("s1", new OrderDeck.Core.Chat.ChatMessage(System.Guid.NewGuid().ToString("N"), "instagram",
                null, "@a", "@a", null, "aldım", 1000, System.Array.Empty<string>()), 250m, "A12");
            svc.MarkPrintedAndRecord(new[] { a.Id });
            var gate = new System.Threading.Tasks.TaskCompletionSource<string?>();
            var vm = new OrderDeck.App.ViewModels.PriceCorrectionViewModel(svc, payments: null, "s1", new[] { a.Id },
                System.DateTime.Now, inFlight: () => null,
                applied: _ => { }, reprint: _ => gate.Task, cloudApiEnabled: true);
            var stack = new DrawerStack();
            stack.ShowAsync("Fiyatı Düzelt", d =>
            {
                vm.Close = () => d.Close(true);
                return PriceCorrectionDrawer.Create(vm);
            });
            vm.NewPriceText = "205";
            vm.ConfirmCommand.Execute(null);
            vm.ReprintCommand.Execute(null);              // yazıcıda bekliyor
            Assert.Equal("Yeniden basılıyor…", vm.ReprintResult);
            var host = Arrange(stack);
            var close = FindChild<Button>(host)!;

            Assert.True(close.IsEnabled);
            close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, close));

            Assert.False(stack.IsOpen);
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
