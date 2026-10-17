using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF App ↔ LicenseServer Shipment senkronizasyonu (Kümülatif kargo PR-D).
/// Pattern: <see cref="PaymentSyncService"/> ile paralel.
///
/// Push-dominant: WPF authoritative — kararlar WPF'te verilir, mobile sadece
/// okur. Reverse sync (pull) yine de implement edildi, ileride server-side
/// CSR aksiyonları gerekirse hazır.
///
/// <para><b>Satış senkronu (spec 2026-10-13 §7.1, W2):</b> mod On'da dosya ve etiket–dosya bağı yalnız sunucuda
/// yazılır; yerel <c>Shipment</c> salt okunur aynadır ve akışla ya da komut yanıtıyla iner. Bu servis o kipte
/// hiçbir istek göndermez — ne gönderim ne <c>/since</c> çekmesi (lisans çözümü de yok). Mod Off'ta v2 yanıtındaki
/// sunucu satırı yerel aynaya yazılır (<see cref="ShipmentRepository.ApplyPushResponse"/>): sunucuda tek yönlü
/// Shipped bayat yerel kararı ezer.</para>
/// </summary>
public sealed class ShipmentSyncService
{
    private const int PushBatchSize = 50;
    private const int PullPageSize = 200;

    /// <summary>Durum satırındaki gönderim ilerlemesinin adı (D2 incelemesi I-3).</summary>
    public const string PushStatusName = "kargo";

    private readonly LicenseApiClient _api;
    private readonly ShipmentRepository _shipments;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly SalesSyncEnvelope _envelope;
    private readonly IClock _clock;
    private readonly ILogger<ShipmentSyncService> _log;
    private readonly SyncStatusTracker? _tracker;
    private readonly SalesSyncStateRepository? _salesState;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public event EventHandler<SyncResult>? Synced;

    public ShipmentSyncService(
        LicenseApiClient api,
        ShipmentRepository shipments,
        SettingsStore settingsStore,
        AppSettings settings,
        ICurrentLicenseProvider licenseProvider,
        SalesSyncEnvelope envelope,
        IClock clock,
        ILogger<ShipmentSyncService> log,
        SyncStatusTracker? statusTracker = null,
        SalesSyncStateRepository? salesState = null)
    {
        _api = api;
        _shipments = shipments;
        _settingsStore = settingsStore;
        _settings = settings;
        _licenseProvider = licenseProvider;
        _envelope = envelope;
        _clock = clock;
        _log = log;
        _tracker = statusTracker;
        _tracker?.RegisterPush(PushStatusName);
        _salesState = salesState;
    }

    public readonly record struct SyncResult(int Pushed, int Pulled);

    public async Task<SyncResult> SyncOnceAsync(CancellationToken ct = default)
    {
        // Mod On (§7.1): dosya sunucunun; gönderilecek bir şey yok, /since da yok — sıfır HTTP.
        if (_salesState?.GetMode() == 1)
        {
            _tracker?.MarkPushOk(PushStatusName, DateTimeOffset.UtcNow);
            return default;
        }

        var licenseId = await ResolveLicenseIdAsync(ct);
        if (licenseId is null)
        {
            _log.LogDebug("Shipment sync skipped — no active license resolved");
            return default;
        }

        int pushed = await PushOutboxAsync(licenseId.Value, ct);
        int pulled = await PullReverseAsync(licenseId.Value, ct);

        var result = new SyncResult(pushed, pulled);
        if (pushed > 0 || pulled > 0)
        {
            _log.LogInformation("Shipment sync: pushed={Pushed} pulled={Pulled}", pushed, pulled);
            Synced?.Invoke(this, result);
        }
        return result;
    }

    // ─── Push (outbox) ────────────────────────────────────────────────

    private async Task<int> PushOutboxAsync(Guid licenseId, CancellationToken ct)
    {
        var batch = _shipments.GetUnsynced(PushBatchSize);
        if (batch.Count == 0)
        {
            _tracker?.MarkPushOk(PushStatusName, DateTimeOffset.UtcNow);   // I-3: gönderecek bir şey yok
            return 0;
        }

        // v2 zarfı (spec 2026-10-13 §4.0, §7.1): sunucu yeni dosyanın OriginReplicaId'sini bu kopya yazar — mod Off'ta
        // dosyanın tek yazıcısı. null: geri yüklemeden sonra hello bekleniyor, gönderim yok (§8.7). Kimlik bozuksa
        // zarfsız (v1) gövde gider; gönderim durmaz.
        if (_envelope.ForPush() is not { } envelope)
        {
            _log.LogDebug("Shipment outbox push skipped — waiting for sales/hello after restore");
            return 0;
        }

        var items = batch.Select(s => new SyncShipmentItem(
            Id: Guid.Parse(s.Id),
            CustomerId: s.CustomerId,
            Status: s.Status.ToString().ToLowerInvariant(),
            CumulativeAmount: s.CumulativeAmount,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(s.CreatedAt),
            HeldAt: s.HeldAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(s.HeldAt.Value) : null,
            ShippedAt: s.ShippedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(s.ShippedAt.Value) : null
        )).ToList();

        List<SalesShipmentItem> response;
        try
        {
            response = await _api.SyncShipmentsAsync(licenseId,
                new SyncShipmentsRequest(items, envelope.ReplicaId, envelope.Protocol, envelope.SeenModeField), ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Shipment outbox push failed: {Code}", ex.Code);
            return 0;
        }

        var now = _clock.UnixNow();
        // v2 yanıtı dosya başına sunucunun güncel satırıdır (§7.1): yerel aynaya yazılır, Shipped tek yönlü olduğu
        // için bayat yerel karar sunucunun Shipped'ine döner. Eski sunucunun v1 yanıtı uygulanmaz (bugünkü onay).
        var server = envelope.IsV2 ? ServerRows(response) : new Dictionary<string, SalesFeedShipment>();
        var mirrored = 0;
        // F05: push'a giden Revision ile onayla — uçuş sırasında satır
        // değiştiyse 0 satır etkiler, sonraki tick tekrar gönderir.
        foreach (var item in batch)
        {
            if (server.TryGetValue(item.Id, out var row))
            {
                if (_shipments.ApplyPushResponse(item.Id, item.Revision, row, now) == ShipmentPushAck.Mirrored)
                    mirrored++;
            }
            else
            {
                _shipments.MarkSynced(item.Id, now, item.Revision);
            }
        }
        if (mirrored > 0)
            _log.LogInformation("Shipment push: {Count} dosyada sunucunun güncel satırı yerel aynaya yazıldı", mirrored);
        _tracker?.MarkPushOk(PushStatusName, DateTimeOffset.UtcNow);       // I-3: parti gitti

        return batch.Count;
    }

    /// <summary>v2 yanıtının satırları, yerel Id'ye ("N") göre. v2 sunucusu her satırın kaynak kopyasını doldurur
    /// (boşsa çağıran); kaynağı boş satır eski sunucunun v1 yanıtıdır ve uygulanmaz. Çözülemeyen satır (bilinmeyen
    /// durum) yalnız onaylanır.</summary>
    private Dictionary<string, SalesFeedShipment> ServerRows(IReadOnlyList<SalesShipmentItem> response)
    {
        var rows = new Dictionary<string, SalesFeedShipment>(StringComparer.Ordinal);
        foreach (var item in response)
        {
            if (item.OriginReplicaId is null) continue;
            try
            {
                rows[item.Id.ToString("N")] = SalesFeedMapper.ToCore(item);
            }
            catch (FormatException ex)
            {
                _log.LogWarning(ex, "Shipment push: sunucunun {Id} satırı okunamadı; yalnız onaylanıyor", item.Id);
            }
        }
        return rows;
    }

    // ─── Pull (reverse sync) ──────────────────────────────────────────

    private async Task<int> PullReverseAsync(Guid licenseId, CancellationToken ct)
    {
        var since = _settings.LastShipmentReverseSync ?? DateTimeOffset.MinValue;
        var sinceId = _settings.LastShipmentReverseSyncId ?? Guid.Empty;

        List<SyncedShipmentDto> rows;
        try
        {
            rows = await _api.GetShipmentsSinceAsync(licenseId, since, sinceId, PullPageSize, ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Shipment reverse sync failed: {Code}", ex.Code);
            return 0;
        }

        if (rows.Count == 0) return 0;

        // Server'dan gelen satırlar apply EDİLMİYOR (WPF authoritative; status
        // conflict riski) — yalnız imleç ilerliyor.
        // R3-01: imleç sunucunun teslim ettiği SON satırdan okunur — yeniden
        // SIRALAMA YOK. Sunucu SQL uniqueidentifier sırasıyla sayfalıyor; .NET
        // Guid sırası farklı, istemci "son"u kendisi seçerse imleç sunucu sayfa
        // sınırının gerisinde kalır ve aynı satırlar tekrar iner.
        var last = rows[^1];
        // N04: bellekteki kopya güncel kalsın (imleç okuması buradan); diske
        // Update ile atomik birleştirme.
        _settings.LastShipmentReverseSync = last.UpdatedAt;
        _settings.LastShipmentReverseSyncId = last.Id;
        _settingsStore.Update(s =>
        {
            s.LastShipmentReverseSync = last.UpdatedAt;
            s.LastShipmentReverseSyncId = last.Id;
        });
        return rows.Count;
    }

    // ─── LicenseId resolution ─────────────────────────────────────────

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
            if (match?.Id is null)
            {
                _log.LogWarning("LicenseId not found for current license key — server response missing Id");
                return null;
            }

            _cachedLicenseId = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Failed to resolve LicenseId: {Code}", ex.Code);
            return null;
        }
    }
}
