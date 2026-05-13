using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF App ↔ LicenseServer StreamSession + Order senkronizasyonu
/// (PR siparis-sync 2026-05-13). Yayıncı ve ekibi mobile Panel
/// "Siparişler" ekranında geçmiş yayınların sipariş listesini görmesi için.
///
/// WPF authoritative — yayın başlatma/bitirme + label oluşturma WPF'te
/// olur, server pasif replika. Pattern: <see cref="PaymentSyncService"/>
/// ile paralel. Push-only (reverse sync gerekmiyor).
/// </summary>
public sealed class SessionOrderSyncService
{
    private const int SessionBatchSize = 50;
    private const int OrderBatchSize = 100;

    private readonly LicenseApiClient _api;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<SessionOrderSyncService> _log;

    private System.Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public SessionOrderSyncService(
        LicenseApiClient api,
        SessionRepository sessions,
        LabelRepository labels,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<SessionOrderSyncService> log)
    {
        _api = api;
        _sessions = sessions;
        _labels = labels;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
    }

    public readonly record struct SyncResult(int SessionsPushed, int OrdersPushed);

    public async Task<SyncResult> SyncOnceAsync(System.Threading.CancellationToken ct = default)
    {
        var licenseId = await ResolveLicenseIdAsync(ct);
        if (licenseId is null)
        {
            _log.LogDebug("Session/Order sync skipped — no active license resolved");
            return default;
        }

        // Önce session'lar (Order.SessionId FK olduğu için)
        int sessionsPushed = await PushSessionsAsync(licenseId.Value, ct);
        int ordersPushed = await PushOrdersAsync(licenseId.Value, ct);

        if (sessionsPushed > 0 || ordersPushed > 0)
            _log.LogInformation(
                "Session/Order sync: sessions={Sessions} orders={Orders}",
                sessionsPushed, ordersPushed);

        return new SyncResult(sessionsPushed, ordersPushed);
    }

    private async Task<int> PushSessionsAsync(System.Guid licenseId, System.Threading.CancellationToken ct)
    {
        var batch = _sessions.GetUnsynced(SessionBatchSize);
        if (batch.Count == 0) return 0;

        var items = batch.Select(s => new SyncSessionItem(
            Id: System.Guid.Parse(s.Id),
            Title: s.Title,
            StartedAt: System.DateTimeOffset.FromUnixTimeSeconds(s.StartedAt),
            EndedAt: s.EndedAt.HasValue ? System.DateTimeOffset.FromUnixTimeSeconds(s.EndedAt.Value) : null,
            Platforms: string.Join(",", s.Platforms),
            Notes: s.Notes
        )).ToList();

        try
        {
            _ = await _api.SyncSessionsAsync(licenseId, new SyncSessionsRequest(items), ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Session outbox push failed: {Code}", ex.Code);
            return 0;
        }

        var now = _clock.UnixNow();
        foreach (var s in batch) _sessions.MarkSynced(s.Id, now);
        return batch.Count;
    }

    private async Task<int> PushOrdersAsync(System.Guid licenseId, System.Threading.CancellationToken ct)
    {
        var batch = _labels.GetUnsynced(OrderBatchSize);
        if (batch.Count == 0) return 0;

        var items = batch.Select(l => new SyncOrderItem(
            Id: System.Guid.Parse(l.Id),
            SessionId: System.Guid.TryParse(l.SessionId, out var sid) ? sid : null,
            CustomerId: l.CustomerId,
            Platform: l.Platform,
            Username: l.Username,
            DisplayName: l.DisplayName,
            MessageText: l.MessageText,
            Code: l.Code,
            Price: l.Price,
            AddedAt: System.DateTimeOffset.FromUnixTimeSeconds(l.AddedAt),
            PrintedAt: l.PrintedAt.HasValue ? System.DateTimeOffset.FromUnixTimeSeconds(l.PrintedAt.Value) : null,
            CancelledAt: l.CancelledAt.HasValue ? System.DateTimeOffset.FromUnixTimeSeconds(l.CancelledAt.Value) : null,
            CancelReason: l.CancelReason,
            IsShippingFee: l.IsShippingFee,
            IsBackupPromoted: l.IsBackupPromoted,
            IsTentativeBackup: l.IsTentativeBackup
        )).ToList();

        try
        {
            _ = await _api.SyncOrdersAsync(licenseId, new SyncOrdersRequest(items), ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Order outbox push failed: {Code}", ex.Code);
            return 0;
        }

        var now = _clock.UnixNow();
        foreach (var l in batch) _labels.MarkSynced(l.Id, now);
        return batch.Count;
    }

    private async Task<System.Guid?> ResolveLicenseIdAsync(System.Threading.CancellationToken ct)
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
                _log.LogWarning("LicenseId not found for current license key");
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
