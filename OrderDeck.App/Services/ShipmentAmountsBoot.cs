using Microsoft.Extensions.Logging;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;

namespace OrderDeck.App.Services;

/// <summary>
/// Açık kargo dosyalarının açılıştaki tek seferlik türetmesi — Y5'in fark tabanı (spec 2026-10-13 §7.4, §8.2-8;
/// W1-04). <c>AppHost</c> göçten, <see cref="SyncApplyScope.ClearStale"/>'den (kalmış kilit satırı kuyruğa almayı
/// susturur) ve müşteri toplamlarının türetmesinden sonra, arayüzden önce çağırır;
/// <c>SalesSyncState.ShipmentsDerivedAt</c> doluysa hiçbir şey yapmaz
/// (<see cref="ShipmentAmountDerivation.DeriveAllOnce"/>).
///
/// <para><b>Günlük:</b> yalnız değişen dosya sayısı ve toplam fark — müşteri adı, kullanıcı adı ya da Id yok.</para>
///
/// <para><b>Hata açılışı durdurmaz:</b> türetme tek işlemdir; düşerse geri sarılır, damga yazılmaz ve sonraki açılışta
/// yeniden denenir. O zamana dek dosya tutarları bugünkü değerinde kalır — canlı yayın bundan etkilenmez. Damga boşken
/// Y5 de dosya tutarına yazmaz (<see cref="ShipmentAmountAdjuster"/>): iptal, geri alma ve fiyat düzeltmesi
/// türetilmemiş tabana fark eklemez; tutarı başarılı türetme etiketlerden kurar.</para>
/// </summary>
public static class ShipmentAmountsBoot
{
    /// <returns>Türetme sonucu; damga zaten doluysa ya da türetme düştüyse <c>null</c>.</returns>
    public static ShipmentDerivationResult? Run(IDbConnectionFactory factory, long nowUnix, ILogger log)
    {
        try
        {
            var result = ShipmentAmountDerivation.DeriveAllOnce(factory, nowUnix);
            if (result is not null)
                log.LogInformation(
                    "Açık kargo dosyaları bağlı etiketlerden türetildi (ilk açılış): {ChangedFiles} dosya değişti, toplam fark {TotalAbsDiff} TL",
                    result.ChangedFiles, result.TotalAbsDiff);
            return result;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Açık kargo dosyaları türetilemedi; sonraki açılışta yeniden denenecek");
            return null;
        }
    }
}
