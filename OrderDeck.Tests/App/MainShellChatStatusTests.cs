using FluentAssertions;
using OrderDeck.App.Services.Chat;
using OrderDeck.App.ViewModels;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;
using Severity = OrderDeck.App.Services.Chat.ChatStatusSeverity;
using State = OrderDeck.Core.Chat.ChatSourceState;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kabuğun birleşik sohbet durum şeridi ve "Sohbeti yenile" komutu (tasarım: Sohbeti yenile §5.4, §5.5).
///
/// NEDEN: şerit dört platformun durumunu tek satırda gösterir ve yenileme düğmesinin evini oluşturur.
/// Kurallar servislerde değil burada birleşir: satır sırası, "Instagram" etiketinin ilk hesaba
/// yapışması, kaynaklar arası "yayın kesildi", üst noktanın sararması, kenar çubuğunun şeritle
/// çelişmemesi ve düğmenin ne zaman meşgul kalıp ne zaman onay işareti göstereceği.
/// Saat elle ilerler; zamanlayıcı beklenmez (testler nabzı ve yenileme tıkını kendileri çağırır).
/// Sayfa kimliği, token, Sayfa adı ve kullanıcı adı üretilir.
/// </summary>
public class MainShellChatStatusTests
{
    private readonly ManualTime _time = new();
    private readonly ChatSourceMonitor _sources;
    private readonly ChatReconnectSignal _reconnect = new();
    private int _reconnectRequests;

    public MainShellChatStatusTests()
    {
        _sources = new ChatSourceMonitor(_time);
        _reconnect.Requested += (_, _) => _reconnectRequests++;
    }

    /// <summary>Kabuğu izleyicilerle kurar; saat elle ilerleyen saattir, bir nabız koşar.</summary>
    private MainShellTestHarness.Harness Build(
        InstagramHealthMonitor? instagram = null, ViewerCountTracker? viewers = null, bool withReconnect = true)
    {
        var h = MainShellTestHarness.Build(
            instagramHealth: instagram, viewers: viewers, chatSources: _sources,
            chatReconnect: withReconnect ? _reconnect : null);
        h.Vm.InstagramNow = () => _time.GetUtcNow();
        h.Vm.UpdateChatHealth();
        return h;
    }

    /// <summary><paramref name="accounts"/> hesaplı Instagram izleyicisi; hepsi canlı.</summary>
    private (InstagramHealthMonitor Monitor, string[] PageIds, string[] Usernames) LiveInstagram(int accounts)
    {
        var pages = Enumerable.Range(0, accounts)
            .Select(_ => new FacebookLinkedPage(
                FacebookOAuthTestKit.NewId(), FacebookOAuthTestKit.NewToken(), $"sayfa-{Guid.NewGuid():N}"))
            .ToArray();
        var usernames = pages.Select(_ => $"hesap-{Guid.NewGuid():N}").ToArray();
        var monitor = new InstagramHealthMonitor(_time);
        monitor.Configure(pages);
        for (var i = 0; i < pages.Length; i++)
        {
            monitor.Report(pages[i].PageId, InstagramAccountState.Starting);
            monitor.SetAccount(pages[i].PageId, usernames[i]);
            monitor.ObserverFor(pages[i].PageId).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        }

        return (monitor, pages.Select(p => p.PageId).ToArray(), usernames);
    }

    /// <summary>Veri yoluna üretilmiş bir yorum basar: üst noktanın mevcut sonucu "ok" olur.</summary>
    private static void Chat(MainShellTestHarness.Harness h) => h.Bus.Publish(new ChatMessage(
        Guid.NewGuid().ToString("N"), "youtube", null, $"izleyici-{Guid.NewGuid():N}", null, null,
        $"mesaj {Guid.NewGuid():N}", 1000, Array.Empty<string>()));

    private static PlatformConnectionViewModel Row(MainShellTestHarness.Harness h, string platform) =>
        h.Vm.Connections.Single(c => c.Platform == platform);

    // ── Şerit ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Strip_lists_platform_rows_then_instagram_rows_with_the_group_label_on_the_first()
    {
        // İzleyiciye ters sırayla yazılır: sıra bildirim sırasından değil kuraldan gelir.
        _sources.Report("facebook", State.Live);
        _sources.Report("tiktok", State.Live);
        _sources.Report("youtube", State.Live);
        var (instagram, pageIds, _) = LiveInstagram(2);

        using var h = Build(instagram);

        h.Vm.ChatStatusRows.Select(r => r.Key).Should().Equal(
            "youtube", "tiktok", "facebook", "instagram:" + pageIds[0], "instagram:" + pageIds[1]);
        // "Instagram" etiketi ilk hesabın satırındadır: şerit alt satıra inerken ondan ayrılmaz.
        h.Vm.ChatStatusRows.Select(r => r.GroupLabel).Should().Equal(null, null, null, "Instagram", null);
    }

    [Fact]
    public void Healthy_rows_show_only_their_name()
    {
        _sources.Report("youtube", State.Live);
        _sources.Report("facebook", State.Live);
        var (instagram, _, usernames) = LiveInstagram(1);

        using var h = Build(instagram);

        h.Vm.ChatStatusRows.Select(r => (r.DisplayText, r.Severity)).Should().Equal(
            ("YouTube", Severity.Live), ("Facebook", Severity.Live), ("@" + usernames[0], Severity.Live));
        h.Vm.ChatStatusRows.Should().OnlyContain(r => !r.IsProblem && !r.IsConnecting);
    }

    [Fact]
    public void Strip_is_visible_for_the_whole_session_and_hidden_without_one()
    {
        // Hiç kaynak satırı yokken de görünür: yenileme düğmesinin yeri orası.
        using var h = Build();
        h.Vm.ChatStatusRows.Should().BeEmpty();
        h.Vm.ShowChatStatus.Should().BeTrue();

        h.Sessions.End(h.Sessions.GetActive()!.Id);
        h.Vm.UpdateChatHealth();

        h.Vm.ShowChatStatus.Should().BeFalse();
    }

    [Fact]
    public void Instagram_account_that_drops_while_youtube_is_live_turns_yellow()
    {
        // Eskiden Instagram yalnız öbür Instagram hesaplarına bakıyordu: YouTube sürerken düşen tek
        // hesap soluk "yayın bitti" görünüyordu.
        _sources.Report("youtube", State.Live);
        var (instagram, pageIds, usernames) = LiveInstagram(1);
        using var h = Build(instagram);

        instagram.ObserverFor(pageIds[0]).RoundCompleted(InstagramPollKind.NoBroadcast, null, TimeSpan.Zero, null);
        h.Vm.UpdateChatHealth();

        var row = h.Vm.ChatStatusRows.Single(r => r.Key == "instagram:" + pageIds[0]);
        row.Severity.Should().Be(Severity.Warning);
        row.DisplayText.Should().StartWith($"@{usernames[0]} yayın kesildi (");
        row.IsProblem.Should().BeTrue();
    }

    [Fact]
    public void A_platform_warning_turns_the_top_dot_yellow_with_its_sentence()
    {
        _sources.Report("youtube", State.NeedsAttention, "YouTube hesabı bağlı değil. Ayarlar > YouTube'dan bağla.");
        using var h = Build();
        Chat(h); // mevcut sonuç "ok"
        h.Vm.DrainPendingChat();

        h.Vm.UpdateChatHealth();

        (h.Vm.ChatHealthState, h.Vm.ChatHealthTooltip)
            .Should().Be(("idle", "YouTube: bağlantı kurulamıyor."));
    }

    [Fact]
    public void Platform_and_instagram_warnings_share_the_top_dot_tooltip()
    {
        _sources.Report("youtube", State.NeedsAttention, "sebep");
        var (instagram, pageIds, usernames) = LiveInstagram(1);
        using var h = Build(instagram);
        Chat(h);
        h.Vm.DrainPendingChat();
        _time.Advance(TimeSpan.FromSeconds(18)); // Instagram hesabı yanıtsız

        h.Vm.UpdateChatHealth();

        h.Vm.ChatHealthState.Should().Be("idle");
        h.Vm.ChatHealthTooltip.Should().Be(
            $"YouTube: bağlantı kurulamıyor.\nInstagram @{usernames[0]}: yanıt yok (18 sn).");
    }

    [Fact]
    public void Sidebar_rows_follow_the_monitor_and_facebook_can_be_connected()
    {
        // Kenar çubuğu izleyici sayısı kaydına bakıyordu: Facebook hiç yeşil olmuyor, yayını kesilen
        // YouTube bayat kayıt yüzünden yeşil kalıp şeritle çelişiyordu.
        var viewers = new ViewerCountTracker();
        viewers.Report("youtube", 1240);
        viewers.Report("tiktok", 40);
        _sources.Report("facebook", State.Live);
        _sources.Report("tiktok", State.Live);
        _sources.Report("youtube", State.NoBroadcast);

        using var h = Build(viewers: viewers);

        (Row(h, "facebook").IsConnected, Row(h, "facebook").ShowViewerCount).Should().Be((true, false));
        (Row(h, "tiktok").IsConnected, Row(h, "tiktok").ShowViewerCount, Row(h, "tiktok").ViewerCount)
            .Should().Be((true, true, 40));
        (Row(h, "youtube").IsConnected, Row(h, "youtube").ShowViewerCount).Should().Be((false, false));
    }

    // ── Yenileme ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Refresh_requests_a_reconnect_and_goes_busy()
    {
        _sources.Report("youtube", State.Live);
        using var h = Build();
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeTrue();

        h.Vm.RefreshChatCommand.Execute(null);

        _reconnectRequests.Should().Be(1);
        (h.Vm.IsRefreshingChat, h.Vm.ChatRefreshDone).Should().Be((true, false));
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeFalse("meşgulken yeniden basılamaz");
    }

    [Fact]
    public void Refresh_shows_connecting_rows_at_once()
    {
        // Servisler isteği alır almaz "bağlanıyor" bildirir; şerit nabzı (5 sn) beklemeden gösterir.
        _sources.Report("youtube", State.Live);
        _reconnect.Requested += (_, _) => _sources.Report("youtube", State.Connecting);
        using var h = Build();

        h.Vm.RefreshChatCommand.Execute(null);

        h.Vm.ChatStatusRows.Single().Should().Match<ChatStatusRow>(r =>
            r.DisplayText == "YouTube bağlanıyor" && r.IsConnecting);
    }

    [Fact]
    public void Refresh_cannot_run_without_a_session_or_while_busy()
    {
        using (var noSignal = Build(withReconnect: false))
            noSignal.Vm.RefreshChatCommand.CanExecute(null).Should().BeFalse("yenileme sinyali verilmedi");

        using var h = Build();
        h.Vm.IsStreamActive = false;
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeFalse("yayın oturumu yok");

        h.Vm.IsStreamActive = true;
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Refresh_ends_with_a_check_mark_when_no_problem_row_remains()
    {
        _sources.Report("youtube", State.Live);
        _reconnect.Requested += (_, _) => _sources.Report("youtube", State.Connecting);
        using var h = Build();
        h.Vm.RefreshChatCommand.Execute(null);

        // Yarım saniyede bağlandı ama düğme en az 1 sn meşgul kalır.
        _sources.Report("youtube", State.Live);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        h.Vm.TickChatRefresh();
        (h.Vm.IsRefreshingChat, h.Vm.ChatRefreshDone).Should().Be((true, false));

        _time.Advance(TimeSpan.FromMilliseconds(500));
        h.Vm.TickChatRefresh();
        (h.Vm.IsRefreshingChat, h.Vm.ChatRefreshDone).Should().Be((false, true));
        // Onay işareti görünürken düğme etkindir: devre dışı kalsaydı işaret soluk çizilirdi.
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeTrue();

        _time.Advance(TimeSpan.FromSeconds(1.5));
        h.Vm.TickChatRefresh();
        (h.Vm.IsRefreshingChat, h.Vm.ChatRefreshDone).Should().Be((false, false));
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Refresh_ends_without_a_check_mark_when_a_dim_problem_row_remains()
    {
        // Bu oturumda hiç bağlanmamış kaynağın "bağlantı koptu" satırı soluktur ama sorundur: onay
        // işareti gösterilmez, sorunu şerit söyler.
        _sources.Report("youtube", State.Retrying, retryAt: _time.GetUtcNow().AddMinutes(1));
        using var h = Build();
        h.Vm.ChatStatusRows.Single().Should().Match<ChatStatusRow>(r => r.Severity == Severity.Neutral && r.IsProblem);
        h.Vm.RefreshChatCommand.Execute(null);

        _time.Advance(TimeSpan.FromSeconds(1));
        h.Vm.TickChatRefresh();

        (h.Vm.IsRefreshingChat, h.Vm.ChatRefreshDone).Should().Be((false, false));
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Refresh_gives_up_after_fifteen_seconds_when_a_source_keeps_connecting()
    {
        _sources.Report("youtube", State.Connecting);
        using var h = Build();
        h.Vm.RefreshChatCommand.Execute(null);

        _time.Advance(TimeSpan.FromSeconds(14));
        h.Vm.TickChatRefresh();
        h.Vm.IsRefreshingChat.Should().BeTrue();

        _time.Advance(TimeSpan.FromSeconds(1));
        h.Vm.TickChatRefresh();

        (h.Vm.IsRefreshingChat, h.Vm.ChatRefreshDone).Should().Be((false, false));
    }

    [Fact]
    public void Ending_the_session_cancels_a_running_refresh()
    {
        _sources.Report("youtube", State.Connecting);
        using var h = Build();
        h.Vm.RefreshChatCommand.Execute(null);

        h.Vm.IsStreamActive = false;

        (h.Vm.IsRefreshingChat, h.Vm.ChatRefreshDone).Should().Be((false, false));
        h.Vm.ShowChatStatus.Should().BeFalse("oturum bitince şerit ve düğme birlikte kalkar");
        // Yeni oturumda düğme yeniden basılabilir (eski yenileme asılı kalmadı).
        h.Vm.IsStreamActive = true;
        h.Vm.RefreshChatCommand.CanExecute(null).Should().BeTrue();
    }
}
