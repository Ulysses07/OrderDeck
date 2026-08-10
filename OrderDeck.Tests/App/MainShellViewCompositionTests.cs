using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Views;
using OrderDeck.App.Views.Drawers;
using OrderDeck.App.Views.Shell;
using OrderDeck.Core.Settings;

namespace OrderDeck.Tests.App;

/// <summary>
/// MainShellView'ün XAML'ı ÇÖZÜLEBİLİYOR mu? XAML hataları derlemede değil
/// çalışma anında XamlParseException olarak patlar — bu test o riski CI'ya
/// çeker. Faz 1'in en pahalı hatası "uygulama hiç açılmıyor" olurdu.
///
/// Tek [Fact]: her Fact kendi STA thread'ini açıyor, sekiz kontrolü tek
/// thread'de örneklemek hem hızlı hem de "süreç başına tek Application"
/// kuralına en az dokunan yol.
/// </summary>
public class MainShellViewCompositionTests
{
    [Fact]
    public void Shell_controls_and_main_shell_resolve()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            // Önce parçalar: biri patlarsa hata mesajı doğrudan onu gösterir.
            Assert.IsType<UserControl>(new ShellSidebar(), exactMatch: false);
            Assert.IsType<UserControl>(new ShellTopBar(), exactMatch: false);
            Assert.IsType<UserControl>(new ShellBanners(), exactMatch: false);
            Assert.IsType<UserControl>(new ActiveProductBar(), exactMatch: false);
            Assert.IsType<UserControl>(new ChatPanel(), exactMatch: false);
            Assert.IsType<UserControl>(new ProductCard(), exactMatch: false);
            Assert.IsType<UserControl>(new PrintQueuePanel(), exactMatch: false);
            Assert.IsType<UserControl>(new PrintSlot(), exactMatch: false);
            Assert.IsType<UserControl>(new DrawerHost(), exactMatch: false);

            // MessageDrawer yalnız yığın üzerinden kurulabiliyor (Drawer'ın
            // ctor'u internal). Aynı zamanda altyapının uçtan duman testi:
            // fabrika → çekmece → içerik zinciri gerçekten kuruluyor mu?
            var stack = new DrawerStack();
            stack.ShowAsync("Onay", d => MessageDrawer.ForConfirm(d, "Emin misin?"));
            Assert.IsType<MessageDrawer>(stack.Top!.Content);

            // Çekiliş çekmecesi (Faz 2b) de aynı yoldan kuruluyor. Katalog
            // istemcisi VERİLMİYOR: test ağa çıkmasın, animasyon listesi boş
            // kalsın — burada sınanan, XAML'in çözülmesi.
            var giveaway = new DrawerStack();
            giveaway.ShowAsync("Yeni Çekiliş",
                d => GiveawayDrawer.Create(d, new AppSettings()));
            Assert.IsType<GiveawayDrawer>(giveaway.Top!.Content);

            // Sonra kompozisyon kökü: sekiz parçayı da kendi ağacında kurar.
            var shell = new MainShellView();
            Assert.NotNull(shell.Content);
        });

        Assert.Null(error);
    }
}
