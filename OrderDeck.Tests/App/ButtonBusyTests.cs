using System.Windows;
using System.Windows.Controls.Primitives;
using OrderDeck.App.Controls;

namespace OrderDeck.Tests.App;

/// <summary>
/// Düğmelerin meşgul durumu (tasarım: düğme hareketleri §4.3).
///
/// NEDEN: Meşgul görünümü düğmenin <c>Content</c>'ini DEĞİŞTİRMEZ ve ölçüsünü oynatmaz: içerik
/// yerinde kalır, yalnız görünmez olur. Düğmeyi içerik metniyle bulan testler ve yanındaki
/// yerleşim buna yaslanıyor. Görünüm şablon tetikleyicisidir; bayrağın şablondan önce ya da sonra
/// verilmesi sonucu değiştirmemeli.
/// </summary>
public class ButtonBusyTests
{
    public static TheoryData<string> Styles => ["OD.Button.Primary", "OD.Button.Secondary", "OD.Button.Ghost"];

    [Theory, MemberData(nameof(Styles))]
    public void Busy_swaps_content_for_the_ring_without_changing_size_or_content(string style)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = SizedToContent(style);
            ButtonMotionTests.Realize(button);
            var content = button.Content;
            var size = new Size(button.ActualWidth, button.ActualHeight);
            Assert.True(size.Width > 0 && size.Width < 400, "düğme içeriğine göre ölçülmeli");

            ButtonState.SetIsBusy(button, true);
            button.UpdateLayout();

            AssertBusy(button);
            Assert.Same(content, button.Content);
            Assert.Equal(size, new Size(button.ActualWidth, button.ActualHeight));

            ButtonState.SetIsBusy(button, false);
            button.UpdateLayout();

            Assert.Equal(1, ButtonMotionTests.Part(button, "Content").Opacity);
            Assert.Equal(Visibility.Collapsed, ButtonMotionTests.Part(button, "Busy").Visibility);
            Assert.True(ButtonMotionTests.Root(button).IsHitTestVisible);
            Assert.Equal(size, new Size(button.ActualWidth, button.ActualHeight));
        });

        Assert.Null(error);
    }

    [Theory, MemberData(nameof(Styles))]
    public void Busy_set_before_the_template_is_applied_gives_the_same_result(string style)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = SizedToContent(style);
            ButtonState.SetIsBusy(button, true);

            ButtonMotionTests.Realize(button);

            AssertBusy(button);
        });

        Assert.Null(error);
    }

    [Theory, MemberData(nameof(Styles))]
    public void A_busy_button_that_is_also_disabled_does_not_dim(string style)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            // Komut çalışırken "çalıştırılamaz" bildirir ve düğmeyi devre dışı bırakır; meşgul
            // düğme o sırada solmamalı (halka tam görünür kalır).
            var button = ButtonMotionTests.Realized(style);
            button.IsEnabled = false;

            ButtonState.SetIsBusy(button, true);
            Assert.Equal(1, ButtonMotionTests.Root(button).Opacity);

            ButtonState.SetIsBusy(button, false);
            Assert.Equal(0.45, ButtonMotionTests.Root(button).Opacity);
        });

        Assert.Null(error);
    }

    [Theory, MemberData(nameof(Styles))]
    public void Ring_takes_the_button_foreground_and_the_icon_size(string style)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = ButtonMotionTests.Realized(style);

            var ring = Assert.IsType<BusyRing>(ButtonMotionTests.Part(button, "Busy"));
            Assert.Equal(16, ring.Width);
            Assert.Equal(16, ring.Height);
            Assert.Same(button.Foreground, ring.Stroke);
            Assert.Equal(TimeSpan.FromMilliseconds(850), ring.Period.TimeSpan);
        });

        Assert.Null(error);
    }

    [Theory]
    [InlineData("OD.Chip")]
    [InlineData("NavButton")]
    public void Styles_without_a_busy_state_ignore_the_flag(string style)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = ButtonMotionTests.Realized(style);
            Assert.Null(button.Template.FindName("Busy", button));

            ButtonState.SetIsBusy(button, true);
            button.UpdateLayout();

            Assert.Equal(1, ButtonMotionTests.Root(button).Opacity);
            Assert.True(ButtonMotionTests.Root(button).IsHitTestVisible);
            Assert.Equal(1, ButtonMotionTests.Part(button, "Content").Opacity);
        });

        Assert.Null(error);
    }

    /// <summary>İçeriği kadar yer kaplayan düğme: "ölçü değişmedi" savı anlamlı olsun.</summary>
    private static ButtonBase SizedToContent(string style)
    {
        var button = ButtonMotionTests.Unrealized(style);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Top;
        return button;
    }

    private static void AssertBusy(ButtonBase button)
    {
        Assert.Equal(0, ButtonMotionTests.Part(button, "Content").Opacity);
        Assert.Equal(Visibility.Visible, ButtonMotionTests.Part(button, "Busy").Visibility);
        Assert.False(ButtonMotionTests.Root(button).IsHitTestVisible);
    }
}
