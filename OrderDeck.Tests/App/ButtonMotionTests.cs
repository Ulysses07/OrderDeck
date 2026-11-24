using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OrderDeck.App.Controls;
using OrderDeck.App.Views.Shell;

namespace OrderDeck.Tests.App;

/// <summary>
/// Düğme stillerinin hareket sözleşmesi (tasarım: düğme hareketleri §3, §4).
///
/// NEDEN: Yaklaşık 150 düğme beş stilden besleniyor; bir şablondaki hata her ekranda görünür.
/// Sözleşme şablonun KENDİSİNDEN okunur (hangi durum hangi parçayı hangi değere götürüyor): fareyi
/// testten oynatmak mümkün değil, zamana bakan test de yük altında kırılır. Mekanizmanın gerçekten
/// çalıştığını ayrıca tek bir uçtan uca test gösterir (durum doğrudan tetiklenir, değer gerçek
/// ileti döngüsünde beklenir).
/// </summary>
public class ButtonMotionTests
{
    /// <summary>Stil, üzerinde durumunun hedefleri, basılı durumunun hedefleri ("Parça.Özellik=değer").</summary>
    public static TheoryData<string, string[], string[]> Expected => new()
    {
        { "OD.Button.Primary",   ["Hover.Opacity=0.14"], ["Press.Opacity=0.12", "Root.ScaleX=0.98", "Root.ScaleY=0.98"] },
        { "OD.Button.Secondary", ["Hover.Opacity=1"],    ["Hover.Opacity=1", "Press.Opacity=0.06", "Root.ScaleX=0.98", "Root.ScaleY=0.98"] },
    };

    /// <summary>Basınca küçülen stiller; öbürlerinde kökte dönüşüm yoktur.</summary>
    private static readonly string[] Scaling = ["OD.Button.Primary", "OD.Button.Secondary"];

    /// <summary>Devre dışı görünümü olan stiller (çip ve kenar çubuğu öğesinde bugün yok).</summary>
    private static readonly string[] Dimming = ["OD.Button.Primary", "OD.Button.Secondary", "OD.Button.Ghost"];

    [Theory, MemberData(nameof(Expected))]
    public void States_animate_exactly_the_declared_targets(string style, string[] over, string[] pressed)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = Realized(style);

            Assert.Equal(Parse(over), StateTargets(button, "MouseOver"));
            Assert.Equal(Parse(pressed), StateTargets(button, "Pressed"));
            Assert.Empty(StateTargets(button, "Normal"));
            Assert.Empty(StateTargets(button, "Disabled"));
        });

        Assert.Null(error);
    }

    [Theory, MemberData(nameof(Expected))]
    public void Root_uses_the_motion_aware_manager_and_the_fast_ease_out_transition(string style, string[] _, string[] __)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = Realized(style);

            Assert.IsType<MotionAwareVisualStateManager>(VisualStateManager.GetCustomVisualStateManager(Root(button)));

            var transition = Assert.Single(CommonStates(button).Transitions.Cast<VisualTransition>());
            Assert.Equal(TimeSpan.FromMilliseconds(150), transition.GeneratedDuration.TimeSpan);
            Assert.Equal(EasingMode.EaseOut, Assert.IsType<CubicEase>(transition.GeneratedEasingFunction).EasingMode);
        });

        Assert.Null(error);
    }

    [Theory, MemberData(nameof(Expected))]
    public void At_rest_overlays_are_invisible_and_scale_is_one(string style, string[] _, string[] __)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = Realized(style);

            Assert.Equal(0, Part(button, "Press").Opacity);
            if (button.Template.FindName("Hover", button) is UIElement hover) Assert.Equal(0, hover.Opacity);

            if (Scaling.Contains(style))
            {
                var scale = Assert.IsType<ScaleTransform>(Root(button).RenderTransform);
                Assert.Equal(1, scale.ScaleX);
                Assert.Equal(1, scale.ScaleY);
            }
            else
            {
                Assert.True(Root(button).RenderTransform.Value.IsIdentity);
            }
        });

        Assert.Null(error);
    }

    [Theory, MemberData(nameof(Expected))]
    public void Disabled_button_dims_to_45_percent(string style, string[] _, string[] __)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = Realized(style);

            button.IsEnabled = false;

            Assert.Equal(Dimming.Contains(style) ? 0.45 : 1, Root(button).Opacity);
        });

        Assert.Null(error);
    }

    [Theory, MemberData(nameof(Expected))]
    public void Pressed_values_arrive_in_a_real_message_loop(string style, string[] _, string[] pressed)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = Unrealized(style);
            var window = WpfWait.OffScreenWindow(button);
            try
            {
                WpfWait.For(50);

                Assert.True(VisualStateManager.GoToState(button, "Pressed", false));

                foreach (var (key, value) in Parse(pressed))
                    Assert.True(WpfWait.Until(() => Math.Abs(Actual(button, key) - value) < 0.001), key);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(error);
    }

    [Fact]
    public void Disabling_a_hovered_button_clears_the_hover_overlay()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = Unrealized("OD.Button.Primary");
            var window = WpfWait.OffScreenWindow(button);
            try
            {
                WpfWait.For(50);
                Assert.True(VisualStateManager.GoToState(button, "MouseOver", false));
                Assert.True(WpfWait.Until(() => Math.Abs(Part(button, "Hover").Opacity - 0.14) < 0.001));

                button.IsEnabled = false;

                Assert.True(WpfWait.Until(() => Part(button, "Hover").Opacity == 0));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(error);
    }

    [Fact]
    public void Derived_shortcut_capture_style_inherits_the_template()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var button = new ShortcutCaptureButton
            {
                Style = (Style)Application.Current!.Resources["OD.ShortcutCapture"],
                Content = "Ctrl + P",
            };
            Realize(button);

            Assert.IsType<MotionAwareVisualStateManager>(VisualStateManager.GetCustomVisualStateManager(Root(button)));
            Assert.Equal(StateTargets(Realized("OD.Button.Secondary"), "Pressed"), StateTargets(button, "Pressed"));
        });

        Assert.Null(error);
    }

    // ── Yardımcılar (ButtonBusyTests de kullanır) ───────────────────────────────────────────────

    /// <summary>Stili uygulanmış, içeriği "Metin" olan, henüz yerleşime girmemiş düğme.</summary>
    internal static ButtonBase Unrealized(string styleKey)
    {
        ButtonBase control = styleKey == "OD.Chip" ? new ToggleButton() : new Button();
        control.Style = styleKey == "NavButton"
            ? (Style)new ShellSidebar().Resources["NavButton"]
            : (Style)Application.Current!.Resources[styleKey];
        control.Content = "Metin";
        return control;
    }

    /// <summary>Penceresiz gerçekleştirilmiş (şablonu uygulanmış, ölçülmüş) düğme.</summary>
    internal static ButtonBase Realized(string styleKey)
    {
        var control = Unrealized(styleKey);
        Realize(control);
        return control;
    }

    internal static void Realize(FrameworkElement element)
    {
        var host = new Border { Width = 400, Height = 200, Child = element };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        host.UpdateLayout();
    }

    /// <summary>Şablonun kök öğesi; adı sözleşme gereği "Root".</summary>
    internal static FrameworkElement Root(ButtonBase control)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetChild(control, 0));
        Assert.Equal("Root", root.Name);
        return root;
    }

    internal static UIElement Part(ButtonBase control, string name) =>
        Assert.IsAssignableFrom<UIElement>(control.Template.FindName(name, control));

    private static VisualStateGroup CommonStates(ButtonBase control) =>
        VisualStateManager.GetVisualStateGroups(Root(control))!.Cast<VisualStateGroup>().Single(g => g.Name == "CommonStates");

    /// <summary>Durumun götürdüğü hedefler: "Parça.Opacity" / "Root.ScaleX" / "Root.ScaleY" → değer.</summary>
    internal static IReadOnlyDictionary<string, double> StateTargets(ButtonBase control, string stateName)
    {
        var state = CommonStates(control).States.Cast<VisualState>().Single(s => s.Name == stateName);
        var targets = new Dictionary<string, double>();
        if (state.Storyboard is null) return targets;

        foreach (var timeline in state.Storyboard.Children)
        {
            // Yalnız "To" veren, süresi sıfır DoubleAnimation: geçişi yönetici üretir.
            var animation = Assert.IsType<DoubleAnimation>(timeline);
            Assert.Equal(TimeSpan.Zero, animation.Duration.TimeSpan);
            Assert.Null(animation.From);

            var path = Storyboard.GetTargetProperty(animation);
            var property = path.PathParameters.Count > 0
                ? ((DependencyProperty)path.PathParameters[^1]).Name
                : path.Path;
            targets.Add($"{Storyboard.GetTargetName(animation)}.{property}", animation.To!.Value);
        }

        return targets;
    }

    private static double Actual(ButtonBase control, string key)
    {
        var (part, property) = (key[..key.IndexOf('.')], key[(key.IndexOf('.') + 1)..]);
        return property switch
        {
            "Opacity" => Part(control, part).Opacity,
            "ScaleX" => ((ScaleTransform)Root(control).RenderTransform).ScaleX,
            "ScaleY" => ((ScaleTransform)Root(control).RenderTransform).ScaleY,
            _ => throw new InvalidOperationException(key),
        };
    }

    private static Dictionary<string, double> Parse(string[] pairs) => pairs.ToDictionary(
        p => p[..p.IndexOf('=')],
        p => double.Parse(p[(p.IndexOf('=') + 1)..], CultureInfo.InvariantCulture));
}
