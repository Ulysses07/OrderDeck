using System.Windows;
using OrderDeck.App.Controls;

namespace OrderDeck.Tests.App;

/// <summary>
/// Meşgul halkası yalnız görünürken ve hareket açıkken döner.
///
/// NEDEN: Halka her düğme şablonunda hazır bekliyor (yaklaşık 150 düğme). Gizliyken de dönseydi
/// hiç görünmeyen 150 sonsuz animasyon çalışırdı; hareket azaltmada dönseydi Motion.xaml'ın kuralı
/// bozulurdu.
/// </summary>
public class BusyRingTests
{
    [Theory]
    [InlineData(true, true, true)]     // görünür, hareket açık
    [InlineData(true, false, false)]   // görünür, hareket azaltılmış: halka durur
    [InlineData(false, true, false)]   // gizli
    public void Ring_spins_only_while_visible_and_motion_is_enabled(bool visible, bool motion, bool expected)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var before = MotionPolicy.Override;
            Window? window = null;
            try
            {
                MotionPolicy.Override = motion;
                var ring = new BusyRing
                {
                    Width = 16, Height = 16,
                    Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
                };
                window = WpfWait.OffScreenWindow(ring);

                Assert.True(WpfWait.Until(() => ring.IsSpinning == expected));
                WpfWait.For(60);
                Assert.Equal(expected, ring.IsSpinning);
            }
            finally
            {
                window?.Close();
                MotionPolicy.Override = before;
            }
        });

        Assert.Null(error);
    }

    [Fact]
    public void Ring_stops_when_hidden()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var before = MotionPolicy.Override;
            Window? window = null;
            try
            {
                MotionPolicy.Override = true;
                var ring = new BusyRing { Width = 16, Height = 16 };
                window = WpfWait.OffScreenWindow(ring);
                Assert.True(WpfWait.Until(() => ring.IsSpinning));

                ring.Visibility = Visibility.Collapsed;

                Assert.True(WpfWait.Until(() => !ring.IsSpinning));
            }
            finally
            {
                window?.Close();
                MotionPolicy.Override = before;
            }
        });

        Assert.Null(error);
    }
}
