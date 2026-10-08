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

    /// <summary>Okuma da aynı kural: paket açıkken paketin bağlantısından (U17). İşlemi açık
    /// bağlantıda işlemsiz komut Microsoft.Data.Sqlite'ta reddedilir; başka bir bağlantı ise
    /// paketin henüz commit edilmemiş satırını görmez (ve denetim açıkken açılamaz).</summary>
    public static T? QueryFirstOrDefault<T>(
        this IDbConnectionFactory factory, DbWrite? write, string sql, object? param = null)
    {
        if (write is not null) return write.Connection.QueryFirstOrDefault<T>(sql, param, write.Transaction);
        using var conn = factory.Open();
        return conn.QueryFirstOrDefault<T>(sql, param);
    }

    /// <inheritdoc cref="QueryFirstOrDefault{T}"/>
    public static IReadOnlyList<T> Query<T>(
        this IDbConnectionFactory factory, DbWrite? write, string sql, object? param = null)
    {
        if (write is not null) return write.Connection.Query<T>(sql, param, write.Transaction).ToList();
        using var conn = factory.Open();
        return conn.Query<T>(sql, param).ToList();
    }

    /// <inheritdoc cref="QueryFirstOrDefault{T}"/>
    public static T? ExecuteScalar<T>(
        this IDbConnectionFactory factory, DbWrite? write, string sql, object? param = null)
    {
        if (write is not null) return write.Connection.ExecuteScalar<T>(sql, param, write.Transaction);
        using var conn = factory.Open();
        return conn.ExecuteScalar<T>(sql, param);
    }
}
