using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Embedded Signup'ın Graph ayağı. Bu sınıf gerçek bir yayıncı bağlanana kadar
/// prod'da hiç çalışmıyor; ilk canlı denemede yanlış parametre adı yüzünden
/// 30 saniyelik <c>code</c>'u yakmamak için istekler burada birebir doğrulanıyor.
/// </summary>
public sealed class WhatsAppOnboardingClientTests
{
    /// <summary>Sıraya konmuş yanıtları teker teker döner ve istekleri kaydeder —
    /// onboarding tek çağrı değil, çağrı ZİNCİRİ; tek yanıtlı sahte yetmez.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();

        public ScriptedHandler(params (HttpStatusCode, string)[] script) =>
            _script = new Queue<(HttpStatusCode, string)>(script);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static WhatsAppOnboardingClient Client(ScriptedHandler handler)
    {
        var opt = Options.Create(new WhatsAppOptions
        {
            GraphBaseUrl = "https://graph.test",
            GraphApiVersion = "v25.0",
            AppId = "APP_1",
            AppSecret = "SECRET_1",
        });
        return new WhatsAppOnboardingClient(
            new HttpClient(handler), opt, NullLogger<WhatsAppOnboardingClient>.Instance);
    }

    [Fact]
    public async Task Exchanging_the_code_sends_app_credentials_and_returns_the_business_token()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, """{ "access_token": "BIZ_TOKEN", "token_type": "bearer" }"""));

        var result = await Client(handler).ExchangeCodeAsync("CODE_123", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Value.Should().Be("BIZ_TOKEN");

        // Kimlik bilgileri GÖVDEDE gitmeli. Sorgu dizesindeyken app secret'ı
        // koruyan tek şey HttpClient log'unun redaction'ı idi — o bir çalışma
        // zamanı varsayılanı (DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION) ve
        // OTel'in URI'yi yazan ayrı bir kapağı daha var.
        var url = handler.Requests[0].RequestUri!.ToString();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        url.Should().Be("https://graph.test/v25.0/oauth/access_token");
        url.Should().NotContain("client_secret");
        url.Should().NotContain("SECRET_1");
        handler.Bodies[0].Should().Contain("client_id=APP_1");
        handler.Bodies[0].Should().Contain("client_secret=SECRET_1");
        handler.Bodies[0].Should().Contain("code=CODE_123");
    }

    [Fact]
    public async Task A_meta_error_becomes_a_structured_failure_not_an_exception()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, """
            { "error": { "code": 100, "message": "Invalid verification code format." } }
            """));

        var result = await Client(handler).ExchangeCodeAsync("EXPIRED", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("100");
        result.ErrorMessage.Should().Contain("Invalid verification code");
    }

    [Fact]
    public async Task Subscribing_the_app_posts_to_the_waba_with_the_business_token()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{ "success": true }"""));

        var result = await Client(handler)
            .SubscribeAppAsync("WABA_9", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/WABA_9/subscribed_apps");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("BIZ_TOKEN");
    }

    [Fact]
    public async Task Reading_the_phone_number_asks_only_for_the_display_fields()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """
            { "display_phone_number": "+90 555 111 22 33", "verified_name": "Emar Global" }
            """));

        var result = await Client(handler)
            .ReadPhoneNumberAsync("PNID_7", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Value!.DisplayPhoneNumber.Should().Be("+90 555 111 22 33");
        result.Value.VerifiedName.Should().Be("Emar Global");
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/PNID_7?fields=display_phone_number,verified_name");
    }

    [Fact]
    public async Task Registering_the_number_sends_the_pin_in_the_body()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{ "success": true }"""));

        var result = await Client(handler)
            .RegisterPhoneNumberAsync("PNID_7", "123456", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/PNID_7/register");
        handler.Bodies[0].Should().Contain("\"messaging_product\":\"whatsapp\"");
        handler.Bodies[0].Should().Contain("\"pin\":\"123456\"");
    }
}
