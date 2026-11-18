using System.Text.Json.Nodes;
using System.Web;
using FluentAssertions;
using OrderDeck.Chat.Facebook;
using OrderDeck.Tests.TestHelpers;
using Confirm = System.Func<System.Collections.Generic.IReadOnlyList<OrderDeck.Chat.Facebook.FacebookLinkedPage>, System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>;
using Kit = OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Yeniden onay ("Facebook izinlerini güncelle", TK4): tarayıcı onayı yeniden koşar; kullanıcı token'ı
/// ve bağlı bütün Sayfaların token'ları yenilenir.
///
/// NEDEN: sonradan açılan Sayfa saklı girişle görünmüyorsa ikinci hesabı eklemenin tek yolu budur ve
/// akış bağlıyken koşar. "Bağlantıyı Kaldır + Facebook'a Bağlan" ekleri silerdi; tek Sayfa dönen bir
/// giriş de birincili kaydırabilirdi. Burada kilitlenenler: Sayfa eklenmez, çıkarılmaz, birincil
/// kaymaz; yeni girişte görünmeyen Sayfa için kaydetmeden önce sorulur ve girdisi silinmez; kayıt adımı
/// güncel demeti kilit altında yeniden okur (onay beklenirken kaldırılan bağlantı dirilmez, kaldırılan
/// ek Sayfa geri gelmez); hatada ve vazgeçmede diske hiçbir şey yazılmaz.
/// </summary>
public sealed partial class FacebookOAuthServiceMultiPageTests
{
    private const string DisconnectedWhileWaitingMessage = "Onay beklenirken Facebook bağlantısı kaldırıldı.";

    /// <summary>
    /// Eksik Sayfa sorusuna hep <paramref name="answer"/> der; <paramref name="asked"/> verilirse
    /// sorulan listeleri toplar.
    /// </summary>
    private static Confirm Answer(bool answer, List<IReadOnlyList<FacebookLinkedPage>>? asked = null) =>
        (missing, _) =>
        {
            asked?.Add(missing);
            return Task.FromResult(answer);
        };

    /// <summary>Yeniden onayı başlatır, servis tarayıcıyı açınca onayı verir ve sonucu döner.</summary>
    private async Task<FacebookReauthorizeResult> RunAsync(FacebookOAuthService service, Confirm confirm)
    {
        var flow = service.ReauthorizeAsync(confirm);
        await CompleteAsync(flow);
        return await flow;
    }

    /// <summary>
    /// Birincil Sayfanın token isteğini tutar: istek Graph'a varınca <c>Reached</c> tamamlanır; yanıt
    /// <c>Release</c> çağrılana dek (en çok <see cref="TestPatience.Upper"/>) bekler, sonra yeni bir
    /// token döner. NEDEN: token'lar kilit dışında türetilir; "türetme sürerken bağlantı değişti"
    /// kurgusu bu aralıkta kurulur.
    /// </summary>
    private (Task Reached, Action Release) HoldPrimaryTokenRequest(SeededPage primary)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _kit.Graph.Map(u => IsPageToken(u, primary.Page.PageId), _ =>
        {
            reached.TrySetResult();
            released.Task.Wait(TestPatience.Upper);
            return FakeGraph.Json(new JsonObject { ["access_token"] = Kit.NewToken() }.ToJsonString());
        });
        return (reached.Task, () => released.TrySetResult());
    }

    [Fact]
    public async Task Butun_bagli_sayfalarin_tokeni_yenilenir_birincil_degismez()
    {
        var log = new CapturingLogger<FacebookOAuthService>();
        var (service, seeded) = await ConnectedAsync(2, log);

        var result = await _kit.ReauthorizeAllAsync(service);

        (result.RefreshedPages, result.MissingPages.Count).Should().Be((3, 0));
        var pages = await service.GetConnectedPagesAsync();
        pages.Select(p => p.PageId).Should().Equal(seeded.Select(s => s.Page.PageId));   // sıra ve birincil aynı
        pages.Select(p => p.PageAccessToken).Should().NotIntersectWith(seeded.Select(s => s.Page.PageAccessToken));
        var bundle = (await _kit.Store.LoadAsync())!;   // diskteki demet
        (bundle.UserAccessToken, bundle.PageAccessToken)
            .Should().Be((_kit.Broker.LastIssuedToken!, pages[0].PageAccessToken));
        bundle.ExpiresAt.Should().NotBeNull();
        _kit.Broker.ExchangeCalls.Should().Be(1);
        AssertLogged(log, "Facebook OAuth reauthorized", seeded[0].Page.PageId, seeded[0].Page.PageName);
        log.Entries.Should().NotContain(e =>
            e.Full.Contains(bundle.UserAccessToken) || pages.Any(p => e.Full.Contains(p.PageAccessToken)));
    }

    /// <summary>
    /// Listeleme ve türetme YENİ kullanıcı token'ıyla yapılır; her Sayfaya Graph'ın O Sayfa için verdiği
    /// token yazılır. Saklı (eski) kullanıcı token'ı kullanılsaydı ya da token'lar Sayfalar arasında
    /// karışsaydı öteki testler yine geçerdi; hata ancak sahada, o hesabın ilk yoklamasında görünürdü.
    /// </summary>
    [Fact]
    public async Task Listeleme_ve_turetme_yeni_kullanici_tokeniyla_yapilir_her_sayfaya_kendi_tokeni_yazilir()
    {
        var (service, seeded) = await ConnectedAsync(2);
        var issued = seeded.ToDictionary(s => s.Page.PageId, _ => Kit.NewToken());
        foreach (var (pageId, token) in issued)
        {
            _kit.Graph.Map(u => IsPageToken(u, pageId),
                _ => FakeGraph.Json(new JsonObject { ["access_token"] = token }.ToJsonString()));
        }

        await _kit.ReauthorizeAllAsync(service);

        // ikinci servis örneği diskten okur
        (await _kit.CreateService().GetConnectedPagesAsync()).Select(p => (p.PageId, p.PageAccessToken))
            .Should().Equal(seeded.Select(s => (s.Page.PageId, issued[s.Page.PageId])));
        var requests = _kit.Graph.Requests;   // bir listeleme, sonra Sayfa başına bir türetme
        requests.Select(u => HttpUtility.ParseQueryString(u.Query)["access_token"])
            .Should().Equal(Enumerable.Repeat(_kit.Broker.LastIssuedToken, 1 + seeded.Count));
        requests.Skip(1).Select(u => u.Segments[^1]).Should().Equal(seeded.Select(s => s.Page.PageId));   // bağlı sırasıyla
    }

    /// <summary>Sayfalamalı listeleme: ilk yanıt sayfasına sığmayan bağlı Sayfa için boşuna onay sorulmaz.</summary>
    [Fact]
    public async Task Ikinci_yanit_sayfasinda_gelen_bagli_sayfa_eksik_sayilmaz()
    {
        var (service, seeded) = await ConnectedAsync(1);
        var (next, asked) = (NextUrl(), new List<IReadOnlyList<FacebookLinkedPage>>());
        _kit.Graph.Map(IsAccounts, u => FakeGraph.Json(u.Query.Contains("after=")
            ? AccountsJson(null, Row(seeded[1]))
            : AccountsJson(next, Row(seeded[0]))));

        var result = await RunAsync(service, Answer(true, asked));

        asked.Should().BeEmpty();
        (result.RefreshedPages, result.MissingPages.Count).Should().Be((2, 0));
    }

    [Fact]
    public async Task Yeni_giriste_olmayan_sayfa_icin_onay_sorulur_hayirda_diske_hicbir_sey_yazilmaz()
    {
        var (service, seeded) = await ConnectedAsync(1);
        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json(AccountsJson(null, Row(seeded[0]))));   // ek Sayfa yeni girişte yok
        var (asked, before) = (new List<IReadOnlyList<FacebookLinkedPage>>(), Blob());

        await FluentActions.Awaiting(() => RunAsync(service, Answer(false, asked)))
            .Should().ThrowAsync<OperationCanceledException>();

        asked.Single().Select(p => p.PageId).Should().Equal(seeded[1].Page.PageId);
        Blob().Should().Equal(before);
        (await service.GetUserAccessTokenAsync()).Should().NotBe(_kit.Broker.LastIssuedToken);   // bellek de değişmedi
    }

    /// <summary>
    /// Eksik olan birincilse yeni girişte tek Sayfa (ek) kalır: ilk bağlantının "tek Sayfa dönerse onu
    /// seç" dalı bu yolda çalışsaydı birincil eke kayardı.
    /// </summary>
    [Theory]
    [InlineData(0)]   // eksik olan: birincil
    [InlineData(1)]   // eksik olan: ek
    public async Task Onay_evetse_eksik_sayfanin_girdisi_silinmez_eski_tokeni_kalir_birincil_kaymaz(int missing)
    {
        var log = new CapturingLogger<FacebookOAuthService>();
        var (service, seeded) = await ConnectedAsync(1, log);
        var (gone, kept) = (seeded[missing].Page, seeded[1 - missing].Page);
        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json(AccountsJson(null, Row(seeded[1 - missing]))));   // yeni girişte tek Sayfa

        var result = await RunAsync(service, Answer(true));

        (result.RefreshedPages, result.MissingPages.Single().PageId).Should().Be((1, gone.PageId));
        var pages = await service.GetConnectedPagesAsync();
        pages.Select(p => p.PageId).Should().Equal(seeded.Select(s => s.Page.PageId));   // girdi silinmez, birincil kaymaz
        pages.Single(p => p.PageId == gone.PageId).PageAccessToken.Should().Be(gone.PageAccessToken);
        pages.Single(p => p.PageId == kept.PageId).PageAccessToken.Should().NotBe(kept.PageAccessToken);
        AssertLogged(log, "Facebook OAuth reauthorized", gone.PageId);
    }

    /// <summary>
    /// Eksik Sayfa sorusu kilit dışında sorulur. NEDEN: soru kullanıcının yanıtını bekler; kilit o sırada
    /// tutulsaydı bağlantıyı kaldırmak ya da ek Sayfa eklemek / kaldırmak yanıt gelene dek asılı kalırdı.
    /// </summary>
    [Fact]
    public async Task Eksik_sayfa_sorusu_kilit_disinda_sorulur_soru_acikken_kaldirilan_ek_sayfa_geri_gelmez()
    {
        var (service, seeded) = await ConnectedAsync(2);
        // ilk ek yeni girişte yok
        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json(AccountsJson(null, Row(seeded[0]), Row(seeded[2]))));
        Confirm removeThenAccept = async (_, _) =>
        {
            // kilit soruyu kapsasaydı bu çağrı dönmezdi
            (await service.RemoveExtraPageAsync(seeded[2].Page.PageId).WaitAsync(TestPatience.Upper))
                .Should().BeTrue();
            return true;
        };

        var result = await RunAsync(service, removeThenAccept);

        (result.RefreshedPages, result.MissingPages.Single().PageId).Should().Be((1, seeded[1].Page.PageId));
        (await IdsAsync()).Should().Equal(seeded[0].Page.PageId, seeded[1].Page.PageId);   // ikinci servis örneği diskten görür
    }

    /// <summary>Listeleme hatası boş liste sayılmaz: sayılsaydı bağlı bütün Sayfalar "eksik" görünürdü.</summary>
    [Theory]
    [InlineData(200)]
    [InlineData(100)]
    [InlineData(190)]
    public async Task Listeleme_hata_verirse_istisna_cikar_onay_sorulmaz_diske_yazilmaz(int code)
    {
        var (service, _) = await ConnectedAsync(1);
        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(code));
        var (asked, before) = (new List<IReadOnlyList<FacebookLinkedPage>>(), Blob());

        (await FluentActions.Awaiting(() => RunAsync(service, Answer(true, asked)))
                .Should().ThrowAsync<FacebookGraphException>())
            .Which.Code.Should().Be(code);

        asked.Should().BeEmpty();
        Blob().Should().Equal(before);
    }

    /// <summary>
    /// Ağ zaman aşımı da (belirteç iptal edilmemişken gelen <see cref="TaskCanceledException"/>) hatadır,
    /// iptal değil: yalnız o Sayfa atlanır.
    /// </summary>
    [Theory]
    [InlineData(false)]   // Graph hatası
    [InlineData(true)]    // ağ zaman aşımı
    public async Task Tokeni_turetilemeyen_sayfanin_eski_tokeni_kalir(bool timedOut)
    {
        var log = new CapturingLogger<FacebookOAuthService>();
        var (service, seeded) = await ConnectedAsync(1, log);
        _kit.Graph.Map(u => IsPageToken(u, seeded[1].Page.PageId),
            _ => timedOut ? throw new TaskCanceledException() : FakeGraph.Error(200));

        var result = await RunAsync(service, Answer(true));

        (result.RefreshedPages, result.MissingPages.Count).Should().Be((1, 0));
        (await service.GetConnectedPagesAsync())[1].PageAccessToken.Should().Be(seeded[1].Page.PageAccessToken);
        // atlanan Sayfa günlükte görünür; satır token ve adres taşımaz
        var warning = log.Warnings.Should().ContainSingle().Which;
        warning.Message.Should().StartWith("Facebook OAuth").And.Contain(seeded[1].Page.PageId);
        warning.Full.Should().NotContain(_kit.Broker.LastIssuedToken!)
            .And.NotContain("access_token").And.NotContain("graph.facebook.com");
    }

    /// <summary>
    /// Önce disk, sonra bellek: yazım düşerse bellekteki demet eski hâlinde kalır (yoksa yeni token'lar
    /// yalnız o oturumda görünür, yeniden başlatınca eskileri geri gelirdi) ve kilit bırakılır.
    /// </summary>
    [Fact]
    public async Task Depo_yazimi_duserse_yeniden_onay_istisna_verir_bellek_degismez_kilit_birakilir()
    {
        var (service, seeded) = await ConnectedAsync(1);
        var (userToken, before) = ((await service.GetUserAccessTokenAsync())!, Blob());
        // hedef salt okunur: deponun taşıma adımı düşer
        File.SetAttributes(_kit.TokenFilePath, FileAttributes.ReadOnly);
        try
        {
            await FluentActions.Awaiting(() => RunAsync(service, Answer(true)))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetAttributes(_kit.TokenFilePath, FileAttributes.Normal);
        }

        Blob().Should().Equal(before);
        (await service.GetUserAccessTokenAsync()).Should().Be(userToken);
        (await service.GetConnectedPagesAsync()).Select(p => p.PageAccessToken)
            .Should().Equal(seeded.Select(s => s.Page.PageAccessToken));
        // kilit bırakıldı: sonraki değişiklik beklemeden çalışır
        (await service.RemoveExtraPageAsync(seeded[1].Page.PageId).WaitAsync(TestPatience.Upper)).Should().BeTrue();
    }

    [Fact]
    public async Task Bagli_degilken_ReauthorizeAsync_tarayici_acmadan_reddedilir()
    {
        await FluentActions.Awaiting(() => _kit.CreateService().ReauthorizeAsync(Answer(true)))
            .Should().ThrowAsync<InvalidOperationException>();

        _kit.Browser.OpenedUrls.Should().BeEmpty();
    }

    /// <summary>TK17: aynı anda tek tarayıcı akışı.</summary>
    [Fact]
    public async Task Bekleyen_akis_varken_ReauthorizeAsync_dinleyiciye_dokunmadan_reddedilir()
    {
        var (service, _) = await ConnectedAsync();
        var before = Blob();
        using var cts = new CancellationTokenSource();
        var first = service.ReauthorizeAsync(Answer(true), cts.Token);
        await _kit.Browser.WaitUntilOpenedAsync();

        (await FluentActions.Awaiting(() => service.ReauthorizeAsync(Answer(true)).WaitAsync(TestPatience.Upper))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(PendingFlowMessage);
        _kit.Browser.OpenedUrls.Should().HaveCount(1);

        cts.Cancel();
        await FluentActions.Awaiting(() => first.WaitAsync(TestPatience.Upper))
            .Should().ThrowAsync<OperationCanceledException>();
        Blob().Should().Equal(before);
    }

    [Fact]
    public async Task Zaman_asiminda_TimeoutException_cikar_diske_yazilmaz()
    {
        await _kit.SeedAsync();
        var (service, before) = (_kit.CreateService(callbackTimeout: TimeSpan.FromMilliseconds(200)), Blob());

        // Servisin kendi zaman aşımı. Hiç çalışmazsa test asılmaz: üst sınırın kendi TimeoutException'ı
        // bu mesajı taşımaz, doğrulama düşer.
        (await FluentActions.Awaiting(() => service.ReauthorizeAsync(Answer(true)).WaitAsync(TestPatience.Upper))
                .Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().StartWith("Facebook onayı");
        Blob().Should().Equal(before);
    }

    /// <summary>Kilit tarayıcı beklemesini kapsamaz; hata, bağlı Sayfalar yeniden okunurken çıkar (bağlı Sayfa kalmadı).</summary>
    [Fact]
    public async Task Onay_beklenirken_baglanti_kaldirilirsa_geri_cagri_gelince_baglanti_dirilmez()
    {
        var (service, _) = await ConnectedAsync(1);
        var pending = service.ReauthorizeAsync(Answer(true));
        await _kit.Browser.WaitUntilOpenedAsync();

        await service.DisconnectAsync().WaitAsync(TestPatience.Upper);
        _kit.Browser.Complete();

        (await FluentActions.Awaiting(() => pending.WaitAsync(TestPatience.Upper))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(DisconnectedWhileWaitingMessage);
        (File.Exists(_kit.TokenFilePath), await service.IsConnectedAsync()).Should().Be((false, false));
    }

    /// <summary>Kilit token türetmeyi kapsamaz; hata kayıt adımında çıkar (kilit altında yeniden okuma).</summary>
    [Fact]
    public async Task Tokenlar_turetilirken_baglanti_kaldirilirsa_kayit_adiminda_baglanti_dirilmez()
    {
        var (service, seeded) = await ConnectedAsync(1);
        var (reached, release) = HoldPrimaryTokenRequest(seeded[0]);
        var pending = RunAsync(service, Answer(true));
        await reached.WaitAsync(TestPatience.Upper);   // bağlı Sayfalar okundu; türetme birincilin token isteğinde bekliyor

        await service.DisconnectAsync().WaitAsync(TestPatience.Upper);
        release();

        (await FluentActions.Awaiting(() => pending).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(DisconnectedWhileWaitingMessage);
        (File.Exists(_kit.TokenFilePath), await service.IsConnectedAsync()).Should().Be((false, false));
    }

    [Fact]
    public async Task Tokenlar_turetilirken_kaldirilan_ek_sayfa_geri_gelmez_eklenen_korunur()
    {
        var (service, seeded) = await ConnectedAsync(1);
        var (fresh, (reached, release)) = (NewGraphPage(), HoldPrimaryTokenRequest(seeded[0]));
        var pending = RunAsync(service, Answer(true));
        await reached.WaitAsync(TestPatience.Upper);

        // kilit türetmeyi kapsamaz: ikisi de beklemeden tamamlanır
        (await service.RemoveExtraPageAsync(seeded[1].Page.PageId).WaitAsync(TestPatience.Upper)).Should().BeTrue();
        await service.AddExtraPageAsync(fresh.PageId, fresh.PageName).WaitAsync(TestPatience.Upper);
        release();

        (await pending).RefreshedPages.Should().Be(1);
        (await IdsAsync(service)).Should().Equal(seeded[0].Page.PageId, fresh.PageId);
        (await IdsAsync()).Should().Equal(seeded[0].Page.PageId, fresh.PageId);   // disk de aynı
    }
}
