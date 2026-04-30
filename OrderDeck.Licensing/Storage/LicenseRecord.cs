namespace OrderDeck.Licensing.Storage;

public sealed record LicenseRecord(
    string LicenseKey,
    string SkuCode,
    DateTimeOffset ExpiresAt,
    int RemainingDaysAtLastCheck,
    DateTimeOffset LastValidatedAt,
    DateTimeOffset LastSuccessfulOnlineAt,
    string LastKnownStatus);
