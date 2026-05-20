using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class PaymentShopperFieldsTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"payshopper-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_persists_shopper_upload_metadata()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Payments.Add(new Payment
        {
            Id = id,
            LicenseId = Guid.NewGuid(),
            PayerName = "ALİ VELİ",
            Amount = 250m,
            PaidAt = now,
            ReferansNo = "REF-1",
            Status = PaymentStatus.Pending,
            ShipmentDirective = ShipmentDirective.Normal,
            CreatedAt = now,
            UpdatedAt = now,
            ShopperId = Guid.NewGuid(),
            MediaObjectKey = "r2/payments/1.pdf",
            MediaContentType = "application/pdf",
            PdfHash = "sha256-abc",
            MetadataHash = "sha256-meta",
            RecipientIban = "TR33...",
            RecipientName = "BURAK YILMAZ",
            FraudFlags = "iban-mismatch",
            ParserConfidence = "Medium",
            PdfPurgedAt = null,
        });
        await db.SaveChangesAsync();

        var loaded = await db.Payments.SingleAsync(p => p.Id == id);
        loaded.ShopperId.Should().NotBeNull();
        loaded.MediaContentType.Should().Be("application/pdf");
        loaded.PdfHash.Should().Be("sha256-abc");
        loaded.FraudFlags.Should().Be("iban-mismatch");
        loaded.ParserConfidence.Should().Be("Medium");
        loaded.PdfPurgedAt.Should().BeNull();
    }

    [Fact]
    public void PdfHash_is_global_unique_to_prevent_cross_tenant_replay()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(Payment))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 1
                && i.Properties[0].Name == nameof(Payment.PdfHash));
        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
    }
}
