using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class GoogleChannelClientTests
{
    // Repo public: kimlik bilgisi ASLA sabit yazılmaz, üretilir.
    private readonly string _clientId = $"cid-{Guid.NewGuid():N}";
    private readonly string _clientSecret = $"cs-{Guid.NewGuid():N}";

    // Stub token da üretilir — sabit token dizesi tarayıcıyı tetikler.
    private static readonly string Tok = $"tok-{Guid.NewGuid():N}";

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

    private (GoogleChannelClient Client, HttpClient Http) NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler);
        var client = new GoogleChannelClient(
            http,
            Options.Create(new IntakeLoginOptions
            {
                GoogleClientId = _clientId,
                GoogleClientSecret = _clientSecret,
                RedirectUri = "https://orderdeckapp.com/musteri-kayit/baglanti-donusu"
            }),
            NullLogger<GoogleChannelClient>.Instance);
        return (client, http);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task Basarili_akista_kanal_kimligi_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json($$"""{"access_token":"{{Tok}}"}""")
                : Json("""
                    {"items":[{"id":"UCkanal000000000000000ab",
                      "snippet":{"title":"Kanalım","customUrl":"@kanalim"}}]}
                    """)
        };

        var (client, http) = NewClient(handler);
        using (http)
        {
            var result = await client.FetchChannelAsync("code-1", CancellationToken.None);

            result.Ok.Should().BeTrue();
            result.Identity.Should().Be(
                new IntakeLinkedIdentity("Kanalım", "@kanalim", "UCkanal000000000000000ab"));

            // SIR HİJYENİ: secret gövdede taşınmalı, URI'de asla — AddHttpClient'ın
            // varsayılan logger'ı giden URI'yi Information seviyesinde yazıyor.
            var tokenReq = handler.Requests[0];
            tokenReq.Uri.ToString().Should().NotContain(_clientSecret);
            tokenReq.Body.Should().Contain(_clientSecret).And.Contain("code-1")
                .And.Contain("grant_type=authorization_code");

            // Kanal çağrısı Bearer başlıkla gitmeli; token URI'ye sızmamalı.
            var chReq = handler.Requests[1];
            chReq.Uri.ToString().Should().Contain("mine=true").And.NotContain(Tok);
            chReq.AuthHeader.Should().Be($"Bearer {Tok}");
        }
    }

    [Fact]
    public async Task Hesapta_kanal_yoksa_kanalyok_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json($$"""{"access_token":"{{Tok}}"}""")
                : Json("""{"items":[]}""")
        };

        var (client, http) = NewClient(handler);
        using (http)
        {
            var result = await client.FetchChannelAsync("code-1", CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.ErrorCode.Should().Be("kanalyok");
        }
    }

    [Fact]
    public async Task Token_takasi_duserse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler(); // her şey 500
        var (client, http) = NewClient(handler);
        using (http)
        {
            var result = await client.FetchChannelAsync("code-1", CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.ErrorCode.Should().Be("saglayici");
            handler.Requests.Should().HaveCount(1, "takas düştüyse kanal çağrısı hiç yapılmamalı");
        }
    }

    [Fact]
    public async Task Kanal_cagrisi_duserse_saglayici_hatasi_doner()
    {
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json($$"""{"access_token":"{{Tok}}"}""")
                : new HttpResponseMessage(HttpStatusCode.Forbidden)
        };

        var (client, http) = NewClient(handler);
        using (http)
        {
            var result = await client.FetchChannelAsync("code-1", CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.ErrorCode.Should().Be("saglayici");
        }
    }
}
