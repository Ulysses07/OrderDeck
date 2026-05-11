using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Payment sync periyodik wrapper. 30 sn interval — IntakeFormSync'ten daha
/// sık çünkü dekontlar canlı yayın akışında geldiği için yayıncının mobile
/// app'inde 1-2 dk içinde görünmesi gerekiyor.
///
/// HostedService pattern: IntakeFormSyncHostedService ile aynı (PeriodicTimer
/// + safe-wait + try/catch retry).
/// </summary>
public sealed class PaymentSyncHostedService : BackgroundService
{
    private readonly PaymentSyncService _syncService;
    private readonly ILogger<PaymentSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public PaymentSyncHostedService(
        PaymentSyncService syncService,
        ILogger<PaymentSyncHostedService> log)
        : this(syncService, log, TimeSpan.FromSeconds(30)) { }

    internal PaymentSyncHostedService(
        PaymentSyncService syncService,
        ILogger<PaymentSyncHostedService> log,
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
                _log.LogWarning(ex, "Payment sync tick failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
