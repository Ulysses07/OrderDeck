using OrderDeck.Core.Settings;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WhatsApp template'lerini LicenseServer'a push'lar (Faz 2, 2026-05-15).
///
/// Pull yok — WPF authoritative (template'ler her zaman lokal AppSettings'te
/// üretilir). Push fire-and-forget: hata fırlarsa kullanıcı SaveAsync akışı
/// etkilenmez; bir sonraki Save'de retry olur.
///
/// SettingsViewModel.Save → SettingsStore.Save (disk) → bu service'i tetikler.
/// </summary>
public sealed class WhatsAppTemplateSyncService
{
    private readonly LicenseApiClient _api;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<WhatsAppTemplateSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public WhatsAppTemplateSyncService(
        LicenseApiClient api,
        ICurrentLicenseProvider licenseProvider,
        ILogger<WhatsAppTemplateSyncService> log)
    {
        _api = api;
        _licenseProvider = licenseProvider;
        _log = log;
    }

    /// <summary>
    /// Verilen template'leri server'a push'lar. Lisans çözümlenemezse no-op.
    /// </summary>
    public async Task PushAsync(string paymentTemplate, string shippingWonTemplate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentTemplate) || string.IsNullOrWhiteSpace(shippingWonTemplate))
            return;

        var licenseId = await ResolveLicenseIdAsync(ct);
        if (licenseId is null)
        {
            _log.LogDebug("WhatsApp template sync skipped — no active license resolved");
            return;
        }

        try
        {
            await _api.PutWhatsAppTemplatesAsync(
                licenseId.Value,
                new WhatsAppTemplatesRequest(paymentTemplate, shippingWonTemplate),
                ct);
            _log.LogInformation("WhatsApp templates pushed to server");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "WhatsApp template push failed; will retry on next save");
        }
    }

    /// <summary>
    /// PaymentSettings'ten okuyup push'lar. UI'da ortak entrypoint.
    /// </summary>
    public Task PushFromSettingsAsync(PaymentSettings payment, CancellationToken ct = default)
        => PushAsync(payment.WhatsAppMessageTemplate, payment.ShippingWonTemplate, ct);

    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null) return null;

            _cachedLicenseId = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "License resolve failed for WhatsApp template sync");
            return null;
        }
    }
}
