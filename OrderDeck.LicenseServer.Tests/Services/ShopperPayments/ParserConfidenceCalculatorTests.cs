using FluentAssertions;
using OrderDeck.PdfParsing;
using OrderDeck.LicenseServer.Services.ShopperPayments;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperPayments;

public class ParserConfidenceCalculatorTests
{
    private static PdfDekontParser.ParseResult Build(
        string? payer = null, decimal? amount = null,
        DateTime? paidAt = null, string? referansNo = null,
        string? recipientIban = null)
        => new(payer, amount, paidAt, referansNo, "pdfhash", "raw", recipientIban, null);

    [Fact]
    public void Five_fields_present_returns_High()
    {
        var r = Build("Ali", 100m, DateTime.UtcNow, "REF", "TR33...");
        ParserConfidenceCalculator.Compute(r).Should().Be("High");
    }

    [Fact]
    public void Four_fields_present_returns_High()
    {
        var r = Build("Ali", 100m, DateTime.UtcNow, "REF");
        ParserConfidenceCalculator.Compute(r).Should().Be("High");
    }

    [Fact]
    public void Three_fields_returns_Medium()
    {
        var r = Build("Ali", 100m, DateTime.UtcNow);
        ParserConfidenceCalculator.Compute(r).Should().Be("Medium");
    }

    [Fact]
    public void Two_fields_returns_Medium()
    {
        var r = Build("Ali", 100m);
        ParserConfidenceCalculator.Compute(r).Should().Be("Medium");
    }

    [Fact]
    public void One_field_returns_Low()
    {
        var r = Build("Ali");
        ParserConfidenceCalculator.Compute(r).Should().Be("Low");
    }

    [Fact]
    public void Zero_fields_returns_Low()
    {
        var r = Build();
        ParserConfidenceCalculator.Compute(r).Should().Be("Low");
    }

    [Fact]
    public void Whitespace_only_strings_count_as_missing()
    {
        var r = Build(payer: "   ", referansNo: "");
        ParserConfidenceCalculator.Compute(r).Should().Be("Low");
    }
}
