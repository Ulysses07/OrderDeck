using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Göç provalarının "var olan veri değişmez" denetimi: göçten ÖNCE var olan her sıradan tablonun ve
/// kolonun içeriğinin özeti. Kolon listesi göçten önce alınır (<see cref="Columns"/>), özet göçten
/// sonra AYNI kolonlarla yeniden hesaplanır (<see cref="Of"/>): yalnız ekleyici bir göç özeti
/// değiştirmez, eklediği kolonlar özete girmez. <c>_meta</c> (sürüm numarası değişir) ve arama
/// indeksinin FTS tabloları (içerik Customer'dan) dışarıda.
/// </summary>
internal static class SchemaDigest
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Columns(IDbConnection c)
        => c.Query<string>(@"SELECT name FROM sqlite_master
                              WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'
                                AND name <> '_meta' AND name NOT LIKE 'CustomerFts%'
                              ORDER BY name")
            .ToDictionary(
                t => t,
                t => (IReadOnlyList<string>)c.Query<string>("SELECT name FROM pragma_table_info(@t) ORDER BY cid", new { t }).ToList(),
                StringComparer.Ordinal);

    public static string Of(IDbConnection c, IReadOnlyDictionary<string, IReadOnlyList<string>> columns)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (table, cols) in columns.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\u001e"));
            var list = string.Join(", ", cols.Select(col => $"\"{col}\""));
            using var reader = c.ExecuteReader($"SELECT {list} FROM \"{table}\" ORDER BY rowid");
            while (reader.Read())
                for (var i = 0; i < reader.FieldCount; i++)
                    hash.AppendData(Encoding.UTF8.GetBytes(
                        (reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)) + "\u001f"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
