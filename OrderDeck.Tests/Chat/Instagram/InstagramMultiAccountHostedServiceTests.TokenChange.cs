using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Instagram;
using OrderDeck.Tests.TestHelpers;
using static OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Sayfa token'ı değişince hesap döngüsünün yaptıkları (çoklu hesap tasarımı §8.3.2 hesap döngüsü 6. adım,
/// §8.3.3 "Token değişimi", TK16).
///
/// NEDEN: bozuk bir hesap "Facebook izinlerini güncelle" ile düzeltilir; düzeltmeden sonra hesap 5 dakika daha
/// susmamalı. Çözümleme ve kalıcı hata beklemeleri bu yüzden saklı token'a bakar: token o turda kullanılandan
/// farklıysa bekleme atlanır ya da kesilir. Token hangi yolda değişirse değişsin sonraki kurulumda hesap bir kez
/// yeniden çözülür (çözümleyici önbelleği boşaltılır): izin güncellemede Instagram hesabının işareti kalktıysa
/// bu, eski önbellekle kurulan poller'ın hatasında değil çözümlemede görünür.
///
/// "Token değişti" kurgusu gerçek izin güncelleme akışıyla üretilir
/// (<see cref="FacebookOAuthTestKit.ReauthorizeAllAsync"/>): bağlı her Sayfaya yeni token yazılır.
/// </summary>
public sealed partial class InstagramMultiAccountHostedServiceTests
{
    /// <summary>O Sayfanın SAKLI token'ı: OAuth servisinin bağlı Sayfalar listesindeki güncel değer.</summary>
    private async Task<string> StoredTokenAsync(SeededPage page) =>
        (await _oauth.GetConnectedPagesAsync()).Single(p => p.PageId == page.Page.PageId).PageAccessToken;

    [Fact]
    public async Task Kalici_hata_beklemesi_token_degisince_kesilir_ve_hesap_yeniden_cozulur()
    {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        feed.Intercept = token => token == old ? FakeGraph.Error(190) : null;
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => Health(page)?.RetryAt is not null); // kalıcı hata beklemesinde
        ResolveRequests(page).Should().Be(1);

        await _kit.ReauthorizeAllAsync(_oauth);

        var fresh = await StoredTokenAsync(page);
        fresh.Should().NotBe(old);
        await Until(() => feed.ServedFor(fresh) >= 1); // Long dolmadan yeni token'la kuruldu
        ResolveRequests(page).Should().Be(2); // önbellek boşaltıldı: tek ek çözümleme
        await Until(() => State(page) == Live);
    }

    /// <summary>
    /// Kesilen (ya da atlanan) beklemeden sonra yeniden deneme hemen başlar; girdideki yeniden deneme saati ileride
    /// kalmamalı. Yeni poller'ın turları geçici hatayla düşerse izleyici hiçbir alanı değiştirmez: saat ileride
    /// kalsaydı satır, yeniden deneme çoktan başlamışken ileri bir saati gösterir, "yanıt yok" (yeniden deneme saati +
    /// 45 sn) da o saate kadar, en çok 5 dakika gecikirdi.
    /// </summary>
    [Fact]
    public async Task Kesilen_kalici_hata_beklemesinden_sonra_yeniden_deneme_saati_simdiye_cekilir()
    {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        feed.Intercept = token => // yeni token'la gelen istek geçici hata alır
            token == old ? FakeGraph.Error(190) : FakeGraph.Error(1, HttpStatusCode.InternalServerError);
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => Health(page)?.RetryAt is not null); // kalıcı hata beklemesinde
        var waiting = Health(page)!;
        waiting.RetryAt.Should().Be(_time.GetUtcNow() + Long); // elle ilerletilen saat kendiliğinden ilerlemez
        waiting.Detail.Should().NotBeNullOrEmpty(); // poller'ın kalıcı hata mesajı

        await _kit.ReauthorizeAllAsync(_oauth);

        var fresh = await StoredTokenAsync(page);
        await Until(() => feed.ServedFor(fresh) >= 1); // bekleme kesildi: yeni poller yeni token'la yokluyor, geçici hata alıyor
        var retrying = Health(page)!;
        (retrying.State, retrying.Detail).Should().Be((Faulted, waiting.Detail));
        retrying.RetryAt.Should().Be(_time.GetUtcNow()); // yeniden deneme başladı: saat şimdi
        Lines(AccountPrefix).Should().ContainSingle(l => l.Contains("-> Faulted")); // durum değişmedi: geçiş satırı yinelenmedi
        // Operatörün gördüğü: saat 45 sn'den fazla geçilince satır "bağlantı yenilenmeli"de kalmaz.
        InstagramHealthPresenter
            .Evaluate(_health.Snapshot(), _time.GetUtcNow() + TimeSpan.FromSeconds(46), sessionActive: true)
            .Rows.Single().StateText.Should().Be("yanıt yok (46 sn)");
    }

    /// <summary>
    /// Saat yalnız atlanan ya da kesilen beklemeden sonra şimdiye çekilir. Süresi dolan beklemeden sonra saate
    /// dokunulmaz: zaten geçmiştir, "yanıt yok" da o saatten ölçülür.
    /// </summary>
    [Fact]
    public async Task Suresi_dolan_kalici_hata_beklemesinden_sonra_yeniden_deneme_saatine_dokunulmaz()
    {
        var (page, feed) = await OneAsync();
        var fatal = 1;
        feed.Intercept = _ =>
            Volatile.Read(ref fatal) == 1 ? FakeGraph.Error(190) : FakeGraph.Error(1, HttpStatusCode.InternalServerError);
        await RunAsync(); // IdleAfterFatal 200 ms; token değişmez: bekleme süresi dolar
        await Until(() => Health(page)?.RetryAt is not null); // kalıcı hata beklemesinde

        Volatile.Write(ref fatal, 0); // bundan sonraki turlar geçici hata: izleyici hiçbir alanı değiştirmez
        var served = feed.Served;

        await Until(() => feed.Served > served); // bekleme doldu, yeni poller yokladı
        Health(page)!.RetryAt.Should().Be(_time.GetUtcNow() + Fast.IdleAfterFatal); // beklemeden önce yazılan saat
    }

    [Fact]
    public async Task Cozumleme_beklemesi_token_degisince_kesilir()
    {
        var (page, _) = await OneAsync();
        var old = page.Page.PageAccessToken;
        _kit.Graph.Map(
            u => IsResolve(u, page) && InstagramLiveFeed.TokenIn(u) == old,
            _ => FakeGraph.Error(1, HttpStatusCode.InternalServerError));
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(page) == ResolveFailed);

        await _kit.ReauthorizeAllAsync(_oauth);

        await Until(() => State(page) == Live); // Long dolmadan çözüldü ve yoklanıyor
    }

    [Fact]
    public async Task Poller_calisirken_token_degisir_sonra_eski_token_duserse_bekleme_atlanir()
    {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        await RunAsync(Fast with { IdleAfterFatal = Long, WatchInterval = Long }); // adım da uzun: "kesildi" değil "atlandı"
        await Until(() => State(page) == Live);
        await _kit.ReauthorizeAllAsync(_oauth);
        var fresh = await StoredTokenAsync(page);
        await feed.NextAsync();
        feed.ServedFor(fresh).Should().Be(0); // sağlıklı poller eski token'la sürüyor

        feed.Intercept = token => token == old ? FakeGraph.Error(190) : null; // eski token düştü

        await Until(() => feed.ServedFor(fresh) >= 1); // ne 5 dk ne bir adım beklendi
        await Until(() => State(page) == Live);
    }

    [Fact]
    public async Task Token_bekleme_disinda_degisince_sonraki_kurulumda_hesap_bir_kez_yeniden_cozulur()
    {
        var (page, feed) = await OneAsync();
        var session = await RunAsync();
        await Until(() => State(page) == Live);
        await _kit.ReauthorizeAllAsync(_oauth); // poller sağlıklı: iki beklemeye de girilmedi
        var fresh = await StoredTokenAsync(page);
        EndSession(session);
        await Until(() => State(page) == Stopped);

        StartSession();

        await Until(() => feed.ServedFor(fresh) >= 1); // yeni oturum yeni token'la kuruldu
        ResolveRequests(page).Should().Be(2); // token değişti: hesap bir kez yeniden çözüldü (token aynıyken 1 kalır)
    }

    /// <summary>
    /// "Bir kez": token kaydı her çözümlemeden önce o turun token'ıyla YAZILIR. Kayıt ilk token'da kalsaydı token
    /// değişiminden sonraki HER kurulum önbelleği yeniden boşaltır, kurulum başına bir çözümleme isteği atardı.
    /// </summary>
    [Fact]
    public async Task Token_degisiminden_sonraki_ikinci_kurulumda_hesap_yeniden_cozulmez()
    {
        var (page, feed) = await OneAsync();
        var session = await RunAsync();
        await Until(() => State(page) == Live);
        await _kit.ReauthorizeAllAsync(_oauth); // poller sağlıklı: iki beklemeye de girilmedi
        var fresh = await StoredTokenAsync(page);
        EndSession(session);
        await Until(() => State(page) == Stopped);
        var second = StartSession();
        await Until(() => feed.ServedFor(fresh) >= 1); // ikinci kurulum yeni token'la
        ResolveRequests(page).Should().Be(2); // token değişti: hesap bir kez yeniden çözüldü
        EndSession(second);
        await Until(() => State(page) == Stopped);

        StartSession();

        await Until(() => PollerStarts(page) == 3); // üçüncü kurulum: token ikincisiyle aynı
        ResolveRequests(page).Should().Be(2); // kayıt güncellendi: önbellek korundu, istek eklenmedi
    }

    [Fact]
    public async Task Token_degismezse_bekleme_suresi_dolunca_yeniden_denenir()
    {
        var (page, _) = await OneAsync();
        _kit.Graph.AddPage(page.Page.PageId, page.Page.PageName); // Instagram'sız
        await RunAsync(); // IdleAfterFatal 200 ms, adım 50 ms; ManualTime ilerletilmez
        await Until(() => State(page) == NotLinked);

        _kit.Graph.AddPage(page.Page.PageId, page.Page.PageName, page.IgUserId, page.IgUsername);

        await Until(() => State(page) == Live); // bekleme adım toplamıyla bitti
    }

    /// <summary>
    /// Son adım kalan süre kadardır: adım beklemeden uzun olsa da bekleme kendi süresinde biter. Tam adım beklenseydi
    /// hesap burada <see cref="Long"/> kadar susardı.
    /// </summary>
    [Fact]
    public async Task Adim_beklemeden_uzunsa_bekleme_yine_kendi_suresinde_biter()
    {
        var (page, _) = await OneAsync();
        _kit.Graph.AddPage(page.Page.PageId, page.Page.PageName); // Instagram'sız
        await RunAsync(Fast with { WatchInterval = Long }); // IdleAfterFatal 200 ms, adım ondan uzun
        await Until(() => State(page) == NotLinked);

        _kit.Graph.AddPage(page.Page.PageId, page.Page.PageName, page.IgUserId, page.IgUsername);

        await Until(() => State(page) == Live);
    }

    /// <summary>
    /// Canlı olmuş bir hesap izin güncellemeden sonra "hesap yok"a düşebilir (§8.5): yeni onayda Instagram hesabının
    /// işareti kaldırılmıştır. Önbellek boşaltılmasaydı poller önbellekteki hesapla yeniden kurulur; kopan bağ
    /// çözümlemede değil, Meta'nın o an döndüğü hataya bağlı olarak poller'da görünürdü.
    /// </summary>
    [Fact]
    public async Task Izin_guncellemede_Instagram_isareti_kalkan_hesap_poller_hatasiyla_degil_NotLinked_ile_gorunur()
    {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(page) == Live);
        _kit.Graph.AddPage(page.Page.PageId, page.Page.PageName); // yeni onayda Instagram hesabının işareti kaldırıldı
        await _kit.ReauthorizeAllAsync(_oauth);

        feed.Intercept = token => token == old ? FakeGraph.Error(190) : null; // eski token düştü

        await Until(() => State(page) == NotLinked); // yeni token'la ilk iş çözümleme
        PollerStarts(page).Should().Be(1); // önbellekteki hesapla poller kurulmadı
        Health(page)!.WasLiveThisSession.Should().BeTrue(); // tek Sayfada şerit bu bilgiyle çıkar: hesap gizlenmez
    }

    /// <summary>
    /// Bekleme adımları yalnız saklı token'ı okur (bellek): Graph'a istek atılmaz, ağın düzelmesi beklemeyi kesmez
    /// (R8). Adım başına bir çözümleme denemesi, bekleyen hesabın Graph yükünü katlardı. Öteki hesabın turları
    /// "bu arada onlarca adım geçti"nin tanığıdır.
    /// </summary>
    [Fact]
    public async Task Cozumleme_beklemesi_ag_duzelince_kesilmez_ve_beklerken_Graph_a_istek_atilmaz()
    {
        var (_, b, feedA, _) = await TwoAsync();
        var down = 1;
        _kit.Graph.Map(
            u => IsResolve(u, b) && Volatile.Read(ref down) == 1,
            _ => FakeGraph.Error(1, HttpStatusCode.InternalServerError));
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(b) == ResolveFailed);

        Volatile.Write(ref down, 0); // ağ düzeldi: çözümleme artık başarılı olurdu

        await FlowAsync(feedA);
        await FlowAsync(feedA); // öteki hesap iki tur daha attı: B bu sırada onlarca adım bekledi
        State(b).Should().Be(ResolveFailed);
        ResolveRequests(b).Should().Be(1);
    }

    /// <summary>
    /// Token'a yalnız iki bekleme bakar (hesap çözülemedi, kalıcı hata). Hesabını başka bir Sayfa yokladığı için
    /// atlanan Sayfanın beklemesi düzdür: Sayfa token yüzünden atlanmadı. Her yeniden bakış bir uyarı satırı yazar.
    /// </summary>
    [Fact]
    public async Task Atlanan_sayfanin_beklemesi_token_degisince_kesilmez()
    {
        var (a, b, feedA, _) = await TwoAsync();
        _kit.Graph.AddPage(b.Page.PageId, b.Page.PageName, a.IgUserId, a.IgUsername); // iki Sayfa aynı hesaba çözülür
        await RunAsync(Fast with { IdleAfterFatal = Long });
        string? skipped = null;
        await Until(() => (skipped = _health.Snapshot().FirstOrDefault(h => h.State == Skipped)?.PageId) is not null);

        await _kit.ReauthorizeAllAsync(_oauth); // iki Sayfanın token'ı da değişti

        await FlowAsync(feedA);
        await FlowAsync(feedA); // hesabı yoklayan Sayfa iki tur daha attı
        _logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(skipped!)).Should().Be(1);
    }
}
