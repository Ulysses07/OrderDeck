using OrderDeck.Licensing.Services;

namespace OrderDeck.App.Services.Sync;

internal sealed class CurrentLicenseProvider : ICurrentLicenseProvider
{
    private readonly LicenseService _licenseService;
    public CurrentLicenseProvider(LicenseService licenseService) => _licenseService = licenseService;
    public string? CurrentLicenseKey => _licenseService.CurrentLicense?.LicenseKey;
}
