using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// PaymentAccountSyncService için background wrapper.
/// PeriodicTimer pattern (PaymentSyncHostedService ile aynı).
/// Cadence 5 dakika — IBAN/AccountHolder nadiren değişir.
/// </summary>
public sealed class PaymentAccountSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromMinutes(5);

    private readonly PaymentAccountSyncService _service;
    private readonly ILogger<PaymentAccountSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public PaymentAccountSyncHostedService(
        PaymentAccountSyncService service,
        ILogger<PaymentAccountSyncHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Internal ctor for tests (inject short cadence without real timer).
    internal PaymentAccountSyncHostedService(
        PaymentAccountSyncService service,
        ILogger<PaymentAccountSyncHostedService> log,
        TimeSpan interval)
    {
        _service  = service;
        _log      = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "PaymentAccountSyncHostedService starting (cadence={Cadence})", _interval);

        // Initial push on startup catches any change made while server was offline.
        try { await _service.SyncIfChangedAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.LogWarning(ex, "PaymentAccount initial sync failed"); }

        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try
            {
                await _service.SyncIfChangedAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PaymentAccount periodic sync failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
