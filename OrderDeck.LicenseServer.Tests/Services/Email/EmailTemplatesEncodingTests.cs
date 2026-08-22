using FluentAssertions;
using OrderDeck.LicenseServer.Services.Email;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Email;

/// <summary>
/// HTML e-posta gövdelerinde encoding (denetim O-05).
///
/// <para><b>Neden var:</b> müşteri adı kendi belirlediği bir alan ve
/// <c>POST /api/v1/auth/register</c> herkese açık (IP başına 3/dk).
/// Doğrulama e-postası tanımı gereği DOĞRULANMAMIŞ bir adrese gidiyor —
/// yani saldırgan kurbanın adresiyle kayıt olup <c>Name</c> alanına
/// (200 karakter, karakter kısıtı yok) HTML koyabiliyor. Sonuç: OrderDeck'in
/// kendi alan adından, geçerli SPF/DKIM ile, saldırganın yazdığı bir bağlantı
/// taşıyan <b>gerçek</b> bir e-posta.</para>
///
/// <para>Sunucu tarafı XSS değil — e-posta istemcileri script çalıştırmıyor.
/// Kaybedilen şey içerik bütünlüğü: alıcının "bu mail OrderDeck'ten geldiyse
/// içindeki bağlantı da OrderDeck'indir" varsayımı.</para>
/// </summary>
public class EmailTemplatesEncodingTests
{
    /// <summary>200 karakterlik <c>Name</c> alanına rahatça sığan, işe yarar
    /// bir yük: sahte bir "doğrula" bağlantısı.</summary>
    private const string Payload =
        "Ahmet</p><a href=\"https://kotu-site.example/dogrula\">Hesabınızı doğrulayın</a><p>";

    private const string Key = "LDK-A1B2C3D4E5F6789012345678ABCDEF12";
    private const string Url = "https://license.orderdeckapp.com/x";

    public static TheoryData<string, string> HtmlBodiesWithHostileName() => new()
    {
        { "ConfirmEmail", EmailTemplates.ConfirmEmail(Payload, Url).Html },
        { "PasswordReset", EmailTemplates.PasswordReset(Payload, Url).Html },
        { "Renewal14d", EmailTemplates.Renewal14d(Payload, Key, Expires, Url, Url).Html },
        { "Renewal7d", EmailTemplates.Renewal7d(Payload, Key, Expires, Url, Url).Html },
        { "Renewal3d", EmailTemplates.Renewal3d(Payload, Key, Expires, Url, Url).Html },
        { "Renewal0d", EmailTemplates.Renewal0d(Payload, Key, Expires, Url, Url).Html },
        { "ExpiredAfter1d", EmailTemplates.ExpiredAfter1d(Payload, Key, Url, Url).Html },
        { "LicenseIssued", EmailTemplates.LicenseIssued(Payload, Key, "STD", Expires, Url).Html },
        { "LicenseRevoked", EmailTemplates.LicenseRevoked(Payload, Key, "ihlal", Url).Html },
        { "LicenseExtended", EmailTemplates.LicenseExtended(Payload, Key, Expires, 30, Url).Html },
    };

    private static DateTimeOffset Expires => new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(HtmlBodiesWithHostileName))]
    public void Musteri_adi_hicbir_sablonda_ham_HTML_olarak_gecmiyor(string template, string html)
    {
        // Tek tek şablon değil, HEPSİ: yeni bir şablon eklendiğinde buraya
        // satır eklenmezse eksik kalır, ama eklenen satır anında korumayı ölçer.
        html.Should().NotContain("<a href=\"https://kotu-site.example/dogrula\">",
            $"{template} şablonu saldırganın yazdığı bağlantıyı ham geçiriyor");
        html.Should().Contain("&lt;a href=",
            $"{template} şablonunda ad encode edilmiş olmalı");
    }

    [Fact]
    public void Encode_edilen_ad_okunabilir_kaliyor()
    {
        // Encoding'in "her şeyi bozalım" olmadığını gösteren nöbetçi: Türkçe
        // karakterler ve normal bir ad aynen görünüyor.
        var html = EmailTemplates.ConfirmEmail("Rıdvan Özcan & Şürekası", Url).Html;

        html.Should().Contain("Merhaba Rıdvan Özcan &amp; Şürekası,");
    }

    [Fact]
    public void Iptal_sebebi_de_encode_ediliyor()
    {
        // Sebep admin girdisi (500 karakter), yani müşteri adı kadar açık
        // değil — ama düz metin olarak gövdeye giren ikinci serbest alan.
        var html = EmailTemplates.LicenseRevoked(
            "Ahmet", Key, "<b>ödeme yapılmadı</b>", Url).Html;

        html.Should().NotContain("<b>ödeme");
        html.Should().Contain("&lt;b&gt;ödeme");
    }

    [Fact]
    public void Sablon_kendi_isaretlemesini_kaybetmiyor()
    {
        // Sabit metin encode EDİLMEMELİ; yoksa mail ham HTML olarak görünür.
        var html = EmailTemplates.ConfirmEmail("Ahmet", Url).Html;

        html.Should().StartWith("<!doctype html>");
        html.Should().Contain($"<a href=\"{Url}\">tıkla</a>");
        html.Should().EndWith("</body></html>");
    }

    [Fact]
    public void Abonelik_iptali_baglantisi_hala_tiklanabilir()
    {
        // Alt bilgi de işleyiciden geçiyor; URL'in bozulmadığını doğrula.
        var html = EmailTemplates.Renewal7d("Ahmet", Key, Expires, Url, Url).Html;

        html.Should().Contain($"<a href=\"{Url}\">E-posta bildirimlerini durdur</a>");
    }

    [Fact]
    public void Duz_metin_govde_encode_EDILMIYOR()
    {
        // Bilinçli fark: text/plain parçasında "&amp;" kullanıcıya aynen
        // görünürdü. Koruma HTML parçasına ait; düz metinde işaretleme
        // kavramı yok, dolayısıyla enjekte edilecek bir şey de yok.
        var plain = EmailTemplates.ConfirmEmail("Ahmet & Mehmet", Url).Plain;

        plain.Should().Contain("Merhaba Ahmet & Mehmet,");
        plain.Should().NotContain("&amp;");
    }
}
