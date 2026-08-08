using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// Themes/Colors.xaml renk sözleşmesi.
///
/// NEDEN: Palet tek doğruluk kaynağı web/app/globals.css'ten hizalandı.
/// Bir fırçanın değeri sessizce kayarsa masaüstü ile canlı site arasında
/// yeniden ayrışma başlar — bugünkü 120 rastgele rengin oluşma biçimi buydu.
/// Test değerleri çivileyerek o kaymayı derleme zamanına çeker.
/// </summary>
public class ThemeColorsTests
{
    private static readonly (string Key, string Hex)[] Expected =
    [
        ("OD.Brush.Bg",           "#FF090A0E"),
        ("OD.Brush.Surface",      "#FF0F111A"),
        ("OD.Brush.Surface2",     "#FF161A26"),
        ("OD.Brush.Border",       "#12FFFFFF"),
        ("OD.Brush.BorderStrong", "#21FFFFFF"),
        ("OD.Brush.Text",         "#FFF4F2EC"),
        ("OD.Brush.TextDim",      "#FFA6ACBA"),
        ("OD.Brush.TextMute",     "#FF868C9C"),
        ("OD.Brush.Accent",       "#FFFF4A38"),
        ("OD.Brush.AccentHot",    "#FFFF6A5A"),
        ("OD.Brush.AccentDeep",   "#FFE23A2A"),
        ("OD.Brush.AccentInk",    "#FF180603"),
        ("OD.Brush.Amber",        "#FFFFB23E"),
        ("OD.Brush.Success",      "#FF2DD06F"),
        ("OD.Brush.Info",         "#FF4D8DF6"),
        ("OD.Brush.OnAccent",     "#FFFFFFFF"),
    ];

    [Fact]
    public void All_brushes_resolve_with_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, hex) in Expected)
            {
                var brush = Assert.IsType<SolidColorBrush>(dict[key]);
                Assert.Equal(hex, brush.Color.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(brush.IsFrozen, key + " dondurulmalı");
            }
        }, "Colors.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Dictionary_has_no_extra_keys()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            // Palet kapalı bir küme. Yeni renk eklemek spec'i güncellemeyi
            // gerektirir; bu test onu unutturmaz.
            Assert.Equal(Expected.Length, dict.Count);
        }, "Colors.xaml");

        Assert.Null(error);
    }
}
