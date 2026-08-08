using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests;

/// <summary>
/// Themes/PlatformIcons.xaml sözlüğünün yüklenebildiğini ve dört platform
/// ikonunun da çözülüp boş olmayan boyut verdiğini doğrular.
///
/// NEDEN: YouTube ikonu SVG path verisinden elle taşındı, diğer üçü gömülü
/// PNG kaynağı. Bozuk bir path dizesi ya da kayıp/yanlış adlı bir Resource
/// derlemede değil, uygulama AÇILIRKEN XamlParseException olarak patlar
/// (App.xaml bu sözlüğü merge ediyor) — yani sohbet paneli değil, tüm
/// uygulama açılmaz. Bu test o riski derleme zamanına çeker.
/// </summary>
public class PlatformIconResourcesTests
{
    private static readonly string[] Platforms =
        ["YouTube", "Instagram", "TikTok", "Facebook"];

    [Fact]
    public void All_platform_icons_load_and_have_geometry()
    {
        // Ortak düzenek: STA thread + "pack:" şeması + süreç başına tek
        // Application. Kendi thread'ini kurmak, Application.Current'ı yanlış
        // tiple yaratıp App.xaml testini düşürüyordu.
        var error = OrderDeck.Tests.App.ThemeTestHost.Run(dict =>
        {
            foreach (var name in Platforms)
            {
                // YouTube vektör (DrawingImage), diğerleri gömülü PNG
                // (BitmapImage) — ikisi de ImageSource; ortak sözleşme
                // "çözülebiliyor ve boyutu var".
                var image = Assert.IsAssignableFrom<ImageSource>(
                    dict["OD.PlatformIcon." + name]);
                Assert.True(image.Width > 0 && image.Height > 0, name);
                // Rozet şablonu da çözülebilmeli (MainShellView bunları
                // StaticResource ile bağlıyor).
                Assert.IsType<DataTemplate>(dict["OD.PlatformChip." + name]);
            }

            Assert.IsType<DataTemplate>(dict["OD.PlatformChip.Unknown"]);
        }, "PlatformIcons.xaml");

        Assert.Null(error);
    }
}
