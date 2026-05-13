using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// PR-D (2026-05-13): 30-saniyelik PeriodicTimer loop ShipmentSyncService
/// üzerinden tick. Pattern: <see cref="PaymentSyncHostedService"/>.
/// </summary>
public sealed class ShipmentSyncHostedService : BackgroundService
{
    private readonly ShipmentSyncService _syncService;
    private readonly ILogger<ShipmentSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public ShipmentSyncHostedService(
        ShipmentSyncService syncService,
        ILogger<ShipmentSyncHostedService> log)
        : this(syncService, log, TimeSpan.FromSeconds(30)) { }

    internal ShipmentSyncHostedService(
        ShipmentSyncService syncService,
        ILogger<ShipmentSyncHostedService> log,
        TimeSpan interval)
    {
        _syncService = syncService;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try
            {
                await _syncService.SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Shipment sync tick failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
