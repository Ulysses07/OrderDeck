using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF lokal Customer kayıtlarının LicenseServer'a periyodik delta sync'i.
/// Müşteri (shopper) app kullanıcısı bir yayıncıya bağlanırken (LicenseId,
/// Platform, Username) ile match yapılır — bu match için server-side projection
/// gerekli. Server retroactive match'i sync endpoint'inde drive-by yapar.
///
/// Watermark: SettingsStore.LastCustomerProjectionSyncAt (long unix seconds).
/// Batch: 500/call. Multi-batch loop until exhausted within a single tick.
///
/// Customer.DisplayName → WpfCustomerSyncItem.FullName mapping: WPF lokal
/// kayıtlarında FullName alanı yok; DisplayName en yakın eşdeğer.
///
/// LicenseId resolution: GetMyLicensesAsync ile key → Guid (cached).
/// </summary>
public sealed class WpfCustomerProjectionSyncService
{
    private const int BatchSize = 500;

    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<WpfCustomerProjectionSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public WpfCustomerProjectionSyncService(
        LicenseApiClient api,
        CustomerRepository customers,
        SettingsStore settingsStore,
        ICurrentLicenseProvider licenseProvider,
        ILogger<WpfCustomerProjectionSyncService> log)
    {
        _api             = api;
        _customers       = customers;
        _settingsStore   = settingsStore;
        _licenseProvider = licenseProvider;
        _log             = log;
    }

    /// <summary>
    /// Single sync tick: runs one or more batches until the repo returns fewer
    /// than BatchSize rows. Returns total customers synced across all batches.
    /// Watermark is advanced in SettingsStore after each successful batch.
    /// On any batch failure the method returns early without advancing further.
    /// </summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var licenseId = await ResolveLicenseIdAsync(ct);
        if (licenseId is null)
        {
            _log.LogDebug("Customer projection sync skipped — no active license resolved");
            return 0;
        }

        var settings     = _settingsStore.Load();
        var watermark    = settings.LastCustomerProjectionSyncAt;
        var totalSynced  = 0;
        var totalMatches = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = _customers.GetUpdatedSince(watermark, BatchSize);
            if (batch.Count == 0) break;

            var items = new List<WpfCustomerSyncItem>(batch.Count);
            foreach (var c in batch)
            {
                if (!Guid.TryParseExact(c.Id, "N", out var customerGuid))
                {
                    _log.LogWarning("Skipping customer with non-GUID Id={Id}", c.Id);
                    continue;
                }
                items.Add(new WpfCustomerSyncItem(
                    Id:        customerGuid,
                    Platform:  c.Platform,
                    Username:  c.Username,
                    // DisplayName is the WPF equivalent of FullName (no separate FullName field)
                    FullName:  c.DisplayName,
                    Phone:     c.Phone,
                    Address:   c.Address,
                    UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(c.LastSeenAt)));
            }

            // If all items in the batch were skipped (invalid GUIDs), advance
            // watermark to prevent an infinite loop, then continue.
            if (items.Count == 0)
            {
                watermark = batch.Max(c => c.LastSeenAt);
                settings.LastCustomerProjectionSyncAt = watermark;
                _settingsStore.Save(settings);
                if (batch.Count < BatchSize) break;
                continue;
            }

            try
            {
                var resp = await _api.SyncWpfCustomersAsync(licenseId.Value, items, ct);
                totalSynced  += resp.Synced;
                totalMatches += resp.RetroactiveMatches;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Customer projection sync batch failed; abandoning this tick");
                return totalSynced; // don't advance watermark on failure
            }

            // Advance watermark to the highest LastSeenAt in this batch.
            var batchMax = batch.Max(c => c.LastSeenAt);
            watermark = batchMax;
            settings.LastCustomerProjectionSyncAt = batchMax;
            _settingsStore.Save(settings);

            if (batch.Count < BatchSize) break; // last page — no more rows
        }

        if (totalSynced > 0)
        {
            _log.LogInformation(
                "Customer projection sync: pushed {Synced} (retro matches {Matches}), watermark→{Watermark}",
                totalSynced, totalMatches, watermark);
        }

        return totalSynced;
    }

    // ─── LicenseId resolution (same caching pattern as other sync services) ──

    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match    = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null) return null;

            _cachedLicenseId  = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "License resolve failed for customer projection sync");
            return null;
        }
    }
}
