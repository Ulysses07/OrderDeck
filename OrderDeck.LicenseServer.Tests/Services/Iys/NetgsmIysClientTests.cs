using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class NetgsmIysClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _respBody;
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }

        public CapturingHandler(string respBody) => _respBody = respBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static NetgsmOptions Opt() => new()
    {
        UserCode = $"user-{Guid.NewGuid():N}",
        Password = $"pw-{Guid.NewGuid():N}",
        BaseUrl = "https://api.netgsm.com.tr",
        // Marka burada YOK: Task 9 NetgsmOptions.BrandCode'u sildi. Global marka
        // diye bir şey kalmadığı için "isteğe sızma" ihtimali de artık derleme
        // düzeyinde imkânsız.
    };

    private static IysAccountContext Account(string brandCode) => new(
        Guid.NewGuid(), $"user-{Guid.NewGuid():N}", $"pw-{Guid.NewGuid():N}", brandCode);

    private static (NetgsmIysClient Client, CapturingHandler Handler) Build(string respBody)
    {
        var handler = new CapturingHandler(respBody);
        var client = new NetgsmIysClient(new HttpClient(handler), Options.Create(Opt()),
            NullLogger<NetgsmIysClient>.Instance);
        return (client, handler);
    }

    private static IysConsentRecord Rec(string phone) => new(
        phone, "BIREYSEL", "MESAJ", IysConsentStatus.Onay,
        new DateTimeOffset(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(3)),
        "HS_WEB", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Istek_markasi_parametreden_gelir_global_configten_DEGIL()
    {
        // Çok kiracılılığın kalbi: istemci global kimliğe BAKMAZ. Bakarsa,
        // B yayıncısı için dönülen tur merkezî markaya sorar ve gelen cevap
        // B'nin satırına yazılır — hiçbir hata fırlatmadan veri bozulur.
        var (client, handler) = Build("{\"code\":\"0\"}");
        var account = Account("763208");   // markanın TEK kaynağı bu bağlam

        await client.AddAsync(account, new[] { Rec("+905551112233") });

        using var doc = JsonDocument.Parse(handler.Body!);
        var header = doc.RootElement.GetProperty("header");
        header.GetProperty("brandCode").GetString().Should().Be("763208");
        header.GetProperty("username").GetString().Should().Be(account.UserCode);
        header.GetProperty("password").GetString().Should().Be(account.Password);
    }

    [Fact]
    public async Task SearchAsync_de_hesap_baglamini_kullanir()
    {
        var (client, handler) = Build("{\"code\":\"0\",\"query\":[]}");
        var account = Account("763208");

        await client.SearchAsync(account, new[] { "+905551112233" });

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("header").GetProperty("brandCode")
            .GetString().Should().Be("763208");
    }

    [Fact]
    public async Task AddAsync_kimligi_govdede_yollar_ve_uca_gider()
    {
        var (client, handler) = Build("{\"code\":\"0\"}");
        var account = Account("731734");

        await client.AddAsync(account, new[] { Rec("+905551112233") });

        handler.Uri!.ToString().Should().Be("https://api.netgsm.com.tr/iys/add");
        using var doc = JsonDocument.Parse(handler.Body!);
        var header = doc.RootElement.GetProperty("header");
        header.GetProperty("brandCode").GetString().Should().Be("731734");
        header.GetProperty("username").GetString().Should().Be(account.UserCode);

        var row = doc.RootElement.GetProperty("body").GetProperty("data")[0];
        row.GetProperty("recipient").GetString().Should().Be("+905551112233");
        row.GetProperty("status").GetString().Should().Be("ONAY");
        row.GetProperty("type").GetString().Should().Be("MESAJ");
        row.GetProperty("source").GetString().Should().Be("HS_WEB");
        // TR yerel saat, saniye hassasiyetinde
        row.GetProperty("consentDate").GetString().Should().Be("2026-09-18 10:30:00");
    }

    [Fact]
    public async Task AddAsync_code_sifir_yalnizca_kuyruga_alindi_demektir()
    {
        // BU TEST 2026-09-17'de 284 kaydı kaybettiren hatayı koda gömer:
        // "code 0" KABUL DEĞİL, yalnız kuyruğa alındı. Confirmed kararı
        // burada verilemez — yalnız /iys/search verebilir.
        var (client, _) = Build("{\"code\":\"0\",\"error\":\"false\"}");

        var result = await client.AddAsync(Account("731734"), new[] { Rec("+905551112233") });

        result.Queued.Should().BeTrue();
        result.Code.Should().Be("0");
        // IysAddResult'ta "Confirmed"/"Accepted" diye bir alan YOKTUR ve olmamalıdır.
        typeof(IysAddResult).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "Confirmed", "Accepted" });
    }

    [Fact]
    public async Task SearchAsync_query_dizisinden_alici_bazinda_durum_cikarir()
    {
        var (client, _) = Build("""
        {"code":"0","error":"false","query":[
          {"consentDate":"","source":"","recipientType":"BIREYSEL","status":"ONAY",
           "type":"MESAJ","recipient":"+905310826728","transactionId":"6716aee7"},
          {"consentDate":"","source":"","recipientType":"BIREYSEL","status":"RET",
           "type":"MESAJ","recipient":"+905000000000","transactionId":"6716aee7"}]}
        """);

        var result = await client.SearchAsync(
            Account("731734"), new[] { "+905310826728", "+905000000000" });

        result.Statuses["+905310826728"].Should().Be(IysConsentStatus.Onay);
        result.Statuses["+905000000000"].Should().Be(IysConsentStatus.Ret);
    }

    [Fact]
    public async Task SearchAsync_yanitta_olmayan_alici_Unknown_kalir()
    {
        var (client, _) = Build("{\"code\":\"0\",\"query\":[]}");

        var result = await client.SearchAsync(Account("731734"), new[] { "+905551112233" });

        result.Statuses.Should().NotContainKey("+905551112233");
    }

    [Theory]
    [InlineData("60")]  // marka kodu hatalı
    [InlineData("30")]  // kimlik hatalı
    public async Task Yapilandirma_hatasi_boru_hattini_durdurur(string code)
    {
        var (client, _) = Build($"{{\"code\":\"{code}\"}}");

        var act = async () => await client.AddAsync(
            Account("731734"), new[] { Rec("+905551112233") });

        await act.Should().ThrowAsync<IysConfigurationException>();
    }
}
