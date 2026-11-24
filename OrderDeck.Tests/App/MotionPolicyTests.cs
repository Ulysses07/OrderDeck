using System.Windows;
using OrderDeck.App.Controls;

namespace OrderDeck.Tests.App;

/// <summary>
/// Hareket azaltma kararının tek yeri: <see cref="MotionPolicy"/>.
///
/// NEDEN: Motion.xaml'ın kuralı "pencere içi animasyonlar kapalıysa hareket atlanır". Karar her
/// şablona ya da her animasyona ayrı ayrı gömülürse biri unutulur; tek noktada durunca test de
/// oradan değiştirebilir (sistem ayarı testten oynatılamaz).
/// </summary>
public class MotionPolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Transitions_run_only_when_requested_and_motion_is_enabled(bool motion, bool requested, bool expected)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var before = MotionPolicy.Override;
            try
            {
                MotionPolicy.Override = motion;

                Assert.Equal(motion, MotionPolicy.IsEnabled);
                Assert.Equal(expected, MotionAwareVisualStateManager.Effective(requested));
            }
            finally
            {
                MotionPolicy.Override = before;
            }
        });

        Assert.Null(error);
    }

    [Fact]
    public void Without_override_the_system_setting_decides()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var before = MotionPolicy.Override;
            try
            {
                MotionPolicy.Override = null;

                Assert.Equal(SystemParameters.ClientAreaAnimation, MotionPolicy.IsEnabled);
            }
            finally
            {
                MotionPolicy.Override = before;
            }
        });

        Assert.Null(error);
    }
}
