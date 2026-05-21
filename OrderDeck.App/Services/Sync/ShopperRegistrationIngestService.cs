using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Server'da shopper register/join sırasında otomatik oluşturulan
/// WpfCustomerProjection kayıtlarını WPF lokal Customer tablosuna ingest eder.
/// Bidirectional sync'i tamamlar:
///   WPF → Server (WpfCustomerProjectionSyncService — Faz 0c-2)
///   Server → WPF (THIS — Faz 0c-3)
///
/// Eğer WPF'te aynı (Platform, Username) ile bir Customer zaten varsa skip
/// (idempotent). Aksi halde yeni Customer kaydı insert eder.
/// </summary>
public sealed class ShopperRegistrationIngestService
{
    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<ShopperRegistrationIngestService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public ShopperRegistrationIngestService(
        LicenseApiClient api,
        CustomerRepository customers,
        SettingsStore settingsStore,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<ShopperRegistrationIngestService> log)
    {
        _api = api;
        _customers = customers;
        _settingsStore = settingsStore;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
    }

    public async Task<int> IngestOnceAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrEmpty(licenseKey)) return 0;

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct);
        if (licenseId is null) return 0;

        var settings = _settingsStore.Load();
        var watermark = settings.LastShopperIngestAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(settings.LastShopperIngestAt)
            : DateTimeOffset.MinValue;

        try
        {
            var items = await _api.GetWpfCustomersSinceAsync(licenseId.Value, watermark, take: 100, ct);
            if (items.Count == 0) return 0;

            var inserted = 0;
            foreach (var item in items)
            {
                // Idempotent: skip if WPF already has a Customer with this (Platform, Username)
                var existing = _customers.FindByPlatformAndUsername(item.Platform, item.Username);
                if (existing is not null) continue;

                var nowUnix = _clock.UnixNow();
                _customers.Insert(new Customer(
                    Id: item.Id.ToString("N"),
                    Platform: item.Platform,
                    Username: item.Username,
                    DisplayName: item.FullName,
                    AvatarUrl: null,
                    FirstSeenAt: nowUnix,
                    LastSeenAt: nowUnix,
                    IsBlacklisted: false,
                    BlacklistReason: null,
                    Notes: null,
                    TotalLabelsPrinted: 0,
                    TotalAmount: 0m,
                    BlacklistedAt: null,
                    Address: item.Address,
                    Phone: item.Phone));
                inserted++;
            }

            // Advance watermark to the latest UpdatedAt in this batch
            var newWatermark = items.Max(i => i.UpdatedAt).ToUnixTimeSeconds();
            settings.LastShopperIngestAt = newWatermark;
            _settingsStore.Save(settings);

            if (inserted > 0)
                _log.LogInformation("Ingested {Count} shopper registrations as new customers", inserted);
            return inserted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "ShopperRegistrationIngest failed; will retry");
            return 0;
        }
    }

    // ─── LicenseId resolution (same caching pattern as WpfCustomerProjectionSyncService) ──

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return null;

            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "License resolve failed for shopper registration ingest");
            return null;
        }
    }
}
