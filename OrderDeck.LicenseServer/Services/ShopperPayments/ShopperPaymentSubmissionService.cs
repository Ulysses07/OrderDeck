using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Push;
using OrderDeck.PdfParsing;

namespace OrderDeck.LicenseServer.Services.ShopperPayments;

public sealed record SubmitInput(
    Guid ShopperId,
    Guid LicenseId,
    byte[] PdfBytes,
    decimal? OverrideAmount,
    string? OverridePayerName,
    DateTimeOffset? OverridePaidAt,
    string? OverrideReferansNo,
    string IpAddress,
    string UserAgent);

public sealed record SubmitResult(
    Guid PaymentId,
    string[] FraudFlags,
    string ParserConfidence,
    PdfDekontParser.ParseResult ParserResult);

public sealed class SubmitFailureException : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }

    public SubmitFailureException(int statusCode, string errorCode, string? detail = null)
        : base(detail ?? errorCode)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

public sealed class ShopperPaymentSubmissionService
{
    private readonly LicenseDbContext _db;
    private readonly IShopperPaymentRateLimiter _rateLimiter;
    private readonly IShopperPaymentStorage _storage;
    private readonly IPdfDekontParser _parser;
    private readonly INotificationSender _notificationSender;
    private readonly ILogger<ShopperPaymentSubmissionService> _log;

    public ShopperPaymentSubmissionService(
        LicenseDbContext db,
        IShopperPaymentRateLimiter rateLimiter,
        IShopperPaymentStorage storage,
        IPdfDekontParser parser,
        INotificationSender notificationSender,
        ILogger<ShopperPaymentSubmissionService> log)
    {
        _db = db;
        _rateLimiter = rateLimiter;
        _storage = storage;
        _parser = parser;
        _notificationSender = notificationSender;
        _log = log;
    }

    public async Task<SubmitResult> SubmitAsync(SubmitInput input, CancellationToken ct)
    {
        // 1. Magic byte check
        if (input.PdfBytes.Length < 5
            || input.PdfBytes[0] != (byte)'%'
            || input.PdfBytes[1] != (byte)'P'
            || input.PdfBytes[2] != (byte)'D'
            || input.PdfBytes[3] != (byte)'F'
            || input.PdfBytes[4] != (byte)'-')
            throw new SubmitFailureException(400, "invalid-pdf", "PDF magic byte mismatch");

        // 2. Rate limit
        var rlReason = await _rateLimiter.CheckAsync(input.ShopperId, input.LicenseId, ct);
        if (rlReason is not null)
            throw new SubmitFailureException(429, rlReason);

        // 3. Parser
        PdfDekontParser.ParseResult parseResult;
        try
        {
            parseResult = _parser.Parse(input.PdfBytes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDF parse failed for shopper={Shopper}", input.ShopperId);
            throw new SubmitFailureException(400, "invalid-pdf", "PDF parse failed");
        }

        // 4. Confidence
        var confidence = ParserConfidenceCalculator.Compute(parseResult);

        // 5. PdfHash duplicate (cross-tenant + same-tenant)
        var existingByHash = await _db.Payments
            .Where(p => p.PdfHash == parseResult.PdfHash)
            .Select(p => new { p.Id, p.LicenseId, p.ShopperId })
            .FirstOrDefaultAsync(ct);
        if (existingByHash is not null)
        {
            if (existingByHash.LicenseId == input.LicenseId && existingByHash.ShopperId == input.ShopperId)
                throw new SubmitFailureException(409, "duplicate-dekont");
            throw new SubmitFailureException(409, "cross-tenant-duplicate");
        }

        // 6. Effective fields (client override priority)
        var effPayer = input.OverridePayerName ?? parseResult.PayerName;
        var effAmount = input.OverrideAmount ?? parseResult.Amount;
        var effPaidAt = input.OverridePaidAt
            ?? (parseResult.PaidAt.HasValue
                ? new DateTimeOffset(parseResult.PaidAt.Value, TimeSpan.Zero)
                : (DateTimeOffset?)null);
        var effRef = input.OverrideReferansNo ?? parseResult.ReferansNo;

        // 7. MetadataHash (soft duplicate)
        var metadataHash = ComputeMetadataHash(effAmount, effPayer, effPaidAt, effRef, parseResult.RecipientIban);
        var flags = new List<string>();
        var metadataDup = await _db.Payments
            .AnyAsync(p => p.LicenseId == input.LicenseId
                && p.MetadataHash == metadataHash, ct);
        if (metadataDup) flags.Add("metadata-duplicate");

        // 8. TC > 9990 check
        if (effAmount.HasValue && effAmount.Value > 9990m)
        {
            var shopper = await _db.Shoppers
                .Where(s => s.Id == input.ShopperId)
                .Select(s => new { s.Tc })
                .FirstOrDefaultAsync(ct);
            if (shopper?.Tc is null)
                throw new SubmitFailureException(400, "tc-required",
                    "Amount > 9990 TL requires TC kimlik no in profile");
        }

        // 9. IBAN match
        var license = await _db.Licenses
            .Where(l => l.Id == input.LicenseId)
            .Select(l => new { l.PaymentIban, l.CustomerId })
            .FirstOrDefaultAsync(ct);
        if (license?.PaymentIban is null)
        {
            flags.Add("no-iban-baseline");
        }
        else
        {
            var ibanA = NormalizeIban(license.PaymentIban);
            var ibanB = NormalizeIban(parseResult.RecipientIban ?? "");
            if (ibanA != ibanB) flags.Add("iban-mismatch");
        }

        // 10. Low confidence flag
        if (confidence == "Low") flags.Add("low-confidence");

        // 11. Upload to R2
        var objectKey = $"payments/{input.LicenseId:N}/{Guid.NewGuid():N}.pdf";
        await _storage.UploadAsync(objectKey, input.PdfBytes, "application/pdf", ct);

        // 12. Persist Payment + Audit
        var paymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var fraudFlagsString = string.Join(",", flags);

        _db.Payments.Add(new Payment
        {
            Id = paymentId,
            LicenseId = input.LicenseId,
            ShopperId = input.ShopperId,
            PayerName = effPayer ?? "",
            Amount = effAmount ?? 0m,
            PaidAt = effPaidAt ?? now,
            ReferansNo = effRef ?? "",
            Status = PaymentStatus.Pending,
            ShipmentDirective = ShipmentDirective.Normal,
            MediaObjectKey = objectKey,
            MediaContentType = "application/pdf",
            PdfHash = parseResult.PdfHash,
            MetadataHash = metadataHash,
            RecipientIban = parseResult.RecipientIban,
            RecipientName = parseResult.RecipientName,
            FraudFlags = fraudFlagsString,
            ParserConfidence = confidence,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.PaymentSubmissionAudits.Add(new PaymentSubmissionAudit
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            ShopperId = input.ShopperId,
            LicenseId = input.LicenseId,
            IpAddress = input.IpAddress,
            UserAgent = input.UserAgent,
            FraudFlags = fraudFlagsString,
            ParserConfidence = confidence,
            ParserRawText = parseResult.RawText,
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        // 13. Push notification to broadcaster (best-effort, don't throw)
        try
        {
            var title = "Yeni dekont";
            var body = $"{effPayer ?? "(parse edilemedi)"}, {effAmount ?? 0:0.##}₺";
            if (flags.Count > 0) body += $" (uyarılı: {string.Join(", ", flags)})";

            await _notificationSender.SendToCustomerAsync(
                license!.CustomerId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["type"] = "payment",
                    ["paymentId"] = paymentId.ToString(),
                    ["licenseId"] = input.LicenseId.ToString(),
                    ["hasFraudFlags"] = (flags.Count > 0 ? "true" : "false"),
                },
                ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Push fan-out failed for payment={PaymentId}", paymentId);
        }

        return new SubmitResult(paymentId, flags.ToArray(), confidence, parseResult);
    }

    private static string ComputeMetadataHash(
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

    private static string NormalizeIban(string raw)
        => new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
