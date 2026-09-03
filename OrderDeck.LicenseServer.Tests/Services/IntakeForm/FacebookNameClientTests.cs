using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class FacebookNameClientTests
{
    private readonly string _appId = $"fbid-{Guid.NewGuid():N}";
    private readonly string _appSecret = $"fbs-{Guid.NewGuid():N}";

    // Stub token da üretilir — sabit token dizesi tarayıcıyı tetikler.
    private static readonly string Tok = $"fbtok-{Guid.NewGuid():N}";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, string? AuthHeader)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return Respond(request);
        }
    }

    private (FacebookNameClient Client, HttpClient Http) NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler);
        var client = new FacebookNameClient(
            http,
            Options.Create(new FacebookOptions { AppId = _appId, AppSecret = _appSecret }),
            Options.Create(new IntakeLoginOptions
            {
                RedirectUri = "https://orderdeckapp.com/musteri-kayit/baglanti-donusu"
            }),
            NullLogger<FacebookNameClient>.Instance);
        return (client, http);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task Basarili_akista_gorunen_ad_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("oauth/access_token")
                ? Json($$"""{"access_token":"{{Tok}}"}""")
                : Json("""{"id":"123","name":"Musa Sevinç"}""")
        };

        var (client, http) = NewClient(handler);
        using (http)
        {
            var result = await client.FetchNameAsync("code-1", CancellationToken.None);

            result.Ok.Should().BeTrue();
            // Handle/ChannelId YOK: canlı yorumlarda Facebook'tan gelen şey görünen
            // ad — eşleştirme de o adla yapılıyor (HandleValidator'da FB kuralı
            // olmamasıyla aynı gerekçe).
            result.Identity.Should().Be(new IntakeLinkedIdentity("Musa Sevinç", null, null));

            var tokenReq = handler.Requests[0];
            tokenReq.Uri.ToString().Should().NotContain(_appSecret, "sır URI'ye sızmamalı");
            tokenReq.Body.Should().Contain(_appSecret).And.Contain("code-1");

            handler.Requests[1].Uri.ToString().Should().Contain("/me").And.Contain("fields=id%2Cname");
            handler.Requests[1].AuthHeader.Should().Be($"Bearer {Tok}");
        }
    }

    [Fact]
    public async Task Takas_duserse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler();
        var (client, http) = NewClient(handler);
        using (http)
        {
            var result = await client.FetchNameAsync("code-1", CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.ErrorCode.Should().Be("saglayici");
        }
    }

    [Fact]
    public async Task Ad_bos_gelirse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("oauth/access_token")
                ? Json($$"""{"access_token":"{{Tok}}"}""")
                : Json("""{"id":"123"}""")
        };

        var (client, http) = NewClient(handler);
        using (http)
        {
            var result = await client.FetchNameAsync("code-1", CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.ErrorCode.Should().Be("saglayici");
        }
    }
}
