namespace OrderDeck.LicenseServer.Services.Licensing;

public enum LicenseStatus
{
    Active,
    Expired,
    Revoked,
    NotActivated,
    SlotMismatch
}

public sealed record ValidationResult(
    LicenseStatus Status,
    DateTimeOffset? ExpiresAt,
    int? RemainingDays,
    string? Sku,
    SlotInfo? SlotInfo);

public sealed record SlotInfo(int Used, int Total, bool ThisDeviceActive);
