using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Katalog replikasını tazeler. Periyot 5 dakika: katalog yayın sırasında
/// nadiren değişiyor (panelde ürün girişi çoğunlukla yayın öncesi), ve her tur
/// TAM anlık görüntü çektiği için sık koşmak sunucuya bedavaya yük bindirir.
/// Açılıştaki ilk koşu, operatör yayına başlamadan replikanın dolmasını sağlıyor.
/// </summary>
public sealed class CatalogSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromMinutes(5);

    private readonly CatalogSyncService _service;
    private readonly ILogger<CatalogSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public CatalogSyncHostedService(
        CatalogSyncService service, ILogger<CatalogSyncHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Testler için kısa periyot enjekte eder.
    internal CatalogSyncHostedService(
        CatalogSyncService service, ILogger<CatalogSyncHostedService> log, TimeSpan interval)
    {
        _service = service;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "CatalogSyncHostedService starting (cadence={Cadence})", _interval);

        // Açılışta ilk koşu.
        try { await _service.SyncOnceAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.LogWarning(ex, "İlk katalog senkronu başarısız"); }

        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try { await _service.SyncOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Katalog senkron turu başarısız; sonraki turda yeniden denenecek");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
