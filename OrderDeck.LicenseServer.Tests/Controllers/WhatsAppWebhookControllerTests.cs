using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers;

/// <summary>
/// Webhook uç noktasının HTTP yüzeyi. Servis birim testleri imza/parse mantığını
/// zaten kapsıyor; burada asıl doğrulanan <b>durum kodları</b> — reddedilen istek
/// 403 dönmeli, 5xx <b>değil</b>: Meta 5xx'i "teslim edilemedi" sayar, tekrar dener
/// ve ısrarlı hatada aboneliği kapatabilir. (Regresyon: <c>Forbid()</c> anonim
/// controller'da DefaultForbidScheme olmadığı için 500 fırlatıyordu.)
/// </summary>
public sealed class WhatsAppWebhookControllerTests : IClassFixture<WhatsAppWebhookControllerTests.Factory>
{
    private const string VerifyToken = "test-verify-token";
    private const string AppSecret = "test-app-secret";
    private const string Url = "/api/v1/whatsapp/webhook";

    public sealed class Factory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["OrderDeck:WhatsApp:VerifyToken"] = VerifyToken,
            ["OrderDeck:WhatsApp:AppSecret"] = AppSecret,
        };
    }

    private readonly Factory _factory;
    public WhatsAppWebhookControllerTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Verify_echoes_challenge_when_token_matches()
    {
        var resp = await _factory.CreateClient().GetAsync(
            $"{Url}?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=CHALLENGE123");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("CHALLENGE123");
    }

    [Fact]
    public async Task Verify_returns_403_when_token_wrong()
    {
        var resp = await _factory.CreateClient().GetAsync(
            $"{Url}?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=CHALLENGE123");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Receive_returns_403_when_signature_missing()
    {
        var resp = await _factory.CreateClient().PostAsync(
            Url, new StringContent("{\"object\":\"whatsapp_business_account\"}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Receive_returns_403_when_signature_forged()
    {
        var body = "{\"object\":\"whatsapp_business_account\"}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Hub-Signature-256", Sign(body, "yanlis-secret"));

        var resp = await _factory.CreateClient().PostAsync(Url, content);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Receive_returns_200_when_signature_valid()
    {
        var body = "{\"object\":\"whatsapp_business_account\",\"entry\":[]}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Hub-Signature-256", Sign(body, AppSecret));

        var resp = await _factory.CreateClient().PostAsync(Url, content);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Receive_returns_413_for_oversized_chunked_body()
    {
        // N09 (2026-09-10 denetimi): ContentLength kontrolü chunked istekte
        // (ContentLength=null) hiç çalışmıyordu — "null > sınır" false döner,
        // 1MB tavanı atlanır ve gövde sonuna kadar okunurdu (Kestrel'in 30MB
        // genel tavanına dek). Limit artık gerçek okumada da uygulanmalı.
        // İmza DOĞRU üretiliyor ki eski kodda istek 200'e ulaşsın: 413 yerine
        // 200 görmek, sınırın delindiğinin kanıtı.
        var body = "{\"pad\":\"" + new string('a', 2 * 1024 * 1024) + "\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Content.Headers.Add("X-Hub-Signature-256", Sign(body, AppSecret));
        // Content-Length'i kaldırıp chunked'a zorla — Meta değil, saldırgan senaryosu.
        request.Headers.TransferEncodingChunked = true;

        var resp = await _factory.CreateClient().SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge,
            "gövde sınırı Content-Length başlığına değil gerçekten okunan bayta bağlı olmalı");
    }

    private static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
