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
}
