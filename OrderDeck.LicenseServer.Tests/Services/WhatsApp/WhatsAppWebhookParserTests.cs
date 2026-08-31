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

        var dropped = events.DroppedNoPhone.Should().ContainSingle().Subject;
        dropped.UserId.Should().Be("US.13491208655302741918");
        // Hangi hatta düştüğü olmadan kayıp bir yayıncıya atfedilemez.
        dropped.PhoneNumberId.Should().Be("PNID_1");
        dropped.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));
    }

    /// <summary>
    /// Yalnız numarasız mesaj içeren paket <c>IsEmpty</c> sayılmamalı — sayılsaydı
    /// <c>ProcessAsync</c> erken döner ve ölçüm hiç yazılmazdı. Üstelik kaybın en
    /// saf hâli tam olarak bu paket: tek mesaj, o da numarasız.
    /// </summary>
    [Fact]
    public void Yalniz_numarasiz_mesaj_iceren_paket_bos_sayilmaz()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "messages": [{ "from_user_id": "US.13491208655302741918", "id": "wamid.NOPHONE",
                           "timestamp": "1753440000", "type": "text",
                           "text": { "body": "numarasız" } }]
          }}]}]
        }
        """;

        WhatsAppWebhookParser.Parse(payload).IsEmpty.Should().BeFalse();
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

        WhatsAppWebhookParser.Parse(payload).DroppedNoPhone.Should().BeEmpty();
    }

    [Fact]
    public void Normal_mesajda_BSUID_listesi_bos_kalir()
    {
        WhatsAppWebhookParser.Parse(TextMessagePayload).DroppedNoPhone.Should().BeEmpty();
    }

    /// <summary>
    /// Coexistence geçmiş aktarımı. Yön <c>from</c>'dan DEĞİL thread'den
    /// çıkarılıyor: bir thread iki yönü birden taşıdığı için canlı akışın
    /// "from = müşteri" varsayımı burada çalışmaz — işletmenin kendi yazdığı
    /// mesaj müşteriden gelmiş gibi kaydedilirdi.
    /// </summary>
    private const string HistoryPayload = """
    {
      "entry": [{ "changes": [{ "field": "history", "value": {
        "metadata": { "phone_number_id": "PNID_1" },
        "history": [{
          "metadata": { "phase": "0", "chunk_order": 1, "progress": 100 },
          "threads": [{
            "id": "905321234567",
            "messages": [
              { "from": "905321234567", "id": "wamid.H_IN", "timestamp": "1753440000",
                "type": "text", "text": { "body": "eski soru" } },
              { "from": "905550000000", "to": "905321234567", "id": "wamid.H_OUT",
                "timestamp": "1753440060", "type": "text", "text": { "body": "eski cevap" } }
            ]
          }]
        }]
      }}]}]
    }
    """;

    [Fact]
    public void Gecmis_thread_kimliginden_yon_cikarilir()
    {
        var events = WhatsAppWebhookParser.Parse(HistoryPayload);

        events.Messages.Should().HaveCount(2);

        var inbound = events.Messages.Single(m => m.WamId == "wamid.H_IN");
        inbound.IsEcho.Should().BeFalse();
        inbound.IsHistory.Should().BeTrue();
        inbound.PhoneNumberId.Should().Be("PNID_1");
        inbound.FromPhone.Should().Be("905321234567");
        inbound.Body.Should().Be("eski soru");

        // İşletmenin yazdığı mesaj giden sayılmalı, ama karşı taraf yine
        // müşteri: sohbet müşterinin numarasına anahtarlı.
        var outbound = events.Messages.Single(m => m.WamId == "wamid.H_OUT");
        outbound.IsEcho.Should().BeTrue();
        outbound.IsHistory.Should().BeTrue();
        outbound.FromPhone.Should().Be("905321234567");
    }

    [Fact]
    public void Canli_mesaj_gecmis_isaretlenmez()
    {
        WhatsAppWebhookParser.Parse(TextMessagePayload)
            .Messages.Should().ContainSingle().Subject
            .IsHistory.Should().BeFalse();
    }

    /// <summary>Thread kimliği yoksa mesajı hangi sohbete koyacağımızı
    /// bilmiyoruz; uydurmak yerine atlanıyor.</summary>
    [Fact]
    public void Kimliksiz_thread_atlanir()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "history", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "history": [{ "threads": [{
              "messages": [{ "from": "905321234567", "id": "wamid.H", "timestamp": "1753440000",
                             "type": "text", "text": { "body": "sahipsiz" } }]
            }]}]
          }}]}]
        }
        """;

        WhatsAppWebhookParser.Parse(payload).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Rehber_senkronu_kisi_adlarini_tasir()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "smb_app_state_sync", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "state_sync": [
              { "type": "contact", "action": "add",
                "contact": { "phone_number": "905321234567", "full_name": "Ayşe Yılmaz" } },
              { "type": "contact", "action": "remove",
                "contact": { "phone_number": "905329999999", "full_name": "Silinen" } },
              { "type": "contact", "action": "add", "contact": { "full_name": "Numarasız" } },
              { "type": "chat", "chat": { "id": "905321234567" } }
            ]
          }}]}]
        }
        """;

        var events = WhatsAppWebhookParser.Parse(payload);

        // "chat" satırı ve numarasız kişi elenir; "remove" ayrıştırıcıda değil
        // işleyicide yok sayılır, çünkü kaybı görmek için önce okumak lazım.
        events.Contacts.Should().HaveCount(2);

        var added = events.Contacts[0];
        added.PhoneNumberId.Should().Be("PNID_1");
        added.Phone.Should().Be("905321234567");
        added.FullName.Should().Be("Ayşe Yılmaz");
        added.Action.Should().Be("add");

        events.Contacts[1].Action.Should().Be("remove");
    }

    /// <summary>Yalnız rehber taşıyan paket boş sayılırsa <c>ProcessAsync</c>
    /// erken döner ve adlar hiç işlenmez.</summary>
    [Fact]
    public void Yalniz_rehber_iceren_paket_bos_sayilmaz()
    {
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "smb_app_state_sync", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "state_sync": [{ "type": "contact", "action": "add",
              "contact": { "phone_number": "905321234567", "full_name": "Ayşe" } }]
          }}]}]
        }
        """;

        WhatsAppWebhookParser.Parse(payload).IsEmpty.Should().BeFalse();
    }
}
