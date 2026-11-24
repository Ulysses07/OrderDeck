using System.Net;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;
using static OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Oturum başlangıcında ve "Sohbeti yenile"de uyanma (tasarım: Sohbeti yenile §5.2, §5.3).
///
/// NEDEN: servis yayın oturumunu 60 sn uyuyarak yokluyordu; "Yayın Başlat"tan sonra Instagram sohbeti
/// bir dakikaya kadar gecikiyordu. Bir kez okunamayan hesap ise 5 dakika bekliyordu ve bunu kesmenin
/// tek yolu oturumu bitirip açmaktı. Yenileme BEKLEYEN döngüleri uyandırır; sağlıklı çalışan okuyucuya
/// dokunmaz (filigranı korunur, yorum kaybolmaz) ve hız sınırı beklemesini kesmez (kesmek sınırı
/// uzatır).
/// </summary>
public sealed partial class InstagramMultiAccountHostedServiceTests
{
    private const string ReconnectPrefix = "[ChatReconnect] instagram: ";

    private readonly ChatReconnectSignal _reconnect = new();

    /// <summary>Eşzamanlı okunan anahtar: Graph kuralları havuz iş parçacıklarında çalışır.</summary>
    private sealed class Switch(bool on)
    {
        private volatile bool _on = on;

        public bool On
        {
            get => _on;
            set => _on = value;
        }
    }

    /// <summary>Oturum AÇMADAN, yenileme sinyaline bağlı servisi kurup başlatır.</summary>
    private Task StartWithReconnectAsync(InstagramChatTimings timings)
    {
        _service = new InstagramChatHostedService(
            _oauth, _bus, _logs, spamFilter: null, _sessions, _kit.HttpFactory, _health, _time, timings, _reconnect);
        return _service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Session_start_wakes_the_service_without_waiting_for_the_offline_interval()
    {
        var (page, feed) = await OneAsync();
        await StartWithReconnectAsync(Fast with { IdleWhenOffline = Long }); // oturum yok: üst döngü uzun beklemede

        StartSession();

        await feed.PrimedAsync(); // uzun bekleme dolmadan ilk istek geldi
        await Until(() => State(page) == Live);
    }

    [Fact]
    public async Task A_new_session_cuts_the_wait_that_follows_the_previous_one()
    {
        var (page, feed) = await OneAsync();
        var first = StartSession();
        await StartWithReconnectAsync(Fast with { IdleAfterPollerExit = Long });
        await feed.PrimedAsync();

        EndSession(first);
        await Until(() => State(page) == Stopped);
        var served = feed.Served;
        StartSession();

        await Until(() => feed.Served > served); // oturum sonrası uzun bekleme dolmadan yeniden yoklandı
        await Until(() => State(page) == Live);
    }

    [Fact]
    public async Task Refresh_retries_an_account_whose_resolve_failed()
    {
        var (page, feed) = await OneAsync();
        var broken = new Switch(on: true);
        _kit.Graph.Map(
            u => broken.On && IsResolve(u, page),
            _ => FakeGraph.Error(1, HttpStatusCode.InternalServerError));
        StartSession();
        await StartWithReconnectAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(page) == ResolveFailed); // beş dakikalık beklemede

        broken.On = false; // ağ düzeldi
        _reconnect.Request();

        await feed.PrimedAsync(); // uzun bekleme dolmadan
        await Until(() => State(page) == Live);
        Lines(ReconnectPrefix).Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_marks_a_waiting_account_as_connecting()
    {
        var (page, _) = await OneAsync();
        _kit.Graph.Map(u => IsResolve(u, page), _ => FakeGraph.Error(1, HttpStatusCode.InternalServerError));
        StartSession();
        await StartWithReconnectAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(page) == ResolveFailed);
        var resolves = ResolveRequests(page);

        _reconnect.Request();

        // Bekleyen hesap "bağlanıyor"a döner ve hemen yeniden dener. Ağ hâlâ bozuk: aynı duruma düşer.
        await Until(() => Lines(AccountPrefix).Any(
            l => l.EndsWith("ResolveFailed -> Starting", StringComparison.Ordinal)));
        await Until(() => ResolveRequests(page) > resolves && State(page) == ResolveFailed);
    }

    [Fact]
    public async Task Refresh_leaves_a_healthy_poller_and_its_watermark_alone()
    {
        var (page, feed) = await OneAsync();
        StartSession();
        await StartWithReconnectAsync(Fast);
        var before = await FlowAsync(feed);
        var starts = PollerStarts(page);
        var transitions = Lines(AccountPrefix).Count;

        _reconnect.Request();

        var after = await FlowAsync(feed);
        PollerStarts(page).Should().Be(starts, "sağlıklı okuyucu yeniden kurulmaz");
        Lines(AccountPrefix).Should().HaveCount(transitions, "sağlıklı hesabın durumu değişmez");
        State(page).Should().Be(Live);
        // Filigran yerinde: önceki yorum yinelenmedi, sonraki kaybolmadı.
        _received.Count(m => m.ExternalId == before.Id).Should().Be(1);
        _received.Count(m => m.ExternalId == after.Id).Should().Be(1);
    }

    [Fact]
    public async Task Refresh_does_not_cut_a_rate_limit_wait()
    {
        var (page, feed) = await OneAsync();
        feed.Intercept = _ => FakeGraph.Error(4); // kota: okuyucu kendi içinde bekler (başlık yokken 1 dk)
        StartSession();
        await StartWithReconnectAsync(Fast);
        await Until(() => State(page) == RateLimited);
        var served = feed.Served;
        var starts = PollerStarts(page);

        _reconnect.Request();

        await Task.Delay(1500); // yoklama aralığından (1 sn) uzun: bekleme kesilseydi yeni istek gelirdi
        feed.Served.Should().Be(served, "hız sınırı beklemesi kesilmez: kesmek sınırı uzatır");
        PollerStarts(page).Should().Be(starts);
        State(page).Should().Be(RateLimited);
    }

    [Fact]
    public async Task Refresh_without_a_session_does_nothing()
    {
        var (_, feed) = await OneAsync();
        await StartWithReconnectAsync(Fast with { IdleWhenOffline = Long });

        _reconnect.Request();

        await Task.Delay(300);
        feed.Served.Should().Be(0, "yayın oturumu açılmadan Graph'a gidilmez");
        Lines(ReconnectPrefix).Should().ContainSingle().Which.Should().Contain("oturum yok");
    }
}
