using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// ShopperRegistrationIngestService için background wrapper.
/// PeriodicTimer pattern (WpfCustomerProjectionSyncHostedService ile aynı).
/// Cadence 30 saniye — shopper register anında WPF'de görünmesi için.
/// </summary>
public sealed class ShopperRegistrationIngestHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(30);

    private readonly ShopperRegistrationIngestService _service;
    private readonly ILogger<ShopperRegistrationIngestHostedService> _log;
    private readonly TimeSpan _interval;

    public ShopperRegistrationIngestHostedService(
        ShopperRegistrationIngestService service,
        ILogger<ShopperRegistrationIngestHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Internal ctor for tests (inject short cadence).
    internal ShopperRegistrationIngestHostedService(
        ShopperRegistrationIngestService service,
        ILogger<ShopperRegistrationIngestHostedService> log,
        TimeSpan interval)
    {
        _service = service;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "ShopperRegistrationIngestHostedService starting (cadence={Cadence})", _interval);

        // Initial run on startup
        try { await _service.IngestOnceAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.LogWarning(ex, "Initial shopper ingest failed"); }

        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try
            {
                await _service.IngestOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Shopper registration ingest tick failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
