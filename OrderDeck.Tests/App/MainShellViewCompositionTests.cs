using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views;
using OrderDeck.App.Views.Drawers;
using OrderDeck.App.Views.Pages;
using OrderDeck.App.Views.Shell;
using OrderDeck.Core.Shortcuts;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.PdfParsing;
using OrderDeck.Tests.TestHelpers;

// OrderDeck.Core.Sales'te de bir GiveawayDrawer var (çekilişi ÇEKEN sınıf,
// görsel değil) — bu dosya iki isim alanını da açtığı için ayırt edilmeli.
using GiveawayDrawerView = OrderDeck.App.Views.Drawers.GiveawayDrawer;

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
            Assert.IsType<UserControl>(new PageHost(), exactMatch: false);

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
                d => GiveawayDrawerView.Create(d, new AppSettings()));
            Assert.IsType<GiveawayDrawerView>(giveaway.Top!.Content);

            // Dekont zinciri (Faz 2b): üç çekmece AYNI yığına üst üste
            // biniyor — yığının kendisi bunun için yazılmıştı, o yüzden
            // burada gerçekten iç içe kuruluyorlar. En derindeki içerik
            // en son itilen olmalı.
            var dekont = new DrawerStack();
            dekont.ShowAsync("Yeni Dekont",
                d => new DekontEkleDrawer(d, BuildDekontViewModel()));
            Assert.IsType<DekontEkleDrawer>(dekont.Top!.Content);

            var match = new PaymentMatcherService.MatchResult(
                PaymentMatcherService.MatchOutcome.ShippingShortage,
                ProductTotal: 250m, ExpectedAmount: 280m, DekontAmount: 250m,
                ShippingFee: 30m);
            dekont.ShowAsync("Kargo Ücreti Eksik",
                d => new ShipmentDirectiveDrawer(
                        d, new ShipmentDirectiveDialogViewModel(match)));
            Assert.IsType<ShipmentDirectiveDrawer>(dekont.Top!.Content);

            var ctx = new ShipmentDecisionContext(
                Shipment: null, AllLabelsPaid: true, ThresholdReached: true,
                AmountToThreshold: 0m, ShouldPrompt: true);
            dekont.ShowAsync("Ücretsiz Kargo Hakkı",
                d => new ShipmentThresholdDrawer(
                        d, new ShipmentThresholdDialogViewModel(
                                ctx, "instagram/@ayse_y", 500m)));
            Assert.IsType<ShipmentThresholdDrawer>(dekont.Top!.Content);

            // Faz 3 sayfaları. Çekmecelerle aynı gerekçe: XAML hatası
            // derlemede değil açılışta patlar. Sayfa fabrikaları Page
            // istemediği için (AccountPage hariç) doğrudan çağrılıyorlar,
            // ama yığın üzerinden kurulmaları uçtan duman testi de oluyor.
            var pages = new PageStack();

            pages.ShowAsync("blacklist", "Kara Liste",
                _ => BlacklistPage.Create(BuildBlacklistViewModel()));
            Assert.IsType<BlacklistPage>(pages.Top!.Content);

            pages.ShowAsync("history", "Yayın Geçmişi",
                _ => StreamHistoryPage.Create(BuildStreamHistoryViewModel()));
            Assert.IsType<StreamHistoryPage>(pages.Top!.Content);

            pages.ShowAsync("period-report", "Dönem Raporu",
                _ => PeriodReportPage.Create(BuildPeriodReportViewModel()));
            Assert.IsType<PeriodReportPage>(pages.Top!.Content);

            pages.ShowAsync("shortcuts", "Kısayollar",
                _ => ShortcutHelpPage.Create(
                        new ShortcutRegistry(new SettingsStore(TempSettingsPath()))));
            Assert.IsType<ShortcutHelpPage>(pages.Top!.Content);

            // StreamReportPage, AccountPage ve SettingsPage burada YOK.
            // İlkinin fabrikası altı gerçek bağımlılık + bir rapor yüklemesi
            // istiyor, ikincisi lisans/oturum servislerine (ağ + DPAPI) bağlı,
            // üçüncüsünün ViewModel'i ctor'unda dört ağ çağrısı başlatıyor
            // (intake form, shopper kodu, YouTube/Facebook bağlantı durumu) —
            // üçünü de kurmak bu duman testini entegrasyon testine çevirirdi.
            // XAML'leri ayrı gözden geçirildi, davranışları kendi
            // ViewModel testlerinde.

            // Sonra kompozisyon kökü: dokuz parçayı da kendi ağacında kurar.
            var shell = new MainShellView();
            Assert.NotNull(shell.Content);
        });

        Assert.Null(error);
    }

    private static BlacklistViewModel BuildBlacklistViewModel()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);
        var clock = new SystemClock();
        return new BlacklistViewModel(
            customers,
            new CustomerService(customers, new SessionRepository(db), new LabelRepository(db), clock));
    }

    private static StreamHistoryViewModel BuildStreamHistoryViewModel()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new StreamHistoryViewModel(new SessionRepository(db), new LabelRepository(db));
    }

    private static PeriodReportViewModel BuildPeriodReportViewModel()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var path = TempSettingsPath();
        return new PeriodReportViewModel(
            new LabelRepository(db), new AppSettings(), new SettingsStore(path));
    }

    /// <summary>Ayar dosyası testte diske yazılmıyor, ama SettingsStore bir
    /// yol istiyor; her çağrı kendi geçici yolunu alsın ki testler
    /// birbirinin dosyasını okumasın.</summary>
    private static string TempSettingsPath()
        => System.IO.Path.Combine(
               System.IO.Path.GetTempPath(),
               $"orderdeck-composition-{Guid.NewGuid():N}.json");

    /// <summary>Dekont formu on bir bağımlılık istiyor; hepsi gerçek ama
    /// bellekte. Çekmece açılırken hiçbiri kullanılmıyor — burada sınanan
    /// XAML'in çözülmesi, davranışı DekontEkleViewModelTests kapsıyor.</summary>
    private static DekontEkleViewModel BuildDekontViewModel()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var labels = new LabelRepository(db);
        var settings = new AppSettings();
        return new DekontEkleViewModel(
            new PaymentRepository(db),
            new CustomerRepository(db),
            new SessionRepository(db),
            new PaymentMatcherService(labels, () => settings),
            labels,
            new ShipmentService(new ShipmentRepository(db), labels, () => settings, () => 1_000L),
            new PdfDekontParser(),
            settings,
            new SystemClock(),
            NullLogger<DekontEkleViewModel>.Instance);
    }
}
