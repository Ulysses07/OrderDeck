using System;
using FluentAssertions;
using OrderDeck.Core.Customers;
using Xunit;

namespace OrderDeck.Tests.Customers;

public class WhatsAppMessageBuilderTests
{
    private readonly WhatsAppMessageBuilder _sut = new();

    [Fact]
    public void BuildMessage_SubstitutesAllPlaceholders()
    {
        var ctx = new PaymentContext(
            DisplayName: "Ali Veli",
            TotalAmount: 245.50m,
            StreamDate: new DateTime(2026, 4, 30),
            Iban: "TR12 0000 0000 0000",
            AccountHolder: "Burak Y",
            Papara: "1234567");

        var template = "Merhaba {ad}, {tarih} yayınımızdan {tutar} TL bekleniyor. IBAN: {iban} ({hesap_sahibi}). Papara: {papara}";

        var result = _sut.BuildMessage(template, ctx);

        result.Should().Be(
            "Merhaba Ali Veli, 30 Nisan 2026 yayınımızdan 245,50 TL bekleniyor. IBAN: TR12 0000 0000 0000 (Burak Y). Papara: 1234567");
    }

    [Fact]
    public void BuildMessage_TrCultureDecimalFormatting()
    {
        var ctx = new PaymentContext("X", 1234.56m, new DateTime(2026, 1, 1), null, null, null);
        var result = _sut.BuildMessage("{tutar}", ctx);
        result.Should().Be("1.234,56");
    }

    [Fact]
    public void BuildMessage_NullPaymentFieldsRenderAsEmpty()
    {
        var ctx = new PaymentContext("X", 0m, new DateTime(2026, 1, 1), null, null, null);
        var result = _sut.BuildMessage("[{iban}][{hesap_sahibi}][{papara}]", ctx);
        result.Should().Be("[][][]");
    }

    [Fact]
    public void BuildWaMeLink_StripsPlusAndEscapesMessage()
    {
        var link = _sut.BuildWaMeLink("+905551234567", "Hello\nWorld");
        link.Should().Be("https://wa.me/905551234567?text=Hello%0AWorld");
    }

    [Fact]
    public void BuildWaMeLink_EscapesTurkishCharsAndSpaces()
    {
        var link = _sut.BuildWaMeLink("+905551234567", "Merhaba Ali, ödeme bekleniyor");
        link.Should().StartWith("https://wa.me/905551234567?text=");
        link.Should().Contain("Merhaba%20Ali");
        link.Should().NotContain(" ");
    }

    // ── Kargo placeholder'ları (2026-05-12) ─────────────────────────────

    [Fact]
    public void BuildMessage_substitutes_kargo_placeholder_with_shipping_note()
    {
        var ctx = new PaymentContext("Ali", 3150m, new DateTime(2026, 1, 1), null, null, null,
            ProductTotal: 3000m, ShippingFee: 150m, ShippingNote: "Kargo: 150,00 TL");
        var result = _sut.BuildMessage("{tutar} TL ({kargo})", ctx);
        result.Should().Be("3.150,00 TL (Kargo: 150,00 TL)");
    }

    [Fact]
    public void BuildMessage_substitutes_urun_toplami_separately_from_tutar()
    {
        var ctx = new PaymentContext("Ali", 5150m, new DateTime(2026, 1, 1), null, null, null,
            ProductTotal: 5000m, ShippingFee: 150m, ShippingNote: "");
        var result = _sut.BuildMessage("Ürün: {urun_toplami} Toplam: {tutar}", ctx);
        result.Should().Be("Ürün: 5.000,00 Toplam: 5.150,00");
    }

    [Fact]
    public void BuildMessage_kargo_ucreti_em_dash_when_null()
    {
        // Ücretsiz kargo veya alıcı ödemeli durumunda ShippingFee null → "—"
        var ctx = new PaymentContext("X", 6000m, new DateTime(2026, 1, 1), null, null, null,
            ProductTotal: 6000m, ShippingFee: null, ShippingNote: "Ücretsiz kargo");
        var result = _sut.BuildMessage("[{kargo_ucreti}]", ctx);
        result.Should().Be("[—]");
    }

    [Fact]
    public void BuildMessage_old_template_without_new_placeholders_still_works()
    {
        // Eski template'ler {kargo} vb. içermez — backward-compat
        var ctx = new PaymentContext("X", 100m, new DateTime(2026, 1, 1), "IBAN", "Holder", "P");
        var result = _sut.BuildMessage("{ad} - {tutar} TL - {iban}", ctx);
        result.Should().Be("X - 100,00 TL - IBAN");
    }

    // ── PR-E: ShippingWon kazandın template ───────────────────────────────

    [Fact]
    public void BuildShippingWonMessage_substitutes_placeholders()
    {
        var result = _sut.BuildShippingWonMessage(
            "Merhaba {ad}, {kumulatif_tutar} TL alımınızla ücretsiz kargo kazandınız!",
            "Ayşe Yılmaz",
            5300m);
        result.Should().Be("Merhaba Ayşe Yılmaz, 5.300,00 TL alımınızla ücretsiz kargo kazandınız!");
    }

    [Fact]
    public void BuildShippingWonMessage_kumulatif_tutar_uses_turkish_culture()
    {
        var result = _sut.BuildShippingWonMessage(
            "{kumulatif_tutar}", "x", 1234567.89m);
        // tr-TR: 1.234.567,89
        result.Should().Be("1.234.567,89");
    }

    [Fact]
    public void BuildShippingWonMessage_supports_tarih_placeholder()
    {
        var result = _sut.BuildShippingWonMessage("{tarih}", "x", 1m);
        // İçeriği assert etmek yerine sadece placeholder substitusyonu yapıldığını doğrula
        result.Should().NotContain("{tarih}");
        result.Should().NotBeEmpty();
    }

    // ── Meta şablon parametreleri (2026-07-29) ────────────────────────────

    private static PaymentContext FullContext(
        string name = "Ayşe Yılmaz", string? iban = "TR12 0006 4000 0011",
        string? holder = "Burak S", string shippingNote = "Ücretsiz kargo") =>
        new(name, 450m, new DateTime(2026, 7, 28), iban, holder, null,
            ProductTotal: 450m, ShippingFee: null, ShippingNote: shippingNote);

    /// <summary>Yayıncının Ayarlar ekranında kurduğu yuva→alan eşlemesi.
    /// Buradaki sıra artık kodda sabit değil; testlerde yalnız örnek bir
    /// eşleme olarak duruyor.</summary>
    private static readonly string[] SevenSlotMapping =
        { "ad", "tarih", "urun_toplami", "kargo", "tutar", "iban", "hesap_sahibi" };

    [Fact]
    public void BuildPaymentTemplateParams_follows_configured_mapping_order()
    {
        // Değerler eşlemenin sırasına göre dizilir. Kayma olursa müşteri
        // IBAN'ın yerinde tarihi görür.
        var result = _sut.BuildPaymentTemplateParams(FullContext(), SevenSlotMapping);

        result.Should().Equal(
            "Ayşe Yılmaz", "28 Temmuz 2026", "450,00", "Ücretsiz kargo",
            "450,00", "TR12 0006 4000 0011", "Burak S");
    }

    [Fact]
    public void BuildPaymentTemplateParams_honours_a_different_mapping()
    {
        // Sahadaki dört parametreli şablon: aynı bağlam, başka sıra.
        var result = _sut.BuildPaymentTemplateParams(
            FullContext(), new[] { "tutar", "ad", "hesap_sahibi", "iban" });

        result.Should().Equal("450,00", "Ayşe Yılmaz", "Burak S", "TR12 0006 4000 0011");
    }

    [Fact]
    public void BuildPaymentTemplateParams_null_when_mapping_is_empty()
    {
        // Eşleme kurulmamış → şablon yolu kapalı, wa.me'ye düşülür.
        _sut.BuildPaymentTemplateParams(FullContext(), Array.Empty<string>())
            .Should().BeNull();
    }

    [Fact]
    public void BuildPaymentTemplateParams_null_when_mapping_has_unknown_key()
    {
        // Ayarlar dosyası elle düzenlenmiş olabilir; tanınmayan alanı
        // tahminle doldurmaktansa göndermemek doğru.
        _sut.BuildPaymentTemplateParams(FullContext(), new[] { "ad", "yok_boyle_bir_alan" })
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null, "Burak S")]
    [InlineData("", "Burak S")]
    [InlineData("TR12", null)]
    [InlineData("TR12", "   ")]
    public void BuildPaymentTemplateParams_null_when_payment_details_missing(
        string? iban, string? holder)
    {
        // Meta boş parametreyi reddediyor → şablonu hiç denememek gerekiyor.
        _sut.BuildPaymentTemplateParams(FullContext(iban: iban, holder: holder), SevenSlotMapping)
            .Should().BeNull();
    }

    [Fact]
    public void BuildPaymentTemplateParams_uses_dash_when_shipping_note_empty()
    {
        // Kargo özelliği kapalı → not boş gelir. Bu meşru bir durum; şablon
        // yolunu tümden kapatmamalı.
        var result = _sut.BuildPaymentTemplateParams(
            FullContext(shippingNote: ""), SevenSlotMapping);

        result.Should().NotBeNull();
        result![3].Should().Be("—");
    }

    [Fact]
    public void BuildPaymentTemplateParams_strips_newlines_and_tabs()
    {
        // Ad sohbetten geliyor; satır sonu/sekme taşıyan parametreyi Meta reddeder.
        var result = _sut.BuildPaymentTemplateParams(
            FullContext(name: "Ayşe\n\tYılmaz  Kaya"), SevenSlotMapping);

        result.Should().NotBeNull();
        result![0].Should().Be("Ayşe Yılmaz Kaya");
    }

    [Fact]
    public void BuildPaymentTemplateParams_null_when_name_is_blank()
    {
        _sut.BuildPaymentTemplateParams(FullContext(name: "  "), SevenSlotMapping)
            .Should().BeNull();
    }
}
