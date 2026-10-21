using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrderDeck.App.Views.Shell;

namespace OrderDeck.Tests.App;

/// <summary>
/// Sol menüde ödeme çekmecesinin satırı "Gelen Ödemeler" (2026-10-19'a kadar "Yeni Dekont"). Komut değişmedi
/// (<c>OpenDekontEkleCommand</c>); metin, ipucu ve açılan çekmecenin başlığı yeni ad.
/// </summary>
public class ShellSidebarGelenOdemelerTests
{
    [Fact]
    public void Odeme_cekmecesinin_menu_satiri_Gelen_Odemeler_der()
    {
        string? label = null, toolTip = null;
        var labels = new List<string>();
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var sidebar = new ShellSidebar();
            var button = Descendants(sidebar).OfType<Button>().Single(b =>
                BindingOperations.GetBinding(b, System.Windows.Controls.Primitives.ButtonBase.CommandProperty)?.Path.Path
                    == "OpenDekontEkleCommand");
            toolTip = button.ToolTip as string;
            label = Descendants(button).OfType<TextBlock>().Single().Text;
            labels.AddRange(Descendants(sidebar).OfType<TextBlock>().Select(t => t.Text ?? ""));
        });

        Assert.Null(error);
        Assert.Equal("Gelen Ödemeler", label);
        Assert.Equal("Gelen Ödemeler", toolTip);
        Assert.DoesNotContain("Yeni Dekont", labels);
        // Açılan çekmecenin başlığı da aynı ad: kabuk başlığı bu sabitten verir. Komutun kendisi App.Host istediği
        // için testte koşturulamıyor; bağ sabit üzerinden.
        Assert.Equal(OrderDeck.App.ViewModels.DekontEkleViewModel.DrawerTitle, label);
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
}
