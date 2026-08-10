using System.Net.Http;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Api;
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

            // Müşteri arama (Faz 3d). Kendi kapatma düğmesi olmadığı için
            // fabrikası Drawer istemiyor, ama yığın üzerinden kuruluyor:
            // gerçek çağrı yolu bu.
            var search = new DrawerStack();
            search.ShowAsync("Müşteriler",
                _ => CustomerSearchDrawer.Create(BuildCustomerSearchViewModel()));
            Assert.IsType<CustomerSearchDrawer>(search.Top!.Content);

            // Müşteri detay zinciri (Faz 3d): arama → detay → iptal sebebi.
            // Aynı yığına biniyorlar, gerçek çağrı yolu bu. Detay çekmecesi
            // veri YÜKLEMİYOR (Load'u çağıran taraf yapar), o yüzden boş
            // ViewModel'le kuruluyor — sınanan XAML'in çözülmesi.
            search.ShowAsync("Müşteri Detayı",
                _ => CustomerDetailDrawer.Create(BuildCustomerDetailViewModel()));
            Assert.IsType<CustomerDetailDrawer>(search.Top!.Content);

            search.ShowAsync("Etiket İptali", d => CancelLabelDrawer.Create(d));
            Assert.IsType<CancelLabelDrawer>(search.Top!.Content);

            // Bakiye çekmecesi ayrı yığında: detay zincirinin kardeşi, aynı
            // anda ikisi birden açılmıyor.
            var balance = new DrawerStack();
            balance.ShowAsync("Bakiye Ekle",
                d => AddBalanceDrawer.Create(d, BuildLicenseApi(), Guid.NewGuid(), "Alice"));
            Assert.IsType<AddBalanceDrawer>(balance.Top!.Content);

            // Faz 3d/3'ün üçlüsü. Birbirinin üstüne binmiyorlar, ayrı
            // yığınlar: amaç sıralama değil, her XAML'in çözülebildiğini
            // görmek.
            var phone = new DrawerStack();
            phone.ShowAsync("WhatsApp Numarası Gerekli",
                d => PhoneEntryDrawer.Create(d, BuildCustomerRepository(), "c1"));
            Assert.IsType<PhoneEntryDrawer>(phone.Top!.Content);

            var blacklistDrawer = new DrawerStack();
            blacklistDrawer.ShowAsync("Kara Listeye Al",
                d => AddToBlacklistDrawer.Create(
                    d, AddToBlacklistDrawer.DrawerMode.Prefilled, "instagram", "alice"));
            Assert.IsType<AddToBlacklistDrawer>(blacklistDrawer.Top!.Content);

            var pagePicker = new DrawerStack();
            pagePicker.ShowAsync("Facebook Sayfası Seç",
                d => FacebookPagePickerDrawer.Create(d, BuildPageCandidates()));
            Assert.IsType<FacebookPagePickerDrawer>(pagePicker.Top!.Content);

            // Faz 3d/4: yedek devri. Gerçekte iptal çekmecesinin üstüne
            // biniyor; burada tek başına, iki yedekle kuruluyor ki kart
            // şablonu (platform ikonu + fiyat + onay) gerçekten çizilsin.
            var backups = new DrawerStack();
            backups.ShowAsync("'alice' yedekleri (2)",
                d => BuildBackupTransferDrawer(d));
            Assert.IsType<BackupTransferDrawer>(backups.Top!.Content);

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

            // Faz 3c'nin iki sayfası. ViewModel'leri lisans sunucusu
            // istemcisine bağlı ama ctor'ları SALT ATAMA — yükleme ayrı bir
            // LoadAsync() ve onu burada çağırmıyoruz, yani ağa çıkılmıyor.
            pages.ShowAsync("support-requests", "Destek Talepleri",
                _ => SupportRequestsPage.Create(BuildSupportRequestsViewModel()));
            Assert.IsType<SupportRequestsPage>(pages.Top!.Content);

            pages.ShowAsync("bulk-sms", "Toplu SMS",
                _ => BulkSmsPage.Create(BuildBulkSmsViewModel()));
            Assert.IsType<BulkSmsPage>(pages.Top!.Content);

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

    /// <summary>Ödeme servisi ve diyalog servisi VERİLMİYOR: ikisi de yalnız
    /// "Öde" düğmesine basılınca kullanılıyor, bu test hiçbir komut
    /// tetiklemiyor. Gerçeklerini kurmak testi ağa ve DPAPI'ye bağlardı.</summary>
    private static CustomerSearchViewModel BuildCustomerSearchViewModel()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        return new CustomerSearchViewModel(
            customers,
            new CustomerService(customers, sessions, labels, new SystemClock()),
            sessions, labels,
            paymentService: null!, dialogService: null!);
    }

    /// <summary>Çekmece servisi VERİLMİYOR (varsayılan null): bakiye ekleme ve
    /// etiket iptali komutları bu testte tetiklenmiyor, ikisi de null görünce
    /// erken dönüyor.</summary>
    private static CustomerDetailViewModel BuildCustomerDetailViewModel()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        var clock = new SystemClock();
        return new CustomerDetailViewModel(
            customers, labels,
            new LabelService(labels, new CustomerService(customers, sessions, labels, clock), clock),
            new GiveawayRepository(db),
            new StreamSessionService(sessions, clock),
            BuildLicenseApi());
    }

    /// <summary>Yedek devri çekmecesi: iptal edilen ana etiket + iki yedek.
    /// Kayıtlar veritabanına yazılmıyor — çekmece listeyi çağırandan hazır
    /// alıyor, burada sınanan yalnız kart şablonunun çözülmesi.</summary>
    private static BackupTransferDrawer BuildBackupTransferDrawer(Drawer drawer)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var labels = new LabelRepository(db);
        var sessions = new SessionRepository(db);
        var customers = new CustomerRepository(db);
        var clock = new SystemClock();
        var service = new LabelService(
            labels, new CustomerService(customers, sessions, labels, clock), clock);

        var parent = MakeLabel("l1", "instagram", "alice", "ALC-1", 250m);
        var backups = new[]
        {
            MakeLabel("l2", "tiktok", "ayse_y", "ALC-1", 250m, isBackup: true),
            MakeLabel("l3", "youtube", "mehmet", "ALC-1", 275m, isBackup: true),
        };
        return BackupTransferDrawer.Create(drawer, service, parent, backups);
    }

    private static OrderDeck.Core.Sales.Label MakeLabel(
        string id, string platform, string username, string code, decimal price,
        bool isBackup = false)
        => new(id, "s1", "c1", platform, username, $"{code} alıyorum", code,
               price, 1_754_300_000, PrintedAt: null,
               ParentLabelId: isBackup ? "l1" : null,
               IsTentativeBackup: isBackup);

    private static CustomerRepository BuildCustomerRepository()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new CustomerRepository(db);
    }

    /// <summary>Sayfa seçici yalnız BİRDEN FAZLA sayfa varken açılıyor; iki
    /// aday veriyoruz ki liste şablonu gerçekten çizilsin.</summary>
    private static IReadOnlyList<OrderDeck.Chat.Facebook.FacebookPageCandidate> BuildPageCandidates()
        => new[]
        {
            new OrderDeck.Chat.Facebook.FacebookPageCandidate("p1", "Emar Mezat"),
            new OrderDeck.Chat.Facebook.FacebookPageCandidate("p2", "Emar Outlet"),
        };

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

    private static SupportRequestsViewModel BuildSupportRequestsViewModel()
        => new(BuildLicenseApi(), new WhatsAppMessageBuilder(), new ProcessUrlLauncher());

    private static BulkSmsViewModel BuildBulkSmsViewModel()
        => new(BuildLicenseApi(), new StubLicenseProvider());

    /// <summary>Gerçek istemci ama HİÇ istek atılmıyor: iki sayfanın da
    /// yükleme çağrısı ayrı bir <c>LoadAsync()</c> ve bu test onu çağırmıyor.
    /// Yine de BaseAddress veriliyor — istemci onsuz kurulmuş sayılmaz.</summary>
    private static LicenseApiClient BuildLicenseApi()
        => new(new HttpClient { BaseAddress = new Uri("http://localhost/") },
               new LicenseTokenStore());

    private sealed class StubLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => null;
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
