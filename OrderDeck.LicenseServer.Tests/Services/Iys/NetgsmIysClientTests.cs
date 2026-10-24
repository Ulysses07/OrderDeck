using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
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

        await client.AddAsync(account, new[] { Rec(TestPhone.NewE164()) });

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

        await client.SearchAsync(account, new[] { TestPhone.NewE164() });

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("header").GetProperty("brandCode")
            .GetString().Should().Be("763208");
    }

    [Fact]
    public async Task AddAsync_kimligi_govdede_yollar_ve_uca_gider()
    {
        var (client, handler) = Build("{\"code\":\"0\"}");
        var account = Account("731734");
        var telefon = TestPhone.NewE164();

        await client.AddAsync(account, new[] { Rec(telefon) });

        handler.Uri!.ToString().Should().Be("https://api.netgsm.com.tr/iys/add");
        using var doc = JsonDocument.Parse(handler.Body!);
        var header = doc.RootElement.GetProperty("header");
        header.GetProperty("brandCode").GetString().Should().Be("731734");
        header.GetProperty("username").GetString().Should().Be(account.UserCode);

        var row = doc.RootElement.GetProperty("body").GetProperty("data")[0];
        row.GetProperty("recipient").GetString().Should().Be(telefon);
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

        var result = await client.AddAsync(Account("731734"), new[] { Rec(TestPhone.NewE164()) });

        result.Queued.Should().BeTrue();
        result.Code.Should().Be("0");
        // IysAddResult'ta "Confirmed"/"Accepted" diye bir alan YOKTUR ve olmamalıdır.
        typeof(IysAddResult).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "Confirmed", "Accepted" });
    }

    [Fact]
    public async Task SearchAsync_query_dizisinden_alici_bazinda_durum_cikarir()
    {
        var onayli = TestPhone.NewE164();
        var retli = onayli[..^1] + (onayli[^1] == '0' ? '1' : '0');   // yalnız son hanesi farklı
        var (client, _) = Build($$"""
        {"code":"0","error":"false","query":[
          {"consentDate":"","source":"","recipientType":"BIREYSEL","status":"ONAY",
           "type":"MESAJ","recipient":"{{onayli}}","transactionId":"6716aee7"},
          {"consentDate":"","source":"","recipientType":"BIREYSEL","status":"RET",
           "type":"MESAJ","recipient":"{{retli}}","transactionId":"6716aee7"}]}
        """);

        var result = await client.SearchAsync(
            Account("731734"), new[] { onayli, retli });

        result.Statuses[onayli].Should().Be(IysConsentStatus.Onay);
        result.Statuses[retli].Should().Be(IysConsentStatus.Ret);
    }

    /// <summary>
    /// 2026-10-22 prod olayı: Netgsm TEK alıcılı sorguda <c>query</c>'yi diziye
    /// sarmıyor, NESNE döndürüyor. İstemci yalnız diziyi okuduğu için İYS'nin
    /// ONAY olarak tuttuğu kayıt Unknown sayıldı; doğrulama takvimi tükendi,
    /// kurtarma son tarih geçince Expired yazdı — 24 onay "Süresi Doldu"da
    /// kaldı. Gövde prod'daki SearchResult olayının alan setiyle birebir.
    /// </summary>
    [Theory]
    [InlineData("ONAY", IysConsentStatus.Onay)]
    [InlineData("RET", IysConsentStatus.Ret)]
    public async Task SearchAsync_tek_alicili_yanitta_query_nesnesinden_durum_okur(
        string iysDurumu, IysConsentStatus beklenen)
    {
        var telefon = TestPhone.NewE164();
        var (client, _) = Build($$"""
        {"code":"0","error":"false","query":{"consentDate":"","source":"","recipientType":"BIREYSEL",
         "status":"{{iysDurumu}}","type":"MESAJ","recipient":"{{telefon}}","retailerCode":"",
         "creationDate":"","retailerTitle":"","retailerAccessCount":"","transactionId":"{{Guid.NewGuid():N}}"}
        }
        """);

        var result = await client.SearchAsync(Account("731734"), new[] { telefon });

        result.Code.Should().Be("0");
        result.Statuses.Should().ContainSingle();
        result.Statuses[telefon].Should().Be(beklenen);
    }

    /// <summary>
    /// code "0" yanıtında istenen alıcı OKUNAMADI: BOŞ sonuç — kimse ONAY
    /// sayılmaz (fail-closed) — ve günlüğe uyarı düşer. Uyarı türe göre değil
    /// SONUCA göre: <c>query</c>'nin türü doğru olsa da içeriği okunamıyorsa
    /// (boş dizi, boş nesne, nesne olmayan eleman, metin olmayan recipient)
    /// sonuç aynı sessiz kayıptır. Tek alıcılı nesne biçimi de böyle sessizce
    /// boş dönüyordu; sessizlik 24 onayın kaybının yarısıydı. Okuyucu
    /// beklenmeyen JSON türünde fırlatmaz: <c>NetgsmAccountVerifier</c> yalnız
    /// HTTP/iptal/JSON hatalarını yakalıyor.
    ///
    /// <para><c>Code</c> "0" KALIR: Netgsm kimliği ve markayı kabul etmiş;
    /// biçimi okuyamamak kurulum hatası değil. "parse-error"a çevirmek
    /// <c>NetgsmAccountVerifier</c>'ı Unavailable'a, ayna işini yeniden
    /// denemeye sokardı. Doğrulama işi okunamayan alıcıyı zaten cevapsız sayar
    /// (takvimden düşmez).</para>
    /// </summary>
    [Theory]
    [InlineData(",\"query\":null")]
    [InlineData(",\"query\":\"beklenmeyen\"")]
    [InlineData(",\"query\":5")]
    [InlineData("")]   // query alanı hiç yok
    [InlineData(",\"query\":[]")]
    [InlineData(",\"query\":{}")]
    [InlineData(",\"query\":[null]")]
    [InlineData(",\"query\":[\"beklenmeyen\"]")]
    [InlineData(",\"query\":{\"recipient\":905,\"status\":\"ONAY\"}")]   // recipient metin değil
    [InlineData(",\"query\":[{\"status\":\"ONAY\"}]")]                  // recipient yok
    public async Task SearchAsync_istenen_alici_okunamazsa_bos_sonuc_ve_uyari(string queryParcasi)
    {
        var telefon = TestPhone.NewE164();
        var log = new LogRecorder<NetgsmIysClient>();
        var client = new NetgsmIysClient(
            new HttpClient(new CapturingHandler($$"""{"code":"0","error":"false"{{queryParcasi}}}""")),
            Options.Create(Opt()), log);

        var result = await client.SearchAsync(Account("731734"), new[] { telefon });

        result.Statuses.Should().BeEmpty("tanınmayan biçimde kimse ONAY sayılmaz");
        result.Code.Should().Be("0");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("query").And.NotContain(telefon[^8..],
                "günlüğe telefon yazılmaz (KVKK)");
    }

    [Fact]
    public async Task SearchAsync_dizi_ve_nesne_yaniti_uyari_uretmez()
    {
        var telefon = TestPhone.NewE164();
        var log = new LogRecorder<NetgsmIysClient>();
        var handler = new NetgsmIysSearchHandler { Statuses = { [telefon] = "ONAY" } };
        var client = new NetgsmIysClient(new HttpClient(handler), Options.Create(Opt()), log);

        await client.SearchAsync(Account("731734"), new[] { telefon });                       // nesne
        await client.SearchAsync(Account("731734"), new[] { telefon, TestPhone.NewE164() });  // dizi

        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// Biçim kayması: Netgsm alıcıyı başka biçimde yansıtırsa (ör. '+' olmadan)
    /// satır okunur ama İSTENEN anahtar sözlükte yoktur — tür doğru, sonuç yine
    /// sessiz kayıp. Testlerin sahte işleyicisi alıcıyı birebir yansıttığı için
    /// bu yol başka hiçbir testte görünmez.
    /// </summary>
    [Fact]
    public async Task SearchAsync_alici_baska_bicimde_yansirsa_uyari_verir()
    {
        var telefon = TestPhone.NewE164();
        var log = new LogRecorder<NetgsmIysClient>();
        var client = new NetgsmIysClient(new HttpClient(new CapturingHandler($$"""
            {"code":"0","error":"false","query":{"status":"ONAY","type":"MESAJ","recipient":"{{telefon[1..]}}"} }
            """)), Options.Create(Opt()), log);

        var result = await client.SearchAsync(Account("731734"), new[] { telefon });

        result.Statuses.Should().NotContainKey(telefon, "istenen anahtar okunmadı — ONAY sayılmaz");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().NotContain(telefon[^8..], "günlüğe telefon yazılmaz (KVKK)");
    }

    /// <summary>Kısmi okuma da uyarır ve yalnız SAYILARI yazar: hangi alıcının
    /// okunamadığı değil, kaçının.</summary>
    [Fact]
    public async Task SearchAsync_partinin_bir_kismi_okunamazsa_sayilarla_uyarir()
    {
        // Üretilen ortak gövde, farklı son hane: iki numara kesin farklı.
        var govde = TestPhone.NewNational()[..9];
        var (okunan, okunamayan) = ($"+90{govde}1", $"+90{govde}2");
        var log = new LogRecorder<NetgsmIysClient>();
        var client = new NetgsmIysClient(new HttpClient(new CapturingHandler($$"""
            {"code":"0","error":"false","query":[{"status":"ONAY","type":"MESAJ","recipient":"{{okunan}}"}]}
            """)), Options.Create(Opt()), log);

        var result = await client.SearchAsync(Account("731734"), new[] { okunan, okunamayan });

        result.Statuses[okunan].Should().Be(IysConsentStatus.Onay);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("1/2")
            .And.NotContain(okunamayan[^8..]).And.NotContain(okunan[^8..]);
    }

    /// <summary>
    /// <c>status</c> metin değilse (ör. sayı) okuyucu fırlatmaz: alıcı Unknown
    /// olur. ONAY/RET okunamadığı için bu da "okunamadı" sayılır ve uyarılır —
    /// doğrulama işi Unknown'ı da cevapsız sayar.
    /// </summary>
    [Fact]
    public async Task SearchAsync_status_metin_degilse_Unknown_istisna_yok_uyari_var()
    {
        var telefon = TestPhone.NewE164();
        var log = new LogRecorder<NetgsmIysClient>();
        var client = new NetgsmIysClient(new HttpClient(new CapturingHandler($$"""
            {"code":"0","query":{"recipient":"{{telefon}}","status":1} }
            """)), Options.Create(Opt()), log);

        var result = await client.SearchAsync(Account("731734"), new[] { telefon });

        result.Code.Should().Be("0");
        result.Statuses[telefon].Should().Be(IysConsentStatus.Unknown);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SearchAsync_yanitta_olmayan_alici_Unknown_kalir()
    {
        var (client, _) = Build("{\"code\":\"0\",\"query\":[]}");
        var telefon = TestPhone.NewE164();

        var result = await client.SearchAsync(Account("731734"), new[] { telefon });

        result.Statuses.Should().NotContainKey(telefon);
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
        // Üretilen ortak önek + sıra no: 20 alıcı kesin farklı.
        var onek = TestPhone.NewNational()[..8];
        var recipients = Enumerable.Range(0, 20).Select(i => $"+90{onek}{i:D2}").ToArray();
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

        var result = await client.SearchAsync(Account("731734"), new[] { TestPhone.NewE164() });

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
            Account("731734"), new[] { Rec(TestPhone.NewE164()) });

        await act.Should().ThrowAsync<IysConfigurationException>();
    }

    /// <summary>
    /// Düz metin kod (<c>30</c>, <c>"60"</c>) geçerli bir JSON DEĞERİDİR ama nesne
    /// değildir: kök üzerinde alan aramak <c>InvalidOperationException</c>
    /// fırlatıyordu — ne yapılandırma hatası olarak durur, ne de
    /// <c>NetgsmAccountVerifier</c>'ın yakaladığı türdendi. Kod okuyucusu da
    /// satır okuyucusu gibi beklenmeyen JSON türünde fırlatmamalı.
    /// </summary>
    [Theory]
    [InlineData("30")]
    [InlineData("\"60\"")]
    public async Task Duz_metin_yapilandirma_kodu_da_boru_hattini_durdurur(string body)
    {
        var (client, _) = Build(body);

        var act = async () => await client.SearchAsync(Account("731734"), new[] { TestPhone.NewE164() });

        await act.Should().ThrowAsync<IysConfigurationException>();
    }
}
