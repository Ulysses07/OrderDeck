using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Onaylı şablon listesinin okunması. Burada sınanan şey Meta'nın şeması değil,
/// bizim ondan çıkardığımız KARAR: hangi şablon gönderilebilir sayılıyor.
/// Yanlış "gönderilebilir" demek, yayıncının <b>ücretli</b> bir mesajı Meta'nın
/// reddedeceği biçimde yollaması demek.
/// </summary>
public sealed class WhatsAppTemplateCatalogTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? Request { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static WhatsAppTemplateCatalog Catalog(HttpMessageHandler handler)
    {
        var opt = Options.Create(new WhatsAppOptions
        {
            GraphBaseUrl = "https://graph.test",
            GraphApiVersion = "v25.0",
        });
        return new WhatsAppTemplateCatalog(
            new HttpClient(handler), opt, NullLogger<WhatsAppTemplateCatalog>.Instance);
    }

    private static Task<GraphResult<IReadOnlyList<ApprovedTemplate>>> ListAsync(
        HttpStatusCode status, string body) =>
        Catalog(new StubHandler(status, body)).ListApprovedAsync("WABA_1", "TOKEN_1", CancellationToken.None);

    private const string OneApproved = """
        { "data": [ {
            "name": "odeme_hatirlatma", "status": "APPROVED",
            "category": "UTILITY", "language": "tr",
            "components": [
              { "type": "HEADER", "format": "TEXT", "text": "Sipariş bilgisi" },
              { "type": "BODY", "text": "Merhaba {{1}}, {{2}} TL ödemeniz bekleniyor.",
                "example": { "body_text": [ [ "Ayşe", "250" ] ] } },
              { "type": "FOOTER", "text": "OrderDeck" },
              { "type": "BUTTONS", "buttons": [ { "type": "QUICK_REPLY", "text": "Tamam" } ] }
            ] } ] }
        """;

    [Fact]
    public async Task The_waba_is_asked_with_the_business_token()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OneApproved);

        await Catalog(handler).ListApprovedAsync("WABA_9", "TOKEN_9", CancellationToken.None);

        var url = handler.Request!.RequestUri!.ToString();
        url.Should().StartWith("https://graph.test/v25.0/WABA_9/message_templates");
        handler.Request.Method.Should().Be(HttpMethod.Get);
        handler.Request.Headers.Authorization!.Parameter.Should().Be("TOKEN_9");
    }

    [Fact]
    public async Task An_approved_template_is_carried_over_whole()
    {
        var result = await ListAsync(HttpStatusCode.OK, OneApproved);

        result.Ok.Should().BeTrue();
        var t = result.Value!.Single();
        t.Name.Should().Be("odeme_hatirlatma");
        t.Language.Should().Be("tr");
        t.Category.Should().Be("UTILITY");
        t.HeaderText.Should().Be("Sipariş bilgisi");
        t.BodyText.Should().Be("Merhaba {{1}}, {{2}} TL ödemeniz bekleniyor.");
        t.FooterText.Should().Be("OrderDeck");
        t.Buttons.Should().Equal("Tamam");
        t.ParameterCount.Should().Be(2);
        // Örnekler panelde alanların yer tutucusu; yayıncı hangi değişkenin ne
        // olduğunu ancak bunlardan anlıyor.
        t.ParameterExamples.Should().Equal("Ayşe", "250");
        t.UnsupportedReason.Should().BeNull();
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("REJECTED")]
    [InlineData("PAUSED")]
    [InlineData("DISABLED")]
    public async Task Only_approved_templates_reach_the_panel(string status)
    {
        // Onaylı olmayan şablon gönderilemiyor. Listede göstermek yayıncıya
        // gönderebileceği izlenimi verirdi.
        var result = await ListAsync(HttpStatusCode.OK, $$"""
            { "data": [ { "name": "t", "status": "{{status}}", "category": "UTILITY",
                "language": "tr",
                "components": [ { "type": "BODY", "text": "Merhaba" } ] } ] }
            """);

        result.Ok.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    /// <summary>Gönderemediğimiz şablon listeden DÜŞMÜYOR, sebebiyle beraber
    /// görünüyor: Meta'da onaylattığı şablonu panelde hiç göremeyen yayıncı
    /// eksikliği bize değil kendi hesabına yorar.</summary>
    [Fact]
    public async Task A_media_header_is_listed_with_the_reason_not_hidden()
    {
        var result = await ListAsync(HttpStatusCode.OK, """
            { "data": [ { "name": "kargo", "status": "APPROVED", "category": "UTILITY",
                "language": "tr", "components": [
                  { "type": "HEADER", "format": "IMAGE" },
                  { "type": "BODY", "text": "Kargonuz yolda." } ] } ] }
            """);

        var t = result.Value!.Single();
        t.UnsupportedReason.Should().Be(WhatsAppTemplateShape.HeaderMedia);
    }

    [Fact]
    public async Task A_variable_in_the_header_is_refused()
    {
        var result = await ListAsync(HttpStatusCode.OK, """
            { "data": [ { "name": "kargo", "status": "APPROVED", "category": "UTILITY",
                "language": "tr", "components": [
                  { "type": "HEADER", "format": "TEXT", "text": "{{1}} numaralı sipariş" },
                  { "type": "BODY", "text": "Kargonuz yolda." } ] } ] }
            """);

        result.Value!.Single().UnsupportedReason.Should().Be(WhatsAppTemplateShape.HeaderVariable);
    }

    [Theory]
    [InlineData("""{ "type": "COPY_CODE", "text": "Kodu kopyala" }""")]
    [InlineData("""{ "type": "URL", "text": "Takip et", "url": "https://x.test/{{1}}" }""")]
    public async Task A_button_that_wants_its_own_parameter_is_refused(string button)
    {
        var result = await ListAsync(HttpStatusCode.OK, $$"""
            { "data": [ { "name": "kampanya", "status": "APPROVED", "category": "MARKETING",
                "language": "tr", "components": [
                  { "type": "BODY", "text": "İndirim başladı." },
                  { "type": "BUTTONS", "buttons": [ {{button}} ] } ] } ] }
            """);

        result.Value!.Single().UnsupportedReason.Should().Be(WhatsAppTemplateShape.ButtonVariable);
    }

    [Fact]
    public async Task A_fixed_button_is_not_a_problem()
    {
        var result = await ListAsync(HttpStatusCode.OK, """
            { "data": [ { "name": "kampanya", "status": "APPROVED", "category": "MARKETING",
                "language": "tr", "components": [
                  { "type": "BODY", "text": "İndirim başladı." },
                  { "type": "BUTTONS", "buttons": [
                    { "type": "URL", "text": "Siteye git", "url": "https://x.test/kampanya" },
                    { "type": "PHONE_NUMBER", "text": "Ara", "phone_number": "+905551112233" } ] } ] } ] }
            """);

        var t = result.Value!.Single();
        t.UnsupportedReason.Should().BeNull();
        t.Buttons.Should().Equal("Siteye git", "Ara");
    }

    [Fact]
    public async Task An_authentication_template_is_refused()
    {
        // OTP şablonu gövde parametresini değil buton parametresini istiyor;
        // bizim gönderim biçimimize hiç uymuyor.
        var result = await ListAsync(HttpStatusCode.OK, """
            { "data": [ { "name": "dogrulama", "status": "APPROVED", "category": "AUTHENTICATION",
                "language": "tr", "components": [
                  { "type": "BODY", "text": "{{1}} doğrulama kodunuz." } ] } ] }
            """);

        result.Value!.Single().UnsupportedReason.Should().Be(WhatsAppTemplateShape.AuthCategory);
    }

    [Fact]
    public async Task Templates_are_sorted_by_name_then_language()
    {
        // Aynı şablonun dil varyantları panelde yan yana dursun diye.
        var result = await ListAsync(HttpStatusCode.OK, """
            { "data": [
              { "name": "zil", "status": "APPROVED", "category": "UTILITY", "language": "tr",
                "components": [ { "type": "BODY", "text": "b" } ] },
              { "name": "alarm", "status": "APPROVED", "category": "UTILITY", "language": "tr",
                "components": [ { "type": "BODY", "text": "b" } ] },
              { "name": "alarm", "status": "APPROVED", "category": "UTILITY", "language": "en",
                "components": [ { "type": "BODY", "text": "b" } ] } ] }
            """);

        result.Value!.Select(t => $"{t.Name}/{t.Language}")
            .Should().Equal("alarm/en", "alarm/tr", "zil/tr");
    }

    [Fact]
    public async Task A_meta_error_becomes_a_structured_failure()
    {
        var result = await ListAsync(HttpStatusCode.BadRequest, """
            { "error": { "code": 190, "message": "Session has expired." } }
            """);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("190");
        result.ErrorMessage.Should().Be("Session has expired.");
    }

    [Fact]
    public async Task A_non_json_response_does_not_throw()
    {
        var result = await ListAsync(HttpStatusCode.BadGateway, "<html>gateway</html>");

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("502");
    }

    [Fact]
    public async Task A_network_failure_does_not_throw()
    {
        var result = await Catalog(new ThrowingHandler())
            .ListApprovedAsync("WABA_1", "TOKEN_1", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("network");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("bağlantı yok");
    }
}

/// <summary>
/// Gövde yer tutucularının çözümlenmesi. Katalogdan ayrı sınanıyor çünkü asıl
/// incelik burada: yanlış sayıda ya da yanlış sırada parametre, Meta'dan 132000
/// ile döner ve şablon ücretli olduğu için yayıncı parasını denemelere yatırır.
/// </summary>
public sealed class WhatsAppTemplateShapeTests
{
    [Fact]
    public void A_body_without_placeholders_has_no_parameters()
    {
        var (count, unsupported) = WhatsAppTemplateShape.CountBodyParams("Merhaba, siparişiniz hazır.");

        count.Should().Be(0);
        unsupported.Should().BeNull();
    }

    [Theory]
    [InlineData("Merhaba {{1}}", 1)]
    [InlineData("Merhaba {{1}}, {{2}} TL", 2)]
    [InlineData("{{1}} {{2}} {{3}}", 3)]
    public void Positional_placeholders_are_counted(string body, int expected)
    {
        WhatsAppTemplateShape.CountBodyParams(body).Count.Should().Be(expected);
    }

    [Fact]
    public void The_same_index_twice_counts_once()
    {
        // Meta aynı değişkeni iki yerde kullanmaya izin veriyor; gönderilecek
        // değer bir tane.
        WhatsAppTemplateShape.CountBodyParams("{{1}} için {{1}} tekrar").Count.Should().Be(1);
    }

    [Fact]
    public void Named_variables_are_out_of_reach()
    {
        // Gönderenimiz konumsal dizi yolluyor; isimli şablonda Meta reddeder.
        WhatsAppTemplateShape.CountBodyParams("Merhaba {{musteri_adi}}")
            .Unsupported.Should().Be(WhatsAppTemplateShape.NamedParams);
    }

    [Theory]
    [InlineData("{{1}} ve {{3}}")]
    [InlineData("{{2}} tek başına")]
    [InlineData("{{0}} sıfırdan")]
    public void Placeholders_that_do_not_start_at_one_and_run_in_order_are_refused(string body)
    {
        // Boşluklu dizide yayıncının girdiği değerler bir sıra kayar ve yanlış
        // bilgi müşteriye gider.
        WhatsAppTemplateShape.CountBodyParams(body).Unsupported.Should().NotBeNull();
    }
}
