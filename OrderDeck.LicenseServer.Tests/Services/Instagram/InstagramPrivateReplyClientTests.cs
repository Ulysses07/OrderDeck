using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.Instagram;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Instagram;

public sealed class InstagramPrivateReplyClientTests
{
    private static readonly string Tok = $"pagetok-{Guid.NewGuid():N}";

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

    private static (InstagramPrivateReplyClient Client, StubHandler Handler, HttpClient Http) NewClient()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"recipient_id":"1","message_id":"m1"}""",
                    Encoding.UTF8, "application/json")
            }
        };
        var http = new HttpClient(handler);
        var client = new InstagramPrivateReplyClient(
            http, Options.Create(new FacebookOptions()),
            NullLogger<InstagramPrivateReplyClient>.Instance);
        return (client, handler, http);
    }

    [Fact]
    public async Task Basarili_gonderimde_true_doner_ve_yorum_kimligine_gider()
    {
        var (client, handler, http) = NewClient();
        using (http)
        {
            var ok = await client.SendAsync(
                pageId: "page-1", commentId: "cmt-42", text: "Kayıt için: https://x",
                pageToken: Tok, CancellationToken.None);

            ok.Should().BeTrue();
            var req = handler.Requests.Single();
            req.Uri.AbsolutePath.Should().EndWith("/page-1/messages");
            req.Uri.ToString().Should().NotContain(Tok, "token URI'ye sızmamalı");
            req.AuthHeader.Should().Be($"Bearer {Tok}");
            req.Body.Should().Contain("cmt-42").And.Contain("comment_id");
        }
    }

    [Fact]
    public async Task Graph_hatasinda_false_doner_firlatmaz()
    {
        var (client, handler, http) = NewClient();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"window closed","code":10}}""",
                Encoding.UTF8, "application/json")
        };
        using (http)
        {
            var ok = await client.SendAsync("p", "c", "t", Tok, CancellationToken.None);
            ok.Should().BeFalse();
        }
    }
}
