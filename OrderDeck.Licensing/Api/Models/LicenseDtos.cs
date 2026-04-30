namespace OrderDeck.Licensing.Api.Models;

public sealed record SlotInfoDto(int Used, int Total, bool ThisDeviceActive);

public sealed record ValidateRequest(string LicenseKey, string HardwareFingerprint);
public sealed record ValidateResponse(string Status, DateTimeOffset? ExpiresAt, int? RemainingDays, string? Sku, SlotInfoDto? SlotInfo);

public sealed record ActivateRequest(string LicenseKey, string HardwareFingerprint, string? MachineName);
public sealed record ActivateResponse(Guid ActivationId, DateTimeOffset? ExpiresAt);

public sealed record DeactivateRequest(string LicenseKey, string HardwareFingerprint);
public sealed record HeartbeatRequest(string LicenseKey, string HardwareFingerprint);
public sealed record HeartbeatResponse(string? Status, DateTimeOffset? ExpiresAt);
