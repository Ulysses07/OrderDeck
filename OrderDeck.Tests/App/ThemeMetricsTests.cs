using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// Themes/Metrics.xaml ölçü sözleşmesi — tip, boşluk, dolgu, yarıçap, ikon,
/// düzen sabitleri ve font aileleri.
///
/// NEDEN: Uygulamada bugün 18 farklı FontSize var. Ölçeği 6 basamağa
/// indirmenin tek koruması, ölçeğin kendisinin test edilmesi.
/// </summary>
public class ThemeMetricsTests
{
    private static readonly (string Key, double Value)[] Doubles =
    [
        ("OD.Font.F0", 11),  ("OD.Font.F1", 12.5), ("OD.Font.F2", 14),
        ("OD.Font.F3", 20),  ("OD.Font.F4", 32),   ("OD.Font.F5", 64),

        ("OD.Space.1", 2),   ("OD.Space.2", 4),    ("OD.Space.3", 8),
        ("OD.Space.4", 12),  ("OD.Space.5", 16),   ("OD.Space.6", 20),
        ("OD.Space.7", 24),

        ("OD.Icon.Sm", 14),  ("OD.Icon.Md", 16),   ("OD.Icon.Lg", 20),
        ("OD.Icon.Xl", 26),

        ("OD.Layout.SideWidth",       224),
        ("OD.Layout.SideWidthMin",     64),
        ("OD.Layout.RightWidth",      344),
        ("OD.Layout.DrawerWidth",     344),
        ("OD.Layout.TopbarHeight",     56),
        ("OD.Layout.ButtonHeight",     46),
        ("OD.Layout.ContentMaxWidth",1760),
        ("OD.Layout.AppMinWidth",     960),
        ("OD.Layout.AppMinHeight",    720),
    ];

    private static readonly (string Key, double Uniform)[] Pads =
    [
        ("OD.Pad.1", 2), ("OD.Pad.2", 4),  ("OD.Pad.3", 8),  ("OD.Pad.4", 12),
        ("OD.Pad.5", 16), ("OD.Pad.6", 20), ("OD.Pad.7", 24),
    ];

    private static readonly (string Key, double Radius)[] Radii =
    [
        ("OD.Radius.Xs", 4), ("OD.Radius.Sm", 6), ("OD.Radius.Md", 8),
        ("OD.Radius.Lg", 10), ("OD.Radius.Full", 999),
    ];

    // Aile adları ölçüldü (Fonts.GetFontFamilies, repo'daki gerçek .ttf'ler).
    // Bricolage'ın gerçek adı "Bricolage Grotesque 14pt"; kısaltılmış hâli
    // kısmi eşleşme olup sentezlenmiş bir ağırlık daha döndürüyor.
    private static readonly (string Key, string Face)[] Fonts =
    [
        ("OD.Font.Sans",    "IBM Plex Sans"),
        ("OD.Font.Mono",    "JetBrains Mono"),
        ("OD.Font.Display", "Bricolage Grotesque 14pt"),
    ];

    [Fact]
    public void Scalar_tokens_have_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, value) in Doubles)
                Assert.Equal(value, Assert.IsType<double>(dict[key]));
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Padding_tokens_mirror_the_spacing_scale()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, uniform) in Pads)
            {
                var t = Assert.IsType<Thickness>(dict[key]);
                Assert.Equal(new Thickness(uniform), t);
            }
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Radius_tokens_have_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, r) in Radii)
                Assert.Equal(new CornerRadius(r), Assert.IsType<CornerRadius>(dict[key]));
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    /// <summary>
    /// Her ailenin BEKLENEN AĞIRLIKLARI sunduğunu doğrular.
    ///
    /// NEDEN sadece "çözülüyor mu" yetmiyor: IBM Plex Sans'ın Medium ve
    /// SemiBold dosyalarında eski aile adı (name ID 1) "IBM Plex Sans Medm" /
    /// "IBM Plex Sans SmBld"; tek aileye ancak tipografik aile adı (ID 16)
    /// üzerinden katılıyorlar. WPF bunu doğru yapıyor (ölçüldü), ama font
    /// dosyalarından biri eksik kalırsa aile yine ÇÖZÜLÜR — yalnız o ağırlık
    /// sessizce en yakınına düşer. Ağırlık listesi bunu yakalar.
    /// </summary>
    [Fact]
    public void Font_families_expose_expected_weights()
    {
        var expectedWeights = new Dictionary<string, int[]>
        {
            ["OD.Font.Sans"]    = [400, 500, 600],
            ["OD.Font.Mono"]    = [400, 500, 700],
            ["OD.Font.Display"] = [400, 700],
        };

        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, face) in Fonts)
            {
                var family = Assert.IsType<FontFamily>(dict[key]);
                // Gömülü font pack URI ile gelir; Source "…/Fonts/#Yüz Adı".
                Assert.Contains("#" + face, family.Source);

                var weights = family.GetTypefaces()
                    .Where(t => t.Style == FontStyles.Normal)
                    .Select(t => t.Weight.ToOpenTypeWeight())
                    .Distinct().Order().ToArray();

                Assert.Equal(expectedWeights[key], weights);
            }
        }, "Metrics.xaml");

        Assert.Null(error);
    }
}
