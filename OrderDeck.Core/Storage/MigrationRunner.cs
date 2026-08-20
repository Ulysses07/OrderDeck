using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;

namespace OrderDeck.Core.Storage;

/// <summary>
/// Applies embedded `Storage/Migrations/NNN_*.sql` scripts in lexical order, skipping
/// scripts whose number is already recorded in `_meta.SchemaVersion`. Each script must
/// end with `UPDATE _meta SET SchemaVersion = N WHERE Id = 1;` to advance the counter.
/// </summary>
public sealed class MigrationRunner
{
    private const string MigrationPrefix = "OrderDeck.Core.Storage.Migrations.";

    private readonly IDbConnectionFactory _factory;
    private readonly IEnumerable<(int Version, string Sql)> _scripts;

    public MigrationRunner(IDbConnectionFactory factory)
        : this(factory, LoadEmbeddedScripts())
    {
    }

    /// <summary>
    /// Script kaynağını dışarıdan alan aşırı yükleme. Gömülü script'ler her zaman
    /// geçerli olduğu için üretimde kullanılmaz; kasten bozuk bir script vererek
    /// yarım göçün geri alındığını doğrulamanın başka yolu yok.
    /// </summary>
    public MigrationRunner(
        IDbConnectionFactory factory, IEnumerable<(int Version, string Sql)> scripts)
    {
        _factory = factory;
        _scripts = scripts;
    }

    public void Run()
    {
        using var conn = _factory.Open();

        var hasMeta = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='_meta'") > 0;

        int currentVersion = hasMeta
            ? conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1")
            : 0;

        // Yabancı anahtar denetimi göç boyunca kapalı. Şema değiştiren script'ler
        // tablo yeniden kurma (yeni tablo → kopyala → eski tabloyu düşür → adını
        // değiştir) yapıyor; denetim açıkken aradaki tutarsız anlar hem patlayabilir
        // hem de DROP TABLE'ın örtük silmesi ON DELETE CASCADE'i tetikleyip çocuk
        // satırları süpürebilir. SQLite'ın kendi belgelediği reçete bu.
        //
        // PRAGMA'nın transaction DIŞINDA olması ŞART: transaction içinde sessizce
        // hiçbir şey yapmaz. 002_pivot_to_labels.sql kendi içinde bu pragma'yı
        // çağırıyor ama artık transaction içinde kaldığı için etkisiz — bu satır
        // onun da yerini tutuyor.
        conn.Execute("PRAGMA foreign_keys = OFF;");
        try
        {
            foreach (var (version, sql) in _scripts)
            {
                if (version <= currentVersion) continue;

                // Her script tek bir transaction. Öncesinde her ifade kendi örtük
                // transaction'ında koşuyordu: script ortada patlarsa ilk ifadeler
                // uygulanmış ama SchemaVersion ilerlememiş oluyordu. Sonraki açılışta
                // aynı script baştan koşuyor ve "table already exists" ile ölüyordu —
                // yani veritabanı yarı göç etmiş, uygulama da hiç açılamaz hâlde
                // kalıyordu. Artık ya script'in tamamı ya hiçbiri.
                using var tx = conn.BeginTransaction();
                try
                {
                    conn.Execute(sql, transaction: tx);
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
                currentVersion = version;
            }
        }
        finally
        {
            conn.Execute("PRAGMA foreign_keys = ON;");
        }
    }

    /// <summary>
    /// Yields (version, sql) pairs sorted by lexical filename. Filename pattern is
    /// `NNN_description.sql` where NNN is the integer version. Files that don't start
    /// with three digits are skipped.
    /// </summary>
    private static IEnumerable<(int Version, string Sql)> LoadEmbeddedScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix) && n.EndsWith(".sql"))
            .OrderBy(n => n);

        foreach (var name in resourceNames)
        {
            var leaf = name.Substring(MigrationPrefix.Length);
            if (leaf.Length < 4 || !int.TryParse(leaf.Substring(0, 3), out var version))
                continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            yield return (version, reader.ReadToEnd());
        }
    }
}
