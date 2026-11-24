using System.Xml.Linq;

namespace OrderDeck.Tests.App;

/// <summary>
/// Düğme stilleri sade kalır (tasarım: düğme hareketleri §3).
///
/// NEDEN: İlk hareket denemesi gradyan + renkli ışıma + parıltı + sıçrayan yay yığınıydı ve
/// reddedildi. Aynı süsler ileride "küçük bir dokunuş" diye geri gelmesin: beş düğme stilinin
/// kaynak XAML'inde bunların adı geçerse test kırılır. Kural kaynağa bakar; derlenmiş şablonda
/// "efekt yok"u aramak, kullanılmayan bir kaynağı kaçırırdı.
/// </summary>
public class ButtonStyleGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Dosya (depo köküne göre) ve içindeki düğme stillerinin anahtarları.</summary>
    private static readonly (string File, string[] Keys)[] Sources =
    [
        ("OrderDeck.App/Themes/Controls.xaml",
            ["OD.Button.Primary", "OD.Button.Secondary", "OD.Button.Ghost", "OD.Chip"]),
        ("OrderDeck.App/Views/Shell/ShellSidebar.xaml", ["NavButton"]),
    ];

    [Theory]
    [InlineData("Effect")]            // DropShadowEffect, BlurEffect: renkli ışıma, gölge
    [InlineData("GradientBrush")]     // LinearGradientBrush, RadialGradientBrush
    [InlineData("OD.Ease.Spring")]    // sıçrayan yay
    [InlineData("BackEase")]
    [InlineData("ElasticEase")]
    [InlineData("BounceEase")]
    [InlineData("ColorAnimation")]    // fırça rengi canlandırılmaz; ton örtüyle verilir
    [InlineData("ThicknessAnimation")] // boşluk/ölçü canlandırılmaz
    public void Button_styles_stay_plain(string forbidden)
    {
        foreach (var (key, markup) in ButtonStyles())
            Assert.False(markup.Contains(forbidden, StringComparison.Ordinal), $"{key} stilinde '{forbidden}' var");
    }

    [Fact]
    public void Button_states_animate_only_opacity_and_scale()
    {
        foreach (var (key, markup) in ButtonStyles())
        {
            var properties = XElement.Parse(markup).Descendants()
                .Select(e => e.Attribute("Storyboard.TargetProperty")?.Value)
                .Where(p => p is not null)
                .ToList();

            Assert.NotEmpty(properties);
            Assert.All(properties, p => Assert.True(
                p is "Opacity"
                    or "(UIElement.RenderTransform).(ScaleTransform.ScaleX)"
                    or "(UIElement.RenderTransform).(ScaleTransform.ScaleY)",
                $"{key} stilinde beklenmeyen hedef: {p}"));
        }
    }

    [Fact]
    public void Every_button_style_is_found_in_its_source_file()
    {
        // Koruma boşa dönmesin: bir stil yeniden adlandırılır ya da taşınırsa yukarıdaki testler
        // hiçbir şeye bakmadan geçerdi.
        Assert.Equal(5, ButtonStyles().Count);
    }

    /// <summary>Beş düğme stilinin anahtarı ve XAML metni (yorumlar atılmış).</summary>
    private static List<(string Key, string Markup)> ButtonStyles()
    {
        var root = RepoRoot();
        var styles = new List<(string, string)>();
        foreach (var (file, keys) in Sources)
        {
            var document = XDocument.Load(Path.Combine(root, file));
            document.DescendantNodes().OfType<XComment>().ToList().ForEach(c => c.Remove());
            foreach (var style in document.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                if (style.Attribute(Xaml + "Key")?.Value is { } key && keys.Contains(key))
                    styles.Add((key, style.ToString()));
            }
        }

        return styles;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderDeck.sln")))
            dir = dir.Parent;

        Assert.True(dir is not null, "OrderDeck.sln bulunamadı — depo kökü tespit edilemedi.");
        return dir!.FullName;
    }
}
