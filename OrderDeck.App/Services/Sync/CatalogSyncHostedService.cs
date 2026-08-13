using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Katalog replikasını tazeler; <b>iki ritimli</b>.
///
/// Yerleşik periyot 5 dakika: katalog yayın sırasında nadiren değişiyor
/// (panelde ürün girişi çoğunlukla yayın öncesi) ve her tur TAM anlık görüntü
/// çektiği için sık koşmak sunucuya bedavaya yük bindirir.
///
/// Ama açılıştaki ilk tur lisans anahtarı henüz çözülmemişken düşebilir
/// (giriş/token gelmemiş) ve o tur hiçbir şey yazmadan 0 döner. Tek ritim 5
/// dakika olsaydı taze giriş yapan operatör bütün yayın hazırlığı boyunca BOŞ
/// katalogla otururdu — sınıfın "açılışta replika dolar" sözü kâğıt üstünde
/// kalırdı. Bu yüzden ilk GERÇEKTEN başarılı tura kadar 30 saniyede bir
/// denenir, sonra 5 dakikaya oturulur. (Kardeş servis
/// <c>ShopperRegistrationIngestHostedService</c> aynı sorunu sabit 30 saniyeyle
/// çözüyor; burada yük daha ağır olduğu için sabitlemek yerine oturuyoruz.)
///
/// "Başarılı" = replikaya en az bir ürün yazılmış tur. Kataloğu gerçekten boş
/// olan yayıncı hızlı ritimde kalır; bu İSTENEN davranış — panele ilk ürününü
/// girmek üzere olan yayıncı odur ve bedeli 30 saniyede bir istek.
/// </summary>
public sealed class CatalogSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupCadence = TimeSpan.FromSeconds(30);

    private readonly CatalogSyncService _service;
    private readonly ILogger<CatalogSyncHostedService> _log;
    private readonly TimeSpan _startupInterval;
    private readonly TimeSpan _steadyInterval;

    public CatalogSyncHostedService(
        CatalogSyncService service, ILogger<CatalogSyncHostedService> log)
        : this(service, log, StartupCadence, DefaultCadence) { }

    // Testler için kısa periyot enjekte eder.
    internal CatalogSyncHostedService(
        CatalogSyncService service, ILogger<CatalogSyncHostedService> log,
        TimeSpan startupInterval, TimeSpan steadyInterval)
    {
        _service = service;
        _log = log;
        _startupInterval = startupInterval;
        _steadyInterval = steadyInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "CatalogSyncHostedService starting (cadence={Startup} → {Steady})",
            _startupInterval, _steadyInterval);

        // Açılışta ilk koşu.
        var settled = await RunRoundAsync(stoppingToken, first: true);
        if (stoppingToken.IsCancellationRequested) return;

        using var timer = new PeriodicTimer(settled ? _steadyInterval : _startupInterval);
        while (await WaitSafe(timer, stoppingToken))
        {
            var wroteSomething = await RunRoundAsync(stoppingToken, first: false);
            if (stoppingToken.IsCancellationRequested) break;
            if (wroteSomething && !settled)
            {
                settled = true;
                timer.Period = _steadyInterval;
                _log.LogInformation(
                    "Katalog replikası doldu; periyot {Steady} değerine oturdu", _steadyInterval);
            }
        }
    }

    /// <returns>Tur replikaya ürün yazdıysa true.</returns>
    private async Task<bool> RunRoundAsync(CancellationToken ct, bool first)
    {
        try { return await _service.SyncOnceAsync(ct) > 0; }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, first
                ? "İlk katalog senkronu başarısız"
                : "Katalog senkron turu başarısız; sonraki turda yeniden denenecek");
            return false;
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
