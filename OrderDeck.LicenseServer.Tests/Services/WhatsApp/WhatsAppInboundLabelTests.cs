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

public sealed class WhatsAppInboundLabelTests
{
    private static (LicenseDbContext Db, WhatsAppInboundJob Job, Guid LicenseId, Guid LabelId) Build()
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wainlbl-{Guid.NewGuid():N}").Options);

        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));

        var licenseId = Guid.NewGuid();
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = "PNID_1",
            DisplayPhoneNumber = "+905550000000",
            AccessTokenProtected = accounts.ProtectToken("t"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });

        var labelId = Guid.NewGuid();
        db.WaLabels.Add(new WaLabel
        {
            Id = labelId,
            LicenseId = licenseId,
            Name = "Dekont geldi",
            Color = "#eab308",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            EventKey = WaLabelEvent.CustomerSentDocument,
            WaLabelId = labelId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var job = new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance));

        return (db, job, licenseId, labelId);
    }

    /// <summary>Tek medya mesajı içeren webhook gövdesi.</summary>
    private static string MediaPayload(string wamId, string type, string from = "905321234567")
        => $$$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "{{{from}}}" }],
            "messages": [{ "from": "{{{from}}}", "id": "{{{wamId}}}", "timestamp": "1753440000",
                           "type": "{{{type}}}",
                           "{{{type}}}": { "id": "MEDIA_1", "mime_type": "application/pdf" } }]
          }}]}]
        }
        """;

    [Theory]
    [InlineData("document")]
    [InlineData("image")]
    public async Task Document_and_image_both_raise_the_label(string type)
    {
        var (db, job, _, labelId) = Build();

        await job.ProcessAsync(MediaPayload("wamid.1", type));

        var link = await db.WaConversationLabels.SingleAsync();
        link.WaLabelId.Should().Be(labelId);
        link.Source.Should().Be("auto");
    }

    [Fact]
    public async Task Text_message_does_not_raise_the_label()
    {
        var (db, job, _, _) = Build();

        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "905321234567" }],
            "messages": [{ "from": "905321234567", "id": "wamid.t", "timestamp": "1753440000",
                           "type": "text", "text": { "body": "merhaba" } }]
          }}]}]
        }
        """);

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_documents_in_one_batch_produce_one_link()
    {
        var (db, job, _, _) = Build();

        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "905321234567" }],
            "messages": [
              { "from": "905321234567", "id": "wamid.a", "timestamp": "1753440000",
                "type": "document", "document": { "id": "M1", "mime_type": "application/pdf" } },
              { "from": "905321234567", "id": "wamid.b", "timestamp": "1753440001",
                "type": "document", "document": { "id": "M2", "mime_type": "application/pdf" } }]
          }}]}]
        }
        """);

        db.WaConversationLabels.Should().ContainSingle();
    }

    [Fact]
    public async Task Label_is_written_in_the_same_save_as_a_brand_new_conversation()
    {
        var (db, job, _, _) = Build();

        // Bu numaradan daha önce hiç mesaj yok → sohbet aynı SaveChanges'te oluşur.
        db.WaConversations.Should().BeEmpty();

        await job.ProcessAsync(MediaPayload("wamid.new", "document", from: "905339998877"));

        var convo = await db.WaConversations.SingleAsync();
        var link = await db.WaConversationLabels.SingleAsync();
        link.ConversationId.Should().Be(convo.Id);
    }

    [Fact]
    public async Task Echo_of_our_own_document_does_not_raise_the_label()
    {
        var (db, job, _, _) = Build();

        // Parser echo'yu field=="smb_message_echoes" ile tanır, "context" alanıyla değil.
        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "smb_message_echoes", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "message_echoes": [{ "from": "905550000000", "to": "905321234567",
                                 "id": "wamid.echo", "timestamp": "1753440000",
                                 "type": "document",
                                 "document": { "id": "M9", "mime_type": "application/pdf" } }]
          }}]}]
        }
        """);

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_rule_no_label_is_written()
    {
        var (db, job, _, _) = Build();
        db.WaLabelRules.RemoveRange(db.WaLabelRules);
        db.SaveChanges();

        await job.ProcessAsync(MediaPayload("wamid.norule", "document"));

        db.WaConversationLabels.Should().BeEmpty();
    }
}
