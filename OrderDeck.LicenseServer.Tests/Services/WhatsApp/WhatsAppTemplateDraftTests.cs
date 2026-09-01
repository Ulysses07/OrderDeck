using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public class WhatsAppTemplateDraftTests
{
    private static WhatsAppTemplateDraft Draft(
        string body = "Merhaba!",
        string? header = null,
        string? footer = null,
        IReadOnlyList<string>? examples = null,
        IReadOnlyList<WhatsAppTemplateButton>? buttons = null) =>
        new(header, body, footer, examples ?? [], buttons ?? []);

    [Theory]
    [InlineData("siparis_hatirlatma")]
    [InlineData("kargo2")]
    public void Gecerli_ad_kabul_edilir(string name) =>
        Assert.Null(WhatsAppTemplateShape.ValidateName(name));

    [Theory]
    [InlineData("")]
    [InlineData("Sipariş")]      // büyük harf + Türkçe karakter
    [InlineData("kargo-bildirim")] // tire
    [InlineData("kargo bildirim")] // boşluk
    public void Gecersiz_ad_reddedilir(string name) =>
        Assert.NotNull(WhatsAppTemplateShape.ValidateName(name));

    [Fact]
    public void Cok_uzun_ad_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.ValidateName(new string('a', 513)));

    [Theory]
    [InlineData("MARKETING")]
    [InlineData("UTILITY")]
    public void Gecerli_kategori_kabul_edilir(string c) =>
        Assert.Null(WhatsAppTemplateShape.ValidateCategory(c));

    // AUTHENTICATION şablonu OTP buton parametresi istiyor; gönderenimiz onu
    // yollamıyor, yani panelde oluşturulup panelde gönderilemezdi.
    [Theory]
    [InlineData("AUTHENTICATION")]
    [InlineData("")]
    [InlineData("marketing")] // küçük harf: Meta büyük harf bekliyor
    public void Gecersiz_kategori_reddedilir(string c) =>
        Assert.NotNull(WhatsAppTemplateShape.ValidateCategory(c));

    [Fact]
    public void Bos_govde_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(body: "   ")));

    [Fact]
    public void Uzun_govde_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(body: new string('a', 1025))));

    [Fact]
    public void Degiskenli_govde_ayni_sayida_ornek_ister()
    {
        var eksik = WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{1}}, {{2}} TL", examples: ["Ayşe"]));
        Assert.NotNull(eksik);

        var tam = WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{1}}, {{2}} TL", examples: ["Ayşe", "250"]));
        Assert.Null(tam);
    }

    // Meta örneksiz değişkenli şablonu reddediyor; boş dizgeyi örnek saymak
    // yayıncıya "gönderdim" deyip Meta'dan ret aldırırdı.
    [Fact]
    public void Bos_ornek_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{1}}", examples: ["  "])));

    [Fact]
    public void Isimli_degisken_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{ad}}", examples: ["Ayşe"])));

    [Fact]
    public void Baslikta_degisken_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(header: "Sipariş {{1}}")));

    [Fact]
    public void Uzun_baslik_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(header: new string('a', 61))));

    [Fact]
    public void Uzun_altbilgi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(footer: new string('a', 61))));

    [Fact]
    public void Gecerli_butonlar_kabul_edilir() =>
        Assert.Null(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", "Evet", null, null),
            new("QUICK_REPLY", "Hayır", null, null),
            new("URL", "Siteye git", "https://orderdeckapp.com", null),
            new("PHONE_NUMBER", "Ara", null, "+905321234567"),
        ])));

    [Fact]
    public void Degiskenli_buton_urlsi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("URL", "Takip", "https://orderdeckapp.com/{{1}}", null),
        ])));

    [Fact]
    public void Bilinmeyen_buton_turu_reddedilir() =>
        Assert.Equal(
            WhatsAppTemplateShape.ButtonTypeUnsupported,
            WhatsAppTemplateShape.Validate(Draft(buttons: [
                new("COPY_CODE", "Kodu kopyala", null, null),
            ])));

    [Fact]
    public void Bos_buton_etiketi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", " ", null, null),
        ])));

    [Fact]
    public void Uzun_buton_etiketi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", new string('a', 26), null, null),
        ])));

    [Fact]
    public void Ikiden_fazla_url_butonu_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("URL", "1", "https://a.test", null),
            new("URL", "2", "https://b.test", null),
            new("URL", "3", "https://c.test", null),
        ])));

    [Fact]
    public void Birden_fazla_telefon_butonu_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("PHONE_NUMBER", "Ara", null, "+905321234567"),
            new("PHONE_NUMBER", "Ara 2", null, "+905321234568"),
        ])));

    // Meta hızlı yanıt butonlarının bitişik olmasını şart koşuyor. Sessizce
    // yeniden sıralamak yayıncının tasarladığı düzeni değiştirmek olurdu.
    [Fact]
    public void Bolunmus_hizli_yanit_grubu_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", "Evet", null, null),
            new("URL", "Site", "https://a.test", null),
            new("QUICK_REPLY", "Hayır", null, null),
        ])));
}
