using System.Globalization;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Obifin banka türleri kataloğu. Kodlar ve zorunlu alanlar Obifin'in kendi <c>bankaapi/bankakodlari/</c>
/// yanıtından (test API'si, 2026-09-28) ölçüldü; isteğe bağlı alanlar Obifin Postman koleksiyonu v1.03.06'dan.
/// <c>BankaApiAdi</c> burada yok: onu bağlantı servisi üretir. Obifin bir bankanın alanlarını değiştirirse bu tablo ve
/// katalog birlikte güncellenir — eski eşleme 35 türün 9'unu biliyordu, ikisi yanlıştı (yapikredi, ziraat).</summary>
public sealed class ObifinBankCatalogTests
{
    /// <summary>Kod → Obifin'in <c>ZorunluAlanlar</c>'ı, Obifin'in sırasıyla.</summary>
    private static readonly Dictionary<string, string[]> Zorunlu = new()
    {
        ["akbank"] = ["FirmaAnahtar", "KullaniciAdi", "Sifre"],
        ["aktifbank"] = ["KullaniciAdi", "Sifre"],
        ["alternatifbank"] = ["KurumKodu", "Sifre"],
        ["albarakaturk"] = ["KullaniciAdi", "Sifre"],
        ["anadolubank"] = ["MusteriNo", "Key"],
        ["burganbank"] = ["KullaniciAdi", "Sifre"],
        ["denizbank"] = ["FirmaAnahtar", "KullaniciAdi"],
        ["dunyakatilim"] = ["StatementCompanyId", "Username", "Password"],
        ["emlakbank"] = ["KullaniciAdi", "Sifre", "MusteriNo"],
        ["enpara"] = ["KullaniciAdi", "Sifre", "Url"],
        ["enparaapi"] = ["ClientId", "ClientSecret", "AccessToken", "RefreshToken"],
        ["fibabanka"] = ["FirmaKodu", "Sifre"],
        ["garanti"] = ["KullaniciAdi", "Sifre", "FirmaKodu"],
        ["garantibbvaapi"] = ["TanimNumarasi"],
        ["halkbank"] = ["KullaniciAdi", "Sifre"],
        ["hsbc"] = ["associationCode", "Username", "Password"],
        ["icbc"] = ["MusteriNo", "Sifre"],
        ["ingbank"] = ["MusteriNo", "KullaniciAdi", "Sifre"],
        ["isbank"] = ["KullaniciAdi", "Sifre"],
        ["kuveytturk"] = ["KullaniciAdi", "Sifre"],
        ["kuveytturkapi"] = [],
        ["odeabank"] = ["KullaniciKodu", "Sifre", "MusteriNo"],
        ["papara"] = ["APIKey", "APISecret"],
        ["turkland"] = ["KullaniciAdi", "Sifre"],
        ["turkticaret"] = ["CorporationCode", "APIKey", "Password", "ClientId", "ClientSecret"],
        ["qnb"] = ["KullaniciAdi", "Sifre", "Url"],
        ["qnbapi"] = ["ClientId", "ClientSecret", "AccessToken", "RefreshToken"],
        ["sekerbank"] = ["KullaniciAdi", "Sifre"],
        ["teb"] = ["FirmaAdi", "FirmaAnahtar"],
        ["turkiyefinans"] = ["KullaniciAdi", "Sifre"],
        ["vakifbank"] = ["KurumKullanici", "Sifre", "MusteriNo"],
        ["vakifkatilim"] = ["KullaniciAdi", "Sifre", "MusteriNo"],
        ["yapikredi"] = ["FirmaKodu", "KullaniciAdi", "Sifre"],
        ["ziraat"] = ["KurumKodu", "Sifre", "MusteriNo"],
        ["ziraatkatilim"] = ["FirmaKodu", "KullaniciAdi", "Sifre"],
    };

    /// <summary>VKN dışındaki isteğe bağlı alanlar (Postman v1.03.06); VKN her bankada, sonda.</summary>
    private static readonly Dictionary<string, string[]> EkIstegeBagli = new()
    {
        ["teb"] = ["KullaniciAdi", "Sifre", "ServiceID"],
        ["anadolubank"] = ["KullaniciAdi", "Sifre", "ClientId", "ClientSecret"],
    };

    /// <summary>Parola kutusuna düşmesi gereken alan anahtarları.</summary>
    private static readonly HashSet<string> MaskeliAnahtarlar =
        ["Sifre", "Password", "ClientSecret", "AccessToken", "RefreshToken", "APIKey", "APISecret", "FirmaAnahtar", "Key"];

    public static TheoryData<string> Kodlar => new(Zorunlu.Keys);

    private static ObifinBankType Tur(string kod)
        => ObifinBankCatalog.All.Single(t => t.Code == kod);

    [Fact]
    public void Katalog_Obifinin_35_banka_turunun_tamamini_bir_kez_icerir()
    {
        ObifinBankCatalog.All.Should().HaveCount(35);
        ObifinBankCatalog.All.Select(t => t.Code).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(Zorunlu.Keys, "Obifin'in desteklediği her banka türü formda seçilebilmeli");
    }

    [Theory]
    [MemberData(nameof(Kodlar))]
    public void Zorunlu_alanlar_Obifinin_ZorunluAlanlari_ile_birebir_ayni_ve_once_gelir(string kod)
    {
        var fields = Tur(kod).Fields;

        fields.Where(f => f.Required).Select(f => f.Key).Should().Equal(Zorunlu[kod], $"{kod}: Obifin bunları ister");
        fields.Take(Zorunlu[kod].Length).Should().NotContain(f => !f.Required, "zorunlular önce"); // kuveytturkapi: hiç yok
        fields.Select(f => f.Key).Should().OnlyHaveUniqueItems().And.NotContain("BankaApiAdi", "etiketi servis üretir");
    }

    [Theory]
    [MemberData(nameof(Kodlar))]
    public void Istege_bagli_alanlar_VKN_her_bankada_sonda_teb_ve_anadolubank_ekleri_ipucuyla(string kod)
    {
        var optional = Tur(kod).Fields.Where(f => !f.Required).ToList();
        var extras = EkIstegeBagli.GetValueOrDefault(kod, []);

        optional.Select(f => f.Key).Should().Equal(extras.Append("VKN"), kod);
        Tur(kod).Fields[^1].Key.Should().Be("VKN", "VKN en sonda");
        var vkn = optional[^1];
        vkn.Label.Should().Be("Şirket VKN (isteğe bağlı)");
        vkn.Secret.Should().BeFalse();
        vkn.Hint.Should().NotBeNullOrWhiteSpace();
        // Obifin'in açıklaması: boş bırakılırsa varsayılan işlenir — admin'e söylenmeli, etiket de isteğe bağlı demeli.
        // (OnlyContain boş koleksiyonda düşer; VKN'den başka isteğe bağlısı olmayan bankalar için NotContain.)
        optional.SkipLast(1).Should().NotContain(f => !f.Label.EndsWith(" (isteğe bağlı)") || string.IsNullOrWhiteSpace(f.Hint));
    }

    [Fact]
    public void Gizli_alanlar_ve_yalniz_onlar_parola_kutusuna_duser()
    {
        var all = ObifinBankCatalog.All.SelectMany(t => t.Fields.Select(f => (t.Code, f))).ToList();

        all.Should().OnlyContain(x => x.f.Secret == MaskeliAnahtarlar.Contains(x.f.Key));
        all.Select(x => x.f.Key).Should().Contain(MaskeliAnahtarlar, "her gizli anahtar en az bir bankada geçer");
    }

    [Fact]
    public void Yontem_api_kodlarinda_ve_API_anahtariyla_baglanan_Papara_Turk_Ticarette_API_digerlerinde_Web_servis()
    {
        // Papara (APIKey/APISecret) ve Türk Ticaret (client kimliği + API anahtarı) API anahtarıyla bağlanır: yardım satırı
        // "web servisi başvurusu" demesin. hsbc, dunyakatilim, anadolubank İngilizce alan adlarına rağmen web servis.
        ObifinBankCatalog.All.Where(t => t.Method == ObifinBankCatalog.Api).Select(t => t.Code)
            .Should().BeEquivalentTo("enparaapi", "garantibbvaapi", "kuveytturkapi", "qnbapi", "papara", "turkticaret");
        Tur("papara").Method.Should().Be(ObifinBankCatalog.Api);
        Tur("turkticaret").Method.Should().Be(ObifinBankCatalog.Api);
        foreach (var kod in new[] { "hsbc", "dunyakatilim", "anadolubank" })
            Tur(kod).Method.Should().Be(ObifinBankCatalog.WebService, kod);
        ObifinBankCatalog.All.Where(t => t.Method != ObifinBankCatalog.Api)
            .Should().OnlyContain(t => t.Method == ObifinBankCatalog.WebService).And.HaveCount(29);
        Tur("papara").DisplayName.Should().Be("Papara", "tek türü olan bankada \"— API\" eki yok");
        Tur("turkticaret").DisplayName.Should().Be("Türk Ticaret Bankası");
        ObifinBankCatalog.Api.Should().Be("API");
        ObifinBankCatalog.WebService.Should().Be("Web servis");
    }

    [Fact]
    public void Gorunen_adlar_tekil_anlasilir_ve_Turkce_alfabeye_gore_sirali()
    {
        var names = ObifinBankCatalog.All.Select(t => t.DisplayName).ToList();

        names.Should().OnlyHaveUniqueItems();
        names.Should().BeInAscendingOrder(StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: false));
        // Sıra sözcüğe göre değil kod noktasına göre olsaydı İ ve Ş en sona düşerdi.
        names.IndexOf("İş Bankası").Should().BeGreaterThan(names.IndexOf("ING Bank")).And.BeLessThan(names.IndexOf("Kuveyt Türk — API"));
        names.IndexOf("Şekerbank").Should().BeGreaterThan(names.IndexOf("QNB — web servis"))
            .And.BeLessThan(names.IndexOf("TEB (Türk Ekonomi Bankası)"));
        foreach (var (kod, ad) in new[]
        {
            ("qnb", "QNB — web servis"), ("qnbapi", "QNB — API"), ("garanti", "Garanti BBVA — web servis"),
            ("garantibbvaapi", "Garanti BBVA — API"), ("enpara", "Enpara — web servis"), ("enparaapi", "Enpara — API"),
            ("kuveytturk", "Kuveyt Türk — web servis"), ("kuveytturkapi", "Kuveyt Türk — API"), ("akbank", "Akbank"),
            ("isbank", "İş Bankası"), ("yapikredi", "Yapı Kredi"), ("ziraat", "Ziraat Bankası"), ("vakifbank", "VakıfBank"),
            ("teb", "TEB (Türk Ekonomi Bankası)"), ("turkiyefinans", "Türkiye Finans Katılım"), ("aktifbank", "Aktif Yatırım Bankası"),
        })
            Tur(kod).DisplayName.Should().Be(ad);
    }

    [Theory]
    [InlineData("isbank", "KullaniciAdi", "Kullanıcı adı")]
    [InlineData("qnb", "Url", "Servis adresi (WSDL)")]
    [InlineData("garanti", "FirmaKodu", "Firma kodu")]
    [InlineData("vakifbank", "KurumKullanici", "Kurum kullanıcısı")]
    [InlineData("odeabank", "KullaniciKodu", "Kullanıcı kodu")]
    [InlineData("anadolubank", "Key", "Anahtar (Key)")]
    [InlineData("qnbapi", "AccessToken", "Erişim token'ı")]
    [InlineData("garantibbvaapi", "TanimNumarasi", "Tanım numarası")]
    [InlineData("papara", "APISecret", "API gizli anahtarı")]
    [InlineData("dunyakatilim", "StatementCompanyId", "Ekstre firma ID")]
    [InlineData("turkticaret", "CorporationCode", "Kurum kodu")]
    [InlineData("hsbc", "associationCode", "Kurum kodu (associationCode)")]
    [InlineData("teb", "FirmaAdi", "Firma adı")]
    [InlineData("teb", "ServiceID", "Servis ID (isteğe bağlı)")]
    [InlineData("denizbank", "FirmaAnahtar", "Firma anahtarı (AppKey)")]
    [InlineData("denizbank", "KullaniciAdi", "Uygulama kodu")]
    [InlineData("turkland", "KullaniciAdi", "Kullanıcı adı (p1)")]
    [InlineData("turkland", "Sifre", "Şifre (seckod)")]
    [InlineData("ziraat", "KurumKodu", "Kurum kodu")]
    [InlineData("ziraat", "MusteriNo", "Müşteri numarası")]
    public void Alan_etiketleri_Turkce_bankaya_ozgu_adlar_uygulanir(string kod, string anahtar, string etiket)
        => Tur(kod).Fields.Single(f => f.Key == anahtar).Label.Should().Be(etiket);

    [Fact]
    public void Ipuclari_Obifin_aciklamalarini_tasir_etiketler_banka_icinde_tekil()
    {
        Tur("ziraat").Fields.Single(f => f.Key == "KurumKodu").Hint.Should().Be("Ziraat'in paylaştığı kurum kodu");
        Tur("ziraat").Fields.Single(f => f.Key == "MusteriNo").Hint.Should().Be("Ziraat'teki müşteri numarası");
        foreach (var kod in new[] { "qnb", "enpara" })
            Tur(kod).Fields.Single(f => f.Key == "Url").Hint.Should().Contain("WSDL", kod);
        // "Eksik alan: …" bildirimi etiketle konuşur: aynı bankada iki alan aynı adı taşırsa hangisinin eksik olduğu belirsiz.
        ObifinBankCatalog.All.Should().OnlyContain(t => t.Fields.Select(f => f.Label).Distinct().Count() == t.Fields.Count
            && t.Fields.All(f => !string.IsNullOrWhiteSpace(f.Label)));
    }

    [Theory]
    [InlineData("qnb", "qnb")]
    [InlineData("QNB", "qnb")]
    [InlineData("QnbApi", "qnbapi")]
    [InlineData("ZIRAAT", "ziraat")]
    [InlineData(" isbank ", "isbank")]
    public void TryGet_harf_duyarsiz_kanonik_kodu_doner(string girilen, string kanonik)
    {
        ObifinBankCatalog.TryGet(girilen, out var type).Should().BeTrue();
        type!.Code.Should().Be(kanonik);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yok")]
    [InlineData("BankaApiAdi")]
    public void TryGet_bilinmeyen_kodda_false(string? girilen)
        => ObifinBankCatalog.TryGet(girilen, out _).Should().BeFalse();
}
