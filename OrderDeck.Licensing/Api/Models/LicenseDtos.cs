namespace OrderDeck.Licensing.Api.Models;

public sealed record SlotInfoDto(int Used, int Total, bool ThisDeviceActive);

// LegacyHardwareFingerprint (Phase 5d) is the pre-SID fingerprint shape kept
// alongside the new HardwareFingerprint during the transition window. The
// server uses it to migrate existing Activation rows from the username-based
// hash to the SID-based one without forcing customers to re-activate.
// Remove once all clients have rolled to >=Phase-5d builds for one renewal cycle.

public sealed record ValidateRequest(string LicenseKey, string HardwareFingerprint, string? LegacyHardwareFingerprint = null);
public sealed record ValidateResponse(string Status, DateTimeOffset? ExpiresAt, int? RemainingDays, string? Sku, SlotInfoDto? SlotInfo);

public sealed record ActivateRequest(string LicenseKey, string HardwareFingerprint, string? MachineName, string? LegacyHardwareFingerprint = null);
public sealed record ActivateResponse(Guid ActivationId, DateTimeOffset? ExpiresAt);

public sealed record DeactivateRequest(string LicenseKey, string HardwareFingerprint, string? LegacyHardwareFingerprint = null);
public sealed record HeartbeatRequest(string LicenseKey, string HardwareFingerprint, string? LegacyHardwareFingerprint = null);
public sealed record HeartbeatResponse(string? Status, DateTimeOffset? ExpiresAt);
