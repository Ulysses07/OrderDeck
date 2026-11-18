using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Tests.TestHelpers;
using Kit = OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// OAuth servisinin çok Sayfalı yüzü: bağlı Sayfaların listesi, ek Sayfanın tarayıcı açmadan
/// eklenmesi ve kaldırılması, eklenebilecek Sayfaların sayfalamalı okunması.
///
/// NEDEN: ikinci Instagram hesabı ayrı bir Facebook Sayfasından okunur ve o Sayfa, çalışan
/// Facebook girişi bozulmadan eklenebilmelidir. Burada kilitlenenler: birincil Sayfa hiçbir
/// yoldan kaymaz (Facebook yorumları ve moderasyon ondan çalışır), ekleme saklı kullanıcı
/// token'ıyla yapılır (tarayıcı ve sunucu takası yok), demet önce diske sonra belleğe yazılır
/// ve eşzamanlı ekle / kaldır güncelleme kaybetmez.
///
/// Gerçek depo (DPAPI, geçici klasör) + sahte Graph + sahte sunucu aracısı; ağa çıkılmaz.
/// </summary>
public sealed partial class FacebookOAuthServiceMultiPageTests : IDisposable
{
    private readonly Kit _kit = new();

    public void Dispose() => _kit.Dispose();

    /// <summary>Bağlı bir kurulum (birincil + ekler) ve onu diskten okuyacak yeni bir servis örneği.</summary>
    private async Task<(FacebookOAuthService Service, IReadOnlyList<SeededPage> Pages)> ConnectedAsync(
        int extraPages = 0, ILogger<FacebookOAuthService>? log = null)
    {
        var pages = await _kit.SeedAsync(extraPages);
        return (_kit.CreateService(log), pages);
    }

    private static string NewName() => "sayfa-" + Kit.NewId();

    private static FacebookLinkedPage NewPage() => new(Kit.NewId(), Kit.NewToken(), NewName());

    /// <summary>Henüz bağlı olmayan, Graph'ın tanıdığı ve Instagram hesabı olan bir Sayfa.</summary>
    private (string PageId, string PageName) NewGraphPage()
    {
        var page = (PageId: Kit.NewId(), PageName: NewName());
        _kit.Graph.AddPage(page.PageId, page.PageName, Kit.NewId(), "hesap-" + Kit.NewId());
        return page;
    }

    /// <summary>Üretilmiş bir <c>/me/accounts</c> satırı; Instagram'sızsa iki Instagram alanı da null.</summary>
    private static FacebookAddablePage NewRow(bool igAccount) => new(
        Kit.NewId(),
        NewName(),
        igAccount ? Kit.NewId() : null,
        igAccount ? "hesap-" + Kit.NewId() : null,
        AlreadyConnected: false);

    private static FacebookAddablePage Row(SeededPage s) =>
        new(s.Page.PageId, s.Page.PageName, s.IgUserId, s.IgUsername, AlreadyConnected: false);

    /// <summary>
    /// <c>/me/accounts</c> yanıt gövdesi: <c>{"data":[…],"paging":{"next":…}}</c>;
    /// <paramref name="next"/> null ise <c>paging</c> yazılmaz (son yanıt sayfası).
    /// </summary>
    private static string AccountsJson(string? next, params FacebookAddablePage[] rows)
    {
        var root = new JsonObject { ["data"] = new JsonArray(rows.Select(RowNode).ToArray()) };
        if (next is not null) root["paging"] = new JsonObject { ["next"] = next };
        return root.ToJsonString();
    }

    private static JsonNode? RowNode(FacebookAddablePage row)
    {
        var node = new JsonObject { ["id"] = row.PageId, ["name"] = row.PageName };
        if (row.IgUserId is not null)
        {
            var account = new JsonObject { ["id"] = row.IgUserId };
            if (row.IgUsername is not null) account["username"] = row.IgUsername;
            node["instagram_business_account"] = account;
        }
        return node;
    }

    private static string NextUrl() =>
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}/me/accounts?after={Kit.NewId()}";

    private static bool IsAccounts(Uri u) => u.AbsolutePath.EndsWith("/me/accounts", StringComparison.Ordinal);

    private static bool IsPageToken(Uri u, string pageId) =>
        u.AbsolutePath.EndsWith("/" + pageId, StringComparison.Ordinal) && u.Query.Contains("fields=access_token");

    /// <summary>
    /// Bağlı Sayfaların kimlikleri. Servis verilmezse AYNI depo üstünde yeni bir örnek kurulur:
    /// o örnek diskten okur, yani sonuç diske inmiş olanı gösterir.
    /// </summary>
    private async Task<string[]> IdsAsync(FacebookOAuthService? service = null) =>
        (await (service ?? _kit.CreateService()).GetConnectedPagesAsync()).Select(p => p.PageId).ToArray();

    private byte[] Blob() => File.ReadAllBytes(_kit.TokenFilePath);

    /// <summary><paramref name="prefix"/> ile başlayan ve parçaların hepsini içeren bir Information satırı var.</summary>
    private static void AssertLogged(
        CapturingLogger<FacebookOAuthService> log, string prefix, params string[] parts) =>
        log.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information
            && e.Message.StartsWith(prefix, StringComparison.Ordinal)
            && parts.All(part => e.Message.Contains(part, StringComparison.Ordinal)));

    /// <summary>AH5 kilidi: ekler varken de Facebook yorumlarının ve moderasyonun okuduğu kimlik birincildir.</summary>
    [Fact]
    public async Task Ekler_varken_liste_birincil_once_doner_GetPageCredentialsAsync_birincilde_kalir()
    {
        var (service, seeded) = await ConnectedAsync(extraPages: 2);

        (await service.GetConnectedPagesAsync()).Select(p => (p.PageId, p.PageAccessToken, p.PageName))
            .Should().Equal(seeded.Select(s => (s.Page.PageId, s.Page.PageAccessToken, s.Page.PageName)));
        (await service.GetPageCredentialsAsync())!.Value
            .Should().Be((seeded[0].Page.PageId, seeded[0].Page.PageAccessToken));
    }

    [Fact]
    public async Task Bagli_sayfalar_PageId_ye_gore_tekildir_bozuk_ek_girdi_atlanir()
    {
        var (primary, good) = (NewPage(), NewPage());
        await _kit.Store.SaveAsync(new FacebookTokenBundle(
            Kit.NewToken(), primary.PageId, primary.PageAccessToken, primary.PageName, null,
            new FacebookLinkedPage[]
            {
                // yinelenenler
                good, good with { PageAccessToken = Kit.NewToken() }, primary with { PageAccessToken = Kit.NewToken() },
                // bozuk girdiler
                NewPage() with { PageId = "" }, NewPage() with { PageAccessToken = null! }, null!,
            }));

        (await _kit.CreateService().GetConnectedPagesAsync()).Select(p => (p.PageId, p.PageAccessToken))
            .Should().Equal((primary.PageId, primary.PageAccessToken), (good.PageId, good.PageAccessToken));
    }

    [Fact]
    public async Task Bagli_degilken_liste_bostur_degistiren_uyeler_reddeder()
    {
        var service = _kit.CreateService();

        (await service.GetConnectedPagesAsync()).Should().BeEmpty();
        (await service.RemoveExtraPageAsync(Kit.NewId())).Should().BeFalse();
        await FluentActions.Awaiting(() => service.ListAddablePagesAsync())
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => service.AddExtraPageAsync(Kit.NewId(), NewName()))
            .Should().ThrowAsync<InvalidOperationException>();
        _kit.Graph.Requests.Should().BeEmpty();
    }

    /// <summary>TK5: ekleme saklı kullanıcı token'ıyla yapılır; tarayıcı açılmaz, sunucu takası çağrılmaz.</summary>
    [Fact]
    public async Task Ek_sayfa_tarayici_ve_sunucu_takasi_olmadan_eklenir_diske_yazilir()
    {
        var log = new CapturingLogger<FacebookOAuthService>();
        var (service, seeded) = await ConnectedAsync(log: log);
        var fresh = NewGraphPage();

        var added = await service.AddExtraPageAsync(fresh.PageId, fresh.PageName);

        (added.PageId, added.PageName).Should().Be(fresh);
        (_kit.Broker.ConfigCalls, _kit.Broker.ExchangeCalls, _kit.Settings.FacebookPageId)
            .Should().Be((0, 0, seeded[0].Page.PageId));
        (await IdsAsync()).Should().Equal(seeded[0].Page.PageId, fresh.PageId);   // ikinci servis örneği diskten görür
        AssertLogged(log, "Facebook OAuth", fresh.PageId, fresh.PageName);
        log.Entries.Should().NotContain(e => e.Full.Contains(added.PageAccessToken));
    }

    /// <summary>TK13: en çok 4 Sayfa (birincil dahil).</summary>
    [Fact]
    public async Task Zaten_bagli_sayfa_yazmadan_doner_sinir_dolunca_yeni_sayfa_Graph_a_gitmeden_reddedilir()
    {
        FacebookOAuthService.MaxInstagramPages.Should().Be(4);
        var (service, seeded) = await ConnectedAsync(extraPages: 3);
        var before = Blob();

        // birincil: mevcut girdi
        (await service.AddExtraPageAsync(seeded[0].Page.PageId, NewName())).Should().Be(seeded[0].Page);
        // zaten ekli: sınır dolu olsa da mevcut girdi
        (await service.AddExtraPageAsync(seeded[2].Page.PageId, NewName())).Should().Be(seeded[2].Page);
        await FluentActions.Awaiting(() => service.AddExtraPageAsync(Kit.NewId(), NewName()))
            .Should().ThrowAsync<FacebookPageLimitException>();

        // Graph'a gidilmedi, dosya yazılmadı
        (_kit.Graph.Requests.Count, Blob().SequenceEqual(before)).Should().Be((0, true));
    }

    [Fact]
    public async Task Kaldirma_yalniz_o_ek_girdiyi_siler_birincil_ve_bilinmeyen_kimlikte_false_doner()
    {
        var log = new CapturingLogger<FacebookOAuthService>();
        var (service, seeded) = await ConnectedAsync(2, log);
        var before = Blob();

        (await service.RemoveExtraPageAsync(seeded[0].Page.PageId) || await service.RemoveExtraPageAsync(Kit.NewId()))
            .Should().BeFalse();
        Blob().Should().Equal(before);   // false yazmaz

        (await service.RemoveExtraPageAsync(seeded[1].Page.PageId)).Should().BeTrue();

        (await IdsAsync()).Should().Equal(seeded[0].Page.PageId, seeded[2].Page.PageId);
        AssertLogged(log, "Facebook OAuth", seeded[1].Page.PageId, seeded[1].Page.PageName);
    }

    [Fact]
    public async Task Listeleme_paging_next_i_izler_bagli_olani_isaretler_Instagramsiz_sayfada_IgUserId_bostur()
    {
        var log = new CapturingLogger<FacebookOAuthService>();
        var (service, seeded) = await ConnectedAsync(log: log);
        var (noIg, withIg, next) = (NewRow(igAccount: false), NewRow(igAccount: true), NextUrl());
        // ikinci yanıt sayfasında birincil yinelenir
        _kit.Graph.Map(IsAccounts, u => FakeGraph.Json(u.Query.Contains("after=")
            ? AccountsJson(null, withIg, Row(seeded[0]))
            : AccountsJson(next, Row(seeded[0]), noIg)));

        var listing = await service.ListAddablePagesAsync();

        listing.NeedsReauthorize.Should().BeFalse();
        // PageId'ye göre tekil; noIg.IgUserId null
        listing.Pages.Should().Equal(Row(seeded[0]) with { AlreadyConnected = true }, noIg, withIg);
        Uri.UnescapeDataString(_kit.Graph.Requests.First(IsAccounts).Query).Should()
            .Contain("fields=id,name,instagram_business_account{id,username}").And.Contain("limit=100");
        log.Entries.Should().NotContain(e => e.Full.Contains(next));   // paging.next loglanmaz
    }

    [Fact]
    public async Task Listeleme_en_cok_on_yanit_sayfasi_izler()
    {
        var (service, _) = await ConnectedAsync();
        // bitmeyen paging.next
        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json(AccountsJson(NextUrl(), NewRow(igAccount: true))));

        (await service.ListAddablePagesAsync()).Pages.Should().HaveCount(10);

        _kit.Graph.Requests.Count(IsAccounts).Should().Be(10);
    }

    /// <summary>100 bu uçta "yayın bitti" değildir: istenen alanın reddedildiğini gösterir.</summary>
    [Theory]
    [InlineData(190)]
    [InlineData(463)]
    [InlineData(200)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Token_izin_ve_100_hatasinda_NeedsReauthorize_ve_bos_liste_doner(int code)
    {
        var (service, _) = await ConnectedAsync();
        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(code));

        var listing = await service.ListAddablePagesAsync();

        (listing.NeedsReauthorize, listing.Pages.Count).Should().Be((true, 0));
    }

    [Fact]
    public async Task Sunucu_ve_kota_hatasinda_NeedsReauthorize_kurulmaz_hata_yukari_cikar()
    {
        var (service, _) = await ConnectedAsync();
        var list = FluentActions.Awaiting(() => service.ListAddablePagesAsync());

        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json("{}", HttpStatusCode.InternalServerError));
        (await list.Should().ThrowAsync<FacebookGraphException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(4, subcode: 2446079));   // kota
        var quota = (await list.Should().ThrowAsync<FacebookGraphException>()).Which;
        quota.Code.Should().Be(4);
        quota.Subcode.Should().Be(2446079);
        quota.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bos_govde_hatasinin_mesajinda_token_ve_adres_gecmez()
    {
        var (service, _) = await ConnectedAsync();
        var (userToken, pageId) = ((await service.GetUserAccessTokenAsync())!, Kit.NewId());
        _kit.Graph.Map(u => IsPageToken(u, pageId), _ => FakeGraph.Json("null"));   // boş gövde

        (await FluentActions.Awaiting(() => service.AddExtraPageAsync(pageId, NewName()))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().NotContain(userToken).And.NotContain("http");

        (await IdsAsync(service)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Depo_yazimi_duserse_ekleme_istisna_verir_bagli_sayfalar_degismez()
    {
        var (service, seeded) = await ConnectedAsync();
        var fresh = NewGraphPage();
        // hedef salt okunur: deponun taşıma adımı düşer
        File.SetAttributes(_kit.TokenFilePath, FileAttributes.ReadOnly);
        try
        {
            await FluentActions.Awaiting(() => service.AddExtraPageAsync(fresh.PageId, fresh.PageName))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetAttributes(_kit.TokenFilePath, FileAttributes.Normal);
        }

        (await IdsAsync(service)).Should().Equal(seeded[0].Page.PageId);
    }

    [Fact]
    public async Task Eszamanli_ekle_kaldir_kayip_guncelleme_uretmez()
    {
        var (service, seeded) = await ConnectedAsync(extraPages: 1);
        var (a, b) = (NewGraphPage(), NewGraphPage());

        await Task.WhenAll(
            Task.Run(() => service.AddExtraPageAsync(a.PageId, a.PageName)),
            Task.Run(() => service.AddExtraPageAsync(b.PageId, b.PageName)),
            Task.Run(() => service.RemoveExtraPageAsync(seeded[1].Page.PageId))).WaitAsync(TestPatience.Upper);

        var expected = new[] { seeded[0].Page.PageId, a.PageId, b.PageId };
        (await IdsAsync(service)).Should().BeEquivalentTo(expected);
        (await IdsAsync()).Should().BeEquivalentTo(expected);   // disk de aynı
    }

    [Fact]
    public async Task DisconnectAsync_ekler_dahil_her_seyi_siler()
    {
        var (service, _) = await ConnectedAsync(extraPages: 2);

        await service.DisconnectAsync();

        (await service.GetConnectedPagesAsync()).Should().BeEmpty();
        (await service.IsConnectedAsync(), File.Exists(_kit.TokenFilePath), _kit.Settings.FacebookPageId)
            .Should().Be((false, false, (string?)null));
    }
}
