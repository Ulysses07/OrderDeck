using LiveDeck.LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Services.Licensing;

public sealed class LicenseValidator
{
    private readonly LicenseDbContext _db;

    public LicenseValidator(LicenseDbContext db) => _db = db;

    public async Task<ValidationResult?> ValidateAsync(
        string licenseKey, string hardwareFingerprint, Guid customerId, CancellationToken ct = default)
    {
        var license = await _db.Licenses
            .Include(l => l.Activations)
            .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey && l.CustomerId == customerId, ct);

        if (license is null) return null;

        var now = DateTimeOffset.UtcNow;
        var remainingDays = (int)Math.Max(0, Math.Ceiling((license.ExpiresAt - now).TotalDays));

        if (license.RevokedAt is not null)
            return new ValidationResult(LicenseStatus.Revoked, license.ExpiresAt, 0, license.SkuCode, null);

        if (license.ExpiresAt < now)
            return new ValidationResult(LicenseStatus.Expired, license.ExpiresAt, 0, license.SkuCode, null);

        var activeActivations = license.Activations
            .Where(a => a.DeactivatedAt is null).ToList();
        var thisDevice = activeActivations
            .FirstOrDefault(a => a.HardwareFingerprint == hardwareFingerprint);

        var slotInfo = new SlotInfo(activeActivations.Count, license.ActivationSlots, thisDevice is not null);

        if (thisDevice is null)
            return new ValidationResult(LicenseStatus.NotActivated, license.ExpiresAt, remainingDays, license.SkuCode, slotInfo);

        return new ValidationResult(LicenseStatus.Active, license.ExpiresAt, remainingDays, license.SkuCode, slotInfo);
    }
}
