using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.Licensing.Services;

/// <summary>
/// Periodically calls <see cref="LicenseService.RefreshAsync"/> while the app is running.
/// Default interval comes from <see cref="LicensingOptions.HeartbeatIntervalHours"/>.
/// </summary>
public sealed class HeartbeatHostedService : BackgroundService
{
    private readonly LicenseService _licenseService;
    private readonly ILogger<HeartbeatHostedService> _log;
    private readonly TimeSpan _interval;

    public HeartbeatHostedService(
        LicenseService licenseService,
        ILogger<HeartbeatHostedService> log,
        IOptions<LicensingOptions> opt)
        : this(licenseService, log, TimeSpan.FromHours(opt.Value.HeartbeatIntervalHours)) { }

    // Test-only ctor with explicit interval.
    internal HeartbeatHostedService(
        LicenseService licenseService,
        ILogger<HeartbeatHostedService> log,
        TimeSpan interval)
    {
        _licenseService = licenseService;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await WaitForNextTickSafely(timer, stoppingToken))
        {
            try
            {
                await _licenseService.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Heartbeat refresh failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitForNextTickSafely(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
