using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.PdfParsing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WaDekontExtractorTests
{
    /// <summary>Gerçek PdfPig'e girmeden ayrıştırıcı sözleşmesini taklit eder.</summary>
    private sealed class FakeParser : IPdfDekontParser
    {
        private readonly PdfDekontParser.ParseResult? _result;
        private readonly Exception? _throw;

        public FakeParser(PdfDekontParser.ParseResult result) => _result = result;
        public FakeParser(Exception ex) => _throw = ex;

        public int Calls { get; private set; }

        public PdfDekontParser.ParseResult Parse(byte[] pdfBytes)
        {
            Calls++;
            if (_throw is not null) throw _throw;
            return _result!;
        }
    }

    private static PdfDekontParser.ParseResult FullResult() => new(
        PayerName: "AYŞE YILMAZ",
        Amount: 1250.50m,
        PaidAt: new DateTime(2026, 8, 18, 14, 30, 0),
        ReferansNo: "REF123456",
        PdfHash: "abc123",
        RawText: "ham metin",
        RecipientIban: "TR330006100519786457841326",
        RecipientName: "EMAR GLOBAL");

    private static WaDekontExtractor Build(IPdfDekontParser parser)
        => new(parser, NullLogger<WaDekontExtractor>.Instance);

    [Fact]
    public void Extracts_all_four_fields_from_a_readable_dekont()
    {
        var licenseId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var row = Build(new FakeParser(FullResult()))
            .TryExtract(licenseId, messageId, [1, 2, 3]);

        row.Should().NotBeNull();
        row!.LicenseId.Should().Be(licenseId);
        row.WaMessageId.Should().Be(messageId);
        row.PayerName.Should().Be("AYŞE YILMAZ");
        row.Amount.Should().Be(1250.50m);
        row.ReferansNo.Should().Be("REF123456");
        row.PdfHash.Should().Be("abc123");
    }

    [Fact]
    public void Dekont_date_is_read_as_Turkish_local_time()
    {
        var row = Build(new FakeParser(FullResult()))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);

        // Dekontta yazan saat yerel saattir; Türkiye 2016'dan beri sabit UTC+3.
        row!.PaidAt.Should().Be(new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void Confidence_is_computed_from_the_parse_result()
    {
        var full = Build(new FakeParser(FullResult()))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);
        full!.ParserConfidence.Should().Be("High");

        var empty = new PdfDekontParser.ParseResult(
            PayerName: null, Amount: null, PaidAt: null, ReferansNo: null,
            PdfHash: "h", RawText: "", RecipientIban: null, RecipientName: null);

        var low = Build(new FakeParser(empty))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);
        low!.ParserConfidence.Should().Be("Low");
    }

    [Fact]
    public void A_broken_pdf_returns_null_instead_of_throwing()
    {
        var row = Build(new FakeParser(new InvalidOperationException("bozuk PDF")))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);

        row.Should().BeNull();
    }

    [Fact]
    public void Empty_bytes_never_reach_the_parser()
    {
        var parser = new FakeParser(FullResult());

        Build(parser).TryExtract(Guid.NewGuid(), Guid.NewGuid(), []).Should().BeNull();
        Build(parser).TryExtract(Guid.NewGuid(), Guid.NewGuid(), null).Should().BeNull();

        parser.Calls.Should().Be(0);
    }
}
