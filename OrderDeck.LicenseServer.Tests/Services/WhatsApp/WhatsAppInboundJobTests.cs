using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.PdfParsing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WhatsAppInboundJobTests
{
    private static (LicenseDbContext Db, WhatsAppInboundJob Job, Guid LicenseId) Build(string pnid = "PNID_1")
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wain-{Guid.NewGuid():N}").Options);

        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));

        var licenseId = Guid.NewGuid();
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = pnid,
            DisplayPhoneNumber = "+905550000000",
            AccessTokenProtected = accounts.ProtectToken("t"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        return (db, new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance),
            new WaDekontExtractor(new PdfDekontParser(), NullLogger<WaDekontExtractor>.Instance)),
            licenseId);
    }

    private static string TextPayload(
        string wamId, string from = "905321234567", string pnid = "PNID_1",
        long ts = 1753440000, string name = "Ayşe") => $$$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "{{{pnid}}}" },
            "contacts": [{ "profile": { "name": "{{{name}}}" }, "wa_id": "{{{from}}}" }],
            "messages": [{ "from": "{{{from}}}", "id": "{{{wamId}}}", "timestamp": "{{{ts}}}",
                           "type": "text", "text": { "body": "merhaba" } }]
          }}]}]
        }
        """;

    [Fact]
    public async Task Inbound_message_creates_conversation_and_opens_window()
    {
        var (db, job, licenseId) = Build();

        await job.ProcessAsync(TextPayload("wamid.1"));

        var convo = db.WaConversations.Single();
        convo.LicenseId.Should().Be(licenseId);
        convo.CustomerPhone.Should().Be("905321234567");
        convo.ProfileName.Should().Be("Ayşe");
        convo.UnreadCount.Should().Be(1);
        convo.LastInboundAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));

        var msg = db.WaMessages.Single();
        msg.Direction.Should().Be("in");
        msg.Status.Should().Be("received");
        msg.Body.Should().Be("merhaba");
    }

    [Fact]
    public async Task Replayed_webhook_does_not_duplicate_the_message()
    {
        var (db, job, _) = Build();
        var payload = TextPayload("wamid.DUP");

        await job.ProcessAsync(payload);
        await job.ProcessAsync(payload);

        db.WaMessages.Should().ContainSingle();
        db.WaConversations.Single().UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_phone_number_id_is_ignored()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(TextPayload("wamid.X", pnid: "PNID_SOMEONE_ELSE"));

        db.WaMessages.Should().BeEmpty();
        db.WaConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task Echo_is_stored_as_outbound_and_does_not_open_window()
    {
        var (db, job, _) = Build();
        var payload = """
        {
          "entry": [{ "changes": [{ "field": "smb_message_echoes", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "message_echoes": [{ "from": "905550000000", "to": "905321234567",
                                 "id": "wamid.ECHO", "timestamp": "1753440000",
                                 "type": "text", "text": { "body": "elden yazdım" } }]
          }}]}]
        }
        """;

        await job.ProcessAsync(payload);

        var msg = db.WaMessages.Single();
        msg.Direction.Should().Be("out");
        msg.Origin.Should().Be("echo");
        msg.Status.Should().Be("sent");

        var convo = db.WaConversations.Single();
        convo.LastInboundAt.Should().BeNull();     // pencere AÇILMADI
        convo.UnreadCount.Should().Be(0);
        convo.LastMessageAt.Should().NotBeNull();
    }

    [Fact]
    public async Task New_inbound_reopens_a_closed_conversation()
    {
        var (db, job, licenseId) = Build();
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1", Status = "closed", CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
        });
        await db.SaveChangesAsync();

        await job.ProcessAsync(TextPayload("wamid.REOPEN"));

        db.WaConversations.Single().Status.Should().Be("open");
    }

    [Fact]
    public async Task Status_updates_advance_but_never_regress()
    {
        var (db, job, licenseId) = Build();
        var convoId = Guid.NewGuid();
        db.WaConversations.Add(new WaConversation
        {
            Id = convoId, LicenseId = licenseId, CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1", Status = "open", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.WaMessages.Add(new WaMessage
        {
            Id = Guid.NewGuid(), ConversationId = convoId, LicenseId = licenseId,
            WamId = "wamid.OUT", Direction = "out", Type = "text", Status = "sent",
            Timestamp = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await job.ProcessAsync(StatusPayload("wamid.OUT", "read"));
        db.WaMessages.Single().Status.Should().Be("read");

        // Gecikmeli "delivered" webhook'u geriye çekmemeli.
        await job.ProcessAsync(StatusPayload("wamid.OUT", "delivered"));
        db.WaMessages.Single().Status.Should().Be("read");
    }

    [Fact]
    public async Task Failed_status_records_the_error_code()
    {
        var (db, job, licenseId) = Build();
        var convoId = Guid.NewGuid();
        db.WaConversations.Add(new WaConversation
        {
            Id = convoId, LicenseId = licenseId, CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1", Status = "open", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.WaMessages.Add(new WaMessage
        {
            Id = Guid.NewGuid(), ConversationId = convoId, LicenseId = licenseId,
            WamId = "wamid.OUT", Direction = "out", Type = "text", Status = "sent",
            Timestamp = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var payload = """
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "statuses": [{ "id": "wamid.OUT", "status": "failed", "timestamp": "1753440000",
                           "errors": [{ "code": 131047, "title": "Re-engagement message" }] }]
          }}]}]
        }
        """;

        await job.ProcessAsync(payload);

        var msg = db.WaMessages.Single();
        msg.Status.Should().Be("failed");
        msg.ErrorCode.Should().Be("131047");
    }

    [Fact]
    public async Task Status_for_unknown_message_is_ignored()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(StatusPayload("wamid.NOT_OURS", "delivered"));

        db.WaMessages.Should().BeEmpty();
    }

    /// <summary>Coexistence geçmiş paketi: bir thread, iki yönlü iki mesaj.</summary>
    private static string HistoryPayload(long inboundTs = 1753440000) => $$$"""
        {
          "entry": [{ "changes": [{ "field": "history", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "history": [{ "threads": [{
              "id": "905321234567",
              "messages": [
                { "from": "905321234567", "id": "wamid.H_IN", "timestamp": "{{{inboundTs}}}",
                  "type": "text", "text": { "body": "eski soru" } },
                { "from": "905550000000", "to": "905321234567", "id": "wamid.H_OUT",
                  "timestamp": "{{{inboundTs + 60}}}", "type": "text",
                  "text": { "body": "eski cevap" } }]
            }]}]
          }}]}]
        }
        """;

    [Fact]
    public async Task History_is_archived_without_unread_badges()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync(HistoryPayload());

        var inbound = db.WaMessages.Single(m => m.WamId == "wamid.H_IN");
        inbound.Direction.Should().Be("in");
        inbound.Origin.Should().Be("history");

        var outbound = db.WaMessages.Single(m => m.WamId == "wamid.H_OUT");
        outbound.Direction.Should().Be("out");
        outbound.Origin.Should().Be("history");

        var convo = db.WaConversations.Single();

        // Aktarım bir olay değil, arşiv: 180 günlük geçmiş yüzlerce okunmamış
        // rozeti üretseydi yayıncı gerçek yeni mesajı göremezdi.
        convo.UnreadCount.Should().Be(0);

        // Ama hizmet penceresi GERÇEK: yayıncı onboarding'den bir saat önce
        // mesaj aldıysa 24 saatlik pencere açık ve bunu bilmemiz gerekiyor.
        convo.LastInboundAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1753440000));
    }

    [Fact]
    public async Task History_does_not_reopen_a_closed_conversation()
    {
        var (db, job, licenseId) = Build();
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1", Status = "closed", CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
        });
        await db.SaveChangesAsync();

        await job.ProcessAsync(HistoryPayload());

        // Mesajların GERÇEKTEN işlendiğini önce doğrula, yoksa test "geri
        // açmıyor" kararını değil, paketin atlandığını ölçerdi.
        db.WaMessages.Should().HaveCount(2);
        db.WaConversations.Single().Status.Should().Be("closed");
    }

    [Fact]
    public async Task Contact_sync_only_fills_an_empty_profile_name()
    {
        var (db, job, licenseId) = Build();

        // Müşteri kendi WhatsApp adını zaten göndermiş.
        await job.ProcessAsync(TextPayload("wamid.1", name: "Ayşe"));

        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "smb_app_state_sync", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "state_sync": [{ "type": "contact", "action": "add",
              "contact": { "phone_number": "905321234567", "full_name": "Rehberdeki Ad" } }]
          }}]}]
        }
        """);

        // Rehber adı yayıncının kendi defterindeki etiket; müşterinin profil adı
        // müşterinin kendi seçtiği ad. Üzerine yazmak, panelde görünen adı
        // sessizce değiştirirdi.
        db.WaConversations.Single().ProfileName.Should().Be("Ayşe");
    }

    [Fact]
    public async Task Contact_sync_names_a_conversation_that_has_none()
    {
        var (db, job, licenseId) = Build();
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1", Status = "open", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "smb_app_state_sync", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "state_sync": [{ "type": "contact", "action": "add",
              "contact": { "phone_number": "905321234567", "full_name": "Rehberdeki Ad" } }]
          }}]}]
        }
        """);

        // Geçmiş aktarımında contacts[] bloğu yok: adsız kalan sohbetlerin tek
        // adı bu senkrondan gelir.
        db.WaConversations.Single().ProfileName.Should().Be("Rehberdeki Ad");
    }

    [Fact]
    public async Task Contact_sync_never_creates_a_conversation()
    {
        var (db, job, _) = Build();

        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "smb_app_state_sync", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "state_sync": [{ "type": "contact", "action": "add",
              "contact": { "phone_number": "905339998877", "full_name": "Hiç Yazmayan" } }]
          }}]}]
        }
        """);

        // Rehberde yayıncının tüm kişileri var — hepsine sohbet açmak paneli
        // hiç mesajlaşmamış yüzlerce satırla doldururdu.
        db.WaConversations.Should().BeEmpty();
    }

    private static string StatusPayload(string wamId, string status) => $$$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "statuses": [{ "id": "{{{wamId}}}", "status": "{{{status}}}", "timestamp": "1753440000" }]
          }}]}]
        }
        """;
}
