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
/// </summary>
public sealed class ShipmentSyncService
{
    private const int PushBatchSize = 50;
    private const int PullPageSize = 200;

    private readonly LicenseApiClient _api;
    private readonly ShipmentRepository _shipments;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<ShipmentSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public event EventHandler<SyncResult>? Synced;

    public ShipmentSyncService(
        LicenseApiClient api,
        ShipmentRepository shipments,
        SettingsStore settingsStore,
        AppSettings settings,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<ShipmentSyncService> log)
    {
        _api = api;
        _shipments = shipments;
        _settingsStore = settingsStore;
        _settings = settings;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
    }

    public readonly record struct SyncResult(int Pushed, int Pulled);

    public async Task<SyncResult> SyncOnceAsync(CancellationToken ct = default)
    {
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
        if (batch.Count == 0) return 0;

        var items = batch.Select(s => new SyncShipmentItem(
            Id: Guid.Parse(s.Id),
            CustomerId: s.CustomerId,
            Status: s.Status.ToString().ToLowerInvariant(),
            CumulativeAmount: s.CumulativeAmount,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(s.CreatedAt),
            HeldAt: s.HeldAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(s.HeldAt.Value) : null,
            ShippedAt: s.ShippedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(s.ShippedAt.Value) : null
        )).ToList();

        try
        {
            _ = await _api.SyncShipmentsAsync(licenseId, new SyncShipmentsRequest(items), ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Shipment outbox push failed: {Code}", ex.Code);
            return 0;
        }

        var now = _clock.UnixNow();
        foreach (var item in batch)
            _shipments.MarkSynced(item.Id, now);

        return batch.Count;
    }

    // ─── Pull (reverse sync) ──────────────────────────────────────────

    private async Task<int> PullReverseAsync(Guid licenseId, CancellationToken ct)
    {
        var since = _settings.LastShipmentReverseSync ?? DateTimeOffset.MinValue;

        List<SyncedShipmentDto> rows;
        try
        {
            rows = await _api.GetShipmentsSinceAsync(licenseId, since, PullPageSize, ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Shipment reverse sync failed: {Code}", ex.Code);
            return 0;
        }

        if (rows.Count == 0) return 0;

        DateTimeOffset newCursor = since;
        foreach (var dto in rows.OrderBy(d => d.UpdatedAt))
        {
            // Server'dan gelen ID lokal'de var mı? WPF authoritative olduğu için
            // genelde server-only update gelmez; gelirse log + bilgi amaçlı.
            // Şu an apply etmiyoruz (status conflict riski). Cursor advance ediyoruz.
            if (dto.UpdatedAt > newCursor) newCursor = dto.UpdatedAt;
        }

        _settings.LastShipmentReverseSync = newCursor;
        _settingsStore.Save(_settings);
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
