using System.Data;
using Microsoft.Data.Sqlite;

namespace OrderDeck.Core.Storage;

public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly object _journalGate = new();
    private bool _journalConfigured;

    public SqliteConnectionFactory(string filePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public IDbConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        EnsureWalMode(conn);
        return conn;
    }

    /// <summary>
    /// Veritabanını WAL (write-ahead log) kipine alır. Varsayılan rollback-journal
    /// kipinde yazma sırasında elektrik kesilirse ya da uygulama çökerse toparlanma
    /// ayrı bir <c>-journal</c> dosyasına bağlıdır; WAL hem çökme sonrası
    /// toparlanmayı sağlamlaştırır hem de yazma sürerken okumaları engellemez —
    /// yayın sırasında etiket yazılırken müşteri listesi donmasın diye.
    ///
    /// Kip veritabanı dosyasının başlığında saklanır, yani kalıcıdır: bir kez
    /// kurulur, sonraki açılışlarda zaten WAL'dir. Yine de her <see cref="Open"/>
    /// çağrısında PRAGMA çalıştırmıyoruz — sıcak yolda (Dapper sorguları) gereksiz
    /// gidiş-dönüş olurdu; süreç ömrü boyunca bir kez yeter.
    ///
    /// <c>synchronous</c> bilerek varsayılanda (FULL) bırakıldı. WAL ile birlikte
    /// yaygın öneri NORMAL'dir ve daha hızlıdır, ama elektrik kesintisinde son
    /// işlemleri kaybettirebilir. Mezat modelinde son işlem çoğu zaman yeni bir
    /// sipariş demek; hızı bunun için takas etmiyoruz.
    ///
    /// Bellek-içi veritabanlarında (testler) journal kipi değiştirilemez; PRAGMA
    /// hata vermez, "memory" döner ve geçilir.
    /// </summary>
    private void EnsureWalMode(SqliteConnection conn)
    {
        if (_journalConfigured) return;
        lock (_journalGate)
        {
            if (_journalConfigured) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteScalar();
            _journalConfigured = true;
        }
    }
}
