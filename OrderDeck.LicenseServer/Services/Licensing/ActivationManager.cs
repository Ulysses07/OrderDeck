using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Services.Licensing;

public sealed class ActivationManager
{
    /// <summary>How many times to retry a SaveChanges that lost the RowVersion race
    /// before surrendering. Above ~3 retries we have bigger problems than concurrency.</summary>
    private const int MaxConcurrencyRetries = 3;

    private readonly LicenseDbContext _db;

    public ActivationManager(LicenseDbContext db) => _db = db;

    public sealed class ActivationException : Exception
    {
        public string Code { get; }
        public ActivationException(string code, string message) : base(message) => Code = code;
    }

    public Task<Activation> ActivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, string? machineName,
        CancellationToken ct = default)
        => ActivateAsync(licenseKey, customerId, hardwareFingerprint, machineName, legacyFingerprint: null, ct);

    /// <summary>Phase 5d: callers can pass the legacy (username-based) fingerprint
    /// alongside the new (SID-based) one. If a row exists for the legacy hash
    /// we silently migrate it forward — the customer keeps their slot without
    /// re-activating after a username rename.</summary>
    public async Task<Activation> ActivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, string? machineName,
        string? legacyFingerprint,
        CancellationToken ct = default)
    {
        // Retry loop guards the slot-allocation race: if two devices try to claim the
        // last slot simultaneously, both load the License with the same RowVersion;
        // whichever SaveChanges first wins, the other gets DbUpdateConcurrencyException
        // and reloads to re-validate the slot count.
        for (var attempt = 0; ; attempt++)
        {
            // Detached state per attempt — Reload doesn't help when we need fresh
            // Activations collection too.
            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;

            var license = await _db.Licenses
                .Include(l => l.Activations)
                .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey && l.CustomerId == customerId, ct);

            if (license is null)
                throw new ActivationException("license-not-found", "Lisans bulunamadı");

            if (license.RevokedAt is not null)
                throw new ActivationException("license-revoked", "Lisans iptal edilmiş");

            if (license.ExpiresAt < DateTimeOffset.UtcNow)
                throw new ActivationException("license-expired", "Lisans süresi dolmuş");

            // Same device already active? Update LastSeenAt — no slot impact, no race.
            var existing = license.Activations
                .FirstOrDefault(a => a.HardwareFingerprint == hardwareFingerprint && a.DeactivatedAt is null);

            // Phase 5d transition: if the new hash didn't match but the legacy one
            // does, migrate that row forward to the new fingerprint. This is the
            // only place we mutate HardwareFingerprint after creation.
            if (existing is null && !string.IsNullOrEmpty(legacyFingerprint))
            {
                existing = license.Activations
                    .FirstOrDefault(a => a.HardwareFingerprint == legacyFingerprint && a.DeactivatedAt is null);
                if (existing is not null)
                {
                    existing.HardwareFingerprint = hardwareFingerprint;
                }
            }

            if (existing is not null)
            {
                existing.LastSeenAt = DateTimeOffset.UtcNow;
                try { await _db.SaveChangesAsync(ct); return existing; }
                catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries) { continue; }
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
            // Touch License so its RowVersion is checked — without this, a parallel
            // INSERT on Activations alone wouldn't detect the slot race.
            license.LastActivationAt = DateTimeOffset.UtcNow;

            try
            {
                await _db.SaveChangesAsync(ct);
                return activation;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                // Another activation got there first — reload and re-check slot count.
                continue;
            }
        }
    }

    public Task<bool> DeactivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, CancellationToken ct = default)
        => DeactivateAsync(licenseKey, customerId, hardwareFingerprint, legacyFingerprint: null, ct);

    public async Task<bool> DeactivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, string? legacyFingerprint,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Phase 5d: accept either fingerprint and migrate the row forward.
            var activation = await FindActivationAsync(licenseKey, customerId, hardwareFingerprint, ct);
            if (activation is null && !string.IsNullOrEmpty(legacyFingerprint))
            {
                activation = await FindActivationAsync(licenseKey, customerId, legacyFingerprint, ct);
                if (activation is not null) activation.HardwareFingerprint = hardwareFingerprint;
            }

            if (activation is null) return false;

            activation.DeactivatedAt = DateTimeOffset.UtcNow;
            // Bump License so a concurrent slot claim sees the freed slot via fresh
            // RowVersion on its next retry.
            activation.License.LastActivationAt = DateTimeOffset.UtcNow;
            try { await _db.SaveChangesAsync(ct); return true; }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;
                continue;
            }
        }
    }

    public Task<bool> HeartbeatAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, CancellationToken ct = default)
        => HeartbeatAsync(licenseKey, customerId, hardwareFingerprint, legacyFingerprint: null, ct);

    public async Task<bool> HeartbeatAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, string? legacyFingerprint,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var activation = await FindActivationAsync(licenseKey, customerId, hardwareFingerprint, ct);
            if (activation is null && !string.IsNullOrEmpty(legacyFingerprint))
            {
                activation = await FindActivationAsync(licenseKey, customerId, legacyFingerprint, ct);
                if (activation is not null) activation.HardwareFingerprint = hardwareFingerprint;
            }

            if (activation is null) return false;

            activation.LastSeenAt = DateTimeOffset.UtcNow;
            try { await _db.SaveChangesAsync(ct); return true; }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;
                continue;
            }
        }
    }

    private Task<Activation?> FindActivationAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, CancellationToken ct)
        => _db.Activations
            .Include(a => a.License)
            .FirstOrDefaultAsync(a =>
                a.License.LicenseKey == licenseKey &&
                a.License.CustomerId == customerId &&
                a.HardwareFingerprint == hardwareFingerprint &&
                a.DeactivatedAt == null, ct);

    public async Task<bool> ForceDeactivateAsync(Guid activationId, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var activation = await _db.Activations
                .Include(a => a.License)
                .FirstOrDefaultAsync(a => a.Id == activationId, ct);
            if (activation is null || activation.DeactivatedAt is not null) return false;
            activation.DeactivatedAt = DateTimeOffset.UtcNow;
            activation.License.LastActivationAt = DateTimeOffset.UtcNow;
            try { await _db.SaveChangesAsync(ct); return true; }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;
                continue;
            }
        }
    }
}
