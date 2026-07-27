using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Giden Graph çağrısının <b>tel üzerindeki şekli</b>. Bu sınıf gerçek bir
/// numara bağlanana kadar prod'da hiç çalışmıyor; ilk canlı denemede yanlış
/// alan adı/eksik zarf yüzünden vakit kaybetmemek için istek burada birebir
/// doğrulanıyor.
///
/// <para>Hata yolları da kapsanıyor: Meta hatası fırlatılmaz, yapısal sonuca
/// çevrilir — çağıran (WhatsAppMessagingService) buna göre mesajı "failed"
/// işaretliyor.</para>
/// </summary>
public sealed class CloudApiWhatsAppSenderTests
{
    private static readonly WhatsAppSendContext Ctx = new("PNID_1", "TOKEN_X");

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _contentType;
        private readonly Exception? _throw;

        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        public CapturingHandler(
            HttpStatusCode status, string body,
            string contentType = "application/json", Exception? throwOnSend = null)
        {
            _status = status;
            _body = body;
            _contentType = contentType;
            _throw = throwOnSend;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_throw is not null) throw _throw;

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            };
        }
    }

    private const string OkBody = """
        {
          "messaging_product": "whatsapp",
          "contacts": [{ "input": "905551112233", "wa_id": "905551112233" }],
          "messages": [{ "id": "wamid.HBgMOTA1NTUx" }]
        }
        """;

    private static (CloudApiWhatsAppSender Sender, CapturingHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = OkBody,
        string contentType = "application/json",
        Exception? throwOnSend = null)
    {
        var handler = new CapturingHandler(status, body, contentType, throwOnSend);
        var sender = new CloudApiWhatsAppSender(
            new HttpClient(handler),
            Options.Create(new WhatsAppOptions
            {
                GraphBaseUrl = "https://graph.test/",
                GraphApiVersion = "v25.0",
            }),
            NullLogger<CloudApiWhatsAppSender>.Instance);
        return (sender, handler);
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public async Task Text_send_posts_the_documented_envelope()
    {
        var (sender, handler) = Build();

        var result = await sender.SendTextAsync(Ctx, "+90 555 111 22 33", "merhaba");

        result.Ok.Should().BeTrue();
        result.MessageId.Should().Be("wamid.HBgMOTA1NTUx");

        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString()
            .Should().Be("https://graph.test/v25.0/PNID_1/messages");
        handler.Request.Headers.Authorization!.ToString().Should().Be("Bearer TOKEN_X");

        var body = Json(handler.RequestBody!);
        body.GetProperty("messaging_product").GetString().Should().Be("whatsapp");
        body.GetProperty("recipient_type").GetString().Should().Be("individual");
        body.GetProperty("type").GetString().Should().Be("text");
        // Graph "to" alanı yalnız rakam ister — '+' ve boşluklar temizlenmeli.
        body.GetProperty("to").GetString().Should().Be("905551112233");
        body.GetProperty("text").GetProperty("body").GetString().Should().Be("merhaba");
        body.GetProperty("text").GetProperty("preview_url").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Template_without_params_omits_components_entirely()
    {
        var (sender, handler) = Build();

        var result = await sender.SendTemplateAsync(
            Ctx, "905551112233", new WhatsAppTemplate("hello_world", "en_US", Array.Empty<string>()));

        result.Ok.Should().BeTrue();

        var body = Json(handler.RequestBody!);
        body.GetProperty("type").GetString().Should().Be("template");
        var tpl = body.GetProperty("template");
        tpl.GetProperty("name").GetString().Should().Be("hello_world");
        tpl.GetProperty("language").GetProperty("code").GetString().Should().Be("en_US");
        // Boş components dizisi Graph'ta hata — parametresiz template'te alan hiç olmamalı.
        tpl.TryGetProperty("components", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Template_with_params_maps_body_parameters_in_order()
    {
        var (sender, handler) = Build();

        await sender.SendTemplateAsync(
            Ctx, "905551112233",
            new WhatsAppTemplate("siparis_hazir", "tr", new[] { "Ayşe", "SP-1042" }));

        var components = Json(handler.RequestBody!).GetProperty("template").GetProperty("components");
        components.GetArrayLength().Should().Be(1);

        var comp = components[0];
        comp.GetProperty("type").GetString().Should().Be("body");

        var prms = comp.GetProperty("parameters");
        prms.GetArrayLength().Should().Be(2);
        prms[0].GetProperty("type").GetString().Should().Be("text");
        prms[0].GetProperty("text").GetString().Should().Be("Ayşe");
        prms[1].GetProperty("text").GetString().Should().Be("SP-1042");
    }

    [Fact]
    public async Task Meta_error_becomes_a_structured_failure_with_details()
    {
        // 131047 = 24 saatlik pencere kapalı. En sık karşılaşacağımız hata.
        var (sender, _) = Build(HttpStatusCode.BadRequest, """
            {
              "error": {
                "message": "(#131047) Re-engagement message",
                "code": 131047,
                "error_data": { "details": "Message failed to send because more than 24 hours have passed." }
              }
            }
            """);

        var result = await sender.SendTextAsync(Ctx, "905551112233", "merhaba");

        result.Ok.Should().BeFalse();
        result.MessageId.Should().BeNull();
        result.ErrorCode.Should().Be("131047");
        result.ErrorMessage.Should().Contain("Re-engagement");
        result.ErrorMessage.Should().Contain("24 hours", "error_data.details mesaja ekleniyor");
    }

    [Fact]
    public async Task Expired_token_error_is_surfaced_not_thrown()
    {
        // 190 = token süresi doldu; dashboard token'ı ~24 saatte ölüyor, bunu
        // teşhis edebilmek için kodun gövdede dönmesi şart.
        var (sender, _) = Build(HttpStatusCode.Unauthorized, """
            { "error": { "message": "Error validating access token", "code": 190 } }
            """);

        var result = await sender.SendTextAsync(Ctx, "905551112233", "merhaba");

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("190");
    }

    [Fact]
    public async Task Non_json_response_falls_back_to_status_code_and_raw_body()
    {
        // Caddy/proxy araya girip HTML dönerse JSON parse patlamamalı.
        var (sender, _) = Build(
            HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>", contentType: "text/html");

        var result = await sender.SendTextAsync(Ctx, "905551112233", "merhaba");

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("502");
        result.ErrorMessage.Should().Contain("Bad Gateway");
    }

    [Fact]
    public async Task Network_failure_is_reported_as_network_not_an_exception()
    {
        var (sender, _) = Build(throwOnSend: new HttpRequestException("no route to host"));

        var result = await sender.SendTextAsync(Ctx, "905551112233", "merhaba");

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("network");
        result.ErrorMessage.Should().Contain("no route to host");
    }

    [Fact]
    public async Task Success_status_without_message_id_is_not_treated_as_sent()
    {
        // 200 ama beklenen zarf yok → wamid uyduramayız, başarısız say.
        var (sender, _) = Build(HttpStatusCode.OK, """{ "messaging_product": "whatsapp" }""");

        var result = await sender.SendTextAsync(Ctx, "905551112233", "merhaba");

        result.Ok.Should().BeFalse();
        result.MessageId.Should().BeNull();
    }
}
