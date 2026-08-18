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

public sealed class WhatsAppInboundLabelTests
{
    /// <summary>Testin kontrol edebildiği sahte PDF ayrıştırıcısı.</summary>
    private sealed class StubParser : IPdfDekontParser
    {
        public int Calls { get; private set; }

        public PdfDekontParser.ParseResult Parse(byte[] pdfBytes)
        {
            Calls++;
            return new PdfDekontParser.ParseResult(
                PayerName: "AYŞE YILMAZ",
                Amount: 1250.50m,
                PaidAt: new DateTime(2026, 8, 18, 14, 30, 0),
                ReferansNo: "REF123456",
                PdfHash: "abc123",
                RawText: "ham metin",
                RecipientIban: "TR330006100519786457841326",
                RecipientName: "EMAR GLOBAL");
        }
    }

    /// <summary>Graph'a çıkmadan sabit bir <see cref="WhatsAppMediaRef"/> döndüren
    /// indirici. HTTP katmanını taklit etmemek için <c>WhatsAppMediaDownloader</c>
    /// mührü kaldırılıp <c>FetchAsync</c> sanal yapıldı; job'a hazır bir alt sınıf
    /// veriyoruz.</summary>
    private static class FakeMedia
    {
        public static WhatsAppMediaDownloader ReturningPdf(byte[] bytes)
            => new StubDownloader(new WhatsAppMediaRef("k.pdf", "application/pdf", bytes.Length, bytes));

        public static WhatsAppMediaDownloader ReturningImage()
            => new StubDownloader(new WhatsAppMediaRef("k.jpg", "image/jpeg", 10, null));

        private sealed class StubDownloader : WhatsAppMediaDownloader
        {
            private readonly WhatsAppMediaRef _ref;

            public StubDownloader(WhatsAppMediaRef mediaRef)
                : base(new HttpClient(), new InMemoryWhatsAppMediaStore(),
                       Options.Create(new WhatsAppOptions()),
                       NullLogger<WhatsAppMediaDownloader>.Instance)
                => _ref = mediaRef;

            public override Task<WhatsAppMediaRef?> FetchAsync(
                string mediaId, string messageType, WhatsAppSendContext ctx,
                Guid licenseId, CancellationToken ct = default)
                => Task.FromResult<WhatsAppMediaRef?>(_ref);
        }
    }

    private static (LicenseDbContext Db, WhatsAppInboundJob Job, Guid LicenseId, Guid LabelId, StubParser Parser)
        Build(WhatsAppMediaDownloader? media = null)
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

        var parser = new StubParser();
        var job = new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance),
            new WaDekontExtractor(parser, NullLogger<WaDekontExtractor>.Instance),
            media);

        return (db, job, licenseId, labelId, parser);
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
        var (db, job, _, labelId, _) = Build();

        await job.ProcessAsync(MediaPayload("wamid.1", type));

        var link = await db.WaConversationLabels.SingleAsync();
        link.WaLabelId.Should().Be(labelId);
        link.Source.Should().Be("auto");
    }

    [Fact]
    public async Task Text_message_does_not_raise_the_label()
    {
        var (db, job, _, _, _) = Build();

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
        var (db, job, _, _, _) = Build();

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

        // İki mesajın DA işlendiğini önce doğrula: ikincisi atlanmış olsaydı
        // tek etiket yine çıkardı ve test tekilleştirmeyi değil, atlamayı ölçerdi.
        db.WaMessages.Should().HaveCount(2);
        db.WaConversationLabels.Should().ContainSingle();
    }

    [Fact]
    public async Task Label_lands_on_a_conversation_created_in_the_same_batch()
    {
        var (db, job, _, _, _) = Build();

        // Bu numaradan daha önce hiç mesaj yok → sohbet bu partide oluşur ve
        // etiket ondan SONRAKİ ayrı kayıtta yazılır; FK yine tutmalı.
        db.WaConversations.Should().BeEmpty();

        await job.ProcessAsync(MediaPayload("wamid.new", "document", from: "905339998877"));

        var convo = await db.WaConversations.SingleAsync();
        var link = await db.WaConversationLabels.SingleAsync();
        link.ConversationId.Should().Be(convo.Id);
    }

    [Fact]
    public async Task Echo_of_our_own_document_does_not_raise_the_label()
    {
        var (db, job, _, _, _) = Build();

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

        // Gövde ayrıştırılamasaydı da etiket çıkmazdı; mesajın gerçekten
        // işlendiğini görelim ki test echo kararını ölçsün, ayrıştırma hatasını değil.
        db.WaMessages.Should().ContainSingle();
        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_rule_no_label_is_written()
    {
        var (db, job, _, _, _) = Build();
        db.WaLabelRules.RemoveRange(db.WaLabelRules);
        db.SaveChanges();

        await job.ProcessAsync(MediaPayload("wamid.norule", "document"));

        db.WaMessages.Should().ContainSingle();
        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Pdf_dekont_is_parsed_and_stored_next_to_the_message()
    {
        var (db, job, licenseId, _, parser) = Build(FakeMedia.ReturningPdf([9, 9, 9]));

        await job.ProcessAsync(MediaPayload("wamid.pdf", "document"));

        parser.Calls.Should().Be(1);

        // Not: InMemory sağlayıcısı FK kısıtı uygulamaz ve ekleme sırasını
        // topolojik olarak çözmez; bu yüzden buradaki hiçbir doğrulama
        // WhatsAppInboundJob'daki `extraction.WaMessage = message` bağının
        // SQL Server'da gerekli olduğunu KANITLAMAZ. O bağ silinse bu testler
        // yine yeşil kalır, prod'da PK=FK ihlali verirdi.
        var msg = await db.WaMessages.SingleAsync();
        var row = await db.WaDekontExtractions.SingleAsync();
        row.WaMessageId.Should().Be(msg.Id);
        row.LicenseId.Should().Be(licenseId);
        row.PayerName.Should().Be("AYŞE YILMAZ");
        row.Amount.Should().Be(1250.50m);
        row.ParserConfidence.Should().Be("High");
    }

    [Fact]
    public async Task Image_dekont_is_labeled_but_not_parsed()
    {
        // Görsel dekont AI gerektirir — ayrı faz. Etiket yine de yapışır.
        var (db, job, _, _, parser) = Build(FakeMedia.ReturningImage());

        await job.ProcessAsync(MediaPayload("wamid.img", "image"));

        // Medyanın GERÇEKTEN indiğini önce doğrula: indirici hiç çağrılmasaydı
        // da parser 0 kalırdı ve test "görsel ayrıştırılmıyor" kararını değil,
        // indirmenin atlandığını ölçerdi.
        (await db.WaMessages.SingleAsync()).MediaR2Key.Should().Be("k.jpg");
        parser.Calls.Should().Be(0);
        db.WaConversationLabels.Should().ContainSingle();
        db.WaDekontExtractions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_document_without_pdf_bytes_is_still_saved_and_labeled()
    {
        // Medya indirici kayıtlı değil → bayt yok. Mesaj yine kaydedilmeli,
        // etiket yine yapışmalı; yalnız özet çıkmaz.
        var (db, job, _, _, parser) = Build();

        await job.ProcessAsync(MediaPayload("wamid.nomedia", "document"));

        parser.Calls.Should().Be(0);
        db.WaMessages.Should().ContainSingle();
        db.WaConversationLabels.Should().ContainSingle();
        db.WaDekontExtractions.Should().BeEmpty();
    }
}
