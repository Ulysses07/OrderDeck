using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// GEÇİCİ TEST — Faz 4b PR 1 ile geldi, PR 2'de <c>DarkControls.xaml</c> ile
/// birlikte silinecek.
///
/// NEDEN VAR: PR 1 tek iş yapıyor — <c>DarkControls.xaml</c>'in 17 eski renk
/// token'ının hex değerini <c>Colors.xaml</c>'deki karşılığına çekmek. Bu 17
/// elle düzenleme; bir hanesi yanlış yazılırsa hiçbir derleyici uyarmaz ve
/// arayüzde ancak gözle fark edilir. Eşleme tablosu spec'te
/// (2026-08-11-arayuz-faz4b-base-tema-design.md, "PR 1 — renk takası").
/// </summary>
public class DarkControlsPaletteBridgeTests
{
    /// <summary>Eski anahtar → yeni anahtar. Spec'teki tablonun birebir kopyası.</summary>
    private static readonly (string Old, string New)[] Mapping =
    [
        ("OD.Bg.Window",        "OD.Brush.Bg"),
        ("OD.Bg.Surface",       "OD.Brush.Surface"),
        ("OD.Bg.Elevated",      "OD.Brush.Surface2"),
        ("OD.Bg.Input",         "OD.Brush.Surface2"),
        ("OD.Bg.InputHover",    "OD.Brush.Surface2"),
        ("OD.Bg.InputPressed",  "OD.Brush.Surface2"),
        ("OD.Bg.InputDisabled", "OD.Brush.Surface2"),
        ("OD.Border.Subtle",    "OD.Brush.Border"),
        ("OD.Border.Hover",     "OD.Brush.BorderStrong"),
        ("OD.Border.Focus",     "OD.Brush.Accent"),
        ("OD.Fg.Primary",       "OD.Brush.Text"),
        ("OD.Fg.Secondary",     "OD.Brush.TextDim"),
        ("OD.Fg.Disabled",      "OD.Brush.TextMute"),
        ("OD.Accent",           "OD.Brush.Accent"),
        ("OD.Accent.Hover",     "OD.Brush.AccentHot"),
        ("OD.Accent.Pressed",   "OD.Brush.AccentDeep"),
        ("OD.Selection",        "OD.Brush.Surface2"),
    ];

    [Fact]
    public void Every_legacy_token_carries_its_new_palette_colour()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var dark = Load("DarkControls.xaml");
            var colors = Load("Colors.xaml");

            foreach (var (oldKey, newKey) in Mapping)
            {
                var actual = ((SolidColorBrush)dark[oldKey]).Color;
                var expected = ((SolidColorBrush)colors[newKey]).Color;

                Assert.True(actual == expected,
                    $"{oldKey} = {actual}, beklenen {newKey} = {expected}");
            }
        });

        Assert.Null(error);
    }

    private static ResourceDictionary Load(string fileName)
        => new()
        {
            Source = new Uri(
                "pack://application:,,,/OrderDeck.App;component/Themes/" + fileName)
        };
}
