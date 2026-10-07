using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// CustomerChangesPullService için background wrapper.
/// PeriodicTimer pattern (WpfCustomerProjectionSyncHostedService ile aynı).
/// Cadence 30 saniye — başka bilgisayarın ya da Shopper'ın değişikliği yayında hemen görünsün.
/// Açılışta HEMEN bir tur (Faz 0): bilgisayar değiştiren operatör diğer bilgisayarın son
/// değişikliklerini 30 sn beklemesin. Kayıt AppHost'ta <c>AddHostedService</c>; başlatma
/// WpfStartupEnvironment'in genel döngüsünde (CLAUDE.md, PR #89).
/// </summary>
public sealed class CustomerChangesPullHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(30);

    private readonly CustomerChangesPullService _service;
    private readonly ILogger<CustomerChangesPullHostedService> _log;
    private readonly TimeSpan _interval;

    public CustomerChangesPullHostedService(
        CustomerChangesPullService service,
        ILogger<CustomerChangesPullHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Internal ctor for tests (inject short cadence).
    internal CustomerChangesPullHostedService(
        CustomerChangesPullService service,
        ILogger<CustomerChangesPullHostedService> log,
        TimeSpan interval)
    {
        _service = service;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("CustomerChangesPullHostedService starting (cadence={Cadence})", _interval);
        await TickAsync(stoppingToken);
        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
            await TickAsync(stoppingToken);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try { await _service.PullOnceAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Customer changes pull tick failed; will retry next interval"); }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
