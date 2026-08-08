using System.Windows;

namespace OrderDeck.Tests.App;

/// <summary>
/// App.xaml'in yeni tema sözlüklerini merge ettiğini ve eski sözlüklerle
/// anahtar çakışması olmadığını doğrular.
///
/// NEDEN: WPF merge'de aynı anahtar iki kez tanımlanırsa SESSİZCE sonuncusu
/// kazanır — hata vermez. Yeni sistem eskisiyle bir süre yan yana yaşayacağı
/// için, çakışmanın fark edilmeden davranış değiştirmesi gerçek bir risk.
///
/// App.xaml'i artık ThemeTestHost yüklüyor (Application örneğiyle aynı
/// thread'de, bir kere); bu dosya yalnızca sonucu doğrular.
/// </summary>
public class ThemeMergeTests
{
    private static readonly string[] NewDictionaries =
        ["Colors.xaml", "Metrics.xaml", "Motion.xaml", "Icons.xaml", "Controls.xaml"];

    // GiveawayTheme.xaml listede YOK: SettingsTheme'i "/Themes/SettingsTheme.xaml"
    // ile merge ediyor; baştaki "/" WPF'te uygulama köküne (giriş assembly'sine)
    // çözülür, test sürecinde giriş assembly'si testhost olduğu için tek başına
    // yüklenemiyor. Kapsam kaybı yok: tanımladığı altı anahtarın hepsi "S." önekli
    // ve SettingsTheme zaten listede.
    private static readonly string[] ExistingDictionaries =
        ["DarkControls.xaml", "PlatformIcons.xaml", "SettingsTheme.xaml"];

    [Fact]
    public void New_dictionaries_do_not_collide_with_existing_ones()
    {
        var error = RunOnSta(() =>
        {
            var newKeys = NewDictionaries.SelectMany(Keys).ToList();
            var oldKeys = ExistingDictionaries.SelectMany(Keys).ToHashSet();

            var collisions = newKeys.Where(oldKeys.Contains).ToList();
            Assert.Empty(collisions);

            // Yeni sözlükler kendi aralarında da çakışmamalı.
            Assert.Equal(newKeys.Count, newKeys.Distinct().Count());
        });

        Assert.Null(error);
    }

    [Fact]
    public void App_resources_expose_the_new_tokens()
    {
        var error = RunOnSta(() =>
        {
            // App.xaml'i RunOnSta zaten örneği yaratan thread'de yükledi
            // (InitializeComponent). Burada ikinci kez LoadComponent çağırmak
            // hem gereksiz hem de başka bir thread'den Application'a dokunmak
            // demekti — sadece sonucu doğruluyoruz.
            var app = Application.Current!;

            // Her yeni sözlükten bir temsilci anahtar.
            Assert.NotNull(app.Resources["OD.Brush.Accent"]);
            Assert.NotNull(app.Resources["OD.Font.F2"]);
            Assert.NotNull(app.Resources["OD.Dur.Base"]);
            Assert.NotNull(app.Resources["OD.Path.History"]);

            // Eski sözlükler hâlâ çözülüyor (regresyon).
            Assert.NotNull(app.Resources["OD.Bg.Window"]);
            Assert.NotNull(app.Resources["OD.PlatformIcon.YouTube"]);
        });

        Assert.Null(error);
    }

    private static IEnumerable<string> Keys(string fileName)
    {
        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/OrderDeck.App;component/Themes/" + fileName)
            };
            return dict.Keys.Cast<object>().Select(k => k.ToString()!).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{fileName} yüklenemedi: {ex.Message}", ex);
        }
    }

    private static string? RunOnSta(Action body) => ThemeTestHost.RunOnSta(body);
}
