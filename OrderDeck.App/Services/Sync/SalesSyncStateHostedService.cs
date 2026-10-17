using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// <see cref="SalesSyncStateService"/>'in arka plan işi: açılışta HEMEN bir tur (hello + kalp atışı; geri
/// yüklenmiş kopyanın gönderimleri hello'yu bekler, §8.7), sonra 30 sn'de bir (§4.5). Kayıt AppHost'ta
/// <c>AddHostedService</c>; başlatma WpfStartupEnvironment'in genel döngüsünde (CLAUDE.md, PR #89).
/// </summary>
public sealed class SalesSyncStateHostedService : BackgroundService
{
    /// <summary>Tur ritmi (§4.5: state her 30 sn'de bir, kalp atışı).</summary>
    internal static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(30);

    private readonly SalesSyncStateService _service;
    private readonly ILogger<SalesSyncStateHostedService> _log;
    private readonly TimeSpan _interval;

    public SalesSyncStateHostedService(
        SalesSyncStateService service,
        ILogger<SalesSyncStateHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Testler kısa ritim verir.
    internal SalesSyncStateHostedService(
        SalesSyncStateService service,
        ILogger<SalesSyncStateHostedService> log,
        TimeSpan interval)
    {
        _service = service;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Başlatma döngüsü arayüz iş parçacığında koşar: ilk turun eşzamanlı kısmı (kimlik ve kip okuması) bile
        // orada çalışmasın.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        _log.LogInformation("SalesSyncStateHostedService starting (cadence={Cadence})", _interval);
        await TickAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken).ConfigureAwait(false))
            await TickAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try { await _service.TourOnceAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Sales state tick failed; will retry next interval"); }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
