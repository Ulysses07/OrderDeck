using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing.Api;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF Settings'teki Iban + AccountHolder bilgisinin LicenseServer'a sync'i.
/// Shopper dekont upload akışında fraud kontrolü (RecipientIban karşılaştırması)
/// için server-side cache. Değişim olmadığında no-op; değişince POST.
///
/// Hosted service startup'ta bir kere + her 5 dakikada bir tetikler. Settings
/// dialog save trigger'ı eklemeye gerek yok — 5min cadence config-class data
/// için yeterli.
///
/// LicenseId resolution: PaymentSyncService ile aynı pattern (key → API /me/licenses
/// → Guid, cached). ICurrentLicenseProvider.CurrentLicenseKey string döner;
/// Guid resolve için GetMyLicensesAsync çağrısı yapılır.
/// </summary>
public sealed class PaymentAccountSyncService
{
    private readonly LicenseApiClient _api;
    private readonly SettingsStore _settingsStore;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<PaymentAccountSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    private string? _lastSyncedIban;
    private string? _lastSyncedAccountHolder;

    public PaymentAccountSyncService(
        LicenseApiClient api,
        SettingsStore settingsStore,
        ICurrentLicenseProvider licenseProvider,
        ILogger<PaymentAccountSyncService> log)
    {
        _api = api;
        _settingsStore = settingsStore;
        _licenseProvider = licenseProvider;
        _log = log;
    }

    public async Task SyncIfChangedAsync(CancellationToken ct)
    {
        var licenseId = await ResolveLicenseIdAsync(ct);
        if (licenseId is null)
        {
            _log.LogDebug("PaymentAccount sync skipped — no active license resolved");
            return;
        }

        var settings = _settingsStore.Load();
        // Iban + AccountHolder live on the nested PaymentSettings block.
        var iban   = string.IsNullOrWhiteSpace(settings.Payment.Iban)          ? null : settings.Payment.Iban.Trim();
        var holder = string.IsNullOrWhiteSpace(settings.Payment.AccountHolder) ? null : settings.Payment.AccountHolder.Trim();

        if (iban == _lastSyncedIban && holder == _lastSyncedAccountHolder)
        {
            _log.LogDebug("PaymentAccount sync skipped — no change since last push");
            return;
        }

        try
        {
            await _api.SyncPaymentAccountAsync(licenseId.Value, iban, holder, ct);
            _lastSyncedIban           = iban;
            _lastSyncedAccountHolder  = holder;
            _log.LogInformation(
                "PaymentAccount synced (iban={IbanLen} chars, holder={Holder})",
                iban?.Length ?? 0, holder is null ? "(null)" : "(set)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "PaymentAccount sync failed; will retry on next interval");
        }
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
            _log.LogDebug(ex, "License resolve failed for PaymentAccount sync");
            return null;
        }
    }
}
