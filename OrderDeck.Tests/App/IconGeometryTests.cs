using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// Icons.xaml'daki tek renkli ikonların gerçekten bir şey çizdiğini doğrular.
///
/// NEDEN: bozuk bir Path mini-dili hemen istisna atar, o kadarını kompozisyon
/// dumanı testi zaten yakalıyor. Asıl sinsi hata bunun bir adım ötesi —
/// GEÇERLİ ama BOŞ ya da kutunun dışına taşan bir geometri. O durumda uygulama
/// açılır, ikon yerinde hiçbir şey görünmez ve kimse fark etmez.
/// </summary>
public class IconGeometryTests
{
    private static readonly string[] IconKeys =
    [
        "OD.Path.History", "OD.Path.Customers", "OD.Path.Blacklist",
        "OD.Path.Receipt", "OD.Path.Message", "OD.Path.Bell",
        "OD.Path.Eye", "OD.Path.More", "OD.Path.Close",
        "OD.Path.Calendar", "OD.Path.ChevronLeft", "OD.Path.ChevronRight",
    ];

    [Fact]
    public void Every_icon_draws_inside_its_24x24_box()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            foreach (var key in IconKeys)
            {
                var geometry = Assert.IsAssignableFrom<Geometry>(
                    Application.Current!.Resources[key]);

                var b = geometry.Bounds;

                // Boş geometri: Rect.Empty ya da sıfır alan.
                Assert.False(b.IsEmpty, $"{key} boş çizim üretti.");
                Assert.True(b.Width > 0 && b.Height > 0,
                    $"{key} tek boyutlu çizdi: {b.Width}x{b.Height}");

                // Uzun kenar ölçüt: bazı ikonlar meşru biçimde YASSI olur
                // (üç nokta 16x4). İkisine birden alt sınır koymak bunları
                // yanlışlıkla reddediyordu.
                Assert.True(Math.Max(b.Width, b.Height) >= 8,
                    $"{key} çok küçük çizdi: {b.Width}x{b.Height}");

                // 24x24 kutunun dışına taşarsa Stretch=Uniform ikonu küçültür
                // ve kardeşleriyle aynı ölçüde görünmez.
                Assert.True(b.Left >= -0.01 && b.Top >= -0.01
                            && b.Right <= 24.01 && b.Bottom <= 24.01,
                    $"{key} 24x24 kutusunu aştı: {b}");
            }
        });

        Assert.Null(error);
    }
}
