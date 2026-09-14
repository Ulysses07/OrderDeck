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
///
/// Tek istisna <c>PurgedAt</c>: KVKK silme talebiyle sunucuda temizlenen kişinin
/// yerel kopyası da boşaltılır (bkz. <c>CustomerRepository.ScrubPersonalData</c>).
/// Kişisel veri üç katmanda duruyor — sunucu Shoppers, sunucu
/// WpfCustomerProjections ve burası; ilk ikisini <c>ShopperPurgeService</c>
/// hallediyor, üçüncüsüne ulaşan tek yol bu ingest.
/// </summary>
public sealed class ShopperRegistrationIngestService
{
    private const string CursorName = "shopper-ingest-in";

    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly SyncCursorRepository _cursors;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<ShopperRegistrationIngestService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public ShopperRegistrationIngestService(
        LicenseApiClient api,
        CustomerRepository customers,
        SettingsStore settingsStore,
        SyncCursorRepository cursors,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<ShopperRegistrationIngestService> log)
    {
        _api = api;
        _customers = customers;
        _settingsStore = settingsStore;
        _cursors = cursors;
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

        var (watermark, watermarkId) = LoadCursor(licenseKey);

        try
        {
            var items = await _api.GetWpfCustomersSinceAsync(
                licenseId.Value, watermark, watermarkId, take: 100, ct);
            if (items.Count == 0) return 0;

            var inserted = 0;
            var scrubbed = 0;
            foreach (var item in items)
            {
                var existing = _customers.FindByPlatformAndUsername(item.Platform, item.Username);

                // KVKK silme talebi (Y-13/Y-14 3. katman). Kişisel verinin
                // üçüncü kopyası yayıncının kendi diskinde; sunucu onu
                // silemediği için tek yol bu işaret.
                //
                // Var olan satır TEMİZLENİR ama yeni satır AÇILMAZ: silinen
                // kişinin kaydı bu bilgisayarda hiç yoksa, sunucudan gelen boş
                // satırı burada oluşturmanın hiçbir faydası yok — sadece adı
                // "[Silindi]" olan sahte bir müşteri kartı üretirdi.
                if (item.PurgedAt is not null)
                {
                    if (existing is not null && _customers.ScrubPersonalData(existing.Id) > 0)
                        scrubbed++;
                    continue;
                }

                // Idempotent: skip if WPF already has a Customer with this (Platform, Username)
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

            // İmleç sayfanın son satırı — (UpdatedAt, Id) çifti, sunucunun
            // sayfaladığı sıra. Saniyeye yuvarlanmıyor: yuvarlama, aynı saniyeyi
            // paylaşan satırlarla sayfa dolduğunda imleci başladığı yere
            // döndürüp ilerlemeyi büsbütün durduruyordu.
            // R3-01: imleç sunucunun teslim ettiği SON satırdan okunur — yeniden
            // SIRALAMA YOK. Sunucu SQL Server'ın uniqueidentifier sırasıyla
            // sayfalıyor; .NET Guid.CompareTo farklı bir sıra üretir (SQL
            // karşılaştırmaya son 6 bayttan başlar). İstemci kendi sırasına göre
            // "son"u seçerse imleç sunucu sayfa sınırının gerisinde kalır ve
            // aynı satırlar tekrar iner.
            var last = items[^1];
            // R6-04: imleç, temizlediği/eklediği Customer satırlarıyla aynı
            // SQLite dosyasına yazılır — yedek/geri yükleme ikisini birlikte
            // taşır, tombstone'un üstünden atlamış bir imleç geri gelemez.
            _cursors.Upsert(CursorName, licenseKey,
                updatedAt: last.UpdatedAt, lastId: last.Id);

            if (inserted > 0)
                _log.LogInformation("Ingested {Count} shopper registrations as new customers", inserted);
            // Ayrı satır: silme işleminin sahaya indiğinin tek kanıtı bu günlük.
            if (scrubbed > 0)
                _log.LogInformation(
                    "KVKK silme: {Count} yerel müşteri kaydının kişisel alanları temizlendi", scrubbed);
            return inserted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "ShopperRegistrationIngest failed; will retry");
            return 0;
        }
    }

    /// <summary>
    /// R6-04 imleç okuma + tek seferlik tohumlama. Satır varsa o kazanır —
    /// settings'te ne yazdığı önemsiz (geri yükleme sonrası settings başka
    /// veri neslinin değerini taşıyor olabilir). Satır yoksa eski settings
    /// zinciri tohum olur: (UpdatedAt, Id) çifti, o da yoksa saniyeye
    /// yuvarlanmış Unix damgası (bkz. AppSettings.LastShopperIngestAt). Tohum
    /// yazıldıktan sonra eski alanlar TEMİZLENİR — temizlenmezse 038-öncesi
    /// bir yedek geri yüklendiğinde bayat settings imleci yeniden tohum olur
    /// ve tombstone'ların (PurgedAt) üstünden atlardı: KVKK silmesi sahada
    /// geri açılmış kalırdı.
    ///
    /// Satır da tohum da yoksa baştan okuma: tombstone'lar yeniden işlenir
    /// (temizlik idempotent), sunucudan gelen kayıtlar (Platform, Username)
    /// eşleşmesiyle zaten atlanır. Yalnız bu makinede SİLİNMİŞ eski müşteri
    /// yeniden inebilir — felaket kurtarma bağlamında kabul edilen bedel;
    /// KVKK ile silinenler İNMEZ (PurgedAt satırı asla insert edilmez).
    /// </summary>
    private (DateTimeOffset At, Guid Id) LoadCursor(string licenseKey)
    {
        var row = _cursors.Get(CursorName, licenseKey);
        if (row is not null)
            return (row.UpdatedAt ?? DateTimeOffset.MinValue, row.LastId ?? Guid.Empty);

        var settings = _settingsStore.Load();
        var seededAt = settings.LastShopperIngestUpdatedAt
            ?? (settings.LastShopperIngestAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(settings.LastShopperIngestAt)
                : (DateTimeOffset?)null);
        if (seededAt is null) return (DateTimeOffset.MinValue, Guid.Empty);

        var seededId = settings.LastShopperIngestId ?? Guid.Empty;
        _cursors.Upsert(CursorName, licenseKey, updatedAt: seededAt, lastId: seededId);
        // N04: Update ile atomik birleştirme — bütün-nesne Save başka
        // bileşenin bu arada yazdığı alanı ezerdi.
        _settingsStore.Update(s =>
        {
            s.LastShopperIngestUpdatedAt = null;
            s.LastShopperIngestId = null;
            s.LastShopperIngestAt = 0;
        });
        return (seededAt.Value, seededId);
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
