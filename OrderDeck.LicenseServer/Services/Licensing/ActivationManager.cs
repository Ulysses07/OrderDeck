using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Services.Licensing;

public sealed class ActivationManager
{
    private readonly LicenseDbContext _db;

    public ActivationManager(LicenseDbContext db) => _db = db;

    public sealed class ActivationException : Exception
    {
        public string Code { get; }
        public ActivationException(string code, string message) : base(message) => Code = code;
    }

    public async Task<Activation> ActivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, string? machineName,
        CancellationToken ct = default)
    {
        var license = await _db.Licenses
            .Include(l => l.Activations)
            .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey && l.CustomerId == customerId, ct);

        if (license is null)
            throw new ActivationException("license-not-found", "Lisans bulunamadı");

        if (license.RevokedAt is not null)
            throw new ActivationException("license-revoked", "Lisans iptal edilmiş");

        if (license.ExpiresAt < DateTimeOffset.UtcNow)
            throw new ActivationException("license-expired", "Lisans süresi dolmuş");

        // Same device already active? Update LastSeenAt.
        var existing = license.Activations
            .FirstOrDefault(a => a.HardwareFingerprint == hardwareFingerprint && a.DeactivatedAt is null);
        if (existing is not null)
        {
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var activeCount = license.Activations.Count(a => a.DeactivatedAt is null);
        if (activeCount >= license.ActivationSlots)
            throw new ActivationException("slot-full", $"Slot dolu ({activeCount}/{license.ActivationSlots})");

        var activation = new Activation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            HardwareFingerprint = hardwareFingerprint,
            MachineName = machineName,
            ActivatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            DeactivatedAt = null
        };
        _db.Activations.Add(activation);
        await _db.SaveChangesAsync(ct);
        return activation;
    }

    public async Task<bool> DeactivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, CancellationToken ct = default)
    {
        var activation = await _db.Activations
            .Include(a => a.License)
            .FirstOrDefaultAsync(a =>
                a.License.LicenseKey == licenseKey &&
                a.License.CustomerId == customerId &&
                a.HardwareFingerprint == hardwareFingerprint &&
                a.DeactivatedAt == null, ct);

        if (activation is null) return false;

        activation.DeactivatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> HeartbeatAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, CancellationToken ct = default)
    {
        var activation = await _db.Activations
            .Include(a => a.License)
            .FirstOrDefaultAsync(a =>
                a.License.LicenseKey == licenseKey &&
                a.License.CustomerId == customerId &&
                a.HardwareFingerprint == hardwareFingerprint &&
                a.DeactivatedAt == null, ct);

        if (activation is null) return false;

        activation.LastSeenAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ForceDeactivateAsync(Guid activationId, CancellationToken ct = default)
    {
        var activation = await _db.Activations.FirstOrDefaultAsync(a => a.Id == activationId, ct);
        if (activation is null || activation.DeactivatedAt is not null) return false;
        activation.DeactivatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
