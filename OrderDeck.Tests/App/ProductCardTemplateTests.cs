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
            var card = Lay(Seed, "A1");

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
            CollectVisible(Lay(Seed, "A1"), loaded);
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

            Assert.Equal(tall, PhotoHeight(Lay(Seed, "A1")));
            Assert.Equal(shortened, PhotoHeight(Lay(Seed, "A1", isShort: true)));
        });

        Assert.Null(error);
    }

    /// <summary>Bir ürün + bir aktif varyantlı replika.</summary>
    private static void Seed(CatalogReplicaRepository repo)
        => repo.Replace(
            [new CatalogProduct("p1", null, "A1", SearchNormalizer.Normalize("A1"),
                                "Güzel Elbise", 199.90m, null, "Renk", 1, "Beden", 2,
                                null, 1_700_000_000)],
            [new CatalogVariant("v1", "p1", "Kırmızı", "M", null, true, 0)],
            [], []);

    /// <summary>Kartı verilen kodla gerçekten yerleştirir.</summary>
    private static ProductCard Lay(
        Action<CatalogReplicaRepository> seed, string code, bool isShort = false)
    {
        // Yerleşim depoya dokunmuyor: Load ne gerekiyorsa çoktan okudu.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CatalogReplicaRepository(db);
        seed(repo);

        var vm = new ProductCardViewModel(
            repo,
            new CatalogPhotoCache(
                Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))));
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
