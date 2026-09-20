using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public class NetgsmTenantSmsSenderTests
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

    private static (NetgsmTenantSmsSender Sender, CapturingHandler Handler) Build(
        NetgsmOptions opt, HttpStatusCode status = HttpStatusCode.OK,
        string respBody = "{\"code\":\"00\"}")
    {
        var handler = new CapturingHandler(status, respBody);
        var http = new HttpClient(handler);
        var sender = new NetgsmTenantSmsSender(http, Options.Create(opt),
            NullLogger<NetgsmTenantSmsSender>.Instance);
        return (sender, handler);
    }

    private static NetgsmOptions Opt() => new()
    {
        BaseUrl = "https://api.netgsm.com.tr",
    };

    // Repo public — kimlikler ÜRETİLİR, asla literal yazılmaz (tarayıcı kuralı).
    private static TenantSmsCredentials NewCreds()
        => new($"u-{Guid.NewGuid():N}", $"p-{Guid.NewGuid():N}", "BASLIK");

    [Fact]
    public async Task SendAsync_basari_jobid_doner_ve_kimlikler_parametreden_gelir()
    {
        var creds = NewCreds();
        var (sender, handler) = Build(Opt(),
            respBody: "{\"code\":\"00\",\"jobid\":\"12345\"}");

        var jobId = await sender.SendAsync(creds, "+905551112233", "Kampanya!");

        jobId.Should().Be("12345");
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString()
            .Should().Be("https://api.netgsm.com.tr/sms/rest/v2/send");

        // Basic auth PARAMETREDEN gelen kimliklerle kurulmalı — NetgsmOptions
        // UserCode/Password boş; oradan gelseydi bu iddia düşerdi.
        var expectedAuth = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{creds.UserCode}:{creds.Password}"));
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Basic");
        handler.Request.Headers.Authorization.Parameter.Should().Be(expectedAuth);

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("msgheader").GetString().Should().Be(creds.Header);
        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("msg").GetString().Should().Be("Kampanya!");
    }

    [Fact]
    public async Task SendAsync_basari_jobid_yoksa_null_doner()
    {
        var (sender, _) = Build(Opt(), respBody: "{\"code\":\"00\"}");

        var jobId = await sender.SendAsync(NewCreds(), "+905551112233", "msg");

        jobId.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_iysfilter_daima_ticari()
    {
        // Bu yol tanımı gereği Commercial: SmsKind parametresi yok,
        // iysfilter koşulsuz CommercialIysFilter ("11") ile gider.
        var (sender, handler) = Build(Opt());

        await sender.SendAsync(NewCreds(), "+905551112233", "msg");

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("iysfilter").GetString().Should().Be("11");
    }

    [Fact]
    public async Task SendAsync_temiz_ret_json_NetgsmSmsException_kod_tasir()
    {
        var (sender, _) = Build(Opt(), status: HttpStatusCode.NotAcceptable,
            respBody: "{\"code\":\"30\",\"description\":\"gecersiz kimlik\"}");

        var act = async () => await sender.SendAsync(NewCreds(), "+905551112233", "msg");

        (await act.Should().ThrowAsync<NetgsmSmsException>())
            .Which.Code.Should().Be("30");
    }

    [Fact]
    public async Task SendAsync_temiz_ret_duz_metin_NetgsmSmsException_kod_tasir()
    {
        var (sender, _) = Build(Opt(), status: HttpStatusCode.NotAcceptable,
            respBody: "40");

        var act = async () => await sender.SendAsync(NewCreds(), "+905551112233", "msg");

        (await act.Should().ThrowAsync<NetgsmSmsException>())
            .Which.Code.Should().Be("40");
    }

    [Fact]
    public async Task SendAsync_2xx_ama_taninmayan_govde_temiz_ret_sayilir()
    {
        // Yanıt alındı ama kabul kodu yok → iş kabul edilmedi varsayılır.
        var (sender, _) = Build(Opt(), respBody: "garip");

        var act = async () => await sender.SendAsync(NewCreds(), "+905551112233", "msg");

        (await act.Should().ThrowAsync<NetgsmSmsException>())
            .Which.Code.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ag_hatasi_ham_cikar_NetgsmSmsExceptiona_sarilmaz()
    {
        // Sözleşme: NetgsmSmsException = "hiçbir şey gitmedi" garantisi.
        // Ağ hatasında mesaj Netgsm'e ulaşmış olabilir — sarmak yalan olurdu.
        var (sender, handler) = Build(Opt());
        handler.ThrowOnSend = new HttpRequestException("connection refused");

        var act = async () => await sender.SendAsync(NewCreds(), "+905551112233", "msg");

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.Should().NotBeOfType<NetgsmSmsException>();
    }

    [Fact]
    public async Task SendAsync_telefon_e164ten_10_haneye_donusur()
    {
        var (sender, handler) = Build(Opt());

        await sender.SendAsync(NewCreds(), "+905321234567", "msg");

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("messages")[0]
            .GetProperty("no").GetString().Should().Be("5321234567");
    }
}
