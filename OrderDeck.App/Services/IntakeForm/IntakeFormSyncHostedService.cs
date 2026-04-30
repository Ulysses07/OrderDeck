using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services.IntakeForm;

/// <summary>
/// 2-minute PeriodicTimer loop calling IntakeFormSyncService.SyncOnceAsync.
/// Phase 4b HeartbeatHostedService pattern.
/// </summary>
public sealed class IntakeFormSyncHostedService : BackgroundService
{
    private readonly IntakeFormSyncService _syncService;
    private readonly ILogger<IntakeFormSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public IntakeFormSyncHostedService(
        IntakeFormSyncService syncService,
        ILogger<IntakeFormSyncHostedService> log)
        : this(syncService, log, TimeSpan.FromMinutes(2)) { }

    internal IntakeFormSyncHostedService(
        IntakeFormSyncService syncService,
        ILogger<IntakeFormSyncHostedService> log,
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
                _log.LogWarning(ex, "Intake form sync tick failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
