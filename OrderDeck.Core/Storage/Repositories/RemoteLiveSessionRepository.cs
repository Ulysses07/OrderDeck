using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary><c>RemoteLiveSession.State</c> değerleri (göç 049'un CHECK kısıtı; <c>sales/state</c>'teki yazımla aynı).</summary>
public static class RemoteLiveSessionState
{
    public const string Live = "live";
    public const string HandingOver = "handing-over";

    public static bool IsKnown(string? state) => state is Live or HandingOver;
}

/// <summary>Başka bilgisayarın canlı ya da devrediliyor yayını (§3.2, §4.5). Zamanlar unix saniye, Id "N".</summary>
/// <param name="OwnerLastSeenAt">Sahip kopyanın son kalp atışı (çipteki "son sinyal").</param>
/// <param name="UnsentLabelCount">Devrediliyor yayında sahibin bildirdiği gönderilmemiş etiket sayısı.</param>
public sealed record RemoteLiveSessionRow(
    string Id,
    string? Title,
    long StartedAt,
    long? EndedAt,
    string? OwnerMachineName,
    long? OwnerLastSeenAt,
    string State,
    int? UnsentLabelCount);

/// <summary>
/// <c>RemoteLiveSession</c> tablosunun yazıcısı ve okuyucusu (göç 049; spec 2026-10-13 §3.2, §4.5, M5). Satış akışı turu
/// her mod On turunda <c>sales/state</c>'in <c>liveSessions</c> listesini buraya aynalar (<see cref="Mirror"/>).
///
/// <para><b>Bu satırlar <c>StreamSession</c>'a hiç girmez</b> ve bu sınıf <c>StreamSession</c>'a hiç yazmaz:
/// <c>GetActive</c> ("<c>EndedAt IS NULL</c> olan herhangi bir satır") ve açılıştaki kurtarma ekranı başka bilgisayarın
/// yayınını görmez. Id'si <c>StreamSession</c>'da olan yayın (bu bilgisayarın kendi oturumu ya da akıştan inmiş uzak
/// oturum) aynaya alınmaz; aynada kalmışsa silinir.</para>
/// </summary>
public sealed class RemoteLiveSessionRepository
{
    private readonly IDbConnectionFactory _factory;

    public RemoteLiveSessionRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>Aynadaki bütün yayınlar, başlama sırasıyla.</summary>
    public IReadOnlyList<RemoteLiveSessionRow> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<Row>(
                """
                SELECT Id, Title, StartedAt, EndedAt, OwnerMachineName, OwnerLastSeenAt, State, UnsentLabelCount
                FROM RemoteLiveSession ORDER BY StartedAt, Id
                """)
            .Select(r => new RemoteLiveSessionRow(r.Id, r.Title, r.StartedAt, r.EndedAt, r.OwnerMachineName,
                r.OwnerLastSeenAt, r.State, (int?)r.UnsentLabelCount))
            .ToList();
    }

    /// <summary>
    /// Aynayı tek işlemde verilen listeye eşitler: listede olup <c>StreamSession</c>'da olmayan yayınlar eklenir ya
    /// da güncellenir, öbürleri silinir. Bilinmeyen hâl taşıyan satır alınmaz (sözleşme dışı; aynada eski hâli de
    /// kalmaz). Değeri aynı satır yeniden yazılmaz: 30 sn'lik tur boşuna yazma kilidi almasın.
    /// </summary>
    /// <returns>Eklenen, değişen ve silinen satır sayısı (0 = ayna zaten güncel).</returns>
    public int Mirror(IReadOnlyCollection<RemoteLiveSessionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var wanted = rows
            .Where(r => !string.IsNullOrEmpty(r.Id) && RemoteLiveSessionState.IsKnown(r.State))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        using var write = DbWrite.Begin(_factory);
        var conn = write.Connection;
        var tx = write.Transaction;

        // BEGIN IMMEDIATE: yazma kilidi okumadan önce alındı — okumayla yazım arasında akış oturumu ekleyemez.
        var ids = wanted.Select(r => r.Id).ToList();
        var inSessions = ids.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : conn.Query<string>("SELECT Id FROM StreamSession WHERE Id IN @ids", new { ids }, tx)
                .ToHashSet(StringComparer.Ordinal);
        wanted.RemoveAll(r => inSessions.Contains(r.Id));
        var keep = wanted.Select(r => r.Id).ToList();

        var changed = keep.Count == 0
            ? conn.Execute("DELETE FROM RemoteLiveSession", transaction: tx)
            : conn.Execute("DELETE FROM RemoteLiveSession WHERE Id NOT IN @keep", new { keep }, tx);
        foreach (var r in wanted)
            changed += conn.Execute(UpsertSql, r, tx);

        write.Commit();
        return changed;
    }

    /// <summary>Bir yayını aynadan siler (W2-09: bu bilgisayarın force-end'i başardı — yayın artık canlı değil; sonraki
    /// <c>sales/state</c> onu zaten listelemez).</summary>
    /// <returns>Silinen satır var mıydı.</returns>
    public bool Remove(string id)
    {
        using var conn = _factory.Open();
        return conn.Execute("DELETE FROM RemoteLiveSession WHERE Id = @id", new { id }) > 0;
    }

    /// <summary>Değeri farklıysa günceller (<c>IS NOT</c>: NULL'a dayanıklı); aynıysa 0 satır.</summary>
    private const string UpsertSql = """
        INSERT INTO RemoteLiveSession
            (Id, Title, StartedAt, EndedAt, OwnerMachineName, OwnerLastSeenAt, State, UnsentLabelCount)
        VALUES (@Id, @Title, @StartedAt, @EndedAt, @OwnerMachineName, @OwnerLastSeenAt, @State, @UnsentLabelCount)
        ON CONFLICT (Id) DO UPDATE SET
            Title = excluded.Title,
            StartedAt = excluded.StartedAt,
            EndedAt = excluded.EndedAt,
            OwnerMachineName = excluded.OwnerMachineName,
            OwnerLastSeenAt = excluded.OwnerLastSeenAt,
            State = excluded.State,
            UnsentLabelCount = excluded.UnsentLabelCount
        WHERE RemoteLiveSession.Title IS NOT excluded.Title
           OR RemoteLiveSession.StartedAt IS NOT excluded.StartedAt
           OR RemoteLiveSession.EndedAt IS NOT excluded.EndedAt
           OR RemoteLiveSession.OwnerMachineName IS NOT excluded.OwnerMachineName
           OR RemoteLiveSession.OwnerLastSeenAt IS NOT excluded.OwnerLastSeenAt
           OR RemoteLiveSession.State IS NOT excluded.State
           OR RemoteLiveSession.UnsentLabelCount IS NOT excluded.UnsentLabelCount
        """;

    /// <summary>Dapper ara sınıfı: SQLite INTEGER → long.</summary>
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public long StartedAt { get; set; }
        public long? EndedAt { get; set; }
        public string? OwnerMachineName { get; set; }
        public long? OwnerLastSeenAt { get; set; }
        public string State { get; set; } = "";
        public long? UnsentLabelCount { get; set; }
    }
}
