using OrderDeck.Core.Payments;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF App ↔ LicenseServer iki yönlü Payment senkronizasyonu.
///
/// <list type="bullet">
///   <item><b>Push</b>: PaymentRepository.GetUnsynced() batch'i
///         POST /api/v1/licenses/{id}/payments/sync ile gönderir,
///         başarılı dönenleri MarkSynced eder.</item>
///   <item><b>Pull</b>: GET .../payments/since?since=cursor ile mobile'ın
///         onayladığı/reddetti payment status'larını lokal'e uygular.
///         Cursor AppSettings.LastPaymentReverseSync.</item>
/// </list>
///
/// LicenseId, LicenseService.CurrentLicense.LicenseKey'den
/// /api/v1/me/licenses üzerinden resolve edilir (cache'lenir).
/// </summary>
public sealed class PaymentSyncService
{
    private const int PushBatchSize = 50;
    private const int PullPageSize = 200;

    private readonly LicenseApiClient _api;
    private readonly PaymentRepository _payments;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<PaymentSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public event EventHandler<SyncResult>? Synced;

    public PaymentSyncService(
        LicenseApiClient api,
        PaymentRepository payments,
        SettingsStore settingsStore,
        AppSettings settings,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<PaymentSyncService> log)
    {
        _api = api;
        _payments = payments;
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
            _log.LogDebug("Payment sync skipped — no active license resolved");
            return default;
        }

        int pushed = await PushOutboxAsync(licenseId.Value, ct);
        int pulled = await PullReverseAsync(licenseId.Value, ct);

        var result = new SyncResult(pushed, pulled);
        if (pushed > 0 || pulled > 0)
        {
            _log.LogInformation("Payment sync: pushed={Pushed} pulled={Pulled}", pushed, pulled);
            Synced?.Invoke(this, result);
        }
        return result;
    }

    // ─── Push (outbox) ────────────────────────────────────────────────

    private async Task<int> PushOutboxAsync(Guid licenseId, CancellationToken ct)
    {
        var batch = _payments.GetUnsynced(PushBatchSize);
        if (batch.Count == 0) return 0;

        var items = batch.Select(p => new SyncPaymentItem(
            Id: Guid.Parse(p.Id),
            PayerName: p.PayerName,
            Amount: p.Amount,
            PaidAt: DateTimeOffset.FromUnixTimeSeconds(p.PaidAt),
            ReferansNo: p.ReferansNo,
            PdfHash: p.PdfHash
        )).ToList();

        List<SyncedPaymentDto> echo;
        try
        {
            echo = await _api.SyncPaymentsAsync(licenseId, new SyncPaymentsRequest(items), ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Payment outbox push failed: {Code}", ex.Code);
            return 0;
        }

        var now = _clock.UnixNow();
        foreach (var item in batch)
            _payments.MarkSynced(item.Id, now);

        // Server'dan dönen status (echo) içinde onaylanmış/reddedilmiş varsa onları
        // da uygula — push ile pull arasında mobile aksiyon olursa kaybolmasın.
        ApplyEcho(echo);
        return batch.Count;
    }

    // ─── Pull (reverse sync) ──────────────────────────────────────────

    private async Task<int> PullReverseAsync(Guid licenseId, CancellationToken ct)
    {
        var since = _settings.LastPaymentReverseSync ?? DateTimeOffset.MinValue;

        List<SyncedPaymentDto> rows;
        try
        {
            rows = await _api.GetPaymentsSinceAsync(licenseId, since, PullPageSize, ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Payment reverse sync failed: {Code}", ex.Code);
            return 0;
        }

        if (rows.Count == 0) return 0;

        DateTimeOffset newCursor = since;
        foreach (var dto in rows.OrderBy(d => d.UpdatedAt))
        {
            ApplyDto(dto);
            if (dto.UpdatedAt > newCursor) newCursor = dto.UpdatedAt;
        }

        _settings.LastPaymentReverseSync = newCursor;
        _settingsStore.Save(_settings);
        return rows.Count;
    }

    private void ApplyEcho(IEnumerable<SyncedPaymentDto> echo)
    {
        foreach (var dto in echo) ApplyDto(dto);
    }

    private void ApplyDto(SyncedPaymentDto dto)
    {
        if (!Enum.TryParse(dto.Status, ignoreCase: true, out PaymentStatus status))
        {
            _log.LogWarning("Unknown status from server: {Status}", dto.Status);
            return;
        }

        _payments.ApplyServerStatus(
            id: dto.Id.ToString(),
            status: status,
            approvedAt: dto.ApprovedAt?.ToUnixTimeSeconds(),
            rejectedAt: dto.RejectedAt?.ToUnixTimeSeconds(),
            rejectReason: dto.RejectReason,
            updatedAt: dto.UpdatedAt.ToUnixTimeSeconds());
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
