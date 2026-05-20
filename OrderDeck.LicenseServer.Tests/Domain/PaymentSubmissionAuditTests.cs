using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class PaymentSubmissionAuditTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"audit-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_with_raw_parser_text()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.PaymentSubmissionAudits.Add(new PaymentSubmissionAudit
        {
            Id = id,
            PaymentId = Guid.NewGuid(),
            ShopperId = Guid.NewGuid(),
            IpAddress = "1.2.3.4",
            UserAgent = "OrderDeck-Shopper/1.0 (iOS 17)",
            FraudFlags = "iban-mismatch,low-confidence",
            ParserConfidence = "Low",
            ParserRawText = "Ödeyen: ALİ VELİ\nTutar: 250 TL",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.PaymentSubmissionAudits.SingleAsync(a => a.Id == id);
        loaded.FraudFlags.Should().Contain("iban-mismatch");
        loaded.ParserRawText.Should().StartWith("Ödeyen:");
    }
}
