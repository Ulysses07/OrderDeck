using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Shell;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kabuğun Instagram sağlık yüzeyleri (spec 2026-11-16 §8.6.3, §8.6.4): sohbet şeridinin satırları ve görünürlüğü,
/// üst bardaki sohbet noktası ve kenar çubuğunun Instagram satırı. İzleyici verilmezse üçü de bugünkü gibidir.
/// Sayfa kimliği, token, Sayfa adı ve kullanıcı adı üretilir.
/// </summary>
public class MainShellInstagramHealthTests
{
    /// <summary>Kenar çubuğunun yalnız bağlantı listesi (WPF bağlaması public tip ister).</summary>
    public sealed class SidebarStub { public ObservableCollection<PlatformConnectionViewModel> Connections { get; } = new(); }

    private readonly ManualTime _time = new();

    /// <summary>Tek Sayfalı izleyici; hesap canlı, son teması saatin şimdisi.</summary>
    private (InstagramHealthMonitor Monitor, string PageId, string Username) LiveMonitor()
    {
        var pageId = FacebookOAuthTestKit.NewId();
        var username = $"hesap-{Guid.NewGuid():N}";
        var monitor = new InstagramHealthMonitor(_time);
        monitor.Configure(new[] { new FacebookLinkedPage(pageId, FacebookOAuthTestKit.NewToken(), $"sayfa-{Guid.NewGuid():N}") });
        monitor.Report(pageId, InstagramAccountState.Starting);
        monitor.SetAccount(pageId, username);
        monitor.ObserverFor(pageId).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        return (monitor, pageId, username);
    }

    /// <summary>Kabuğu izleyici ve sayaçla kurar; Instagram saati elle ilerleyen saattir, bir nabız koşar.</summary>
    private MainShellTestHarness.Harness Build(InstagramHealthMonitor? monitor, ViewerCountTracker? viewers = null)
    {
        var h = MainShellTestHarness.Build(instagramHealth: monitor, viewers: viewers);
        h.Vm.InstagramNow = () => _time.GetUtcNow();
        h.Vm.UpdateChatHealth();
        return h;
    }

    private static ViewerCountTracker Viewers(params (string Platform, int Count)[] reports)
    {
        var tracker = new ViewerCountTracker();
        foreach (var (platform, count) in reports) tracker.Report(platform, count);
        return tracker;
    }

    /// <summary>Veri yoluna üretilmiş bir yorum basar: üst noktanın mevcut sonucu "ok" olur.</summary>
    private static void Chat(MainShellTestHarness.Harness h) => h.Bus.Publish(new ChatMessage(
        Guid.NewGuid().ToString("N"), "youtube", null, $"izleyici-{Guid.NewGuid():N}", null, null,
        $"mesaj {Guid.NewGuid():N}", 1000, Array.Empty<string>()));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)    // görsel ağaç
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deep in Descendants(child)) yield return deep;
        }
    }

    private static PlatformConnectionViewModel Row(MainShellTestHarness.Harness h, string platform) =>
        h.Vm.Connections.Single(c => c.Platform == platform);

    [Fact]
    public void Izleyici_verilmezse_serit_bos_ve_dort_satir_bugunku_gibi()
    {
        using var h = Build(null, Viewers(("instagram", 55)));
        h.Vm.InstagramAccounts.Should().BeEmpty();
        h.Vm.ShowInstagramStatus.Should().BeFalse();
        h.Vm.Connections.Select(c => c.Platform).Should().Equal("youtube", "instagram", "tiktok", "facebook");
        (Row(h, "instagram").IsConnected, Row(h, "instagram").ShowViewerCount, Row(h, "instagram").ViewerCount).Should().Be((true, true, 55));
        Row(h, "youtube").ShowViewerCount.Should().BeFalse();
    }

    [Fact]
    public void Canli_hesapta_Instagram_satiri_yesil_ve_sayisiz_diger_uc_satir_ayni()
    {
        using var h = Build(LiveMonitor().Monitor, Viewers(("youtube", 1240), ("instagram", 55)));
        (Row(h, "instagram").IsConnected, Row(h, "instagram").ShowViewerCount).Should().Be((true, false));
        (Row(h, "youtube").IsConnected, Row(h, "youtube").ShowViewerCount, Row(h, "youtube").ViewerCount).Should().Be((true, true, 1240));
        h.Vm.Connections.Where(c => c.Platform is "tiktok" or "facebook").Should().OnlyContain(c => !c.IsConnected && !c.ShowViewerCount);
        h.Vm.ShowInstagramStatus.Should().BeFalse("tek hesap sağlıklı: şerit yok");
        _time.Advance(TimeSpan.FromSeconds(11));                        // Live ama yanıtsız: canlı sayılmaz
        h.Vm.RefreshConnections();
        Row(h, "instagram").IsConnected.Should().BeTrue("RefreshConnections son nabzın sonucunu okur");
        h.Vm.UpdateChatHealth();
        Row(h, "instagram").IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Sari_durumda_ust_nokta_sarar_ipucu_hesabi_adiyla_soyler_duzelince_geri_doner()
    {
        var (monitor, pageId, username) = LiveMonitor();
        using var h = Build(monitor);
        Chat(h);                                                        // mevcut sonuç "ok"
        _time.Advance(TimeSpan.FromSeconds(18));
        h.Vm.UpdateChatHealth();
        (h.Vm.ChatHealthState, h.Vm.ChatHealthTooltip).Should().Be(("idle", $"Instagram @{username}: yanıt yok (18 sn)."));
        h.Vm.ShowInstagramStatus.Should().BeTrue();
        h.Vm.InstagramAccounts.Should().ContainSingle().Which.Text.Should().Be($"@{username} yanıt yok (18 sn)");
        var changes = 0;
        h.Vm.InstagramAccounts.CollectionChanged += (_, _) => changes++;
        h.Vm.UpdateChatHealth();
        changes.Should().Be(0, "satırlar değişmediyse koleksiyona dokunulmaz");
        monitor.ObserverFor(pageId).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);    // düzeldi
        h.Vm.UpdateChatHealth();
        h.Vm.ChatHealthState.Should().Be("ok");
        h.Vm.ChatHealthTooltip.Should().StartWith("Chat aktif");
        h.Vm.ShowInstagramStatus.Should().BeFalse();
    }

    [Fact]
    public void Mevcut_sonuc_idle_iken_Instagram_ipucu_mevcut_ipucuyla_birlikte_yazilir()
    {
        var (monitor, _, username) = LiveMonitor();
        using var h = Build(monitor);                                   // hiç yorum yok: mevcut sonuç "idle"
        _time.Advance(TimeSpan.FromSeconds(18));
        h.Vm.UpdateChatHealth();
        h.Vm.ChatHealthState.Should().Be("idle");
        h.Vm.ChatHealthTooltip.Should().StartWith($"Instagram @{username}: yanıt yok (18 sn).\nChat takibi açık").And.EndWith("henüz mesaj yok");
    }

    /// <summary>
    /// Satırlardan biri değişince koleksiyon baştan kurulmaz: yalnız o öğe değiştirilir, değişmeyen satırın örneği
    /// yerinde kalır (onun açık ipucu nabızda kapanmaz). İki hesap: biri canlı (her nabızdan önce yanıt verir, satırı
    /// aynı kalır), biri yanıtsız (sessizlik saniyesi her nabızda artar).
    /// </summary>
    [Fact]
    public void Yalniz_degisen_serit_satiri_yenilenir_oteki_satirin_ornegi_ayni_kalir()
    {
        var (live, silent) = (FacebookOAuthTestKit.NewId(), FacebookOAuthTestKit.NewId());
        var monitor = new InstagramHealthMonitor(_time);
        monitor.Configure(new[]
        {
            new FacebookLinkedPage(live, FacebookOAuthTestKit.NewToken(), $"sayfa-{Guid.NewGuid():N}"),
            new FacebookLinkedPage(silent, FacebookOAuthTestKit.NewToken(), $"sayfa-{Guid.NewGuid():N}"),
        });
        foreach (var pageId in new[] { live, silent })
        {
            monitor.Report(pageId, InstagramAccountState.Starting);
            monitor.SetAccount(pageId, $"hesap-{Guid.NewGuid():N}");
            monitor.ObserverFor(pageId).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        }
        _time.Advance(TimeSpan.FromSeconds(11));                        // ikinci hesap yanıtsız
        monitor.ObserverFor(live).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        using var h = Build(monitor);
        var before = h.Vm.InstagramAccounts.ToArray();
        before.Select(r => r.Text).Should().HaveCount(2).And.Contain(t => t.EndsWith("yanıt yok (11 sn)", StringComparison.Ordinal));

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        h.Vm.InstagramAccounts.CollectionChanged += (_, e) => actions.Add(e.Action);
        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.ObserverFor(live).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        h.Vm.UpdateChatHealth();

        actions.Should().Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Replace);
        h.Vm.InstagramAccounts.Should().HaveCount(2);
        h.Vm.InstagramAccounts[0].Should().BeSameAs(before[0], "değişmeyen canlı satırın örneği aynı kalır");
        h.Vm.InstagramAccounts[1].Text.Should().EndWith("yanıt yok (13 sn)");
    }

    [Fact]
    public void Oturum_yokken_Instagram_kurali_uygulanmaz()
    {
        using var h = Build(LiveMonitor().Monitor);
        h.Sessions.End(h.Sessions.GetActive()!.Id);
        _time.Advance(TimeSpan.FromSeconds(18));
        h.Vm.UpdateChatHealth();
        (h.Vm.ChatHealthState, h.Vm.ChatHealthTooltip).Should().Be(("off", "Chat takibi kapalı (yayın aktif değil)"));
        h.Vm.ShowInstagramStatus.Should().BeFalse();
    }

    [Fact]                                                              // XAML bağ yolu gerçek ViewModel ile çizilerek sınanır (yanlış yol hata vermez, alan boş kalır)
    public void Kenar_cubugunda_sayi_sutunu_ShowViewerCount_ile_gorunur() => ThemeTestHost.RunOnSta(() =>
    {
        var youtube = new PlatformConnectionViewModel("youtube") { IsConnected = true, ShowViewerCount = true, ViewerCount = 1240 };
        var instagram = new PlatformConnectionViewModel("instagram") { IsConnected = true, ShowViewerCount = false };
        var sidebar = new ShellSidebar { DataContext = new SidebarStub { Connections = { youtube, instagram } } };
        ThemeTestHost.Pump();
        sidebar.Measure(new Size(400, 900));
        sidebar.Arrange(new Rect(0, 0, 400, 900));
        sidebar.UpdateLayout();
        Visibility CountOf(PlatformConnectionViewModel row) => Descendants(sidebar).OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, row)
            && BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == nameof(PlatformConnectionViewModel.ViewerCount)).Visibility;
        CountOf(youtube).Should().Be(Visibility.Visible);
        CountOf(instagram).Should().Be(Visibility.Collapsed, "Instagram satırı yeşil ama sayısız");
    }).Should().BeNull();
}
