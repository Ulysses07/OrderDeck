using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WpfCustomerProjectionSyncService için background wrapper.
/// PeriodicTimer pattern (PaymentSyncHostedService ile aynı).
/// Cadence 60 saniye — customer kayıtları aktif yayın sırasında sürekli güncellenir.
/// </summary>
public sealed class WpfCustomerProjectionSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(60);

    private readonly WpfCustomerProjectionSyncService _service;
    private readonly ILogger<WpfCustomerProjectionSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public WpfCustomerProjectionSyncHostedService(
        WpfCustomerProjectionSyncService service,
        ILogger<WpfCustomerProjectionSyncHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Internal ctor for tests (inject short cadence).
    internal WpfCustomerProjectionSyncHostedService(
        WpfCustomerProjectionSyncService service,
        ILogger<WpfCustomerProjectionSyncHostedService> log,
        TimeSpan interval)
    {
        _service  = service;
        _log      = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "WpfCustomerProjectionSyncHostedService starting (cadence={Cadence})", _interval);

        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try
            {
                await _service.SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Customer projection sync tick failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
