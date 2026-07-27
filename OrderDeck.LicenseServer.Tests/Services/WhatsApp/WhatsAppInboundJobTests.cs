using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
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

        return (db, new WhatsAppInboundJob(db, accounts, NullLogger<WhatsAppInboundJob>.Instance), licenseId);
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

    private static string StatusPayload(string wamId, string status) => $$$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "statuses": [{ "id": "{{{wamId}}}", "status": "{{{status}}}", "timestamp": "1753440000" }]
          }}]}]
        }
        """;
}
