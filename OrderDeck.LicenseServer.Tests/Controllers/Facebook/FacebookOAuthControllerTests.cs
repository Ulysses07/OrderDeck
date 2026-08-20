using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Facebook;

/// <summary>
/// Masaüstünün Facebook OAuth ayağı. Graph sahtelenmiş — sınanan şey Meta'nın
/// cevabı değil, ucun garantileri: App Secret hiçbir yanıtta yok, yetkisiz
/// çağıran takas yaptıramıyor ve yapılandırılmamış sunucu sessizce değil
/// açıkça başarısız oluyor.
/// </summary>
public sealed class FacebookOAuthControllerTests : IDisposable
{
    private readonly List<FbApiFactory> _factories = [];

    private sealed class FakeExchanger : IFacebookOAuthExchanger
    {
        public GraphResult<FacebookUserToken> Result =
            GraphResult<FacebookUserToken>.Success(new FacebookUserToken("LONG", 5183944));

        public string? SeenCode;

        public Task<GraphResult<FacebookUserToken>> ExchangeCodeForLongLivedAsync(
            string code, CancellationToken ct)
        {
            SeenCode = code;
            return Task.FromResult(Result);
        }
    }

    private sealed class FbApiFactory : ApiFactory
    {
        public FakeExchanger Exchanger { get; } = new();

        /// <summary>Varsayılan sunucu yapılandırılmamıştır (App Secret yok);
        /// "yapılandırılmış" hâli testler açıkça istediğinde kuruluyor.</summary>
        public bool Configured { get; init; } = true;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IFacebookOAuthExchanger>(Exchanger);
                s.Configure<FacebookOptions>(o =>
                {
                    o.AppId = Configured ? "APP_1" : "";
                    o.AppSecret = Configured ? "SECRET_1" : "";
                    o.LoginConfigId = "CONFIG_1";
                    o.RedirectUri = "https://orderdeckapp.com/facebook/callback";
                    o.GraphApiVersion = "v22.0";
                });
            });
        }
    }

    private sealed record Seed(HttpClient Client, FbApiFactory Factory)
    {
        public FakeExchanger Exchanger => Factory.Exchanger;
    }

    private async Task<Seed> SeedAsync(bool configured = true)
    {
        var factory = new FbApiFactory { Configured = configured };
        _factories.Add(factory);
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        return new Seed(client, factory);
    }

    private sealed record ConfigDto(
        string AppId, string LoginConfigId, string RedirectUri, string GraphApiVersion);

    private sealed record ExchangeDto(string AccessToken, long ExpiresInSeconds);

    private static Task<HttpResponseMessage> ExchangeAsync(Seed s, string code) =>
        s.Client.PostAsJsonAsync("/api/v1/facebook/oauth/exchange", new { code });

    [Fact]
    public async Task Config_istemcinin_url_kurmasi_icin_gerekeni_verir()
    {
        var s = await SeedAsync();

        var resp = await s.Client.GetAsync("/api/v1/facebook/oauth/config");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cfg = (await resp.Content.ReadFromJsonAsync<ConfigDto>())!;
        cfg.AppId.Should().Be("APP_1");
        cfg.LoginConfigId.Should().Be("CONFIG_1");
        cfg.RedirectUri.Should().Be("https://orderdeckapp.com/facebook/callback");
        cfg.GraphApiVersion.Should().Be("v22.0");
    }

    [Fact]
    public async Task Config_app_secreti_asla_dondurmez()
    {
        // Bu ucun varlık sebebi sırrın istemciye gitmemesi; yanlışlıkla
        // eklenen bir alan tüm işi boşa çıkarır.
        var s = await SeedAsync();

        var body = await (await s.Client.GetAsync("/api/v1/facebook/oauth/config"))
            .Content.ReadAsStringAsync();

        body.Should().NotContain("SECRET_1");
        body.Should().NotContain("appSecret");
    }

    [Fact]
    public async Task Takas_uzun_omurlu_tokeni_dondurur()
    {
        var s = await SeedAsync();

        var resp = await ExchangeAsync(s, "CODE_1");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ExchangeDto>())!;
        dto.AccessToken.Should().Be("LONG");
        dto.ExpiresInSeconds.Should().Be(5183944);
        s.Exchanger.SeenCode.Should().Be("CODE_1");
    }

    [Fact]
    public async Task Bos_code_metaya_hic_gitmez()
    {
        var s = await SeedAsync();

        var resp = await ExchangeAsync(s, "   ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        s.Exchanger.SeenCode.Should().BeNull();
    }

    [Fact]
    public async Task Meta_hatasi_502_olarak_ve_koduyla_doner()
    {
        // Kod yüzeye çıkmazsa yayıncıya "bağlanamadım" demekten başka bir şey
        // söyleyemeyiz; 100 = süresi geçmiş code, 190 = token sorunu.
        var s = await SeedAsync();
        s.Exchanger.Result = GraphResult<FacebookUserToken>.Failure(
            "100", "This authorization code has expired.");

        var resp = await ExchangeAsync(s, "ESKI_CODE");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("facebook-exchange-failed");
        body.Should().Contain("100");
    }

    [Fact]
    public async Task Yapilandirilmamis_sunucu_acikca_503_der()
    {
        var s = await SeedAsync(configured: false);

        (await s.Client.GetAsync("/api/v1/facebook/oauth/config"))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var resp = await ExchangeAsync(s, "CODE_1");
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("facebook-not-configured");
        s.Exchanger.SeenCode.Should().BeNull();
    }

    [Fact]
    public async Task Yetkisiz_cagiran_bizim_app_secretimizi_kullanamaz()
    {
        var s = await SeedAsync();
        var anon = s.Factory.CreateClient();

        (await anon.GetAsync("/api/v1/facebook/oauth/config"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/v1/facebook/oauth/exchange", new { code = "CODE_1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        s.Exchanger.SeenCode.Should().BeNull();
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }
}
