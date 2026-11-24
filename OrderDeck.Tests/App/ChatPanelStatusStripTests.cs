using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using OrderDeck.App.Controls;
using OrderDeck.App.Services.Chat;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Shell;
using Path = System.Windows.Shapes.Path;

namespace OrderDeck.Tests.App;

/// <summary>
/// Sohbet panelindeki durum şeridi ve "Sohbeti yenile" düğmesi, gerçek WPF bağlarıyla (tasarım: Sohbeti
/// yenile §5.4, §5.5).
///
/// NEDEN: yanlış yazılmış bir bağ yolunda WPF hata vermez, alan boş görünür; yerleşim de yalnız çizilince
/// belli olur. Şerit yarım ekranda (panel 400 px) alt satıra iner: satırlar soldan hizalı başlamalı,
/// "Instagram" etiketi ilk hesabından ayrılmamalı, düğme ilk satırın hizasında sağda kalmalı ve başlık
/// (arama kutusu) şeritten etkilenmemeli. Satır metinleri üretilir.
/// </summary>
public class ChatPanelStatusStripTests
{
    /// <summary>Panelin bağlandığı kabuk yüzeyi (bağlamaların okuduğu adlarla); WPF bağlaması public tip ister.</summary>
    public sealed class ChatStub
    {
        public ObservableCollection<ChatMessageViewModel> ChatView { get; } = new();
        public string ChatSearchText { get; set; } = "";
        public string ActiveCode { get; set; } = "";
        public bool OnlyActiveCode { get; set; }
        public ObservableCollection<ChatStatusRow> ChatStatusRows { get; } = new();
        public bool ShowChatStatus { get; set; }
        public bool IsRefreshingChat { get; set; }
        public bool ChatRefreshDone { get; set; }
        public int Clicks { get; private set; }
        public IRelayCommand RefreshChatCommand { get; }

        public ChatStub() => RefreshChatCommand = new RelayCommand(() => Clicks++);
    }

    private static void OnSta(Action body) => ThemeTestHost.RunOnSta(body).Should().BeNull();

    private static ChatStatusRow Row(
        ChatStatusSeverity severity = ChatStatusSeverity.Live, string? text = null, string? groupLabel = null) =>
        new($"kaynak-{Guid.NewGuid():N}", groupLabel, text ?? $"Kaynak{Guid.NewGuid():N}"[..14], severity,
            $"ipucu {Guid.NewGuid():N}", IsProblem: false, IsConnecting: false);

    /// <summary>Dört platformlu, bir satırı uzun (düşmüş kaynak) tipik şerit: 400 px'e tek satırda sığmaz.</summary>
    private static ChatStub Busy()
    {
        var stub = new ChatStub { ShowChatStatus = true };
        stub.ChatStatusRows.Add(Row(ChatStatusSeverity.Warning, "YouTube yayın kesildi (21:14)"));
        stub.ChatStatusRows.Add(Row(text: "TikTok"));
        stub.ChatStatusRows.Add(Row(text: "Facebook"));
        stub.ChatStatusRows.Add(Row(text: "@" + Guid.NewGuid().ToString("N")[..14], groupLabel: "Instagram"));
        stub.ChatStatusRows.Add(Row(text: "@" + Guid.NewGuid().ToString("N")[..14]));
        return stub;
    }

    /// <summary>Paneli verilen genişliğe yerleştirir: ölçüm, bekleyen bağlamalar, yeniden ölçüm.</summary>
    private static ChatPanel Layout(ChatStub context, double width = 700)
    {
        var size = new Size(width, 400);
        var panel = new ChatPanel { DataContext = context, Width = size.Width, Height = size.Height };
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        ThemeTestHost.Pump();
        panel.UpdateLayout();
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        panel.UpdateLayout();
        return panel;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deep in Descendants(child)) yield return deep;
        }
    }

    private static Button RefreshButton(ChatPanel panel) => (Button)panel.FindName("RefreshChatButton");

    private static ItemsControl StatusList(ChatPanel panel) => (ItemsControl)panel.FindName("ChatStatusList");

    /// <summary>Şeridin kökü: listeyi ve düğmeyi saran, görünürlüğü <c>ShowChatStatus</c>'a bağlı kenarlık.</summary>
    private static FrameworkElement Strip(ChatPanel panel)
    {
        DependencyObject current = StatusList(panel);
        while (current is not Border) current = VisualTreeHelper.GetParent(current);
        return (Border)current;
    }

    /// <summary>Satırın şeritteki kabı.</summary>
    private static ContentPresenter Item(ChatPanel panel, ChatStatusRow row) =>
        Descendants(StatusList(panel)).OfType<ContentPresenter>().Single(p => ReferenceEquals(p.Content, row));

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static Geometry Icon(string key) => (Geometry)Application.Current!.FindResource(key);

    [Fact]
    public void Strip_and_button_are_hidden_without_a_session() => OnSta(() =>
    {
        var stub = Busy();
        stub.ShowChatStatus = false;

        var panel = Layout(stub);

        // Pencere yokken IsVisible hep yanlıştır; görünürlük ve ölçü üzerinden bakılır.
        Strip(panel).Visibility.Should().Be(Visibility.Collapsed);
        Strip(panel).ActualHeight.Should().Be(0, "gizli şerit yer kaplamaz");
        // Düğme şeridin İÇİNDEDİR: onunla birlikte kalkar (gizlenen kabın çocukları yeniden yerleştirilmez,
        // o yüzden düğmenin kendi ölçüsüne değil kabına bakılır).
        Descendants(Strip(panel)).Should().Contain(RefreshButton(panel));

        // Aynı panel, oturum açıkken: şerit ve düğme çizilir (yukarıdaki sıfırlar gizlilikten).
        var shown = Layout(Busy());
        Strip(shown).ActualHeight.Should().BeGreaterThan(0);
        RefreshButton(shown).ActualHeight.Should().Be(24);
    });

    [Fact]
    public void Strip_draws_the_real_rows_between_the_header_and_the_list() => OnSta(() =>
    {
        var stub = Busy();
        var panel = Layout(stub);

        foreach (var row in stub.ChatStatusRows)
        {
            var host = Item(panel, row);
            Descendants(host).OfType<TextBlock>().Should().Contain(t => t.Text == row.DisplayText);
            Descendants(host).OfType<FrameworkElement>().Should().Contain(e => Equals(e.ToolTip, row.Tooltip));
        }

        var strip = Strip(panel);
        var list = (ListBox)panel.FindName("ChatList");
        list.ActualHeight.Should().BeGreaterThan(0);
        Bounds(strip, panel).Bottom.Should().BeLessThanOrEqualTo(Bounds(list, panel).Top, "şerit başlık ile liste arasında");
    });

    [Fact]
    public void Button_sits_at_the_right_end_on_the_first_line_of_the_strip() => OnSta(() =>
    {
        var stub = Busy();
        var panel = Layout(stub, width: 400);
        var button = Bounds(RefreshButton(panel), panel);
        var items = stub.ChatStatusRows.Select(row => Bounds(Item(panel, row), panel)).ToArray();
        items.Select(item => item.Top).Distinct().Should().HaveCountGreaterThan(1, "ön koşul: şerit alt satıra indi");

        (button.Width, button.Height).Should().Be((24d, 24d));
        items.Should().OnlyContain(item => item.Right <= button.Left, "satırlar düğmeye kadar uzanır, altına girmez");
        // Düğme şerit iki satır olsa da İLK satırın hizasındadır (ortalanmaz).
        var firstLine = items.Where(item => item.Top == items.Min(i => i.Top)).ToArray();
        var lineCentre = (firstLine.Min(i => i.Top) + firstLine.Max(i => i.Bottom)) / 2;
        (button.Top + button.Height / 2).Should().BeApproximately(lineCentre, 1.5);
    });

    [Fact]
    public void Header_keeps_its_layout() => OnSta(() =>
    {
        // Düğme başlığa konsaydı arama kutusu yarım ekranda 181 → 141 px daralırdı (ölçüldü); şeritte durduğu
        // için başlık şeritten bağımsız.
        static double SearchWidth(ChatPanel panel) => Descendants(panel).OfType<TextBox>().Single().ActualWidth;
        var hidden = Busy();
        hidden.ShowChatStatus = false;

        SearchWidth(Layout(Busy(), width: 400)).Should().Be(SearchWidth(Layout(hidden, width: 400)));
    });

    [Fact]
    public void At_half_screen_the_strip_wraps_inside_the_panel_and_lines_start_flush_left() => OnSta(() =>
    {
        var stub = Busy();
        var panel = Layout(stub, width: 400);
        var strip = Bounds(Strip(panel), panel);
        var items = stub.ChatStatusRows.Select(row => Bounds(Item(panel, row), panel)).ToArray();

        var lines = items.GroupBy(item => item.Top).ToArray();
        lines.Should().HaveCountGreaterThan(1, "ön koşul: şerit alt satıra indi");
        items.Should().OnlyContain(item => item.Left >= strip.Left && item.Right <= strip.Right);
        lines.Select(line => line.Min(item => item.Left)).Distinct().Should().ContainSingle(
            "her satır soldan hizalı başlar (öğeler arası boşluk sağda)");
    });

    [Fact]
    public void Instagram_label_stays_with_its_first_account() => OnSta(() =>
    {
        var stub = Busy();
        var labelled = stub.ChatStatusRows.Single(row => row.GroupLabel is not null);
        var panel = Layout(stub, width: 400);

        // Etiket ile hesap adı AYNI öğenin içindedir: şerit nereden kırılırsa kırılsın ayrılmazlar.
        var host = Item(panel, labelled);
        var texts = Descendants(host).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible).ToArray();
        texts.Select(t => t.Text).Should().Equal("Instagram", labelled.DisplayText);
        Bounds(texts[0], panel).Right.Should().BeLessThanOrEqualTo(Bounds(texts[1], panel).Left);

        // Etiketsiz satırda etiket yer kaplamaz.
        var plain = Item(panel, stub.ChatStatusRows.First(row => row.GroupLabel is null));
        Descendants(plain).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible)
            .Should().ContainSingle().Which.ActualWidth.Should().BeGreaterThan(0);
        Descendants(plain).OfType<TextBlock>().Single(t => t.Visibility != Visibility.Visible)
            .ActualWidth.Should().Be(0);
    });

    [Fact]
    public void Dot_colour_follows_severity() => OnSta(() =>
    {
        var stub = new ChatStub { ShowChatStatus = true };
        var rows = new[]
        {
            Row(ChatStatusSeverity.Live), Row(ChatStatusSeverity.Warning), Row(ChatStatusSeverity.Neutral),
        };
        foreach (var row in rows) stub.ChatStatusRows.Add(row);

        var panel = Layout(stub);

        foreach (var (row, brush) in rows.Zip(new[] { "OD.Brush.Success", "OD.Brush.Amber", "OD.Brush.TextMute" }))
        {
            var dot = Descendants(Item(panel, row)).OfType<Ellipse>().Single();
            dot.Fill.Should().BeSameAs(panel.FindResource(brush));
            (dot.Width, dot.Height).Should().Be((8d, 8d));
        }
    });

    [Fact]
    public void Button_runs_the_refresh_command_and_is_named_for_assistive_tools() => OnSta(() =>
    {
        var stub = Busy();
        var button = RefreshButton(Layout(stub));

        button.Command.Should().BeSameAs(stub.RefreshChatCommand);
        button.ToolTip.Should().Be("Sohbet bağlantılarını yenile");
        AutomationProperties.GetName(button).Should().Be("Sohbet bağlantılarını yenile");
        Descendants(button).OfType<Path>().Single().Data.Should().BeSameAs(Icon("OD.Path.Refresh"));
    });

    [Fact]
    public void Button_is_busy_while_refreshing_and_shows_the_check_mark_when_done() => OnSta(() =>
    {
        var refreshing = Busy();
        refreshing.IsRefreshingChat = true;
        var busy = RefreshButton(Layout(refreshing));
        ButtonState.GetIsBusy(busy).Should().BeTrue();
        ((UIElement)busy.Template.FindName("Busy", busy)).Visibility.Should().Be(Visibility.Visible);
        AutomationProperties.GetName(busy).Should().Be("Sohbet bağlantıları yenileniyor");

        var finished = Busy();
        finished.ChatRefreshDone = true;
        var done = RefreshButton(Layout(finished));
        ButtonState.GetIsBusy(done).Should().BeFalse();
        Descendants(done).OfType<Path>().Single().Data.Should().BeSameAs(Icon("OD.Path.Check"));
    });

    [Fact]
    public void Icon_geometries_exist_and_fit_the_24_box() => OnSta(() =>
    {
        foreach (var key in new[] { "OD.Path.Refresh", "OD.Path.Check" })
        {
            var bounds = Icon(key).Bounds;
            bounds.IsEmpty.Should().BeFalse(key);
            new Rect(0, 0, 24, 24).Contains(bounds).Should().BeTrue($"{key} 24x24 kutuya çizilir");
            // Kutunun büyük kısmını doldurur: öbür ikonlarla aynı görsel ağırlık.
            Math.Max(bounds.Width, bounds.Height).Should().BeGreaterThan(12, key);
        }
    });
}
