using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
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
/// <para>R10-D03/D04: "değişti mi?" kararı süreç-içi önbellekle DEĞİL,
/// lisans anahtarı başına kalıcılaşan son-başarılı-gönderim kaydıyla verilir
/// (<see cref="PaymentAccountSyncStateRepository"/>). Süreç belleği iki
/// arızaya birden açıktı: hedef lisans değişince eski hedefin değerleri
/// "değişmedi" sayılıyordu (D03) ve operatörün boşaltma niyeti yeniden
/// başlatmada kayboluyordu (D04). Kayıt YOK = sunucu durumu bilinmiyor →
/// yerel değer de boşsa dokunulmaz (AC46: taze profil meşru uzak hesabı
/// körlemesine silemez).</para>
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
    private readonly PaymentAccountSyncStateRepository _stateRepo;
    private readonly ILogger<PaymentAccountSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public PaymentAccountSyncService(
        LicenseApiClient api,
        SettingsStore settingsStore,
        ICurrentLicenseProvider licenseProvider,
        PaymentAccountSyncStateRepository stateRepo,
        ILogger<PaymentAccountSyncService> log)
    {
        _api = api;
        _settingsStore = settingsStore;
        _licenseProvider = licenseProvider;
        _stateRepo = stateRepo;
        _log = log;
    }

    public async Task SyncIfChangedAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        var licenseId = await ResolveLicenseIdAsync(ct);
        if (licenseId is null || string.IsNullOrWhiteSpace(licenseKey))
        {
            _log.LogDebug("PaymentAccount sync skipped — no active license resolved");
            return;
        }

        var settings = _settingsStore.Load();
        // Iban + AccountHolder live on the nested PaymentSettings block.
        var iban   = string.IsNullOrWhiteSpace(settings.Payment.Iban)          ? null : settings.Payment.Iban.Trim();
        var holder = string.IsNullOrWhiteSpace(settings.Payment.AccountHolder) ? null : settings.Payment.AccountHolder.Trim();

        // R10-D04: karşılaştırma tabanı kalıcı son-başarılı-gönderim kaydı.
        var state = _stateRepo.Get(licenseKey);
        if (state is null)
        {
            // Sunucunun bu lisans için ne bildiği KAYITLI DEĞİL (taze kurulum,
            // göç 041 öncesi geçmiş ya da hiç görülmemiş hedef — D03). Yerel
            // değer de boşsa dokunma: "kayıt yok" ≠ "boşaltıldı"; körlemesine
            // null-POST meşru bir uzak hesabı silerdi (AC46).
            if (iban is null && holder is null)
            {
                _log.LogDebug(
                    "PaymentAccount sync skipped — server state unknown and local values empty");
                return;
            }
        }
        else if (iban == state.Iban && holder == state.AccountHolder)
        {
            _log.LogDebug("PaymentAccount sync skipped — no change since last push");
            return;
        }

        try
        {
            await _api.SyncPaymentAccountAsync(licenseId.Value, iban, holder, ct);
            // Kayıt yalnız BAŞARIDA ilerler; hata yolunda eski taban kalır ve
            // sonraki tur aynı farkı yeniden görür (boşaltma dahil).
            _stateRepo.Upsert(licenseKey, iban, holder, DateTimeOffset.UtcNow);
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
