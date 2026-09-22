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
    public async Task Istek_basliginin_UCU_de_hesap_baglamindan_gelir()
    {
        // Çok kiracılılığın kalbi: marka, kullanıcı ve şifrenin ÜÇÜ de aynı
        // bağlamdan gelmeli. Biri sabitlenirse B yayıncısı için dönülen tur
        // yanlış kimlikle sorar ve gelen cevap B'nin satırına yazılır —
        // hiçbir hata fırlatmadan veri bozulur.
        //
        // (Adı eskiden "global configten DEĞİL" idi; Task 9 NetgsmOptions'taki
        // global BrandCode'u sildiği için o sızıntının bekçiliğini artık
        // derleyici yapıyor, test değil.)
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

    /// <summary>
    /// 2026-09-22 prod olayı: 20'lik parti yanıtı 2000 karakteri aşınca gövde
    /// ayrıştırılmadan ÖNCE kesiliyordu; JSON yarım kaldığı için her alıcı
    /// Unknown sayıldı — ayna 614 numarada 0 ONAY yazdı, doğrulama işi de aynı
    /// partiyle hiçbir onayı "doğrulanmış" yapamazdı. Kesme yalnız tanı
    /// kopyasına (RawBody) uygulanır; durumlar tam gövdeden çıkar.
    /// </summary>
    [Fact]
    public async Task SearchAsync_2000_karakteri_asan_yanitta_tum_alicilari_ayristirir()
    {
        var recipients = Enumerable.Range(0, 20).Select(i => $"+9053{i:D8}").ToArray();
        var rows = string.Join(",", recipients.Select((r, i) =>
            $$"""{"consentDate":"2026-09-20 10:00:00","source":"HS_WEB","recipientType":"BIREYSEL","status":"{{(i % 2 == 0 ? "ONAY" : "RET")}}","type":"MESAJ","recipient":"{{r}}","transactionId":"6716aee7-{{i:D4}}"}"""));
        var body = $$"""{"code":"0","error":"false","query":[{{rows}}]}""";
        body.Length.Should().BeGreaterThan(2000, "test ancak eski kesme sınırını aşan yanıtla anlamlı");
        var (client, _) = Build(body);

        var result = await client.SearchAsync(Account("731734"), recipients);

        result.Code.Should().Be("0");
        result.Statuses.Should().HaveCount(20);
        result.Statuses[recipients[0]].Should().Be(IysConsentStatus.Onay);
        result.Statuses[recipients[1]].Should().Be(IysConsentStatus.Ret);
        result.Statuses[recipients[19]].Should().Be(IysConsentStatus.Ret);
        result.RawBody.Length.Should().BeLessThanOrEqualTo(2000, "tanı kopyası kesik kalır, ayrıştırma değil");
    }

    /// <summary>Sözleşme: ayrıştırılamayan yanıt ASLA "0 + boş liste" olarak dönmez —
    /// çağıran onu "cevap: ONAY değil" sanıp kapıyı sessizce kapatırdı.</summary>
    [Fact]
    public async Task SearchAsync_ayristirilamayan_yanit_code_0_donmez()
    {
        var (client, _) = Build("{\"code\":\"0\",\"error\":\"false\",\"query\":[");

        var result = await client.SearchAsync(Account("731734"), new[] { "+905551112233" });

        result.Code.Should().NotBe("0");
        result.Statuses.Should().BeEmpty();
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
