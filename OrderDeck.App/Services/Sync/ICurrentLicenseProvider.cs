namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Thin abstraction over <c>LicenseService.CurrentLicense.LicenseKey</c> so
/// <c>PaymentSyncService</c> can be unit-tested without instantiating the
/// real LicenseService (sealed class, heavy ctor dependencies). Production
/// implementation in <c>CurrentLicenseProvider</c>.
/// </summary>
public interface ICurrentLicenseProvider
{
    string? CurrentLicenseKey { get; }
}
