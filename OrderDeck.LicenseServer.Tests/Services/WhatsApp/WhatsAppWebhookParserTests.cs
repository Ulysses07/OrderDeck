using FluentAssertions;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WhatsAppWebhookParserTests
{
    private const string TextMessagePayload = """
    {
      "object": "whatsapp_business_account",
      "entry": [{
        "id": "WABA_ID",
        "changes": [{
          "field": "messages",
          "value": {
            "messaging_product": "whatsapp",
            "metadata": { "display_phone_number": "905550000000", "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe Yılmaz" }, "wa_id": "905321234567" }],
            "messages": [{
              "from": "905321234567",
              "id": "wamid.ABC",
              "timestamp": "1753440000",
              "type": "text",
              "text": { "body": "12 numaralı ürün bende" }
            }]
          }
        }]
      }]
    }
    """;

    [Fact]
    public void Parses_inbound_text_message()
    {
        var events = WhatsAppWebhookParser.Parse(TextMessagePayload);

        var m = events.Messages.Should().ContainSingle().Subject;
        m.PhoneNumberId.Should().Be("PNID_1");
        m.WamId.Should().Be("wamid.ABC");
        m.FromPhone.Should().Be("905321234567");
        m.ProfileName.Should().Be("Ayşe Yılmaz");
        m.Type.Should().Be("text");
        m.Body.Should().Be("12 numaralı ürün bende");
        m.IsEcho.Should().BeFalse();
        m.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));
    }

    [Fact]
    public void Parses_image_message_with_media_id_and_caption()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "messages": [{
              "from": "905321234567", "id": "wamid.IMG", "timestamp": "1753440000",
              "type": "image",
              "image": { "id": "MEDIA_9", "mime_type": "image/jpeg", "caption": "dekont" }
            }]
          }}]}]
        }
        """;

        var m = WhatsAppWebhookParser.Parse(payload).Messages.Should().ContainSingle().Subject;
        m.Type.Should().Be("image");
        m.MediaId.Should().Be("MEDIA_9");
        m.MediaMimeType.Should().Be("image/jpeg");
        m.Body.Should().Be("dekont");
    }

    [Fact]
    public void Echo_uses_to_as_counterparty_and_is_flagged()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "smb_message_echoes", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "message_echoes": [{
              "from": "905550000000", "to": "905321234567",
              "id": "wamid.ECHO", "timestamp": "1753440000",
              "type": "text", "text": { "body": "elden yazdım" }
            }]
          }}]}]
        }
        """;

        var m = WhatsAppWebhookParser.Parse(payload).Messages.Should().ContainSingle().Subject;
        m.IsEcho.Should().BeTrue();
        m.FromPhone.Should().Be("905321234567");
        m.Body.Should().Be("elden yazdım");
    }

    [Fact]
    public void Parses_status_updates_with_errors()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "statuses": [
              { "id": "wamid.OK", "status": "delivered", "timestamp": "1753440000", "recipient_id": "905321234567" },
              { "id": "wamid.BAD", "status": "failed", "timestamp": "1753440001",
                "errors": [{ "code": 131026, "title": "Message undeliverable" }] }
            ]
          }}]}]
        }
        """;

        var events = WhatsAppWebhookParser.Parse(payload);
        events.Messages.Should().BeEmpty();
        events.Statuses.Should().HaveCount(2);
        events.Statuses[0].Status.Should().Be("delivered");
        events.Statuses[1].ErrorCode.Should().Be("131026");
        events.Statuses[1].ErrorMessage.Should().Be("Message undeliverable");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "entry": [] }""")]
    [InlineData("""{ "entry": [{ "changes": [{ "field": "account_update", "value": {} }] }] }""")]
    public void Unknown_or_broken_payloads_yield_empty_instead_of_throwing(string payload)
    {
        WhatsAppWebhookParser.Parse(payload).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Message_without_id_is_skipped()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "messages": [{ "from": "905321234567", "timestamp": "1753440000", "type": "text",
                           "text": { "body": "kimliksiz" } }]
          }}]}]
        }
        """;

        WhatsAppWebhookParser.Parse(payload).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Unsupported_type_is_kept_without_body()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "messages": [{ "from": "905321234567", "id": "wamid.X", "timestamp": "1753440000",
                           "type": "contacts", "contacts": [{ "name": { "first_name": "Ali" } }] }]
          }}]}]
        }
        """;

        var m = WhatsAppWebhookParser.Parse(payload).Messages.Should().ContainSingle().Subject;
        m.Type.Should().Be("contacts");
        m.Body.Should().BeNull();
    }

    /// <summary>
    /// Kullanıcı adı özelliğini açmış müşteri: Meta <c>from</c>/<c>wa_id</c>
    /// göndermiyor, yerine yalnız BSUID geliyor. Mesajı kaydedemiyoruz (sohbet
    /// telefona anahtarlı) ama sessizce kaybolmamalı.
    /// </summary>
    [Fact]
    public void Numarasiz_mesaj_BSUID_ile_olculur()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "user_id": "US.13491208655302741918" }],
            "messages": [{ "from_user_id": "US.13491208655302741918", "id": "wamid.NOPHONE",
                           "timestamp": "1753440000", "type": "text",
                           "text": { "body": "numarasız" } }]
          }}]}]
        }
        """;

        var events = WhatsAppWebhookParser.Parse(payload);

        events.Messages.Should().BeEmpty();
        events.DroppedNoPhoneUserIds.Should().ContainSingle()
            .Which.Should().Be("US.13491208655302741918");
    }

    /// <summary>
    /// Bozuk payload (kimliksiz mesaj) BSUID sayacını şişirmemeli — aksi hâlde
    /// ölçüm, "kullanıcı adı" olgusunu gerçekte olduğundan büyük gösterirdi.
    /// </summary>
    [Fact]
    public void Kimliksiz_mesaj_BSUID_sayacini_sismez()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "messages": [{ "timestamp": "1753440000", "type": "text",
                           "text": { "body": "ne id ne numara" } }]
          }}]}]
        }
        """;

        WhatsAppWebhookParser.Parse(payload).DroppedNoPhoneUserIds.Should().BeEmpty();
    }

    [Fact]
    public void Normal_mesajda_BSUID_listesi_bos_kalir()
    {
        WhatsAppWebhookParser.Parse(TextMessagePayload).DroppedNoPhoneUserIds.Should().BeEmpty();
    }
}
