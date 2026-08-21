using Dapper;

namespace OrderDeck.Core.Storage;

/// <summary>
/// Depo metotlarının "çağıran bir işlem verdiyse ona katıl, vermediyse kendi
/// bağlantını aç" davranışını tek yerde tutar.
///
/// Bu ikilik bilinçli: <see cref="DbWrite"/> parametresi <b>opsiyonel</b>
/// olduğu için mevcut çağıranların hiçbiri değişmek zorunda kalmıyor ve
/// davranışları da bit düzeyinde aynı kalıyor. Yalnız birden çok depoya
/// yazan iş akışları paketi doldurur.
/// </summary>
internal static class DbWriteExtensions
{
    public static void Execute(
        this IDbConnectionFactory factory, DbWrite? write, string sql, object? param = null)
    {
        if (write is not null)
        {
            write.Connection.Execute(sql, param, write.Transaction);
            return;
        }

        using var conn = factory.Open();
        conn.Execute(sql, param);
    }
}
