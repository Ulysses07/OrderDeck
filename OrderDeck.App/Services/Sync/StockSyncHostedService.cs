using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Stok bakiyelerini tazeler. <b>Tek ritim: 60 saniye.</b>
///
/// Kardeş <c>CatalogSyncHostedService</c> iki ritimli (30 sn → 5 dk) ve
/// "yerleşme" ölçütü "replikaya satır yazıldı". Stokta o ölçüt işlemez: hiç
/// stok hareketi olmayan lisans hiçbir zaman yerleşmez ve sonsuza dek 30
/// saniyede bir sunucuyu yorardı.
///
/// 60 saniye keyfi değil: sunucu <c>UtcNow - 60sn</c>'den yeni hareketleri
/// hiç okumuyor (commit sırası ≠ zaman damgası sırası), yani daha sık sormak
/// tanım gereği yeni veri getirmez.
/// </summary>
public sealed class StockSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(60);

    private readonly StockSyncService _service;
    private readonly ILogger<StockSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public StockSyncHostedService(
        StockSyncService service, ILogger<StockSyncHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Testler için kısa periyot enjekte eder.
    internal StockSyncHostedService(
        StockSyncService service, ILogger<StockSyncHostedService> log, TimeSpan interval)
    {
        _service = service;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("StockSyncHostedService starting (cadence={Interval})", _interval);

        await RunRoundAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            await RunRoundAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;
        }
    }

    private async Task RunRoundAsync(CancellationToken ct)
    {
        // SyncOnceAsync kendi içinde yutuyor; bu ağ dışı beklenmedik hatalar için.
        try { await _service.SyncOnceAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stok senkron turu başarısız; sonraki turda yeniden denenecek");
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
