using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Obifin sözleşmesi: header kimlik, form gövde, `Hata:[]` = başarı, sayısal alanlar
/// STRING gelir, tarih aralığı ≤ 31 gün, ham gövde kesmesi ayrıştırmayı etkilemez.</summary>
public sealed class ObifinClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public HttpRequestMessage? Last { get; private set; }
        public string? LastForm { get; private set; }
        public CapturingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            LastForm = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ObifinCredentials Creds() => new(
        "https://example.invalid", $"u-{Guid.NewGuid():N}@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}");

    private static (ObifinClient Client, CapturingHandler Handler) Build(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var h = new CapturingHandler(body, status);
        var c = new ObifinClient(new HttpClient(h), Options.Create(new ObifinOptions()),
            NullLogger<ObifinClient>.Instance);
        return (c, h);
    }

    [Fact]
    public async Task Kimlik_uc_header_ile_gider_govde_form_urlencoded()
    {
        var creds = Creds();
        var (client, h) = Build("""{"Hata":[],"KayitSayisi":0,"Liste":[]}""");

        await client.ListAccountsAsync(creds);

        h.Last!.Headers.GetValues("KullaniciAdi").Single().Should().Be(creds.UserCode);
        h.Last.Headers.GetValues("Sifre").Single().Should().Be(creds.Password);
        h.Last.Headers.GetValues("APIKey").Single().Should().Be(creds.ApiKey);
        h.Last.RequestUri!.ToString().Should().Be("https://example.invalid/webservis/hesaplar/hesaplistesi/");
        h.Last.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
    }

    [Fact]
    public async Task Hata_listesi_doluysa_istisna_mesajlari_tasir()
    {
        var (client, _) = Build("""{"Hata":["Kullanici Bilgileri Hatali!"]}""");

        var act = () => client.ListAccountsAsync(Creds());

        (await act.Should().ThrowAsync<ObifinApiException>()).Which.Messages
            .Should().ContainSingle().Which.Should().Be("Kullanici Bilgileri Hatali!");
    }

    [Fact]
    public async Task Json_degilse_protokol_istisnasi()
    {
        var (client, _) = Build("<html>502 Bad Gateway</html>");
        var act = () => client.ListAccountsAsync(Creds());
        await act.Should().ThrowAsync<ObifinProtocolException>();
    }

    [Fact]
    public async Task Http_basarisiz_ve_Hata_bos_ise_protokol_istisnasi()
    {
        // Vekil/WAF hatası (429/502/503) JSON gövdeyle de gelebilir; "Hata boş" tek başına başarı sayılmaz —
        // aksi hâlde boş liste döner ve çağıran "yeni hareket yok / hesaplar kayboldu" sanır.
        var (client, _) = Build("{}", HttpStatusCode.BadGateway);
        var act = () => client.ListAccountsAsync(Creds());
        (await act.Should().ThrowAsync<ObifinProtocolException>()).Which.Message.Should().Contain("502");
    }

    [Fact]
    public async Task Http_basarisiz_ama_Hata_doluysa_Obifin_mesajlari_one_gecer()
    {
        // Obifin'in kendi 4xx'i Hata[] taşıyorsa mesajlar kaybolmasın: Hata denetimi HTTP denetiminden ÖNCE.
        var (client, _) = Build("""{"Hata":["Kullanici Bilgileri Hatali!"]}""", HttpStatusCode.Unauthorized);
        var act = () => client.ListAccountsAsync(Creds());
        (await act.Should().ThrowAsync<ObifinApiException>()).Which.Messages
            .Should().ContainSingle().Which.Should().Be("Kullanici Bilgileri Hatali!");
    }

    [Fact]
    public async Task Hesap_listesi_string_sayilari_ve_tarihi_ayristirir()
    {
        var iban = BankHasherTests.TestIban(); // repo public — IBAN üretilir, yazılmaz
        var (client, _) = Build($$"""
        {"Hata":[],"KayitSayisi":1,"Liste":[{"Id":"9298","BankaKodu":"qnb","BankaApiId":"77","HesapNo":"123",
          "IBAN":"{{iban}}","ParaBirimi":"TL","Bakiye":"42736392.00","Durum":"1",
          "GuncellemeTarihi":"2022-10-17 10:32:08","BildirimNotu":""}]}
        """);

        var list = await client.ListAccountsAsync(Creds());

        var a = list.Should().ContainSingle().Subject;
        a.Id.Should().Be(9298); a.BankaApiId.Should().Be(77); a.Active.Should().BeTrue();
        a.Iban.Should().Be(iban);
        a.Balance.Should().Be(42736392.00m);
        a.UpdatedAtTr.Should().Be(new DateTime(2022, 10, 17, 10, 32, 8));
    }

    [Fact]
    public async Task Hareket_listesi_sayfa_meta_ve_isaretli_tutar()
    {
        var karsiIban = BankHasherTests.TestIban(); // repo public — IBAN üretilir, yazılmaz
        var (client, h) = Build($$"""
        {"SayfaBasinaKayitSayisi":1000,"ToplamKayitSayisi":2,"ToplamSayfaSayisi":1,"SayfaNo":1,"Hata":[],"Liste":[
          {"Id":"326404","HesapId":"6","IslemNo":"X1","IslemZamaniDT":"2022-10-10 12:07:30","Aciklama":"HAVALE test kodu",
           "IslemKodu":"FT37","OrtakIslemTipi":"EFT","Tutar":"10.00","TutarEksiArti":"10.00","ParaBirimi":"TL",
           "KarsiHesapIBAN":"{{karsiIban}}","GonderenAdi":null,"BorcluVKN":"","BankaKodu":"garanti"},
          {"Id":"326405","HesapId":"6","IslemNo":"X2","IslemZamaniDT":"2022-10-10 13:25:08","Aciklama":"giden",
           "IslemKodu":"WPSO","Tutar":"-10.00","TutarEksiArti":"-10.00","ParaBirimi":"TL","BankaKodu":"garanti"}]}
        """);

        var page = await client.ListTransactionsAsync(Creds(),
            new DateOnly(2022, 10, 1), new DateOnly(2022, 10, 17), sinceId: 326000, pageNo: 1, pageSize: 1000);

        h.LastForm.Should().Contain("BaslangicTarihi=2022-10-01").And.Contain("BitisTarihi=2022-10-17")
            .And.Contain("BaslangicHareketId=326000").And.Contain("SayfaBasinaKayitSayisi=1000").And.Contain("SayfaNo=1");
        page.TotalPages.Should().Be(1);
        page.Items.Should().HaveCount(2);
        page.Items[0].Id.Should().Be(326404);
        page.Items[0].SignedAmount.Should().Be(10.00m);
        page.Items[1].SignedAmount.Should().Be(-10.00m);
        page.Items[0].OccurredAtTr.Should().Be(new DateTime(2022, 10, 10, 12, 7, 30));
        page.Items[0].CounterpartyIban.Should().Be(karsiIban);
        page.Items[0].CounterpartyName.Should().BeNull("JSON null");
        page.Items[0].CounterpartyTaxId.Should().BeNull("boş string null sayılır");
        page.Items[0].RawJson.Should().Contain("\"Id\":\"326404\"");
    }

    [Fact]
    public async Task Virgullu_tutar_sessizce_yuz_katina_cikmaz_protokol_istisnasi()
    {
        // "10,50" InvariantCulture'da binlik ayracı sayılıp 1050 olurdu (100x). Bu alan SignedAmount'a, oradan
        // ödeme eşleştirmesine gider — finansal tutarda sessiz hata yerine gürültülü hata.
        var (client, _) = Build("""
        {"Hata":[],"Liste":[{"Id":"1","HesapId":"6","IslemZamaniDT":"2022-10-10 12:07:30",
          "TutarEksiArti":"10,50","Tutar":"10,50","ParaBirimi":"TL","BankaKodu":"garanti"}]}
        """);
        var act = () => client.ListTransactionsAsync(Creds(), new DateOnly(2022, 10, 1), new DateOnly(2022, 10, 17), null, 1, 1000);
        await act.Should().ThrowAsync<ObifinProtocolException>();
    }

    [Fact]
    public async Task Otuz_bir_gunden_uzun_aralik_istemcide_reddedilir()
    {
        var (client, h) = Build("""{"Hata":[],"Liste":[]}""");
        var act = () => client.ListTransactionsAsync(Creds(), new DateOnly(2022, 1, 1), new DateOnly(2022, 2, 5), null, 1, 1000);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        h.Last.Should().BeNull("sunucuya hiç gidilmedi");
    }

    [Fact]
    public async Task Banka_baglantisi_ekle_form_alanlarini_bankaya_gore_iletir()
    {
        var (client, h) = Build("""{"Hata":[]}""");
        var form = new Dictionary<string, string>
        {
            ["BankaApiAdi"] = "OrderDeck-test", ["KullaniciAdi"] = $"u-{Guid.NewGuid():N}",
            ["Sifre"] = $"pw-{Guid.NewGuid():N}", ["Url"] = "https://example.invalid/wsdl",
        };

        await client.AddBankConnectionAsync(Creds(), "qnb", form);

        h.Last!.RequestUri!.ToString().Should().EndWith("/webservis/bankaapi/ekle/qnb/");
        h.LastForm.Should().Contain("BankaApiAdi=OrderDeck-test").And.Contain("Url=");
    }
}
