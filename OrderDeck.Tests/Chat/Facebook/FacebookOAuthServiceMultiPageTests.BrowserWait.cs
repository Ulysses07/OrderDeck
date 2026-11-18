using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using OrderDeck.Chat.Facebook;
using OrderDeck.Tests.TestHelpers;
using Kit = OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Tarayıcı onayı beklemesi (TK17): zaman aşımı, iptal, aynı anda tek akış.
///
/// NEDEN: bekleme eskiden yalnız geri çağrıyla ya da iptalle bitiyordu. Onay sekmesi kapatılınca
/// komut süresiz bekliyor, dinleyici açık kaldığı için sonraki deneme "Failed to bind" ile
/// düşüyordu; çıkış yolu uygulamayı yeniden başlatmaktı. Burada kilitlenenler: bekleme süresi
/// dolunca biter, her çıkış yolunda dinleyici bırakılır ve bekleyen akış varken ikinci deneme
/// dinleyiciye dokunmadan reddedilir.
/// </summary>
public sealed partial class FacebookOAuthServiceMultiPageTests
{
    private const string PendingFlowMessage =
        "Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da birkaç dakika sonra yeniden dene.";

    /// <summary>Sayfa seçici: seçim yapmaz. Tek Sayfada servis seçiciyi hiç çağırmaz.</summary>
    private static Task<FacebookPageCandidate?> NoPicker(
        IReadOnlyList<FacebookPageCandidate> pages, CancellationToken ct) =>
        Task.FromResult<FacebookPageCandidate?>(null);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Servis tarayıcıyı açınca onayı verir ve akışın bitmesini bekler.</summary>
    private async Task CompleteAsync(Task flow)
    {
        await _kit.Browser.WaitUntilOpenedAsync();
        _kit.Browser.Complete();
        await flow.WaitAsync(TestPatience.Upper);
    }

    [Fact]
    public async Task Tek_sayfada_ConnectAsync_secici_sormadan_eksiz_demet_yazar()
    {
        var page = NewGraphPage();

        await CompleteAsync(_kit.CreateService().ConnectAsync(NoPicker));

        var bundle = (await _kit.Store.LoadAsync())!;
        (bundle.PageId, bundle.PageName, bundle.UserAccessToken)
            .Should().Be((page.PageId, page.PageName, _kit.Broker.LastIssuedToken!));
        bundle.ExtraPages.Should().BeNull();
        _kit.Settings.FacebookPageId.Should().Be(page.PageId);
        _kit.Browser.OpenedUrls.Single().Should().Contain("response_type=code").And.Contain("state=");
        _kit.Browser.PendingWaits.Should().Be(0);
    }

    [Fact]
    public async Task Geri_cagri_gelmezse_sure_dolunca_TimeoutException_firlar_diske_yazilmaz_dinleyici_birakilir()
    {
        // spec'in süresi (TK17); akışın kendisi kısaltılmış süreyle koşar
        FacebookOAuthService.CallbackTimeout.Should().Be(TimeSpan.FromMinutes(5));
        var service = _kit.CreateService(callbackTimeout: TimeSpan.FromMilliseconds(200));
        // Servis zaman aşımı hiç çalışmazsa test asılmaz: üst sınırın kendi TimeoutException'ı
        // aşağıdaki mesajı taşımaz, doğrulama düşer.
        var connect = FluentActions.Awaiting(() => service.ConnectAsync(NoPicker).WaitAsync(TestPatience.Upper));

        (await connect.Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().Be("Facebook onayı 5 dakika içinde tamamlanmadı. Yeniden dene.");
        (File.Exists(_kit.TokenFilePath), _kit.Browser.PendingWaits).Should().Be((false, 0));

        // hemen ardından başlatılan akış reddedilmez, yeniden bağlanır
        (await connect.Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().StartWith("Facebook onayı");
        _kit.Browser.OpenedUrls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Belirtec_iptali_OperationCanceledException_uretir_ve_dinleyiciyi_birakir()
    {
        using var cts = new CancellationTokenSource();
        var connecting = _kit.CreateService().ConnectAsync(NoPicker, cts.Token);
        await _kit.Browser.WaitUntilOpenedAsync();

        cts.Cancel();

        await FluentActions.Awaiting(() => connecting.WaitAsync(TestPatience.Upper))
            .Should().ThrowAsync<OperationCanceledException>();
        (File.Exists(_kit.TokenFilePath), _kit.Browser.PendingWaits).Should().Be((false, 0));
    }

    [Fact]
    public async Task Bekleyen_akis_varken_ikinci_ConnectAsync_dinleyiciye_dokunmadan_reddedilir()
    {
        var service = _kit.CreateService();
        using var cts = new CancellationTokenSource();
        var first = service.ConnectAsync(NoPicker, cts.Token);
        await _kit.Browser.WaitUntilOpenedAsync();

        (await FluentActions.Awaiting(() => service.ConnectAsync(NoPicker).WaitAsync(TestPatience.Upper))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(PendingFlowMessage);
        (_kit.Browser.OpenedUrls.Count, _kit.Broker.ConfigCalls).Should().Be((1, 1));

        await service.DisconnectAsync().WaitAsync(TestPatience.Upper);   // _mutate tarayıcı beklemesini kapsamaz
        cts.Cancel();
        await FluentActions.Awaiting(() => first.WaitAsync(TestPatience.Upper))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Facebook ekranındaki "İptal" bir geri çağrıdır (<c>access_denied</c>): iptal değil hata
    /// sayılır, arayüzde "başarısız" metnine düşer.
    /// </summary>
    [Fact]
    public async Task Access_denied_ve_state_uyusmazligi_InvalidOperationException_olarak_kalir()
    {
        var (service, aciklama) = (_kit.CreateService(), Kit.NewId());
        var denied = service.ConnectAsync(NoPicker);
        await _kit.Browser.WaitUntilOpenedAsync();
        _kit.Browser.CompleteWithError("access_denied", aciklama);
        (await FluentActions.Awaiting(() => denied.WaitAsync(TestPatience.Upper))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be($"Facebook OAuth error: access_denied — {aciklama}");

        var forged = service.ConnectAsync(NoPicker);   // koruma bırakılmış: yeni akış açılır
        await _kit.Browser.WaitUntilOpenedAsync();
        _kit.Browser.CompleteWithState(Kit.NewId());
        (await FluentActions.Awaiting(() => forged.WaitAsync(TestPatience.Upper))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("OAuth state mismatch — possible CSRF.");

        _kit.Broker.ExchangeCalls.Should().Be(0);
    }

    /// <summary>Üretimdeki dinleyici: yalnız loopback; tarayıcı yerine adresi kaydeden bir temsilci.</summary>
    [Fact]
    public async Task Gercek_dinleyici_iptalden_sonra_ayni_oneki_yeniden_baglar_ve_geri_cagriya_HTML_doner()
    {
        var (launched, code, state, url) =
            (new List<string>(), Kit.NewId(), Kit.NewId(), "https://example.invalid/" + Kit.NewId());
        var prefix = $"http://localhost:{FreePort()}/facebook/callback/";
        var browser = new LoopbackFacebookOAuthBrowser(prefix, launched.Add);

        using var cts = new CancellationTokenSource();
        var canceled = browser.Open(url);
        var waiting = canceled.WaitAsync(cts.Token);
        cts.Cancel();
        await FluentActions.Awaiting(() => waiting.WaitAsync(TestPatience.Upper))
            .Should().ThrowAsync<OperationCanceledException>();
        canceled.Dispose();

        // belirteç WaitAsync'ten ÖNCE iptal edilmiş: yine OperationCanceledException
        using (var late = browser.Open(url))
        {
            await FluentActions.Awaiting(() => late.WaitAsync(new CancellationToken(canceled: true)))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        using var wait = browser.Open(url);   // iptalden sonra aynı önek yeniden bağlanır
        var callback = wait.WaitAsync(CancellationToken.None);
        using var http = new HttpClient();
        var html = await http.GetStringAsync($"{prefix}?code={code}&state={state}");

        (await callback.WaitAsync(TestPatience.Upper)).Should().Be(new FacebookOAuthCallback(code, state, null, null));
        html.Should().Contain("Facebook bağlantısı tamamlandı.");
        launched.Should().Equal(url, url, url);
    }

    /// <summary>TK5: ek Sayfa saklı kullanıcı token'ıyla eklenir, tarayıcı açılmaz.</summary>
    [Fact]
    public async Task Tarayicisiz_ekleme_tarayici_acmaz()
    {
        var (service, _) = await ConnectedAsync();
        var fresh = NewGraphPage();

        await service.AddExtraPageAsync(fresh.PageId, fresh.PageName);

        _kit.Browser.OpenedUrls.Should().BeEmpty();
    }
}
