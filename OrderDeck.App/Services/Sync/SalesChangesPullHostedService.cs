using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// <see cref="SalesChangesPullService"/>'in arka plan işi (spec 2026-10-13 §5.4 Servis): açılışta HEMEN bir tur (hello +
/// kalp atışı; geri yüklenmiş kopyanın gönderimleri hello'yu bekler, §8.7), sonra 30 sn'de bir. Satış senkronunun TEK
/// 30 sn'lik sürücüsüdür: <c>sales/state</c> kalp atışı turun ilk adımı (§4.5) — ayrı bir state zamanlayıcısı yok,
/// 30 sn'de tam bir state isteği gider ve mod Off'ta da sürer. Kayıt AppHost'ta <c>AddHostedService</c>; başlatma
/// WpfStartupEnvironment'in genel döngüsünde (CLAUDE.md, PR #89).
/// </summary>
public sealed class SalesChangesPullHostedService : BackgroundService
{
    /// <summary>Tur ritmi (§4.5: state her 30 sn'de bir, kalp atışı; §5.4: akış 30 sn).</summary>
    internal static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(30);

    private readonly SalesChangesPullService _service;
    private readonly ILogger<SalesChangesPullHostedService> _log;
    private readonly TimeSpan _interval;

    public SalesChangesPullHostedService(SalesChangesPullService service, ILogger<SalesChangesPullHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Testler kısa ritim verir.
    internal SalesChangesPullHostedService(
        SalesChangesPullService service, ILogger<SalesChangesPullHostedService> log, TimeSpan interval)
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

        _log.LogInformation("SalesChangesPullHostedService starting (cadence={Cadence})", _interval);
        await TickAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken).ConfigureAwait(false))
            await TickAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try { await _service.TourOnceAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Sales changes tick failed; will retry next interval"); }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
