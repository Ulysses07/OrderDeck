using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Sunucu stok defterindeki bakiyeleri yerel replikaya çeker.
///
/// <b>Katalogdan farkı sayfa sayfa yazması:</b> katalog TAM anlık görüntü
/// olduğu için "ya hep ya hiç" yazılır; burada her sayfa kendi imleciyle
/// birlikte kalıcılaşır. Yarıda kopan tur veri kaybetmez — bir sonraki tur
/// kaldığı yerden devam eder.
/// </summary>
public sealed class StockSyncService
{
    private const int PageSize = 500;

    /// <summary>
    /// Tavan. Sunucu imleci ilerletmezse döngü sonsuza dönmesin. 200 sayfa ×
    /// 500 = 100.000 anahtar; gerçek katalogların kat kat üstünde. Tavana
    /// çarpmak veri kaybı DEĞİL: yazılanlar kalıcı, kalanı sonraki tura kalır.
    /// </summary>
    private const int MaxPages = 200;

    private readonly LicenseApiClient _api;
    private readonly StockBalanceRepository _repo;
    private readonly StockBalanceProvider _provider;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<StockSyncService> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Yalnız kapının içinde okunup yazılıyor; kalıp CatalogSyncService.
    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public StockSyncService(
        LicenseApiClient api,
        StockBalanceRepository repo,
        StockBalanceProvider provider,
        ICurrentLicenseProvider licenseProvider,
        ILogger<StockSyncService> log)
    {
        _api = api;
        _repo = repo;
        _provider = provider;
        _licenseProvider = licenseProvider;
        _log = log;
    }

    /// <summary>Yazılan bakiye satırı sayısı; senkron yapılamadıysa 0.</summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct))
        {
            _log.LogDebug("Stok senkronu zaten sürüyor; bu çağrı atlandı");
            return 0;
        }

        int written;
        try { written = await SyncCoreAsync(ct); }
        finally { _gate.Release(); }

        // Bildirim KAPININ DIŞINDA: abonesi görünüm modelleri ve fırlatan bir
        // abone kapı tutulurken patlarsa, yazılmış ve kalıcı olmuş bir sayfa
        // "başarısız tur" diye günlüğe düşerdi. Yazma bitti; haber vermek ayrı iş.
        if (written > 0) _provider.RaiseBalancesChanged();
        return written;
    }

    private async Task<int> SyncCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrEmpty(licenseKey)) return 0;

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct);
        if (licenseId is null) return 0;

        var written = 0;
        try
        {
            var cursor = _repo.GetCursor();
            var pages = 0;
            var more = false;

            for (; pages < MaxPages; pages++)
            {
                var res = await _api.GetStockBalancesSinceAsync(
                    licenseId.Value, cursor.CreatedAt, cursor.Id, PageSize, ct);

                var balances = res.Balances
                    .Select(b => new CatalogStockBalance(
                        b.ProductId.ToString("N"),
                        b.ProductVariantId?.ToString("N"),
                        b.Quantity))
                    .ToList();

                cursor = new StockCursor(res.CursorCreatedAt, res.CursorId);

                // Boş sayfada da yazılıyor: sunucu imleci geri sarmadığı için
                // bu bir no-op, ama imlecin tek yazma yolu bu kalsın.
                _repo.ApplyPage(balances, cursor);
                written += balances.Count;

                more = res.HasMore;
                if (!more) break;
            }

            // Tavana çarpmak SESSİZ kalmamalı. İki ayrı arıza aynı yerden
            // çıkıyor ve çareleri zıt: (a) defter gerçekten tavanı aştı →
            // sonraki tur kaldığı yerden devam eder, yapılacak bir şey yok,
            // (b) sunucu imleci ilerletmiyor → aynı sayfa turda 200 kez
            // yeniden yazılır, sonsuza dek, hiçbir iz bırakmadan. Ayrımı
            // yazılan satır sayısı veriyor; imleç de sorguyu sunucu tarafında
            // birebir tekrarlamayı sağlıyor.
            if (more)
                _log.LogWarning(
                    "Stok senkronu {MaxPages} sayfada bitmedi ({PageSize} satır/sayfa); "
                  + "{Rows} satır yazıldı, son imleç {Cursor}. Yazılanlar KALICI. "
                  + "Satır sayısı tavana (MaxPages×PageSize) yakınsa defter gerçekten "
                  + "büyümüştür; çok daha küçükse sunucu imleci ilerletmiyor demektir.",
                    MaxPages, PageSize, written, cursor);
            else if (written > 0)
                _log.LogInformation(
                    "Stok senkronu: {Rows} bakiye satırı, {Pages} sayfa", written, pages + 1);
        }
        catch (LicenseApiUnknownException ex)
            when (!ct.IsCancellationRequested && ex.StatusCode is >= 200 and < 300)
        {
            // 2xx ama gövde bozuk: ağ sorunu DEĞİL, sözleşme ihlali. Uyarı
            // seviyesinde saklamak bunu gürültüde kaybederdi.
            _log.LogError(ex, "Stok senkronu bozuk gövde aldı");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Süzgeç TİPTE değil TOKEN'da: HttpClient zaman aşımı
            // TaskCanceledException olarak yüzeye çıkıyor ve bu bir ağ hatası.
            _log.LogWarning(ex, "Stok senkronu başarısız; sonraki turda yeniden denenecek");
        }

        return written;
    }

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return null;

            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Stok senkronu için lisans çözümlenemedi");
            return null;
        }
    }
}
