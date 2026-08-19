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
    public async Task An_unparseable_response_never_carries_its_body_back_to_the_caller()
    {
        // Meta'nın eski OAuth ucu JSON değil form-encoded dönüyordu ve
        // GraphApiVersion operatör ayarı — yani bu gövde hâlâ gelebilir.
        // İçindeki access_token, çağıranın Detail(...) yoluyla panele ve
        // oradan tarayıcıya iner; ham gövde ASLA dışarı çıkmamalı.
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, "access_token=EAA_SAHTE_TOKEN_DEGERI&expires=5184000"));

        var result = await Client(handler).ExchangeCodeAsync("CODE_123", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotContain("EAA_SAHTE_TOKEN_DEGERI");
        result.ErrorMessage.Should().NotContain("access_token");
    }

    [Fact]
    public async Task An_unexpected_shape_reports_a_code_without_echoing_the_body()
    {
        // 200 ama beklenen alan yok: gövde yine dışarı sızmamalı, ama
        // panelin gösterebileceği bir kod kalmalı.
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, """{ "gizli": "EAA_SAHTE_TOKEN_DEGERI" }"""));

        var result = await Client(handler).ExchangeCodeAsync("CODE_123", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("unexpected-shape");
        result.ErrorMessage.Should().NotContain("EAA_SAHTE_TOKEN_DEGERI");
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
    public async Task Unsubscribing_the_app_deletes_the_subscription_on_the_waba()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{ "success": true }"""));

        var result = await Client(handler)
            .UnsubscribeAppAsync("WABA_9", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/WABA_9/subscribed_apps");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("BIZ_TOKEN");
    }

    [Fact]
    public async Task Reading_the_phone_number_goes_through_the_wabas_own_number_list()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """
            { "data": [
                { "id": "PNID_1", "display_phone_number": "+90 555 000 00 00", "verified_name": "Başka" },
                { "id": "PNID_7", "display_phone_number": "+90 555 111 22 33", "verified_name": "Emar Global" }
            ] }
            """));

        var result = await Client(handler)
            .ReadPhoneNumberAsync("WABA_9", "PNID_7", "BIZ_TOKEN", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Value!.DisplayPhoneNumber.Should().Be("+90 555 111 22 33");
        result.Value.VerifiedName.Should().Be("Emar Global");

        // Numarayı doğrudan `GET /{pnid}` ile okumak da görünen numarayı verirdi
        // ama numaranın O WABA'ya ait olduğunu KANITLAMAZDI: Meta'nın numara
        // düğümünde üst WABA'ya geri işaret eden bir alan yok. Tek çağrıda hem
        // görünen numara hem eşleşme buradan geliyor.
        handler.Requests[0].RequestUri!.ToString().Should().Be(
            "https://graph.test/v25.0/WABA_9/phone_numbers?fields=id,display_phone_number,verified_name&limit=100");
    }

    [Fact]
    public async Task A_number_that_belongs_to_a_different_waba_is_reported_as_a_mismatch()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """
            { "data": [ { "id": "PNID_1", "display_phone_number": "+90 555 000 00 00" } ] }
            """));

        var result = await Client(handler)
            .ReadPhoneNumberAsync("WABA_9", "PNID_7", "BIZ_TOKEN", CancellationToken.None);

        // Eşleşme doğrulanmasaydı satıra numaranın SAHİBİ OLMAYAN bir WABA
        // yazılırdı: abonelik yanlış hesaba gider, o numaraya gelen mesajlar
        // webhook'umuza hiç düşmez ve panel "bağlı" göstermeye devam eder.
        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("phone-number-not-in-waba");
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
