using System.Data;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Yok sayılan ya da reddedilen satış değişikliklerinin izi (göç 049'un <c>SalesConflictLog</c> tablosu; spec
/// 2026-10-13 §3.2, §5.2). Yalnız kimlik taşır: ad, kullanıcı adı ya da mesaj metni YAZILMAZ (§9).
///
/// <para><b>Oturum düzeyindeki kayıt:</b> <c>LabelId</c> boştur, oturumun kimliği <c>Reason</c>'da
/// <see cref="SessionReason"/> biçimiyle durur (tablonun oturum kolonu yok; göç yalnız ekleyici).</para>
/// </summary>
public static class SalesConflictLog
{
    /// <summary>Sunucu, bu kopyanın kapanışını ya da devir isteğini başka kopyanın oturumu diye yok saydı
    /// (§4.1, §8.7: sahip olmayanın kapanışı). Son durum: oturum yeniden gönderilmez, <c>server</c> olmaz.</summary>
    public const string SessionOwnedElsewhere = "session-owned-elsewhere";

    /// <summary>Oturum düzeyindeki kaydın <c>Reason</c>'ı: <c>session:{id}</c>.</summary>
    public static string SessionReason(string sessionId) => $"session:{sessionId}";

    /// <summary>Çağıranın işleminde tek satır ekler.</summary>
    internal static void Insert(
        IDbConnection connection, IDbTransaction? transaction, long at, string? labelId, string kind, string? reason)
        => connection.Execute(
            "INSERT INTO SalesConflictLog (At, LabelId, Kind, Reason) VALUES (@at, @labelId, @kind, @reason)",
            new { at, labelId, kind, reason }, transaction);
}
