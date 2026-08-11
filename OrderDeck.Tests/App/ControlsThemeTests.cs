using System.Windows;

namespace OrderDeck.Tests.App;

/// <summary>
/// Controls.xaml Faz 1'in tükettiği bileşen stillerini tanımlar; Metrics.xaml
/// da Faz 1'e özgü ölçüleri. İkisi de App.xaml'den merge ediliyor.
///
/// NEDEN: Bir Style anahtarı yeniden adlandırılırsa XAML'deki StaticResource
/// derlemede değil, pencere AÇILIRKEN patlar. Bu test o riski test koşumuna
/// çeker. Ayrıca Faz 4'ün "sabit hex 0" ölçümünün ön şartı: her görsel değer
/// bir token'dan gelmeli.
/// </summary>
public class ControlsThemeTests
{
    private static readonly string[] StyleKeys =
    [
        "OD.Panel", "OD.Button.Primary", "OD.Button.Ghost", "OD.Chip",
        "OD.TextBox", "OD.Text.Micro", "OD.Text.Mono", "OD.CountPill",
        // Faz 4b: DarkControls'un örtük stillerinin keyed karşılıkları.
        "OD.ListBoxItem", "OD.ListBox", "OD.PasswordBox", "OD.CheckBox",
        "OD.ShortcutCapture"
    ];

    private static readonly string[] Faz1Metrics =
    [
        "OD.Layout.ProductImageHeight",     // 142 / kısa modda 56
        "OD.Layout.ProductImageHeightShort",
        "OD.Layout.QueueMinHeight",         // 64
        "OD.Layout.ChatBadgeColumn",        // 28
        "OD.Layout.ChatUserMaxWidth",       // 132
        "OD.Layout.CodeFont",               // 44
        "OD.Layout.CodeFontShort"           // 32 (kısa-mod karşılığı)
    ];

    [Fact]
    public void Controls_dictionary_defines_every_faz1_style()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var key in StyleKeys)
                Assert.IsType<Style>(dict[key]);
        }, "Controls.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Metrics_defines_faz1_layout_sizes()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var key in Faz1Metrics)
                Assert.True(Assert.IsType<double>(dict[key]) > 0, key);
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void App_merges_controls_dictionary()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var app = Application.Current!;
            Application.LoadComponent(
                app, new Uri("/OrderDeck.App;component/App.xaml", UriKind.Relative));

            Assert.IsType<Style>(app.Resources["OD.Panel"]);
        });

        Assert.Null(error);
    }

    [Fact]
    public void Controls_defines_the_progress_bar_style()
    {
        // BootGate ve LoginGate belirsiz çubuk kullanıyor. Anahtar yoksa
        // Windows'un varsayılan YEŞİL Aero çubuğu çizilir — kapalı palet dışı.
        var error = ThemeTestHost.Run(dict =>
        {
            var style = Assert.IsType<Style>(dict["OD.ProgressBar"]);
            Assert.Equal(typeof(System.Windows.Controls.ProgressBar), style.TargetType);
        }, "Controls.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Metrics_defines_the_progress_height()
    {
        var error = ThemeTestHost.Run(
            dict => Assert.True(Assert.IsType<double>(dict["OD.Layout.ProgressHeight"]) > 0),
            "Metrics.xaml");

        Assert.Null(error);
    }
}
