namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// U12 — yerel yeniden anahtarlamanın ve miras satırı dönüştürmesinin sildiği Id'yi güncel
/// Id'ye çözen SQL parçası (göç 045 <c>CustomerRedirect</c>). Id ile yazan her yol çözümü
/// YAZIMLA AYNI İFADEDE yapar: yazma ifadesi yazma kilidini ifadenin başında alır, yani
/// taşıma çözüm ile yazım arasına giremez. Zincir izlenir (yazımda kısaltıldığı için
/// pratikte tek adım); derinlik sınırı olası bir döngüye karşı. İç sorgu dış satıra
/// bağlı değil: SQLite bir kez hesaplar, Customer'da birincil anahtar araması kalır
/// (Python sqlite3 3.50 ile EXPLAIN QUERY PLAN'da doğrulandı).
/// </summary>
internal static class CustomerIdSql
{
    /// <param name="parameter">Dapper parametresi, ör. <c>"@id"</c>.</param>
    public static string Resolve(string parameter) => $@"COALESCE((
            WITH RECURSIVE hop(Id, Depth) AS (
                SELECT ToId, 1 FROM CustomerRedirect WHERE FromId = {parameter}
                UNION ALL
                SELECT r.ToId, hop.Depth + 1
                  FROM CustomerRedirect r JOIN hop ON r.FromId = hop.Id
                 WHERE hop.Depth < 8)
            SELECT Id FROM hop ORDER BY Depth DESC LIMIT 1), {parameter})";

    /// <summary>
    /// Birden çok Id'yi TEK ifadede çözer (<see cref="Resolve"/> ile aynı zincir ve derinlik
    /// kuralı): yalnız yönlendirilmiş Id'ler için birer <c>(FromId, ToId)</c> satırı döner;
    /// dönmeyen Id kendisidir. Çekilişin kişi başına tekilleştirmesi gibi satır başına
    /// gidiş-dönüşün pahalı olduğu yerler için.
    /// </summary>
    /// <param name="listParameter">Dapper liste parametresi, ör. <c>"@ids"</c> (IN açılımı).</param>
    public static string ResolveMany(string listParameter) => $@"
        WITH RECURSIVE hop(StartId, Id, Depth) AS (
            SELECT FromId, ToId, 1 FROM CustomerRedirect WHERE FromId IN {listParameter}
            UNION ALL
            SELECT hop.StartId, r.ToId, hop.Depth + 1
              FROM CustomerRedirect r JOIN hop ON r.FromId = hop.Id
             WHERE hop.Depth < 8)
        SELECT h.StartId AS FromId, h.Id AS ToId FROM hop h
         WHERE h.Depth = (SELECT MAX(m.Depth) FROM hop m WHERE m.StartId = h.StartId)";
}
