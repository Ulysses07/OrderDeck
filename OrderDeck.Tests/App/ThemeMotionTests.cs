using System.Windows;
using System.Windows.Media.Animation;

namespace OrderDeck.Tests.App;

/// <summary>
/// Themes/Motion.xaml hareket sözleşmesi.
///
/// NEDEN: Uygulamada bugün SIFIR animasyon var. Hareket eklenirken her
/// ekranın kendi süresini uydurması, 120 rastgele rengin animasyon
/// karşılığını üretir. Üç süre + iki easing; başka değer yok.
/// </summary>
public class ThemeMotionTests
{
    [Fact]
    public void Durations_have_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(150),
                Assert.IsType<Duration>(dict["OD.Dur.Fast"]).TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(350),
                Assert.IsType<Duration>(dict["OD.Dur.Base"]).TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(850),
                Assert.IsType<Duration>(dict["OD.Dur.Slow"]).TimeSpan);
        }, "Motion.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Easings_are_ease_out_and_spring()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            var outEase = Assert.IsType<CubicEase>(dict["OD.Ease.Out"]);
            Assert.Equal(EasingMode.EaseOut, outEase.EasingMode);

            var spring = Assert.IsType<BackEase>(dict["OD.Ease.Spring"]);
            Assert.Equal(EasingMode.EaseOut, spring.EasingMode);
            Assert.Equal(0.3, spring.Amplitude);
        }, "Motion.xaml");

        Assert.Null(error);
    }
}
