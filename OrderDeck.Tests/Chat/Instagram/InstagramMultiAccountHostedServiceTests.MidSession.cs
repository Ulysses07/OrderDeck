using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using FluentAssertions;
using OrderDeck.App.Services.Instagram;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Tests.TestHelpers;
using static OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Oturum açıkken bağlı Sayfa listesinin değişmesi: hesap ekleme, çıkarma, tekten çoka geçiş, listenin boşalıp
/// yeniden dolması (çoklu hesap tasarımı §8.3.2 üst döngü 4. adım, §8.3.3, TK14).
///
/// NEDEN: ikinci hesap yayın ortasında eklenir; kaldırılan hesabın yoklaması oturum sonunu beklemeden durmalı,
/// eklenen hesap yeni oturumu beklemeden başlamalı ve bunların hiçbiri öteki hesapların sohbetini kesmemelidir.
/// Çalışan hesabın poller'ına ve filigranına dokunulmaz (ilk çekimi yeniden yutulmaz); tek hesaptan çoka geçerken
/// çalışan hesap kulvara yorum kaybetmeden ve beklemeden taşınır.
///
/// Servis listeyi izleme aralığında (testlerde 50 ms) yeniden okur; değişiklikler gerçek akışlarla yapılır (ek Sayfa
/// ekle / kaldır, bağlantıyı kaldır, izinleri güncelle). Tek istisna yeniden bağlanmadır: tarayıcı onayı yerine demet
/// depoya yazılır (<see cref="ReconnectAsync"/>).
/// </summary>
public sealed partial class InstagramMultiAccountHostedServiceTests
{
    /// <summary>
    /// Oturum içinde bir ek Sayfa bağlar: kimlik ve adları üretir, Sayfayı Instagram hesabıyla Graph'a tanıtır,
    /// beslemesini bağlar ve SONRA gerçek ekleme akışıyla ekler. NEDEN bu sıra: servis yeni Sayfayı izleme
    /// aralığında görüp hemen çözer ve yoklar; Graph o an Sayfayı tanımıyorsa hesap "bağlı değil"e düşerdi. Dönen
    /// kayıt saklanan Sayfa token'ını taşır.
    /// </summary>
    private async Task<(SeededPage Page, InstagramLiveFeed Feed)> AddPageAsync()
    {
        var (pageId, pageName) = (FacebookOAuthTestKit.NewId(), $"sayfa-{Guid.NewGuid():N}");
        var (igUserId, igUsername) = (FacebookOAuthTestKit.NewId(), $"hesap-{Guid.NewGuid():N}");
        _kit.Graph.AddPage(pageId, pageName, igUserId, igUsername);
        var feed = new InstagramLiveFeed(_kit.Graph, igUserId);

        var linked = await _oauth.AddExtraPageAsync(pageId, pageName);

        return (new SeededPage(linked, igUserId, igUsername), feed);
    }

    /// <summary>
    /// Aynı Sayfayı yeniden bağlar: demeti (<paramref name="pageToken"/> verilirse birincil Sayfanın token'ı o
    /// olarak) depoya yazar. Bağlantısı kaldırılmış servis bellekte demet tutmaz; dosyayı izleme turunda görür.
    /// </summary>
    private Task ReconnectAsync(FacebookTokenBundle saved, string? pageToken = null) =>
        _kit.Store.SaveAsync(pageToken is null ? saved : saved with { PageAccessToken = pageToken });

    /// <summary>Operatörün o an gördüğü: sağlık şeridi, oturum açıkken ve servisin saatiyle.</summary>
    private InstagramHealthView View() =>
        InstagramHealthPresenter.Evaluate(_health.Snapshot(), _time.GetUtcNow(), sessionActive: true);

    [Fact]
    public async Task Kaldirilan_hesabin_istekleri_kesilir_oteki_kesintisiz_surer()
    {
        var (a, b, feedA, feedB) = await TwoAsync();
        await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());

        (await _oauth.RemoveExtraPageAsync(b.Page.PageId)).Should().BeTrue();

        await Until(() => Health(b) is null); // döngü bitti, girdi silindi
        var served = feedB.Served;
        await FlowAsync(feedA);
        await FlowAsync(feedA); // A en az iki tur daha attı, yorumları aktı
        feedB.Served.Should().Be(served);
        PollerStarts(a).Should().Be(1);
    }

    [Fact]
    public async Task Ucuncu_sayfa_eklenince_ilk_iki_hesabin_filigrani_bozulmaz()
    {
        var (a, b, feedA, feedB) = await TwoAsync();
        await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());

        var (_, feedC) = await AddPageAsync();

        await feedC.PrimedAsync(); // yalnız yeni döngü başladı
        await Task.WhenAll(FlowAsync(feedA), FlowAsync(feedB));
        (PollerStarts(a), PollerStarts(b)).Should().Be((1, 1)); // ilk çekimleri yeniden yutulmadı
        _health.Snapshot().Should().HaveCount(3);
    }

    [Fact]
    public async Task Tekten_coka_geciste_yorum_kaybolmaz_ve_otuz_saniye_beklenmez()
    {
        var (a, feedA) = await OneAsync();
        await RunAsync(Fast with { IdleAfterPollerExit = Long });
        await feedA.PrimedAsync();
        await feedA.NextAsync(); // eski poller az önce yanıt aldı; sıradaki isteğine ~1 sn var
        var during = feedA.Add(); // ilk kez yeni poller'ın ilk çekiminde görünür

        var (b, _) = await AddPageAsync();

        await Until(() => PollerStarts(a) == 2 && Got(during)); // Long beklenmedi; devredilen filigranla ilk çekim yutulmadı
        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername); // artık kulvardan
        _received.Count(m => m.ExternalId == during.Id).Should().Be(1);
        await Until(() => State(b) == Live); // 1 → 2'de de Configure çağrıldı: yeni Sayfanın girdisi var ve canlı
        _health.Snapshot().Select(h => h.PageId).Should().Equal(a.Page.PageId, b.Page.PageId);
    }

    [Fact]
    public async Task Liste_bosalip_dolunca_girdi_starting_olur_ve_yanit_yok_45_saniye_sonra_gorunur()
    {
        var (page, feed) = await OneAsync();
        await RunAsync();
        await Until(() => State(page) == Live); // son temas şimdi
        var saved = (await _kit.Store.LoadAsync())!;
        await _oauth.DisconnectAsync();
        await Until(() => State(page) == Stopped);
        _time.Advance(TimeSpan.FromSeconds(60)); // önceki temas 60 sn eskidi
        feed.Intercept = _ => FakeGraph.Error(1, HttpStatusCode.InternalServerError); // yeni bağlantı yalnız geçici hata veriyor

        await ReconnectAsync(saved);

        await Until(() => State(page) == Starting);
        View().Rows.Single().StateText.Should().Be("bağlanıyor"); // önceki temas yüzünden anında "yanıt yok" değil
        _time.Advance(TimeSpan.FromSeconds(46));
        View().Rows.Single().StateText.Should().Be("yanıt yok (46 sn)");
        View().ShowStrip.Should().BeTrue(); // "bu oturumda canlıydı" korundu: tek Sayfada da şerit çıkar (R13)
    }

    [Fact]
    public async Task Token_degisince_saglikli_hesabin_istekleri_kesilmez_ve_ilk_cekimi_yeniden_yutulmaz()
    {
        var (page, feed) = await OneAsync();
        await RunAsync();
        await feed.PrimedAsync();

        await _kit.ReauthorizeAllAsync(_oauth);

        await FlowAsync(feed);
        PollerStarts(page).Should().Be(1);
        feed.Tokens.Should().Equal(page.Page.PageAccessToken); // eski token'la sürüyor
    }

    [Fact]
    public async Task Liste_bosalinca_istekler_kesilir_dolunca_izleme_araliginda_baslar()
    {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        await RunAsync(Fast with { IdleWhenOffline = Long });
        await Until(() => State(page) == Live);
        var saved = (await _kit.Store.LoadAsync())!;
        await _oauth.DisconnectAsync();
        await Until(() => State(page) == Stopped); // girdi silinmedi
        var served = feed.ServedFor(old);
        feed.NoBroadcast = true;
        var fresh = FacebookOAuthTestKit.NewToken();

        await ReconnectAsync(saved, fresh);

        await Until(() => State(page) == BroadcastEnded); // Long beklenmedi; "bu oturumda canlıydı" korundu (ResetSession yok)
        feed.ServedFor(fresh).Should().BeGreaterThan(0);
        feed.ServedFor(old).Should().Be(served); // boşalmadan sonra eski bağlantıyla tek istek yok
        ResolveRequests(page).Should().Be(2); // yeni token: hesap bir kez yeniden çözüldü (tur başında token karşılaştırması)
    }

    [Fact]
    public async Task Ek_sayfa_kaldirilinca_kalan_hesap_etiketli_surer_ve_yeniden_eklemede_yeniden_kurulmaz()
    {
        var (a, b, feedA, feedB) = await TwoAsync();
        await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        await _oauth.RemoveExtraPageAsync(b.Page.PageId);
        await Until(() => Health(b) is null);
        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername); // çoktan teke dönülmez
        var served = feedB.Served;

        await _oauth.AddExtraPageAsync(b.Page.PageId, b.Page.PageName); // kapı kuruluyken ekleme

        await Until(() => feedB.Served > served);
        PollerStarts(a).Should().Be(1); // kulvarlı poller yeniden kurulmadı
    }

    [Fact] // §8.3.3 "çoktan teke" ikinci yarı; TK6
    public async Task Ek_sayfa_kaldirildiktan_sonraki_oturum_tek_hesap_kipinde_etiketsiz_kurulur()
    {
        var (a, b, feedA, feedB) = await TwoAsync();
        var session = await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        await _oauth.RemoveExtraPageAsync(b.Page.PageId);
        await Until(() => Health(b) is null);
        EndSession(session);
        await Until(() => State(a) == Stopped);

        StartSession();

        Message(await FlowAsync(feedA)).SourceAccount.Should().BeNull(); // kapı önceki oturumdan taşınmadı: tek Sayfa, etiket yok
    }

    // ── Liste boşalınca ve yeniden dolunca ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Liste boşalırken de oturum sonundaki sıra geçerlidir: hesap döngülerinin BİTMESİ → kapının kapanması (özet
    /// satırı) → girdilerin durması. Tanık, oturum sonu testindeki gibi izleyicinin saatidir: test yalnız
    /// <c>StopAll</c>'ın saati okumasına dayanır; son okuma girdileri durduran çağrınındır ve o an kapının özet satırı
    /// yazılmış olmalıdır. Tanık yalnız kapı özetinin StopAll'dan önce yazıldığını gösterir; kapının ya da girdilerin
    /// döngüler bitmeden kapanması bu testle yakalanmaz.
    /// </summary>
    [Fact]
    public async Task Liste_bosalirken_once_donguler_biter_sonra_kapi_kapanir_en_son_girdiler_durur()
    {
        var (_, _, feedA, feedB) = await TwoAsync();
        var clock = new SamplingClock(() => Lines("[InstagramMergeGate]").Count);
        var health = new InstagramHealthMonitor(clock);
        StartSession();
        await StartServiceAsync(health, _time);
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync()); // iki girdi de "durdu"dan çıktı

        await _oauth.DisconnectAsync();

        await Until(() => health.Snapshot() is { Count: 2 } entries && entries.All(h => h.State == Stopped)); // silinmediler
        clock.LastSample.Should().Be(1); // izleyicinin son saat okuması StopAll'ındır: özet satırı o an yazılmıştı
    }

    /// <summary>
    /// Listenin boşalması tam sökümdür: kapı kapanır. Liste yeniden dolunca kurulum baştan, o anki Sayfa sayısına göre
    /// yapılır: tek Sayfayla dolduysa kapı kurulmaz, mesaj etiketsizdir. ("Çoktan teke dönülmez" yalnız liste
    /// boşalmadan azalan Sayfa sayısını anlatır.)
    /// </summary>
    [Fact]
    public async Task Liste_bosalinca_kapi_kapanir_tek_sayfayla_dolunca_kapisiz_ve_etiketsiz_kurulur()
    {
        var (a, b, feedA, feedB) = await TwoAsync();
        await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        var saved = (await _kit.Store.LoadAsync())!;
        await _oauth.DisconnectAsync();
        await Until(() => State(a) == Stopped && State(b) == Stopped);
        Lines("[InstagramMergeGate]").Should().ContainSingle(); // kapı kapandı: özet satırı

        await ReconnectAsync(saved with { ExtraPages = null }); // yalnız birincil Sayfa yeniden bağlandı

        Message(await FlowAsync(feedA)).SourceAccount.Should().BeNull(); // kapı kurulmadı: etiket yok
        Health(b).Should().BeNull(); // artık bağlı olmayan Sayfanın girdisi silindi
    }

    /// <summary>
    /// Bağlantı kaldırılıp BAŞKA bir Sayfayla yeniden kurulursa girdi yenidir (§12 R13): eski Sayfanın girdisi silinir,
    /// yeni Sayfa onun "bu oturumda canlıydı" bilgisini devralmaz. Yeniden dolan liste için girdiler yapılandırılmasaydı
    /// yeni Sayfanın girdisi olmaz, hesap hiçbir yerde görünmezdi.
    /// </summary>
    [Fact]
    public async Task Liste_bosalip_baska_sayfayla_dolunca_eski_girdi_silinir_yenisi_canliydi_bilgisini_devralmaz()
    {
        var (old, _) = await OneAsync();
        await RunAsync();
        await Until(() => State(old) == Live);
        var saved = (await _kit.Store.LoadAsync())!;
        await _oauth.DisconnectAsync();
        await Until(() => State(old) == Stopped);
        var other = new FacebookLinkedPage(
            FacebookOAuthTestKit.NewId(), FacebookOAuthTestKit.NewToken(), $"sayfa-{Guid.NewGuid():N}");
        var igUserId = FacebookOAuthTestKit.NewId();
        _kit.Graph.AddPage(other.PageId, other.PageName, igUserId, $"hesap-{Guid.NewGuid():N}");
        new InstagramLiveFeed(_kit.Graph, igUserId).NoBroadcast = true; // yeni hesapta yayın yok

        await _kit.Store.SaveAsync(
            saved with { PageId = other.PageId, PageAccessToken = other.PageAccessToken, PageName = other.PageName });

        // BroadcastEnded değil: "bu oturumda canlıydı" eski Sayfanın girdisinde kaldı.
        await Until(() => _health.Snapshot().Any(h => h.PageId == other.PageId && h.State == WaitingForBroadcast));
        _health.Snapshot().Should().ContainSingle(); // eski Sayfanın girdisi silindi
    }

    // ── Kapıya devir ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kapı, poller'ı çalışmayan (beklemedeki) döngüye takılınca hiçbir şey durdurulmaz, bekleme de kesilmez; yalnız
    /// sonraki kurulum kulvarı kullanır. Bekleme kesilseydi ikinci hesabı eklemek, kalıcı hatada bekleyen hesabı
    /// süresi dolmadan yeniden denetirdi.
    /// </summary>
    [Fact]
    public async Task Tekten_coka_geciste_beklemedeki_hesabin_beklemesi_kesilmez_sonraki_kurulumu_kulvardan_yazar()
    {
        var (a, feedA) = await OneAsync();
        var old = a.Page.PageAccessToken;
        feedA.Intercept = token => token == old ? FakeGraph.Error(190) : null;
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => Health(a)?.RetryAt is not null); // kalıcı hata beklemesinde: poller'ı çalışmıyor

        var (_, feedB) = await AddPageAsync();

        await FlowAsync(feedB);
        await FlowAsync(feedB); // eklenen hesap iki tur attı: kapı ondan önce kuruldu ve A'nın döngüsüne takıldı
        State(a).Should().Be(Faulted);
        PollerStarts(a).Should().Be(1); // bekleme kesilmedi, poller yeniden kurulmadı
        await _kit.ReauthorizeAllAsync(_oauth); // token değişti: bekleme kesilir, poller yeni token'la kurulur
        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername); // sonraki kurulum kulvardan
    }

    /// <summary>
    /// Filigran yalnız kapıya devirde aktarılır. Devralan poller bitince sonraki kurulum yine taze filigranla başlar ve
    /// ilk çekimi yutulur; devredilen filigran döngüde kalsaydı her yeniden kurulum, hesap susarken yazılmış yorumları
    /// geçmişten basardı.
    /// </summary>
    [Fact]
    public async Task Devirden_sonraki_yeniden_kurulumda_filigran_yine_tazedir_ilk_cekim_yutulur()
    {
        var (a, feedA) = await OneAsync();
        await RunAsync(); // 5 dakikalık bekleme 200 ms
        await feedA.PrimedAsync();
        await AddPageAsync();
        await Until(() => PollerStarts(a) == 2); // devir: A'nın poller'ı filigranıyla kulvara taşındı
        feedA.Intercept = _ => FakeGraph.Error(190); // A kalıcı hatayla çıkar, 200 ms'de bir yeniden kurulur
        await Until(() => PollerStarts(a) >= 3); // devralan poller bitti
        var gap = feedA.Add(); // A'nın yanıt alan poller'ı yokken yazıldı

        feedA.Intercept = null;

        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername);
        Got(gap).Should().BeFalse(); // filigran taze: yeni poller'ın ilk çekimi yutuldu
    }

    /// <summary>
    /// Zaman damgası İLK okunduğunda okuyanı test bırakana dek tutar (sistem saati; üst sınır
    /// <see cref="TestPatience.Upper"/>). Servis damgayı ilk kez, hesabın sonuç veren ilk turunun sonunda, kota satırını
    /// yazarken okur: o turun poller'ı böylece "sonucunu bildirdi ama henüz bitmedi" anında durur.
    /// </summary>
    private sealed class HeldFirstStamp : TimeProvider
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        /// <summary>Damga ilk kez okundu: okuyan tutuluyor (ya da test onu bıraktı).</summary>
        public bool Holding => Volatile.Read(ref _reads) >= 1;

        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _reads) == 1) _released.Task.Wait(TestPatience.Upper);
            return base.GetTimestamp();
        }

        public void Release() => _released.SetResult();
    }

    /// <summary>
    /// Kapı, kalıcı hatayla kendi bitmekte olan poller'ı devretmez: o poller'ı kapı durdurmamıştır. Döngü kalıcı hatanın
    /// 5 dakikalık beklemesine girer, kapıyı sonraki kurulum kullanır. Devredilseydi hesap beklemeden yeniden kurulur,
    /// aynı hatayı bir kez daha alırdı.
    /// </summary>
    [Fact]
    public async Task Kapi_takilirken_kalici_hatayla_bitmekte_olan_poller_devredilmez_beklemesi_gecerlidir()
    {
        var (a, feedA) = await OneAsync();
        feedA.Intercept = _ => FakeGraph.Error(190);
        var time = new HeldFirstStamp(); // A'nın poller'ı kalıcı hata turunu bildirdikten sonra, bitmeden tutulur
        StartSession();
        await StartServiceAsync(_health, time, Fast with { IdleAfterFatal = Long });
        await Until(() => State(a) == Faulted); // tur izleyiciye işlendi; poller henüz bitmedi
        // Faulted, poller damgayı okumadan önce görünür. Tek Sayfada kapı yoktur: damgayı bu ana dek okuyabilecek tek
        // yer poller'ın kota satırıdır; kapı kurucusu da aynı saati okuduğu için Sayfa ancak tutulma görülünce eklenir.
        await Until(() => time.Holding);
        var (_, feedB) = await AddPageAsync();
        await feedB.PrimedAsync(); // eklenen hesap yoklanıyor: kapı ondan önce kuruldu ve A'nın döngüsüne takıldı

        time.Release(); // poller kalıcı hatayla biter

        await Until(() => Health(a)?.RetryAt is not null); // kalıcı hata beklemesine girdi
        await FlowAsync(feedB);
        await FlowAsync(feedB); // eklenen hesap iki tur daha attı
        PollerStarts(a).Should().Be(1); // devredilmedi: beklemeden yeniden kurulmadı
    }

    // ── İzleme turu ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// İzleme beklemesini test bırakana dek tutar: <see cref="Interval"/> süresiyle kurulan zamanlayıcı kendiliğinden
    /// dolmaz, öbür beklemeler gerçek süre bekler (sistem saati). İzleme turunun ANI böylece testin elindedir: "iki
    /// izleme turu arasında şu oldu" kurgusu zamanlamaya yaslanmaz.
    /// </summary>
    private sealed class HeldWatch : TimeProvider
    {
        /// <summary>Servise izleme aralığı olarak verilir; servis başka hiçbir beklemeyi bu süreyle kurmaz.</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        // Tutulan beklemeleri bitirecek çağrılar, kuruluş sırasıyla.
        private readonly ConcurrentQueue<Action> _held = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Interval) return base.CreateTimer(callback, state, dueTime, period);

            _held.Enqueue(() => callback(state));
            return new Unfired();
        }

        /// <summary>Servis izleme beklemesine girene dek bekler ve beklemeyi bitirir: servis bir izleme turu atar.</summary>
        public async Task TickAsync()
        {
            Action? fire = null;
            await Until(() => _held.TryDequeue(out fire));
            // Havuzdan, gerçek zamanlayıcı gibi: servisin devamı testin iş parçacığında koşmaz.
            await Task.Run(fire!);
        }

        /// <summary>Hiç dolmayan zamanlayıcı; beklemeyi ancak <see cref="TickAsync"/> ya da iptal bitirir.</summary>
        private sealed class Unfired : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
                // Bırakılacak kaynak yok.
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Aynı izleme turunda hem çıkan hem eklenen Sayfa varsa önce çıkarma işlenir: çıkan Sayfanın döngüsü BİTMEDEN
    /// (sahipliği bırakılmadan) eklenenin döngüsü başlamaz. Eklenen Sayfa çıkanın Instagram hesabına çözülüyorsa hesabı
    /// hemen devralır; sıra ters olsaydı "hesabı başka Sayfa yokluyor" diye atlanıp 5 dakika bekleyebilirdi.
    /// </summary>
    [Fact]
    public async Task Ayni_izleme_turunda_cikan_ve_eklenen_sayfada_once_cikarma_islenir_eklenen_hesabi_hemen_devralir()
    {
        var (a, b, _, feedB) = await TwoAsync();
        var time = new HeldWatch();
        StartSession();
        await StartServiceAsync(_health, time, Fast with { WatchInterval = HeldWatch.Interval, IdleAfterFatal = Long });
        await Until(() => State(b) == Live); // hesabı B yokluyor
        var (pageId, pageName) = (FacebookOAuthTestKit.NewId(), $"sayfa-{Guid.NewGuid():N}");
        _kit.Graph.AddPage(pageId, pageName, b.IgUserId, b.IgUsername); // yeni Sayfa B'nin Instagram hesabına çözülür
        (await _oauth.RemoveExtraPageAsync(b.Page.PageId)).Should().BeTrue();
        var added = await _oauth.AddExtraPageAsync(pageId, pageName);

        await time.TickAsync(); // tek tur: B çıktı, yeni Sayfa eklendi

        await Until(() => feedB.ServedFor(added.PageAccessToken) > 0); // atlanmadı: hesabı artık yeni Sayfa yokluyor
        _health.Snapshot().Select(h => h.PageId).Should().Equal(a.Page.PageId, pageId); // B'nin girdisi silindi, yenisi eklendi
    }

    /// <summary>
    /// Hesap döngüsü 1. adımında Sayfasını bağlı bulamazsa kendi biter. Sayfa iki izleme turu ARASINDA kaldırılıp
    /// yeniden eklenirse izleme Sayfa kümesinde fark görmez; küme çalışan döngülerle değil de başlatılmış döngülerle
    /// karşılaştırılsaydı Sayfa oturum sonuna dek yoklanmazdı. Eski döngünün bittiği an üretim kodu değişmeden
    /// gözlenemez: yeniden ekleme, döngünün <c>ResolveFailed</c>'dan sonraki Sayfa aramasından önce yetişirse eski döngü
    /// Sayfasını bulup kendisi <c>Live</c>'a çıkar ve test korunan yolu sınamadan geçer.
    /// </summary>
    [Fact]
    public async Task Iki_izleme_turu_arasinda_kaldirilip_yeniden_eklenen_sayfanin_biten_dongusunun_yerine_yenisi_baslar()
    {
        var (_, b, _, _) = await TwoAsync();
        // B'nin döngüsü çözümleme isteğinde tutulur; bırakılınca istek düşer ve döngü Sayfa listesine yeniden bakar.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = 1;
        _kit.Graph.Map(
            u => IsResolve(u, b) && Volatile.Read(ref failing) == 1,
            _ =>
            {
                release.Task.Wait(TestPatience.Upper);
                return FakeGraph.Error(1, HttpStatusCode.InternalServerError);
            });
        var time = new HeldWatch();
        StartSession();
        await StartServiceAsync(_health, time, Fast with { WatchInterval = HeldWatch.Interval });
        await Until(() => ResolveRequests(b) == 1); // B'nin döngüsü çözümleme isteğinde bekliyor

        (await _oauth.RemoveExtraPageAsync(b.Page.PageId)).Should().BeTrue();
        release.SetResult();
        await Until(() => State(b) == ResolveFailed); // istek düştü: döngü Sayfasını listede bulamadı, bitiyor
        Volatile.Write(ref failing, 0);
        await _oauth.AddExtraPageAsync(b.Page.PageId, b.Page.PageName); // izleme turu atılmadan yeniden eklendi

        // İzleme aynı Sayfa kümesini görür. Döngünün görevi durum yazıldıktan SONRA tamamlanır: tek tur o aralığa denk
        // gelirse bir şey yapmaz. Tur, koşul sağlanana dek yinelenir (izleme her turdan sonra yeniden beklemeye girer).
        var waited = Stopwatch.StartNew();
        while (State(b) != Live && waited.Elapsed < TestPatience.Upper)
        {
            await time.TickAsync();
            await Task.Delay(20);
        }

        State(b).Should().Be(Live); // biten döngünün yerine yenisi başladı
    }
}
