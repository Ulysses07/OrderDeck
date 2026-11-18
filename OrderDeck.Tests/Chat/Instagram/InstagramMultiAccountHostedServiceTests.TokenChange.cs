using System.Net;
using FluentAssertions;
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
/// "Token değişti" kurgusu gerçek izin güncelleme akışıyla üretilir (<see cref="FacebookOAuthTestKit.ReauthorizeAllAsync"/>):
/// bağlı her Sayfaya yeni token yazılır.
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
}
