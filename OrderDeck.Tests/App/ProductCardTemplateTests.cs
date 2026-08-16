using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Shell;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kartın XAML'i GERÇEKTEN çiziliyor mu? Üç şey ölçülüyor: varyant rozeti
/// şablonu açılıyor mu, üç durumdan hangisinin görünür olduğu, ve alçak
/// pencerede fotoğrafın kısalması.
///
/// NEDEN ayrı bir test: <c>MainShellViewCompositionTests</c> kartı
/// <c>new ProductCard()</c> ile kuruyor ve bu, kökteki StaticResource'ları
/// çözüyor — ama <c>DataTemplate</c> içindekileri ÇÖZMÜYOR. Ölçüldü: şablonun
/// içindeki anahtarı bozup kompozisyon testi koşturulduğunda test YEŞİL
/// kalıyor, çünkü şablon ancak bir öğe materyalize olurken açılıyor. Yani
/// rozetteki yanlış bir anahtar CI'dan geçer ve operatör ilk ürünü yükleyince
/// XamlParseException olarak patlardı — kartın en sık görülen hâlinde.
///
/// Bu yüzden burada kart gerçekten yerleşiyor: şablon açılır, anahtarları
/// çözülür, görünürlük tetikleyicileri koşar.
/// </summary>
public class ProductCardTemplateTests
{
    /// <summary>Kartın kökündeki Border <c>{Binding ProductCard}</c> diyor.</summary>
    private sealed class ShellStub
    {
        public ShellStub(ProductCardViewModel card, bool isShort = false)
        {
            ProductCard = card;
            IsShort = isShort;
        }

        public ProductCardViewModel ProductCard { get; }

        /// <summary>
        /// Fotoğraf yüksekliğinin <c>DataTrigger</c>'ı bunu okuyor; her iki
        /// değeriyle de yerleşim koşuluyor
        /// (<see cref="A_short_window_shortens_the_product_photo"/>).
        /// </summary>
        public bool IsShort { get; }
    }

    [Fact]
    public void Variant_chip_template_renders_for_a_loaded_product()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var card = Lay(Seed, "Ateş");

            var texts = new List<string>();
            CollectVisible(card, texts);
            Assert.Contains("Kırmızı · M", texts);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// Üç bölüm (kod yok · katalogda yok · ürün) TEK bir <c>Grid</c>'i
    /// paylaşıyor: yanlış bağlanan bir görünürlük onları üst üste bindirir.
    /// Ölçüldü — "katalogda yok" bloğunun bağı <c>IsUnknown</c> yerine
    /// <c>HasProduct</c> yapıldığında geri kalan bütün paket yeşil kalıyor,
    /// oysa ekranda uyarı yazısı dolu ürünün üstüne biniyor.
    /// </summary>
    [Fact]
    public void Only_one_of_the_three_sections_is_visible_at_a_time()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var unknown = new List<string>();
            CollectVisible(Lay(Seed, "YOKBOYLEKOD"), unknown);
            Assert.Contains(unknown, t => t.Contains("katalogda yok"));
            Assert.DoesNotContain(unknown, t => t.Contains("Güzel Elbise"));

            var loaded = new List<string>();
            CollectVisible(Lay(Seed, "Ateş"), loaded);
            Assert.Contains(loaded, t => t.Contains("Güzel Elbise"));
            Assert.DoesNotContain(loaded, t => t.Contains("katalogda yok"));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// 850px altındaki pencerede fotoğraf kısalır ki varyant listesi ekranda
    /// kalsın. Tetikleyicinin bağ yolu uzun ve kırılgan (ata <c>UserControl</c>
    /// üstünden <c>DataContext.IsShort</c>); kırıldığında hiçbir istisna
    /// atmaz, yalnız yükseklik sabit kalır — operatör küçük ekranda varyantları
    /// göremez ve bunu kimse fark etmez.
    /// </summary>
    [Fact]
    public void A_short_window_shortens_the_product_photo()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var tall = (double)Application.Current.Resources["OD.Layout.ProductImageHeight"];
            var shortened =
                (double)Application.Current.Resources["OD.Layout.ProductImageHeightShort"];
            Assert.NotEqual(tall, shortened);

            Assert.Equal(tall, PhotoHeight(Lay(Seed, "Ateş")));
            Assert.Equal(shortened, PhotoHeight(Lay(Seed, "Ateş", isShort: true)));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// Değer ve sayı AYRI satırlarda. Tek satırda ("Kırmızı · M · 3") uzun
    /// eksen değerlerinde <c>TextTrimming</c> önce sayıyı yerdi — yani
    /// operatörün tam da bakmak istediği şeyi.
    /// </summary>
    [Fact]
    public void Variant_chip_shows_the_value_and_the_quantity()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var texts = new List<string>();
            CollectVisible(Lay(Seed, "Ateş", stock: Qty("v1", 3)), texts);

            Assert.Contains("Kırmızı · M", texts);
            Assert.Contains("3", texts);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// 0 sönük (bilgi — satış engellenmiyor), eksi accent (tema kuralı:
    /// "Danger ayrı renk değil, Accent'in kendisi"). Fırçalar kaynak
    /// sözlüğünden okunuyor: sabit renk yazmak, tema değiştiğinde testi
    /// sessizce yalancı yapardı.
    /// </summary>
    [Fact]
    public void Zero_is_dimmed_and_negative_is_accented()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var dim = Application.Current.Resources["OD.Brush.TextDim"];
            var accent = Application.Current.Resources["OD.Brush.Accent"];
            var normal = Application.Current.Resources["OD.Brush.Text"];

            // Bakiye tohumlanmazsa varyantın sayısı 0: eksik satır = 0 adet.
            Assert.Same(dim, FindText(Lay(Seed, "Ateş"), "0").Foreground);
            Assert.Same(accent,
                FindText(Lay(Seed, "Ateş", stock: Qty("v1", -2)), "-2").Foreground);
            Assert.Same(normal,
                FindText(Lay(Seed, "Ateş", stock: Qty("v1", 5)), "5").Foreground);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// Varyantsız bakiye satırı yalnız sıfırdan farklıysa çiziliyor: her
    /// eksenli üründe "Varyantsız: 0" yazmak kartı gürültüye boğardı.
    /// </summary>
    [Fact]
    public void Product_level_line_appears_only_when_non_zero()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var none = new List<string>();
            CollectVisible(Lay(Seed, "Ateş", stock: Qty(null, 0)), none);
            Assert.DoesNotContain(none, t => t.Contains("Varyantsız"));

            var shown = new List<string>();
            CollectVisible(Lay(Seed, "Ateş", stock: Qty(null, -2)), shown);
            Assert.Contains(shown, t => t.Contains("Varyantsız") && t.Contains("2"));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// Bir ürün + bir aktif varyant + bir YAYIN KODU. Kod kutusu stok kodunu
    /// aramıyor, kart ürüne ancak "Ateş" ile ulaşıyor.
    /// </summary>
    private static void Seed(CatalogReplicaRepository repo)
        => repo.Replace(
            [new CatalogProduct("p1", null, "SK00001", SearchNormalizer.Normalize("SK00001"),
                                "Güzel Elbise", 199.90m, null, "Renk", 1, "Beden", 2,
                                null, 1_700_000_000)],
            [new CatalogVariant("v1", "p1", "Kırmızı", "M", null, true, 0)],
            [],
            [new CatalogBroadcastCode("p1", "Kırmızı", "Ateş",
                                      SearchNormalizer.Normalize("Ateş"), 1_700_000_000, 0)]);

    /// <summary>Kartı verilen kodla gerçekten yerleştirir.</summary>
    private static ProductCard Lay(
        Action<CatalogReplicaRepository> seed, string code, bool isShort = false,
        Action<StockBalanceRepository>? stock = null)
    {
        // Yerleşim depoya dokunmuyor: Load ne gerekiyorsa çoktan okudu —
        // bakiyeler dahil (RefreshBalances, Load'un son adımı).
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CatalogReplicaRepository(db);
        seed(repo);

        var balances = new StockBalanceRepository(db);
        stock?.Invoke(balances);

        var vm = new ProductCardViewModel(
            new BroadcastCodeResolver(repo),
            new CatalogPhotoCache(
                Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))),
            new StockBalanceProvider(balances, new LabelRepository(db)));
        vm.Load(code);

        var card = new ProductCard { DataContext = new ShellStub(vm, isShort) };
        card.Measure(new Size(320, 640));
        card.Arrange(new Rect(0, 0, 320, 640));
        card.UpdateLayout();
        // Bağ güncellemesi kuyruğa giriyor; boşaltmadan ItemsSource null
        // kalıyor ve şablon HİÇ açılmıyor — test hiçbir şey doğrulamadan
        // geçerdi (bkz. ThemeTestHost.Pump).
        ThemeTestHost.Pump();
        card.UpdateLayout();
        return card;
    }

    /// <summary>
    /// Tek satırlık sunucu bakiyesi. <c>variantId</c> null verilirse ürün
    /// seviyesine yazar — varyanta bağlanmamış hareketler ayrı kovada toplanıyor.
    /// </summary>
    private static Action<StockBalanceRepository> Qty(string? variantId, int quantity)
        => repo => repo.ApplyPage(
            [new CatalogStockBalance("p1", variantId, quantity)],
            new StockCursor(DateTimeOffset.UnixEpoch, Guid.Empty));

    private static TextBlock? TryFindText(DependencyObject root, string text)
    {
        if (root is TextBlock tb && tb.Text == text) return tb;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (TryFindText(VisualTreeHelper.GetChild(root, i), text) is { } hit)
                return hit;

        return null;
    }

    /// <summary>
    /// Verilen metni taşıyan <c>TextBlock</c>. Rozetteki sayının RENGİNİ
    /// okumak için gerekiyor: <c>CollectVisible</c> yalnız metni topluyor,
    /// fırçayı görmüyor.
    /// </summary>
    private static TextBlock FindText(DependencyObject root, string text)
        => TryFindText(root, text)
           ?? throw new InvalidOperationException(
               $"'{text}' metinli TextBlock görsel ağaçta yok — rozet çizilmemiş.");

    /// <summary>Fotoğrafı taşıyan <c>Border</c>'ın yerleşim sonrası yüksekliği.</summary>
    private static double PhotoHeight(DependencyObject root)
    {
        var image = Find<Image>(root) ?? throw new InvalidOperationException(
            "Ürün fotoğrafı görsel ağaçta yok — kart dolu durumda yerleşmemiş.");

        for (DependencyObject? cur = VisualTreeHelper.GetParent(image);
             cur is not null;
             cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is Border border) return border.ActualHeight;
        }

        throw new InvalidOperationException("Fotoğrafın Border'ı bulunamadı.");
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (Find<T>(child) is { } deeper) return deeper;
        }

        return null;
    }

    /// <summary>
    /// Yalnız EKRANDA duran metinleri toplar. <c>Visibility="Collapsed"</c>
    /// bir öğe görsel ağaçtan silinmiyor — düz metin taraması üç bölümü de
    /// bulur ve görünürlükle ilgili hiçbir şey ölçmezdi.
    /// </summary>
    private static void CollectVisible(DependencyObject root, List<string> texts)
    {
        if (root is UIElement { Visibility: not Visibility.Visible }) return;

        if (root is TextBlock tb) texts.Add(tb.Text);

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            CollectVisible(VisualTreeHelper.GetChild(root, i), texts);
    }
}
