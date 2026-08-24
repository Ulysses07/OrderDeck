using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Push;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.PdfParsing;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperPayments;

// ──────────────────────────────────────────────────────────────────────────────
// Fakes
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Configurable fake parser — returns canned ParseResult (or throws).</summary>
internal sealed class FakePdfDekontParser : IPdfDekontParser
{
    private readonly PdfDekontParser.ParseResult _result;
    public bool ThrowOnParse { get; set; }

    public FakePdfDekontParser(PdfDekontParser.ParseResult result) => _result = result;

    public PdfDekontParser.ParseResult Parse(byte[] pdfBytes)
    {
        if (ThrowOnParse) throw new InvalidOperationException("Simulated parse failure");
        return _result;
    }
}

/// <summary>Capture-only notification sender for test assertions.</summary>
internal sealed class CapturingNotificationSender : INotificationSender
{
    public record Notification(Guid CustomerId, string Title, string Body, IReadOnlyDictionary<string, string>? Data);
    public List<Notification> Sent { get; } = new();
    public bool ThrowOnSend { get; set; }

    public Task SendToCustomerAsync(
        Guid customerId,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default)
    {
        if (ThrowOnSend) throw new InvalidOperationException("Push failed");
        Sent.Add(new Notification(customerId, title, body, data));
        return Task.CompletedTask;
    }

    public Task SendToShoppersAsync(
        IReadOnlyCollection<Guid> shopperIds,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default) => Task.CompletedTask;
}

// ──────────────────────────────────────────────────────────────────────────────
// Test byte constants
// ──────────────────────────────────────────────────────────────────────────────

file static class PdfBytes
{
    // Valid: starts with %PDF-
    public static readonly byte[] Valid =
    {
        0x25, 0x50, 0x44, 0x46, 0x2D, // %PDF-
        0x31, 0x2E, 0x34, 0x0A,        // 1.4\n
        0x00, 0x01, 0x02, 0x03         // junk tail
    };

    // Invalid: no PDF magic
    public static readonly byte[] Invalid = { 0xDE, 0xAD, 0xBE, 0xEF };
}

// ──────────────────────────────────────────────────────────────────────────────
// Default parse result helpers
// ──────────────────────────────────────────────────────────────────────────────

file static class ParseResults
{
    /// <summary>All fields set — confidence High.</summary>
    public static PdfDekontParser.ParseResult FullHigh(
        string pdfHash = "aabbccdd",
        string? recipientIban = "TR330006100519786457841326") =>
        new(
            PayerName: "RIDVAN ÖZCAN",
            Amount: 500m,
            PaidAt: new DateTime(2026, 5, 1, 12, 0, 0),
            ReferansNo: "123456789",
            PdfHash: pdfHash,
            RawText: "RIDVAN ÖZCAN Tutar 500,00 TL",
            RecipientIban: recipientIban,
            RecipientName: "TEST ALICI");

    /// <summary>Only RawText set — confidence Low.</summary>
    public static PdfDekontParser.ParseResult LowConfidence(string pdfHash = "lowconfhash") =>
        new(
            PayerName: null,
            Amount: null,
            PaidAt: null,
            ReferansNo: null,
            PdfHash: pdfHash,
            RawText: "unreadable content",
            RecipientIban: null,
            RecipientName: null);
}

// ──────────────────────────────────────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class ShopperPaymentSubmissionServiceTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"sps-{Guid.NewGuid():N}")
            .Options);

    private static Customer SeedCustomer(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@test.com",
            Name = "Test Customer",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        return customer;
    }

    private static Shopper SeedShopper(LicenseDbContext db, string? tc = null)
    {
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Test Shopper",
            Phone = $"+9050{Guid.NewGuid():N}".Substring(0, 13),
            PasswordHash = "x",
            Address = "Test Address",
            Tc = tc,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);
        return shopper;
    }

    private static License SeedLicense(LicenseDbContext db, Guid customerId, string? iban = null)
    {
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant(),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            PaymentIban = iban,
        };
        db.Licenses.Add(license);
        return license;
    }

    private static Sku SeedSku(LicenseDbContext db)
    {
        var sku = new Sku
        {
            Code = "STD",
            DisplayName = "Standard",
            DefaultDurationDays = 365,
            DefaultActivationSlots = 1,
        };
        db.Skus.Add(sku);
        return sku;
    }

    private static ShopperPaymentSubmissionService BuildService(
        LicenseDbContext db,
        IPdfDekontParser parser,
        INotificationSender? notif = null)
    {
        return new ShopperPaymentSubmissionService(
            db,
            new ShopperPaymentRateLimiter(db),
            new StubShopperPaymentStorage(),
            parser,
            notif ?? new CapturingNotificationSender(),
            NullLogger<ShopperPaymentSubmissionService>.Instance);
    }

    private static SubmitInput MakeInput(
        Guid shopperId,
        Guid licenseId,
        byte[]? pdfBytes = null,
        decimal? overrideAmount = null,
        string? overridePayerName = null,
        DateTimeOffset? overridePaidAt = null,
        string? overrideReferansNo = null,
        string userAgent = "TestAgent/1.0") =>
        new SubmitInput(
            ShopperId: shopperId,
            LicenseId: licenseId,
            PdfBytes: pdfBytes ?? PdfBytes.Valid,
            OverrideAmount: overrideAmount,
            OverridePayerName: overridePayerName,
            OverridePaidAt: overridePaidAt,
            OverrideReferansNo: overrideReferansNo,
            IpAddress: "1.2.3.4",
            UserAgent: userAgent);

    // ─── 1. Happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task Happy_path_full_parse_no_fraud_flags()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: "TR330006100519786457841326");
        await db.SaveChangesAsync();

        var parseResult = ParseResults.FullHigh(recipientIban: "TR330006100519786457841326");
        var storage = new StubShopperPaymentStorage();
        var svc = new ShopperPaymentSubmissionService(
            db,
            new ShopperPaymentRateLimiter(db),
            storage,
            new FakePdfDekontParser(parseResult),
            new CapturingNotificationSender(),
            NullLogger<ShopperPaymentSubmissionService>.Instance);

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        // Result checks
        result.FraudFlags.Should().BeEmpty();
        result.ParserConfidence.Should().Be("High");
        result.PaymentId.Should().NotBeEmpty();

        // DB: payment + audit rows exist
        var payment = await db.Payments.FindAsync(result.PaymentId);
        payment.Should().NotBeNull();
        payment!.LicenseId.Should().Be(license.Id);
        payment.ShopperId.Should().Be(shopper.Id);

        var audit = await db.PaymentSubmissionAudits.FirstAsync(a => a.PaymentId == result.PaymentId);
        audit.Should().NotBeNull();

        // Storage: bytes uploaded
        storage.Contains(payment.MediaObjectKey!).Should().BeTrue();
        storage.GetBytes(payment.MediaObjectKey!)!.Should().BeEquivalentTo(PdfBytes.Valid);
    }

    // ─── 2. Magic byte mismatch ──────────────────────────────────────────────

    [Fact]
    public async Task Magic_byte_mismatch_throws_400_invalid_pdf()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id);
        await db.SaveChangesAsync();

        var svc = BuildService(db, new FakePdfDekontParser(ParseResults.FullHigh()));

        var act = async () => await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, pdfBytes: PdfBytes.Invalid), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 400 && e.ErrorCode == "invalid-pdf");
    }

    // ─── 3. Parser throws ────────────────────────────────────────────────────

    [Fact]
    public async Task Parser_throws_throws_400_invalid_pdf()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id);
        await db.SaveChangesAsync();

        var fake = new FakePdfDekontParser(ParseResults.FullHigh()) { ThrowOnParse = true };
        var svc = BuildService(db, fake);

        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 400 && e.ErrorCode == "invalid-pdf");
    }

    // ─── 4. Rate limit — shopper ─────────────────────────────────────────────

    [Fact]
    public async Task Rate_limit_shopper_exceeded_throws_429()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id);
        await db.SaveChangesAsync();

        // Seed 5 recent audits for this shopper
        for (int i = 0; i < 5; i++)
        {
            db.PaymentSubmissionAudits.Add(new PaymentSubmissionAudit
            {
                Id = Guid.NewGuid(),
                PaymentId = Guid.NewGuid(),
                ShopperId = shopper.Id,
                LicenseId = license.Id,
                IpAddress = "1.2.3.4",
                UserAgent = "test",
                FraudFlags = "",
                ParserConfidence = "High",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i * 5),
            });
        }
        await db.SaveChangesAsync();

        var svc = BuildService(db, new FakePdfDekontParser(ParseResults.FullHigh()));

        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 429 && e.ErrorCode == "shopper-hourly-limit");
    }

    // ─── 5. Rate limit — license ─────────────────────────────────────────────

    [Fact]
    public async Task Rate_limit_license_exceeded_throws_429()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id);
        await db.SaveChangesAsync();

        // Seed 150 audits across different shoppers for the same license
        for (int i = 0; i < 150; i++)
        {
            db.PaymentSubmissionAudits.Add(new PaymentSubmissionAudit
            {
                Id = Guid.NewGuid(),
                PaymentId = Guid.NewGuid(),
                ShopperId = Guid.NewGuid(),
                LicenseId = license.Id,
                IpAddress = "1.2.3.4",
                UserAgent = "test",
                FraudFlags = "",
                ParserConfidence = "High",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-(i % 50)),
            });
        }
        await db.SaveChangesAsync();

        var svc = BuildService(db, new FakePdfDekontParser(ParseResults.FullHigh()));

        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 429 && e.ErrorCode == "license-hourly-limit");
    }

    // ─── 6. PdfHash same-tenant duplicate ────────────────────────────────────

    [Fact]
    public async Task PdfHash_same_tenant_throws_409_duplicate_dekont()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id);
        await db.SaveChangesAsync();

        const string hash = "samehash001";
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ShopperId = shopper.Id,
            PayerName = "TEST",
            Amount = 100m,
            PaidAt = DateTimeOffset.UtcNow,
            ReferansNo = "REF001",
            PdfHash = hash,
            FraudFlags = "",
            ParserConfidence = "High",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var parseResult = ParseResults.FullHigh(pdfHash: hash);
        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 409 && e.ErrorCode == "duplicate-dekont");
    }

    // ─── 7. PdfHash cross-tenant duplicate ───────────────────────────────────

    [Fact]
    public async Task PdfHash_different_tenant_throws_409_cross_tenant()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var otherCustomer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id);
        var otherLicense = SeedLicense(db, otherCustomer.Id);
        await db.SaveChangesAsync();

        const string hash = "crosstenanthash";
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = otherLicense.Id,
            ShopperId = Guid.NewGuid(),
            PayerName = "OTHER",
            Amount = 200m,
            PaidAt = DateTimeOffset.UtcNow,
            ReferansNo = "REF002",
            PdfHash = hash,
            FraudFlags = "",
            ParserConfidence = "High",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var parseResult = ParseResults.FullHigh(pdfHash: hash);
        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 409 && e.ErrorCode == "cross-tenant-duplicate");
    }

    // ─── 8. MetadataHash soft flag ────────────────────────────────────────────

    [Fact]
    public async Task MetadataHash_duplicate_soft_flag()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        // Build a parse result and pre-compute its metadata hash by inserting
        // a payment that will share the same effective fields.
        var parseResult = new PdfDekontParser.ParseResult(
            PayerName: "META DUP PAYER",
            Amount: 300m,
            PaidAt: new DateTime(2026, 4, 1),
            ReferansNo: "METAREF",
            PdfHash: "uniquehash001",
            RawText: "raw",
            RecipientIban: null,
            RecipientName: null);

        // Pre-compute meta hash same way service does
        var metaHash = ComputeMetadataHashForTest(
            parseResult.Amount,
            parseResult.PayerName,
            new DateTimeOffset(parseResult.PaidAt!.Value, TimeSpan.Zero),
            parseResult.ReferansNo,
            parseResult.RecipientIban);

        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ShopperId = Guid.NewGuid(),
            PayerName = "META DUP PAYER",
            Amount = 300m,
            PaidAt = DateTimeOffset.UtcNow,
            ReferansNo = "METAREF",
            PdfHash = "otherhash999",
            MetadataHash = metaHash,
            FraudFlags = "",
            ParserConfidence = "High",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        result.FraudFlags.Should().Contain("metadata-duplicate");
    }

    // ─── 9. IBAN match — no flag ─────────────────────────────────────────────

    [Fact]
    public async Task IBAN_match_no_flag()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: "TR330006100519786457841326");
        await db.SaveChangesAsync();

        var parseResult = ParseResults.FullHigh(recipientIban: "TR330006100519786457841326");
        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        result.FraudFlags.Should().NotContain("iban-mismatch");
    }

    // ─── 10. IBAN mismatch soft flag ──────────────────────────────────────────

    [Fact]
    public async Task IBAN_mismatch_soft_flag()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: "TR330006100519786457841326");
        await db.SaveChangesAsync();

        // Parser returns a DIFFERENT IBAN
        var parseResult = ParseResults.FullHigh(recipientIban: "TR480011100000000107020132");
        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        result.FraudFlags.Should().Contain("iban-mismatch");
    }

    // ─── 11. No baseline IBAN soft flag ──────────────────────────────────────

    [Fact]
    public async Task No_baseline_IBAN_soft_flag()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        var svc = BuildService(db, new FakePdfDekontParser(ParseResults.FullHigh()));

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        result.FraudFlags.Should().Contain("no-iban-baseline");
    }

    // ─── 12. Low confidence flag ─────────────────────────────────────────────

    [Fact]
    public async Task Low_confidence_soft_flag()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        var svc = BuildService(db, new FakePdfDekontParser(ParseResults.LowConfidence()));

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        result.FraudFlags.Should().Contain("low-confidence");
        result.ParserConfidence.Should().Be("Low");
    }

    // ─── 13. Amount > 9990, no TC → throws ───────────────────────────────────

    [Fact]
    public async Task Amount_over_9990_no_TC_throws_400_tc_required()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db, tc: null);   // no TC
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        var parseResult = new PdfDekontParser.ParseResult(
            PayerName: "HIGH AMOUNT",
            Amount: 10000m,
            PaidAt: new DateTime(2026, 5, 1),
            ReferansNo: "HIGHREF",
            PdfHash: "highpdfhash",
            RawText: "high amount raw",
            RecipientIban: null,
            RecipientName: null);

        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 400 && e.ErrorCode == "tc-required");
    }

    // ─── 14. Amount > 9990, TC present → succeeds ────────────────────────────

    [Fact]
    public async Task Amount_over_9990_with_TC_succeeds()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db, tc: "12345678901");   // TC present
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        var parseResult = new PdfDekontParser.ParseResult(
            PayerName: "HIGH AMOUNT",
            Amount: 10000m,
            PaidAt: new DateTime(2026, 5, 1),
            ReferansNo: "HIGHREF2",
            PdfHash: "highpdfhash2",
            RawText: "high amount raw",
            RecipientIban: null,
            RecipientName: null);

        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        result.FraudFlags.Should().NotContain("tc-required");
        result.PaymentId.Should().NotBeEmpty();
    }

    // ─── 15. Client override takes precedence ─────────────────────────────────

    [Fact]
    public async Task Client_override_takes_precedence()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        // Parser returns one set of values; client overrides all
        var parseResult = ParseResults.FullHigh(pdfHash: "overridehash");
        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var overrideAt = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var result = await svc.SubmitAsync(
            MakeInput(
                shopper.Id,
                license.Id,
                overrideAmount: 999m,
                overridePayerName: "OVERRIDE PAYER",
                overridePaidAt: overrideAt,
                overrideReferansNo: "OVERRIDE-REF"),
            default);

        var payment = await db.Payments.FindAsync(result.PaymentId);
        payment!.PayerName.Should().Be("OVERRIDE PAYER");
        payment.Amount.Should().Be(999m);
        payment.PaidAt.Should().Be(overrideAt);
        payment.ReferansNo.Should().Be("OVERRIDE-REF");
    }

    // ─── 16. Storage bytes persisted before DB ────────────────────────────────

    [Fact]
    public async Task R2_upload_bytes_stored()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        var storage = new StubShopperPaymentStorage();
        var svc = new ShopperPaymentSubmissionService(
            db,
            new ShopperPaymentRateLimiter(db),
            storage,
            new FakePdfDekontParser(ParseResults.FullHigh()),
            new CapturingNotificationSender(),
            NullLogger<ShopperPaymentSubmissionService>.Instance);

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        var payment = await db.Payments.FindAsync(result.PaymentId);
        storage.GetBytes(payment!.MediaObjectKey!).Should().BeEquivalentTo(PdfBytes.Valid);
    }

    // ─── 17. PaymentSubmissionAudit row inserted ──────────────────────────────

    [Fact]
    public async Task PaymentSubmissionAudit_row_inserted()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        var parseResult = ParseResults.LowConfidence("auditpdfhash");
        var svc = BuildService(db, new FakePdfDekontParser(parseResult));

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        var audit = await db.PaymentSubmissionAudits
            .FirstOrDefaultAsync(a => a.PaymentId == result.PaymentId);
        audit.Should().NotBeNull();
        audit!.ShopperId.Should().Be(shopper.Id);
        audit.LicenseId.Should().Be(license.Id);
        audit.FraudFlags.Should().Contain("low-confidence");
        audit.ParserConfidence.Should().Be("Low");
        audit.ParserRawText.Should().Be("unreadable content");
        audit.IpAddress.Should().Be("1.2.3.4");
        audit.UserAgent.Should().Be("TestAgent/1.0");
    }

    // ─── 18. Push notification sent to broadcaster ───────────────────────────

    [Fact]
    public async Task Push_notification_sent_to_broadcaster_customer()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: "TR330006100519786457841326");
        await db.SaveChangesAsync();

        var notifSender = new CapturingNotificationSender();
        var parseResult = ParseResults.FullHigh(recipientIban: "TR330006100519786457841326");
        var svc = new ShopperPaymentSubmissionService(
            db,
            new ShopperPaymentRateLimiter(db),
            new StubShopperPaymentStorage(),
            new FakePdfDekontParser(parseResult),
            notifSender,
            NullLogger<ShopperPaymentSubmissionService>.Instance);

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        notifSender.Sent.Should().ContainSingle();
        var notif = notifSender.Sent[0];
        notif.CustomerId.Should().Be(customer.Id);
        notif.Title.Should().Be("Yeni dekont");
        notif.Data.Should().ContainKey("paymentId")
            .WhoseValue.Should().Be(result.PaymentId.ToString());
    }

    // ─── 19. Push failure doesn't propagate ──────────────────────────────────

    [Fact]
    public async Task Push_failure_does_not_throw()
    {
        await using var db = NewDb();
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();

        var throwingSender = new CapturingNotificationSender { ThrowOnSend = true };
        var svc = new ShopperPaymentSubmissionService(
            db,
            new ShopperPaymentRateLimiter(db),
            new StubShopperPaymentStorage(),
            new FakePdfDekontParser(ParseResults.FullHigh()),
            throwingSender,
            NullLogger<ShopperPaymentSubmissionService>.Instance);

        // Should not throw even though push fails
        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);
        await act.Should().NotThrowAsync();
    }

    // ─── 20. Alan sınırları (Y-12) ───────────────────────────────────────────
    //
    // Bu bloğun sorduğu tek soru: sütuna sığmayan bir değer, PDF R2'ye
    // yazılmadan ÖNCE mi durduruluyor? InMemory sağlayıcısı MaxLength'i zaten
    // uygulamıyor, dolayısıyla "SaveChanges patlıyor mu" diye ölçmenin anlamı
    // yok — ölçülen şey davranış: 400 dönüyor ve depoda hiçbir nesne kalmıyor.

    private static (ShopperPaymentSubmissionService Svc, StubShopperPaymentStorage Storage) BuildWithStorage(
        LicenseDbContext db, PdfDekontParser.ParseResult parseResult)
    {
        var storage = new StubShopperPaymentStorage();
        var svc = new ShopperPaymentSubmissionService(
            db,
            new ShopperPaymentRateLimiter(db),
            storage,
            new FakePdfDekontParser(parseResult),
            new CapturingNotificationSender(),
            NullLogger<ShopperPaymentSubmissionService>.Instance);
        return (svc, storage);
    }

    private async Task<(ShopperPaymentSubmissionService Svc, StubShopperPaymentStorage Storage, Shopper Shopper, License License)>
        SeedForValidationAsync(LicenseDbContext db, PdfDekontParser.ParseResult? parseResult = null)
    {
        SeedSku(db);
        var customer = SeedCustomer(db);
        var shopper = SeedShopper(db);
        var license = SeedLicense(db, customer.Id, iban: null);
        await db.SaveChangesAsync();
        var (svc, storage) = BuildWithStorage(db, parseResult ?? ParseResults.FullHigh());
        return (svc, storage, shopper, license);
    }

    [Fact]
    public async Task Cok_uzun_gonderen_adi_yuklemeden_once_reddediliyor()
    {
        await using var db = NewDb();
        var (svc, storage, shopper, license) = await SeedForValidationAsync(db);

        var act = async () => await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, overridePayerName: new string('A', 201)), default);

        var ex = (await act.Should().ThrowAsync<SubmitFailureException>()).Which;
        ex.StatusCode.Should().Be(400);
        ex.ErrorCode.Should().Be("invalid-payer-name");
        storage.Count.Should().Be(0, "doğrulama R2 yüklemesinden önce çalışmalı");
    }

    [Fact]
    public async Task Sinirdaki_gonderen_adi_kabul_ediliyor()
    {
        await using var db = NewDb();
        var (svc, _, shopper, license) = await SeedForValidationAsync(db);

        var name = new string('A', 200);
        var result = await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, overridePayerName: name), default);

        var payment = await db.Payments.FirstAsync(p => p.Id == result.PaymentId);
        payment.PayerName.Should().Be(name);
    }

    [Fact]
    public async Task Cok_uzun_referans_no_yuklemeden_once_reddediliyor()
    {
        await using var db = NewDb();
        var (svc, storage, shopper, license) = await SeedForValidationAsync(db);

        var act = async () => await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, overrideReferansNo: new string('9', 65)), default);

        var ex = (await act.Should().ThrowAsync<SubmitFailureException>()).Which;
        ex.StatusCode.Should().Be(400);
        ex.ErrorCode.Should().Be("invalid-referans-no");
        storage.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("-1")]                              // negatif
    [InlineData("10000000000000000")]               // decimal(18,2) taşması
    [InlineData("100.999")]                         // kuruştan küçük basamak
    public async Task Gecersiz_tutar_yuklemeden_once_reddediliyor(string rawAmount)
    {
        await using var db = NewDb();
        var (svc, storage, shopper, license) = await SeedForValidationAsync(db);

        var amount = decimal.Parse(rawAmount, System.Globalization.CultureInfo.InvariantCulture);
        var act = async () => await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, overrideAmount: amount), default);

        var ex = (await act.Should().ThrowAsync<SubmitFailureException>()).Which;
        ex.StatusCode.Should().Be(400);
        ex.ErrorCode.Should().Be("invalid-amount");
        storage.Count.Should().Be(0);
    }

    [Fact]
    public async Task Gelecek_tarihli_odeme_yuklemeden_once_reddediliyor()
    {
        await using var db = NewDb();
        var (svc, storage, shopper, license) = await SeedForValidationAsync(db);

        var act = async () => await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, overridePaidAt: DateTimeOffset.UtcNow.AddDays(2)), default);

        var ex = (await act.Should().ThrowAsync<SubmitFailureException>()).Which;
        ex.StatusCode.Should().Be(400);
        ex.ErrorCode.Should().Be("invalid-paid-at");
        storage.Count.Should().Be(0);
    }

    [Fact]
    public async Task Saat_kaymasi_toleransi_icindeki_tarih_kabul_ediliyor()
    {
        await using var db = NewDb();
        var (svc, _, shopper, license) = await SeedForValidationAsync(db);

        var act = async () => await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, overridePaidAt: DateTimeOffset.UtcNow.AddHours(2)), default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Asiri_uzun_user_agent_istegi_bozmuyor_kirpiliyor()
    {
        await using var db = NewDb();
        var (svc, _, shopper, license) = await SeedForValidationAsync(db);

        var result = await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, userAgent: new string('U', 4000)), default);

        var audit = await db.PaymentSubmissionAudits.FirstAsync(a => a.PaymentId == result.PaymentId);
        audit.UserAgent.Length.Should().Be(512);
    }

    [Fact]
    public async Task Parser_ciktisi_reddedilmiyor_sinira_kirpiliyor()
    {
        await using var db = NewDb();
        var parseResult = ParseResults.FullHigh() with
        {
            PayerName = new string('B', 400),
            ReferansNo = new string('7', 100),
            RecipientName = new string('C', 400),
        };
        var (svc, _, shopper, license) = await SeedForValidationAsync(db, parseResult);

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        var payment = await db.Payments.FirstAsync(p => p.Id == result.PaymentId);
        payment.PayerName.Length.Should().Be(200);
        payment.ReferansNo.Length.Should().Be(64);
        payment.RecipientName!.Length.Should().Be(200);
    }

    [Fact]
    public async Task Sutuna_sigmayan_parser_tutari_okunamadi_sayiliyor()
    {
        await using var db = NewDb();
        var parseResult = ParseResults.FullHigh() with { Amount = 10_000_000_000_000_000m };
        var (svc, _, shopper, license) = await SeedForValidationAsync(db, parseResult);

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        var payment = await db.Payments.FirstAsync(p => p.Id == result.PaymentId);
        payment.Amount.Should().Be(0m);
    }

    // ─── 21. Reddedilen deneme de oran sınırını tüketiyor ────────────────────
    //
    // Eski davranışta defter satırı YALNIZ başarı yolunda yazılıyordu; yani
    // geçersiz PDF ya da mükerrer dekont gönderen bir istemci sayaca hiç
    // dokunmadan sonsuz kez deneyebiliyordu. Oysa sınırın koruduğu iş — PDF
    // ayrıştırma, bazı yollarda R2 yazması — reddedilen istekte de yapılıyor.

    [Fact]
    public async Task Reddedilen_deneme_deftere_yaziliyor()
    {
        await using var db = NewDb();
        var (svc, _, shopper, license) = await SeedForValidationAsync(db);

        var act = async () => await svc.SubmitAsync(
            MakeInput(shopper.Id, license.Id, pdfBytes: PdfBytes.Invalid), default);
        await act.Should().ThrowAsync<SubmitFailureException>();

        var audit = await db.PaymentSubmissionAudits.SingleAsync();
        audit.PaymentId.Should().BeNull("reddedilen denemede doğmuş bir ödeme yok");
        audit.Outcome.Should().Be("invalid-pdf");
        audit.ShopperId.Should().Be(shopper.Id);
        audit.LicenseId.Should().Be(license.Id);
    }

    [Fact]
    public async Task Bes_reddedilen_deneme_saatlik_butceyi_tuketiyor()
    {
        await using var db = NewDb();
        var (svc, _, shopper, license) = await SeedForValidationAsync(db);

        for (var i = 0; i < 5; i++)
        {
            var bad = async () => await svc.SubmitAsync(
                MakeInput(shopper.Id, license.Id, pdfBytes: PdfBytes.Invalid), default);
            await bad.Should().ThrowAsync<SubmitFailureException>()
                .Where(e => e.ErrorCode == "invalid-pdf");
        }

        // Altıncı istek geçerli bir PDF olsa bile bütçe bitti.
        var act = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        await act.Should().ThrowAsync<SubmitFailureException>()
            .Where(e => e.StatusCode == 429 && e.ErrorCode == "shopper-hourly-limit");
    }

    /// <summary>
    /// 429'un kendisi deftere YAZILMAMALI. Yazılsaydı ısrarla deneyen istemci
    /// kendi penceresini sürekli tazeler, kayan bir saatlik sınır kalıcı bir
    /// yasağa dönüşürdü.
    /// </summary>
    [Fact]
    public async Task Oran_siniri_reddi_deftere_yazilmiyor()
    {
        await using var db = NewDb();
        var (svc, _, shopper, license) = await SeedForValidationAsync(db);

        for (var i = 0; i < 5; i++)
        {
            var bad = async () => await svc.SubmitAsync(
                MakeInput(shopper.Id, license.Id, pdfBytes: PdfBytes.Invalid), default);
            await bad.Should().ThrowAsync<SubmitFailureException>();
        }

        var before = await db.PaymentSubmissionAudits.CountAsync();

        for (var i = 0; i < 3; i++)
        {
            var limited = async () => await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);
            await limited.Should().ThrowAsync<SubmitFailureException>()
                .Where(e => e.StatusCode == 429);
        }

        (await db.PaymentSubmissionAudits.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Basarili_gonderim_outcome_ok_yaziyor()
    {
        await using var db = NewDb();
        var (svc, _, shopper, license) = await SeedForValidationAsync(db);

        var result = await svc.SubmitAsync(MakeInput(shopper.Id, license.Id), default);

        var audit = await db.PaymentSubmissionAudits.SingleAsync();
        audit.PaymentId.Should().Be(result.PaymentId);
        audit.Outcome.Should().Be(SubmissionOutcomes.Ok);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Mirrors the private ComputeMetadataHash in the service.</summary>
    private static string ComputeMetadataHashForTest(
        decimal? amount,
        string? payerName,
        DateTimeOffset? paidAt,
        string? referansNo,
        string? recipientIban)
    {
        var canonical =
            $"{amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}" +
            $"|{payerName ?? ""}" +
            $"|{paidAt?.ToString("O") ?? ""}" +
            $"|{referansNo ?? ""}" +
            $"|{recipientIban ?? ""}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
