using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services;

/// <summary>
/// Müşteri toplamlarının açılıştaki tek seferlik türetmesi (spec 2026-10-13 §6.1, §8.2-7; W1-03). <c>AppHost</c> göçten
/// ve <see cref="SyncApplyScope.ClearStale"/>'den sonra çağırır; <c>SalesSyncState.AggregatesDerivedAt</c> doluysa
/// hiçbir şey yapmaz (<see cref="CustomerAggregateSql.DeriveAllOnce"/>).
///
/// <para><b>Günlük:</b> yalnız değişen satır sayısı ve toplam fark — müşteri adı, kullanıcı adı ya da Id yok.</para>
///
/// <para><b>Hata açılışı durdurmaz:</b> türetme tek işlemdir; düşerse geri sarılır, damga yazılmaz ve sonraki açılışta
/// yeniden denenir. O zamana dek sayaçlar bugünkü değerinde kalır — canlı yayın bundan etkilenmez.</para>
/// </summary>
public static class CustomerAggregatesBoot
{
    /// <returns>Hesap sonucu; damga zaten doluysa ya da hesap düştüyse <c>null</c>.</returns>
    public static AggregateRecomputeResult? Run(IDbConnectionFactory factory, long nowUnix, ILogger log)
    {
        try
        {
            var result = CustomerAggregateSql.DeriveAllOnce(factory, nowUnix);
            if (result is not null)
                log.LogInformation(
                    "Müşteri toplamları etiketlerden türetildi (ilk açılış): {ChangedRows} satır değişti, toplam ciro farkı {TotalAbsDiff} TL",
                    result.ChangedRows, result.TotalAbsDiff);
            return result;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Müşteri toplamları türetilemedi; sonraki açılışta yeniden denenecek");
            return null;
        }
    }
}
