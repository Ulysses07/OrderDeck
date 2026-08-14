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
/// Varyant rozeti şablonu GERÇEKTEN çiziliyor mu?
///
/// NEDEN ayrı bir test: <c>MainShellViewCompositionTests</c> kartı
/// <c>new ProductCard()</c> ile kuruyor ve bu, kökteki StaticResource'ları
/// çözüyor — ama <c>DataTemplate</c> içindekileri ÇÖZMÜYOR. Ölçüldü: şablonun
/// içindeki anahtarı bozup kompozisyon testi koşturulduğunda test YEŞİL
/// kalıyor, çünkü şablon ancak bir öğe materyalize olurken açılıyor. Yani
/// rozetteki yanlış bir anahtar CI'dan geçer ve operatör ilk ürünü yükleyince
/// XamlParseException olarak patlardı — kartın en sık görülen hâlinde.
///
/// Bu yüzden burada kart gerçekten yerleşiyor, dolu bir ürünle: şablon açılır,
/// anahtarları çözülür.
/// </summary>
public class ProductCardTemplateTests
{
    /// <summary>Kartın kökündeki Border <c>{Binding ProductCard}</c> diyor.</summary>
    private sealed class ShellStub
    {
        public ShellStub(ProductCardViewModel card) => ProductCard = card;

        public ProductCardViewModel ProductCard { get; }

        /// <summary>Fotoğraf yüksekliği tetikleyicisi bunu okuyor.</summary>
        public bool IsShort => false;
    }

    [Fact]
    public void Variant_chip_template_renders_for_a_loaded_product()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            using var db = new InMemorySqlite();
            new MigrationRunner(db).Run();
            var repo = new CatalogReplicaRepository(db);
            repo.Replace(
                [new CatalogProduct("p1", null, "A1", SearchNormalizer.Normalize("A1"),
                                    "Güzel Elbise", 199.90m, null, "Renk", 1, "Beden", 2,
                                    null, 1_700_000_000)],
                [new CatalogVariant("v1", "p1", "Kırmızı", "KIRM", "M", "M",
                                    "A1-KIRM-M", null, true, 0)],
                []);

            var vm = new ProductCardViewModel(
                repo,
                new CatalogPhotoCache(
                    Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))));
            vm.Load("A1");

            var card = new ProductCard { DataContext = new ShellStub(vm) };
            card.Measure(new Size(320, 640));
            card.Arrange(new Rect(0, 0, 320, 640));
            card.UpdateLayout();
            // Bağ güncellemesi kuyruğa giriyor; boşaltmadan ItemsSource null
            // kalıyor ve şablon HİÇ açılmıyor — test hiçbir şey doğrulamadan
            // geçerdi (bkz. ThemeTestHost.Pump).
            ThemeTestHost.Pump();
            card.UpdateLayout();

            var texts = new List<string>();
            Collect(card, texts);
            Assert.Contains("Kırmızı · M", texts);
        });

        Assert.Null(error);
    }

    private static void Collect(DependencyObject root, List<string> texts)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb) texts.Add(tb.Text);
            Collect(child, texts);
        }
    }
}
