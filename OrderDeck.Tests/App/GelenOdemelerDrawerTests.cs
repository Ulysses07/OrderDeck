using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Drawers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>
/// "Gelen Ödemeler" formu (<see cref="DekontEkleDrawer"/>; 2026-10-19'a kadar "Yeni Dekont"): operatörün gördüğü
/// metinler. Müşteri zorunlu, ama boşken Kaydet yalnız pasifleşir ve satır içi hata hiç görünmez; bu yüzden zorunluluğu
/// formun kendisi söylemeli. Dekont kaydı kalktığı için formda "dekont" geçmez.
/// </summary>
public class GelenOdemelerDrawerTests
{
    [Fact]
    public void Form_musterinin_zorunlu_oldugunu_soyler_dekont_demez()
    {
        var texts = new List<string>();
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var stack = new DrawerStack();
            stack.ShowAsync("Gelen Ödemeler", d => new DekontEkleDrawer(d, BuildViewModel()));
            var drawer = Assert.IsType<DekontEkleDrawer>(stack.Top!.Content);
            texts.AddRange(Descendants(drawer).OfType<TextBlock>().Select(t => t.Text ?? ""));
        });

        Assert.Null(error);
        Assert.Contains("MÜŞTERİ (ZORUNLU)", texts);
        Assert.DoesNotContain(texts, t => t.Contains("dekont", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mantıksal ağaç: şablon uygulanmadan (yerleşim yok) içerik çocuklarını da verir.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>Bağımlılıklar gerçek ama bellekte; çekmece açılırken hiçbiri kullanılmıyor.</summary>
    private static DekontEkleViewModel BuildViewModel()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var labels = new LabelRepository(db);
        var shipping = new ShippingSettingsProvider(new SalesSyncStateRepository(db), new AppSettings());
        return new DekontEkleViewModel(
            new CustomerRepository(db),
            new SessionRepository(db),
            new PaymentMatcherService(labels, shipping),
            new ShipmentService(new ShipmentRepository(db), labels, shipping, () => 1_000L),
            shipping,
            NullLogger<DekontEkleViewModel>.Instance);
    }
}
