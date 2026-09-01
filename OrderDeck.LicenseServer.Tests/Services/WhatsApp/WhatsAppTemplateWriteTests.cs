using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public class WhatsAppTemplateWriteTests
{
    /// <summary>Giden isteğin gövdesini metin olarak saklar — istek elden
    /// çıktıktan sonra <c>Content</c> okunamıyor.</summary>
    private sealed class CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpMethod? Method;
        public string? Url;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri!.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static WhatsAppTemplateCatalog Catalog(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new WhatsAppOptions
            {
                GraphBaseUrl = "https://graph.test",
                GraphApiVersion = "v25.0",
            }),
            NullLogger<WhatsAppTemplateCatalog>.Instance);

    private static WhatsAppTemplateDraft Draft() => new(
        "Sipariş bilgisi",
        "Merhaba {{1}}, {{2}} TL tutarındaki siparişiniz hazır.",
        "OrderDeck",
        ["Ayşe", "250"],
        [new WhatsAppTemplateButton("QUICK_REPLY", "Tamam", null, null)]);

    [Fact]
    public async Task Create_dogru_uca_dogru_govdeyi_gonderiyor()
    {
        var handler = new CapturingHandler("""{"id":"9001","status":"PENDING","category":"UTILITY"}""");

        var result = await Catalog(handler).CreateAsync(
            "WABA1", "TOKEN", "siparis_hazir", "UTILITY", "tr", Draft(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("9001", result.Value!.Id);
        Assert.Equal("PENDING", result.Value!.Status);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://graph.test/v25.0/WABA1/message_templates", handler.Url);

        using var sent = JsonDocument.Parse(handler.Body!);
        var root = sent.RootElement;
        Assert.Equal("siparis_hazir", root.GetProperty("name").GetString());
        Assert.Equal("UTILITY", root.GetProperty("category").GetString());
        Assert.Equal("tr", root.GetProperty("language").GetString());

        var comps = root.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(["HEADER", "BODY", "FOOTER", "BUTTONS"],
            comps.Select(c => c.GetProperty("type").GetString()));

        var examples = comps[1].GetProperty("example").GetProperty("body_text")[0]
            .EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["Ayşe", "250"], examples);
    }

    // Değişkensiz şablona boş bir example nesnesi eklemek Meta'dan ret getiriyor.
    [Fact]
    public async Task Degiskensiz_govdede_ornek_alani_gonderilmiyor()
    {
        var handler = new CapturingHandler("""{"id":"9002","status":"PENDING"}""");

        await Catalog(handler).CreateAsync(
            "WABA1", "TOKEN", "kargo", "UTILITY", "tr",
            new WhatsAppTemplateDraft(null, "Kargonuz yolda.", null, [], []),
            CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.Body!);
        var body = Assert.Single(sent.RootElement.GetProperty("components").EnumerateArray().ToList());
        Assert.Equal("BODY", body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("example", out _));
    }

    [Fact]
    public async Task Create_meta_hatasini_veri_olarak_donduruyor()
    {
        var handler = new CapturingHandler(
            """{"error":{"code":100,"message":"Template name already exists"}}""",
            HttpStatusCode.BadRequest);

        var result = await Catalog(handler).CreateAsync(
            "WABA1", "TOKEN", "kargo", "UTILITY", "tr", Draft(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("100", result.ErrorCode);
        Assert.Contains("already exists", result.ErrorMessage);
    }

    /// <summary>Oluşturmada gönderdiğimiz bileşenleri Meta'nın liste yanıtına
    /// koyup katalogla geri okuyoruz. Gönderilemez çıkarsa panel kendi
    /// gönderemeyeceği şablonu üretiyor demektir.</summary>
    [Theory]
    [MemberData(nameof(GonderilebilirTaslaklar))]
    public async Task Olusturulan_sablon_katalogca_gonderilebilir_okunuyor(WhatsAppTemplateDraft draft)
    {
        Assert.Null(WhatsAppTemplateShape.Validate(draft));

        var create = new CapturingHandler("""{"id":"9100","status":"PENDING"}""");
        var created = await Catalog(create).CreateAsync(
            "WABA1", "TOKEN", "gidis_donus", "UTILITY", "tr", draft, CancellationToken.None);
        Assert.True(created.Ok);

        using var sent = JsonDocument.Parse(create.Body!);
        var components = sent.RootElement.GetProperty("components").GetRawText();

        var listJson = $$"""
        {"data":[{"id":"9100","name":"gidis_donus","status":"APPROVED","category":"UTILITY",
                  "language":"tr","components":{{components}}}]}
        """;

        var read = await Catalog(new CapturingHandler(listJson))
            .ListAllAsync("WABA1", "TOKEN", CancellationToken.None);

        Assert.True(read.Ok);
        var t = Assert.Single(read.Value!);
        Assert.Null(t.UnsupportedReason);
        Assert.Equal(draft.BodyExamples.Count, t.ParameterCount);
    }

    public static TheoryData<WhatsAppTemplateDraft> GonderilebilirTaslaklar() => new()
    {
        new WhatsAppTemplateDraft(null, "Kargonuz yolda.", null, [], []),
        new WhatsAppTemplateDraft("Sipariş bilgisi", "Merhaba {{1}}.", "OrderDeck", ["Ayşe"], []),
        new WhatsAppTemplateDraft(null, "Merhaba {{1}}, {{2}} TL.", null, ["Ayşe", "250"],
        [
            new WhatsAppTemplateButton("QUICK_REPLY", "Evet", null, null),
            new WhatsAppTemplateButton("QUICK_REPLY", "Hayır", null, null),
            new WhatsAppTemplateButton("URL", "Siteye git", "https://orderdeckapp.com", null),
            new WhatsAppTemplateButton("PHONE_NUMBER", "Ara", null, "+905321234567"),
        ]),
    };
}
