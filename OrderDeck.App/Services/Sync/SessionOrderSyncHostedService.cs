using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// PR siparis-sync (2026-05-13): 60-saniyelik tick. Session + Order
/// pattern: PaymentSyncHostedService ile aynı. Daha düşük frekans çünkü
/// yayın geçmişi real-time mobile feed değil, periyodik raporlama.
/// </summary>
public sealed class SessionOrderSyncHostedService : BackgroundService
{
    private readonly SessionOrderSyncService _syncService;
    private readonly ILogger<SessionOrderSyncHostedService> _log;
    private readonly System.TimeSpan _interval;

    public SessionOrderSyncHostedService(
        SessionOrderSyncService syncService,
        ILogger<SessionOrderSyncHostedService> log)
        : this(syncService, log, System.TimeSpan.FromSeconds(60)) { }

    internal SessionOrderSyncHostedService(
        SessionOrderSyncService syncService,
        ILogger<SessionOrderSyncHostedService> log,
        System.TimeSpan interval)
    {
        _syncService = syncService;
        _log = log;
        _interval = interval;
    }

    protected override async System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken)
    {
        using var timer = new System.Threading.PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try
            {
                await _syncService.SyncOnceAsync(stoppingToken);
            }
            catch (System.OperationCanceledException)
            {
                break;
            }
            catch (System.Exception ex)
            {
                _log.LogWarning(ex, "Session/Order sync tick failed; will retry next interval");
            }
        }
    }

    private static async System.Threading.Tasks.Task<bool> WaitSafe(
        System.Threading.PeriodicTimer timer, System.Threading.CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (System.OperationCanceledException) { return false; }
    }
}
