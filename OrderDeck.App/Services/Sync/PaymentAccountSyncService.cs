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
/// <para>R11-D02: kayıt POST'tan ÖNCE "belirsiz" olarak açılır, başarıda
/// kesinleşir. Sunucuya ulaşıp yanıtı kaybolan gönderim aksi hâlde hiç iz
/// bırakmıyor, operatörün sonraki boşaltma niyeti "kayıt yok + yerel boş"
/// dalında sessizce atlanıyordu.</para>
///
/// <para>R12-D03: kayıt, kendisini yazan AYAR DOSYASININ kimliğiyle
/// damgalanır. Kaydın değeri güvenilirdir ve yedekle taşınması doğrudur;
/// güvenilmez olan, yerel boşluğun niyet mi yokluk mu olduğu — ve o kanıt
/// yalnız ayar dosyasında yaşar, yedeğe ise girmez (BackupService sadece
/// orderdeck.db'yi zipler). Damga tutmuyorsa kayıt bu dosya için
/// karşılaştırma tabanı değildir: boş yerel değer "boşaltıldı" diye
/// okunamaz, örtüşen dolu değer ise gönderimsiz sahiplenilir.</para>
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
        var installationId = EnsureInstallationId(settings);

        // R10-D04: karşılaştırma tabanı kalıcı son-başarılı-gönderim kaydı.
        var state = _stateRepo.Get(licenseKey);

        // R12-D03: kaydın DEĞERİ güvenilir (sunucunun bu lisans için ne bildiğini
        // söyler, yedekle taşınması da doğrudur); güvenilmez olan şey, YEREL
        // boşluğun niyet mi yoksa yokluk mu olduğu. O kanıt ayar dosyasında
        // yaşar ve yedeğe girmez — bu yüzden satır, kendisini yazan dosyanın
        // kimliğiyle damgalanıyor. Damga tutmuyorsa (yedekten dönen DB, silinmiş
        // ya da bozulup karantinaya alınmış ayar dosyası) bu dosya bu lisans
        // için hiçbir şey doğrulamamıştır.
        var ackIsOurs = state is not null
            && string.Equals(state.InstallationId, installationId, StringComparison.Ordinal);

        if (iban is null && holder is null && !ackIsOurs)
        {
            // Yapılandırılmamış bir ayar bloğu "boşalt" diyemez. "Kayıt yok" bu
            // kuralın özel hâli (taze kurulum / göç 041 öncesi geçmiş / hiç
            // görülmemiş hedef — R10-D03); körlemesine null-POST meşru bir uzak
            // hesabı, üstelik dekont IBAN kontrolünün dayanağını silerdi (AC46).
            _log.LogDebug(
                "PaymentAccount sync skipped — local values empty and no ack from this settings file");
            return;
        }

        if (ackIsOurs && state!.PendingSince is null
         && iban == state.Iban && holder == state.AccountHolder)
        {
            // Yalnız DOĞRULANMIŞ bir taban karşılaştırmaya elverir. PendingSince
            // doluysa değerlerin sunucuya işlenip işlenmediğini bilmiyoruz —
            // karşılaştırmak "atla" demek olurdu (R11-D02).
            _log.LogDebug("PaymentAccount sync skipped — no change since last push");
            return;
        }

        if (!ackIsOurs && state is { PendingSince: null }
         && iban == state.Iban && holder == state.AccountHolder)
        {
            // Yerel yapılandırma kaydın söylediğiyle örtüşüyor: sunucuya
            // gidecek bir şey yok, ama satırı sahiplenmeliyiz — yoksa bu
            // cihazdaki SONRAKİ bilinçli boşaltma "yerel boş + damga tutmuyor"
            // dalına düşüp sessizce atlanır. Sağlıklı kurulumda göç 044'ün
            // damgasız satırları da bu turda kapanır.
            _stateRepo.Adopt(licenseKey, installationId);
            _log.LogDebug("PaymentAccount ack adopted by this settings file — values already match");
            return;
        }

        // R11-D02: denemenin kendisi, sonucundan ÖNCE kalıcılaşır. Sunucuya
        // ULAŞIP yanıtı kaybolan bir gönderim aksi hâlde hiç iz bırakmıyordu;
        // operatör sonra hesabı boşaltıp uygulamayı yeniden başlattığında
        // "satır yok + yerel boş" dalına düşülüp atlanıyor, kaldırılmak istenen
        // hesap sunucuda kalıyordu.
        _stateRepo.MarkPending(licenseKey, iban, holder, DateTimeOffset.UtcNow, installationId);

        try
        {
            await _api.SyncPaymentAccountAsync(licenseId.Value, iban, holder, ct);
            // Değerler yalnız BAŞARIDA kesinleşir (PendingSince NULL'a çekilir);
            // hata yolunda satır belirsiz kalır ve sonraki tur koşulsuz gönderir.
            _stateRepo.Upsert(licenseKey, iban, holder, DateTimeOffset.UtcNow, installationId);
            _log.LogInformation(
                "PaymentAccount synced (iban={IbanLen} chars, holder={Holder})",
                iban?.Length ?? 0, holder is null ? "(null)" : "(set)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "PaymentAccount sync failed; will retry on next interval");
        }
    }

    /// <summary>
    /// Ayar dosyasının kimliği; yoksa üretilip kalıcılaştırılır. Yedeğin
    /// dışında yaşadığı için yeni cihazda/silinmiş/karantinaya alınmış dosyada
    /// yeniden üretilir — R12-D03 kapısının dayandığı olgu tam olarak budur.
    /// </summary>
    private string EnsureInstallationId(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.InstallationId))
            return settings.InstallationId;

        var id = Guid.NewGuid().ToString("N");
        _settingsStore.Update(s => s.InstallationId = id);
        return id;
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
