using System.Data;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Bir yeniden hesabın sonucu: değeri değişip yazılan müşteri satırı sayısı ve ciro farklarının mutlak
/// toplamı (TL). Günlüğe yalnız bunlar yazılır; ad ya da Id yazılmaz (spec 2026-10-13 §6.1).</summary>
public sealed record AggregateRecomputeResult(int ChangedRows, decimal TotalAbsDiff)
{
    public static AggregateRecomputeResult None { get; } = new(0, 0m);
}

/// <summary>
/// Müşteri toplamları (<c>Customer.TotalLabelsPrinted</c>, <c>Customer.TotalAmount</c>) etiketlerden TÜRETİLİR
/// (spec 2026-10-13 §6.1, W1). Eskiden bilgisayar başına fark sayacıydılar (<c>+ delta</c>): beş ayrı yol
/// (baskı, iptal, iptali geri alma, fiyat düzeltme, yedek onayı) kuralı elle tekrarlıyordu, kaçan ya da iki
/// kez uygulanan bir olay sayacı kalıcı kaydırıyordu ve sunucudan inen müşteri 0/0 ile açılıyordu.
///
/// <para><b>Kural</b> <see cref="OrderDeck.Shared.Sales.LabelRules.Contribution"/>'ın SQL karşılığıdır: basılmış ∧
/// iptal edilmemiş ∧ geçici yedek değil → (1, Price). Kargo ücreti satırı ve dosya durumu kuralın dışındadır
/// (bugün de öyle). Eşliği <c>CustomerAggregateSqlTests</c> ortak vektörlerle sabitler.</para>
///
/// <para><b>İdempotent:</b> aynı etiketlerden iki hesap aynı değeri verir; yalnız değeri farklı satır yazılır.
/// Ciro <c>TOTAL(Price)</c> ile ikili kayan noktada toplanır (kolonlar REAL) ve aynı değer olarak geri yazılır
/// (double parametre): Id listesiyle hesap ile tam hesap aynı ilişkili alt sorguyu kullanır, satır sırası
/// <c>IX_Label_Customer</c> sırasıdır — iki yol bit düzeyinde aynı sonucu verir, ikinci hesap sıfır fark üretir.</para>
///
/// <para><b>Gönderim kuyruğu ve damgalar:</b> <c>TotalLabelsPrinted</c>, <c>TotalAmount</c> ve <c>LastSeenAt</c>
/// hiçbir tetikleyicinin <c>UPDATE OF</c> listesinde değil (036/045): yeniden hesap SyncSeq'i ilerletmez, birim
/// damgası basmaz, müşteriyi yeniden göndermez.</para>
///
/// <para><b>Bağlantı (U17):</b> her çağrı verilen paketin/kapsamın bağlantısı ve işlemiyle koşar; ikinci bağlantı
/// açmaz. Id'ler <see cref="CustomerIdSql"/> ile güncel karşılıklarına çözülür (okuma ile yazma arasında yerel
/// taşıma olmuşsa toplam asıl kayda yazılır).</para>
/// </summary>
public static class CustomerAggregateSql
{
    /// <summary><see cref="OrderDeck.Shared.Sales.LabelRules.Contribution"/>: katkısı olan etiket (takma ad
    /// <c>l</c>).</summary>
    internal const string ContributesCondition =
        "l.PrintedAt IS NOT NULL AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0";

    /// <summary>Müşteri başına eski ve türetilmiş değer. İlişkili alt sorgular IX_Label_Customer'dan okur;
    /// iki yol (Id listesi ve tam hesap) bu metni paylaşır — sıra ve dolayısıyla toplam aynı.</summary>
    private const string DerivedSql = $@"
        SELECT Id, OldCount, OldAmount, NewCount, NewAmount
          FROM (SELECT c.Id AS Id,
                       c.TotalLabelsPrinted AS OldCount,
                       c.TotalAmount AS OldAmount,
                       (SELECT COUNT(*) FROM Label l WHERE l.CustomerId = c.Id AND {ContributesCondition}) AS NewCount,
                       (SELECT TOTAL(l.Price) FROM Label l WHERE l.CustomerId = c.Id AND {ContributesCondition}) AS NewAmount
                  FROM Customer c
                  /**where**/)
         WHERE OldCount IS NOT NewCount OR OldAmount IS NOT NewAmount";

    /// <summary>Id çözümünün parça boyu (SQLite parametre sınırının çok altında; <c>CustomerRepository.ResolveIds</c>
    /// ile aynı).</summary>
    private const int ChunkSize = 900;

    /// <summary>Verilen müşterilerin toplamlarını paketin işleminde etiketlerden yeniden hesaplar.</summary>
    public static AggregateRecomputeResult Recompute(IEnumerable<string> customerIds, DbWrite write)
        => Recompute(write.Connection, write.Transaction, customerIds);

    /// <summary>
    /// Verilen müşterilerin toplamlarını verilen işlemde (ör. <see cref="SyncApplyScope"/>'un yeniden
    /// anahtarlaması) etiketlerden yeniden hesaplar. Taşınmış Id asıl kayda çözülür; yerelde olmayan Id atlanır.
    /// </summary>
    public static AggregateRecomputeResult Recompute(
        IDbConnection conn, IDbTransaction tx, IEnumerable<string> customerIds)
    {
        var resolved = Resolve(conn, tx, customerIds);
        if (resolved.Count == 0) return AggregateRecomputeResult.None;

        var changed = 0;
        var diff = 0m;
        foreach (var chunk in resolved.Chunk(ChunkSize))
        {
            var r = RecomputeWhere(conn, tx, "WHERE c.Id IN @ids", new { ids = chunk });
            changed += r.ChangedRows;
            diff += r.TotalAbsDiff;
        }
        return new AggregateRecomputeResult(changed, diff);
    }

    /// <summary>Bütün müşterilerin toplamlarını yeniden hesaplar (açılıştaki tek seferlik türetme, §8.2-7).</summary>
    public static AggregateRecomputeResult RecomputeAll(DbWrite write)
        => RecomputeAll(write.Connection, write.Transaction);

    /// <inheritdoc cref="RecomputeAll(DbWrite)"/>
    public static AggregateRecomputeResult RecomputeAll(IDbConnection conn, IDbTransaction tx)
        => RecomputeWhere(conn, tx, "", null);

    /// <summary>
    /// <c>LastSeenAt</c>'i bugünkü anlamıyla ilerletir: yalnız yerel olayda, yalnız eskiden fark sayacına dokunan
    /// müşteriler için ve aynı değerle (<c>= now</c>, en büyüğü değil). Hangi müşterinin dokunulacağını çağıran
    /// seçer (<c>LabelService</c>); yeniden hesap bu kolona hiç dokunmaz.
    /// </summary>
    public static void TouchLastSeen(IEnumerable<string> customerIds, long nowUnix, DbWrite write)
    {
        var ids = customerIds.Distinct(StringComparer.Ordinal).Select(id => new { id, now = nowUnix }).ToList();
        if (ids.Count == 0) return;
        write.Connection.Execute(
            "UPDATE Customer SET LastSeenAt = @now WHERE Id = " + CustomerIdSql.Resolve("@id"),
            ids, write.Transaction);
    }

    /// <summary>
    /// Açılışta bir kez (spec §6.1, §8.2-7): <c>SalesSyncState.AggregatesDerivedAt</c> boşsa bütün toplamları
    /// TEK işlemde yeniden hesaplar ve damgayı yazar. Damga doluysa hiçbir şey yazmaz.
    /// </summary>
    /// <returns>Hesap sonucu; damga zaten doluysa <c>null</c>.</returns>
    public static AggregateRecomputeResult? DeriveAllOnce(IDbConnectionFactory factory, long nowUnix)
    {
        using var write = DbWrite.Begin(factory);
        var conn = write.Connection;
        var tx = write.Transaction;
        // BEGIN IMMEDIATE: yazma kilidi okumadan önce alındı — iki açılış (ya da açılış ve başka yazıcı) aynı
        // damgayı ikinci kez kararlaştıramaz.
        if (conn.ExecuteScalar<long?>("SELECT AggregatesDerivedAt FROM SalesSyncState WHERE Id = 1", transaction: tx)
            is not null)
            return null;

        var result = RecomputeAll(conn, tx);
        if (conn.Execute(
                "UPDATE SalesSyncState SET AggregatesDerivedAt = @now WHERE Id = 1 AND AggregatesDerivedAt IS NULL",
                new { now = nowUnix }, tx) != 1)
            throw new InvalidOperationException("SalesSyncState satırı yok (göç 049); toplamlar türetilmedi.");
        write.Commit();
        return result;
    }

    private static List<string> Resolve(IDbConnection conn, IDbTransaction tx, IEnumerable<string> customerIds)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in customerIds)
            if (!string.IsNullOrEmpty(id)) map[id] = id;
        if (map.Count == 0) return [];

        foreach (var chunk in map.Keys.ToList().Chunk(ChunkSize))
            foreach (var (from, to) in conn.Query<(string From, string To)>(
                         CustomerIdSql.ResolveMany("@ids"), new { ids = chunk }, tx))
                map[from] = to;
        return map.Values.Distinct(StringComparer.Ordinal).ToList();
    }

    private static AggregateRecomputeResult RecomputeWhere(
        IDbConnection conn, IDbTransaction tx, string where, object? param)
    {
        var rows = conn.Query<Row>(DerivedSql.Replace("/**where**/", where), param, tx).ToList();
        if (rows.Count == 0) return AggregateRecomputeResult.None;

        // Türetilmiş değer okunduğu double olarak geri yazılır: decimal parametre metin olarak bağlanır ve REAL'e
        // yeniden çevrilirken son hanesi değişebilirdi — o zaman ikinci hesap yine "farklı" bulurdu.
        conn.Execute(
            "UPDATE Customer SET TotalLabelsPrinted = @NewCount, TotalAmount = @NewAmount WHERE Id = @Id",
            rows, tx);
        var diff = rows.Sum(r => Math.Abs(ToDecimal(r.NewAmount) - ToDecimal(r.OldAmount)));
        return new AggregateRecomputeResult(rows.Count, diff);
    }

    private static decimal ToDecimal(double value) => (decimal)value;

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public long OldCount { get; set; }
        public double OldAmount { get; set; }
        public long NewCount { get; set; }
        public double NewAmount { get; set; }
    }
}
