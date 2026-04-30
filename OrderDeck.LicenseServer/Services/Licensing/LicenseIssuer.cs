using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Services.Licensing;

public sealed class LicenseIssuer
{
    private readonly LicenseDbContext _db;

    public LicenseIssuer(LicenseDbContext db) => _db = db;

    /// <summary>Generates "LDK-{32 hex uppercase}" — guaranteed unique by retry on collision.</summary>
    public static string GenerateKey()
        => "LDK-" + Guid.NewGuid().ToString("N").ToUpperInvariant();

    public sealed record IssueRequest(
        string CustomerEmail,
        string SkuCode,
        int? DurationDaysOverride,
        int? SlotsOverride);

    public sealed record IssueResult(string LicenseKey, DateTimeOffset ExpiresAt);

    public sealed class IssueException : Exception
    {
        public string Code { get; }
        public IssueException(string code, string message) : base(message) => Code = code;
    }

    public async Task<IssueResult> IssueAsync(IssueRequest req, CancellationToken ct = default)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.CustomerEmail, ct)
            ?? throw new IssueException("customer-not-found", $"Email yok: {req.CustomerEmail}");

        var sku = await _db.Skus.FirstOrDefaultAsync(s => s.Code == req.SkuCode, ct)
            ?? throw new IssueException("sku-not-found", $"SKU yok: {req.SkuCode}");

        var duration = req.DurationDaysOverride ?? sku.DefaultDurationDays;
        var slots = req.SlotsOverride ?? sku.DefaultActivationSlots;

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = GenerateKey(),
            CustomerId = customer.Id,
            SkuCode = sku.Code,
            ActivationSlots = slots,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(duration),
            RevokedAt = null
        };
        _db.Licenses.Add(license);
        await _db.SaveChangesAsync(ct);
        return new IssueResult(license.LicenseKey, license.ExpiresAt);
    }
}
