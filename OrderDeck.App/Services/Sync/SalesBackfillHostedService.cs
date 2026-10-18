using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// <see cref="SalesBackfillService"/>'in arka plan işi (spec 2026-10-13 §8.2; W1-09): 60 sn'de bir tur. İlk tur da
/// bir aralık sonra: açılıştaki hello (<see cref="SalesChangesPullHostedService"/>) ve müşteri akışının ilk turu aynı
/// 100 istek/dk/IP bütçesini paylaşır (§12 risk 9); geri doldurma zaten hello'yu bekler. Tamamlanmış geri doldurmada
/// tur tek bir imleç okumasıdır; bekleyen ödeme kayıtları (W3-05) her turda, iki kipte gider — boş kuyrukta yalnız bir
/// sayım. Turun ikinci adımı ödeme geçmişi geri doldurmasıdır (<see cref="PaymentHistoryBackfill"/>, §8.2-6; W3-06):
/// geri doldurmanın sonucundan bağımsız koşar, kuyruğa yazdığı kayıtları sonraki turun gönderimi götürür. Kayıt
/// AppHost'ta <c>AddHostedService</c>; başlatma WpfStartupEnvironment'in genel döngüsünde (CLAUDE.md, PR #89).
/// </summary>
public sealed class SalesBackfillHostedService : BackgroundService
{
    /// <summary>Tur ritmi (§8.2-3: turda en çok 10 istek, tur 60 sn'de bir).</summary>
    internal static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(60);

    private readonly SalesBackfillService _service;
    private readonly PaymentHistoryBackfill _history;
    private readonly ILogger<SalesBackfillHostedService> _log;
    private readonly TimeSpan _interval;

    public SalesBackfillHostedService(
        SalesBackfillService service, PaymentHistoryBackfill history, ILogger<SalesBackfillHostedService> log)
        : this(service, history, log, DefaultCadence) { }

    // Testler kısa ritim verir.
    internal SalesBackfillHostedService(
        SalesBackfillService service, PaymentHistoryBackfill history, ILogger<SalesBackfillHostedService> log,
        TimeSpan interval)
    {
        _service = service;
        _history = history;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Başlatma döngüsü arayüz iş parçacığında koşar: hiçbir kısım orada çalışmasın.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        _log.LogInformation("SalesBackfillHostedService starting (cadence={Cadence})", _interval);
        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken).ConfigureAwait(false))
        {
            try { await _service.TourOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Sales backfill tick failed; will retry next interval"); }

            // Ödeme geçmişi (W3-06): satış geri doldurmasının hatası onu atlatmaz.
            try { await _history.TourOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Payment history backfill tick failed; will retry next interval"); }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
