using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.ShopperPayments;

public interface IShopperPaymentRateLimiter
{
    /// <returns>null if allowed; reason string ("shopper-hourly-limit" / "license-hourly-limit") if blocked.</returns>
    Task<string?> CheckAsync(Guid shopperId, Guid licenseId, CancellationToken ct);
}

public sealed class ShopperPaymentRateLimiter : IShopperPaymentRateLimiter
{
    public const int ShopperHourlyLimit = 5;
    public const int LicenseHourlyLimit = 150;

    private readonly LicenseDbContext _db;
    public ShopperPaymentRateLimiter(LicenseDbContext db) => _db = db;

    public async Task<string?> CheckAsync(Guid shopperId, Guid licenseId, CancellationToken ct)
    {
        var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1);

        var shopperRecentCount = await _db.PaymentSubmissionAudits
            .Where(a => a.ShopperId == shopperId && a.CreatedAt > oneHourAgo)
            .CountAsync(ct);
        if (shopperRecentCount >= ShopperHourlyLimit) return "shopper-hourly-limit";

        var licenseRecentCount = await _db.PaymentSubmissionAudits
            .Where(a => a.LicenseId == licenseId && a.CreatedAt > oneHourAgo)
            .CountAsync(ct);
        if (licenseRecentCount >= LicenseHourlyLimit) return "license-hourly-limit";

        return null;
    }
}
