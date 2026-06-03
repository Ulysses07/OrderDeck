using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public class NetgsmSmsSenderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _respBody;
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public Exception? ThrowOnSend { get; set; }

        public CapturingHandler(HttpStatusCode status, string respBody)
        {
            _status = status;
            _respBody = respBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (ThrowOnSend is not null) throw ThrowOnSend;
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_respBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (NetgsmSmsSender Sender, CapturingHandler Handler) Build(
        NetgsmOptions opt, HttpStatusCode status = HttpStatusCode.OK, string respBody = "{\"code\":\"00\"}")
    {
        var handler = new CapturingHandler(status, respBody);
        var http = new HttpClient(handler);
        var sender = new NetgsmSmsSender(http, Options.Create(opt),
            NullLogger<NetgsmSmsSender>.Instance);
        return (sender, handler);
    }

    private static NetgsmOptions Opt() => new()
    {
        UserCode = "user1",
        Password = "pass1",
        Header = "ODHEADER",
        BaseUrl = "https://api.netgsm.com.tr",
    };

    [Fact]
    public async Task SendAsync_posts_to_v2_endpoint_with_basic_auth_and_payload()
    {
        var (sender, handler) = Build(Opt());

        await sender.SendAsync("+905551112233", "Kodunuz 123456");

        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString()
            .Should().Be("https://api.netgsm.com.tr/sms/rest/v2/send");

        var expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("user1:pass1"));
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Basic");
        handler.Request.Headers.Authorization.Parameter.Should().Be(expectedAuth);

        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        root.GetProperty("msgheader").GetString().Should().Be("ODHEADER");
        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("msg").GetString().Should().Be("Kodunuz 123456");
        // +90 strip → 10 hane
        messages[0].GetProperty("no").GetString().Should().Be("5551112233");
    }

    [Fact]
    public async Task SendAsync_omits_iysfilter_and_encoding_when_blank()
    {
        var (sender, handler) = Build(Opt());

        await sender.SendAsync("+905551112233", "msg");

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.TryGetProperty("iysfilter", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("encoding", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_includes_iysfilter_and_encoding_when_configured()
    {
        var opt = Opt();
        opt.IysFilter = "0";
        opt.Encoding = "TR";
        var (sender, handler) = Build(opt);

        await sender.SendAsync("+905551112233", "msg");

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("iysfilter").GetString().Should().Be("0");
        doc.RootElement.GetProperty("encoding").GetString().Should().Be("TR");
    }

    [Fact]
    public async Task SendAsync_success_code_00_does_not_throw()
    {
        var (sender, _) = Build(Opt(), respBody: "{\"code\":\"00\",\"bulkid\":\"123\"}");
        var act = async () => await sender.SendAsync("+905551112233", "msg");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendAsync_rejected_code_throws()
    {
        var (sender, _) = Build(Opt(), respBody: "{\"code\":\"30\"}");
        var act = async () => await sender.SendAsync("+905551112233", "msg");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendAsync_non_success_status_throws()
    {
        var (sender, _) = Build(Opt(), status: HttpStatusCode.InternalServerError, respBody: "{}");
        var act = async () => await sender.SendAsync("+905551112233", "msg");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendAsync_network_error_propagates()
    {
        var (sender, handler) = Build(Opt());
        handler.ThrowOnSend = new HttpRequestException("connection refused");
        var act = async () => await sender.SendAsync("+905551112233", "msg");
        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
