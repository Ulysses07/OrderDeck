using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class LabelRuleApplierTests
{
    private static (LicenseDbContext Db, LabelRuleApplier Applier, Guid LicenseId) Build()
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"labelrule-{Guid.NewGuid():N}").Options);
        return (db, new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance), Guid.NewGuid());
    }

    private static Guid SeedLabelAndRule(
        LicenseDbContext db, Guid licenseId, WaLabelEvent ev, string name = "Dekont geldi")
    {
        var label = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Name = name,
            Color = "#22c55e",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            EventKey = ev,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return label.Id;
    }

    private static Guid SeedConversation(
        LicenseDbContext db, Guid licenseId, string canonicalPhone = "905321234567")
    {
        var convo = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = canonicalPhone,
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(convo);
        db.SaveChanges();
        return convo.Id;
    }

    [Theory]
    [InlineData("+905321234567")]
    [InlineData("05321234567")]
    [InlineData("905321234567")]
    [InlineData("0532 123 45 67")]
    public async Task Attaches_the_rule_label_whatever_shape_the_phone_arrives_in(string phone)
    {
        var (db, applier, licenseId) = Build();
        var labelId = SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        var conversationId = SeedConversation(db, licenseId);

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, phone, default);
        await db.SaveChangesAsync();

        var row = db.WaConversationLabels.Single();
        row.ConversationId.Should().Be(conversationId);
        row.WaLabelId.Should().Be(labelId);
        row.LicenseId.Should().Be(licenseId);
        row.Source.Should().Be("auto");
    }

    [Fact]
    public async Task Does_nothing_when_the_license_has_no_rule_for_the_event()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, licenseId);

        // Kural PaymentApproved için tanımlı; gelen olay PaymentRejected.
        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentRejected, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Customer_who_never_wrote_on_whatsapp_is_skipped_silently()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        // Sohbet YOK.

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_turkish_number_is_skipped_silently()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, licenseId, canonicalPhone: "14155552671");

        // Eleyen adımın normalize olduğunu açıkça yaz: sohbet duruyor ve
        // numarası birebir tutuyor, buna rağmen etiket yapışmıyorsa sebep
        // "sohbet bulunamadı" değil, numaranın TR olmaması.
        LabelRuleApplier.ToConversationPhone("+14155552671").Should().BeNull();

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+14155552671", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_licenses_conversation_is_never_touched()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, Guid.NewGuid());   // BAŞKA yayıncının sohbeti, aynı numara

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
        // Numara tutuyor, sohbet duruyor — tek fark lisans. Bu satır olmasa
        // test, numara hiç çözülemediği için de yeşil kalabilirdi.
        db.WaConversations.Single().CustomerPhone.Should().Be("905321234567");
    }

    [Fact]
    public async Task Repeated_event_does_not_duplicate_the_label()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, licenseId);

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();
        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().ContainSingle();
    }

    /// <summary>
    /// Tek bir SaveChanges'ten önce aynı olay iki kez işlenirse (webhook
    /// paketinde iki mesaj) DB'de henüz satır YOK — kontrol yalnız sorguya
    /// dayansaydı iki satır eklenirdi ve unique index SaveChanges'i patlatırdı.
    /// </summary>
    [Fact]
    public async Task Two_events_in_the_same_unit_of_work_do_not_duplicate_the_label()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.CustomerSentDocument);
        SeedConversation(db, licenseId);

        await applier.ApplyAsync(licenseId, WaLabelEvent.CustomerSentDocument, "+905321234567", default);
        await applier.ApplyAsync(licenseId, WaLabelEvent.CustomerSentDocument, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().ContainSingle();
    }

    /// <summary>
    /// Yayıncı etiketi elle yapıştırmışsa kural onu "auto"ya çevirmemeli:
    /// kaynak bilgisi panelde "bunu ben mi koydum, sistem mi" sorusunun cevabı.
    /// </summary>
    [Fact]
    public async Task Existing_manual_label_is_left_alone()
    {
        var (db, applier, licenseId) = Build();
        var labelId = SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        var conversationId = SeedConversation(db, licenseId);
        db.WaConversationLabels.Add(new WaConversationLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ConversationId = conversationId,
            WaLabelId = labelId,
            Source = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Single().Source.Should().Be("manual");
    }
}
