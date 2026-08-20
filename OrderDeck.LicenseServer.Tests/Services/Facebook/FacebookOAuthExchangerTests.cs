using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Facebook;

/// <summary>
/// Code → uzun ömürlü token takası. Burada sınanan şey Meta'nın şeması değil,
/// bizim garantimiz: App Secret sorgu dizesine sızmıyor, iki adım gerçekten
/// zincirleniyor ve Meta'nın hatası çağırana anlaşılır biçimde ulaşıyor.
/// </summary>
public sealed class FacebookOAuthExchangerTests
{
    /// <summary>Sırayla yanıt veren sahte taşıyıcı — iki adımlı akışta hangi
    /// isteğin ne gönderdiğini de saklıyor.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        public ScriptedHandler(params (HttpStatusCode, string)[] script)
            => _script = new Queue<(HttpStatusCode, string)>(script);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct));

            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("dns patladı");
    }

    private static FacebookOAuthExchanger Exchanger(HttpMessageHandler handler)
    {
        var opt = Options.Create(new FacebookOptions
        {
            GraphBaseUrl = "https://graph.test",
            GraphApiVersion = "v22.0",
            AppId = "APP_1",
            AppSecret = "SECRET_1",
            RedirectUri = "https://orderdeckapp.com/facebook/callback",
        });
        return new FacebookOAuthExchanger(
            new HttpClient(handler), opt, NullLogger<FacebookOAuthExchanger>.Instance);
    }

    private const string ShortLived = """{ "access_token": "SHORT", "expires_in": 3600 }""";
    private const string LongLived = """{ "access_token": "LONG", "expires_in": 5183944 }""";

    [Fact]
    public async Task Kisa_omurlu_tokeni_uzun_omurluye_cevirir()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, ShortLived),
            (HttpStatusCode.OK, LongLived));

        var result = await Exchanger(handler)
            .ExchangeCodeForLongLivedAsync("CODE_1", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Value!.AccessToken.Should().Be("LONG");
        result.Value.ExpiresInSeconds.Should().Be(5183944);

        // İkinci adım gerçekten BİRİNCİNİN çıktısını taşımalı; aksi hâlde
        // masaüstü 60 gün sanıp 1 saatlik token saklardı.
        handler.Bodies[1].Should().Contain("fb_exchange_token=SHORT");
        handler.Bodies[1].Should().Contain("grant_type=fb_exchange_token");
    }

    [Fact]
    public async Task Sir_ve_code_govdede_gider_url_de_degil()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, ShortLived),
            (HttpStatusCode.OK, LongLived));

        await Exchanger(handler).ExchangeCodeForLongLivedAsync("CODE_1", CancellationToken.None);

        // HttpClient logger'ı istek URI'sini Information seviyesinde yazıyor —
        // sırrın oraya düşmediği testle sabitleniyor.
        foreach (var req in handler.Requests)
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString()
                .Should().Be("https://graph.test/v22.0/oauth/access_token");
        }

        handler.Bodies[0].Should().Contain("client_secret=SECRET_1");
        handler.Bodies[0].Should().Contain("code=CODE_1");
        handler.Bodies[0].Should()
            .Contain("redirect_uri=https%3A%2F%2Forderdeckapp.com%2Ffacebook%2Fcallback");
    }

    [Fact]
    public async Task Ilk_adim_patlarsa_ikinci_adim_hic_denenmez()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest,
             """{ "error": { "code": 100, "message": "Invalid verification code format." } }"""));

        var result = await Exchanger(handler)
            .ExchangeCodeForLongLivedAsync("BOZUK", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("100");
        result.ErrorMessage.Should().Contain("verification code");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Uzun_omurlu_adimindaki_meta_hatasi_yuzeye_cikar()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, ShortLived),
            (HttpStatusCode.BadRequest,
             """{ "error": { "code": 190, "message": "Invalid OAuth access token." } }"""));

        var result = await Exchanger(handler)
            .ExchangeCodeForLongLivedAsync("CODE_1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("190");
    }

    [Fact]
    public async Task Token_tasimayan_yanit_beklenmedik_sekil_sayilir()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{ "veri": "yok" }"""));

        var result = await Exchanger(handler)
            .ExchangeCodeForLongLivedAsync("CODE_1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("unexpected-shape");
    }

    [Fact]
    public async Task Json_olmayan_govde_disari_sizmaz()
    {
        // Meta'nın eski OAuth ucu form-encoded dönüyordu; o gövdede access_token
        // açıkta duruyor ve çağırana DÖNMEMELİ.
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, "access_token=SIZAN_TOKEN&expires=5183944"));

        var result = await Exchanger(handler)
            .ExchangeCodeForLongLivedAsync("CODE_1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("200");
        result.ErrorMessage.Should().Be("beklenmedik yanıt");
        result.ErrorMessage.Should().NotContain("SIZAN_TOKEN");
    }

    [Fact]
    public async Task Ag_hatasi_firlatmaz_yapisal_sonuc_doner()
    {
        var result = await Exchanger(new ThrowingHandler())
            .ExchangeCodeForLongLivedAsync("CODE_1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("network");
    }

    [Fact]
    public async Task Expires_in_yoksa_sifir_doner()
    {
        // "Bilinmiyor" = 0. Uydurulmuş bir tarih, süresi dolmuş token'ı geçerli
        // gösterip yayının ortasında sessizce kırardı.
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, ShortLived),
            (HttpStatusCode.OK, """{ "access_token": "LONG" }"""));

        var result = await Exchanger(handler)
            .ExchangeCodeForLongLivedAsync("CODE_1", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Value!.ExpiresInSeconds.Should().Be(0);
    }
}
