using System.Data;
using Microsoft.Data.Sqlite;

namespace OrderDeck.Core.Storage;

public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly object _journalGate = new();
    private bool _journalConfigured;

    /// <summary>
    /// Yazma kilidi çakışmasında bir bağlantının bekleyeceği en uzun süre
    /// (saniye). Testlerin ve tanılamanın sınırı okuyabilmesi için açık.
    /// </summary>
    public const int WriteContentionTimeoutSeconds = 10;

    public SqliteConnectionFactory(string filePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            // Yazma kilidi çakışmasında bekleme bütçesi.
            //
            // NEDEN BURADA, NEDEN `PRAGMA busy_timeout` DEĞİL. Kilit çakışması
            // SQLite'tan SQLITE_BUSY olarak döner; Microsoft.Data.Sqlite bunu
            // görünce komutu KENDİ döngüsünde, CommandTimeout dolana dek
            // yeniden dener. Yani bekleme süresini belirleyen şey SQLite'ın
            // busy handler'ı değil, sürücünün bütçesi. Ölçüldü (bir yazar
            // kilidi tutarken ikinci yazar):
            //
            //   ayar yok                            → 30.033 ms sonra hata
            //   PRAGMA busy_timeout = 3000          → 31.428 ms sonra hata
            //   Default Timeout = 5                 →  5.047 ms sonra hata
            //   busy_timeout = 3000 + Timeout = 5   →  6.910 ms sonra hata
            //
            // busy_timeout tek başına hiçbir şeyi bağlamıyor; ikisi birlikte
            // ise bütçeyi AŞIRIYOR, çünkü sürücü bütçeyi ancak iki deneme
            // ARASINDA kontrol edebiliyor ve SQLite tek denemenin içinde
            // uyuyor. Bu yüzden busy_timeout bilerek kurulmuyor.
            //
            // 10 sn neden yeter: buradaki her yazma işlemi birkaç satırlık ve
            // milisaniyeler sürüyor; 10 sn boyunca serbest kalmayan bir kilit
            // geçici çekişme değil, takılmış bir işlem demektir. Görünmez 30
            // sn'lik varsayılan ise operatörün arayüzünü yarım dakika
            // dondurabiliyordu. Bütçe dolduğunda yazma hata veriyor; senkron
            // servisleri imleçlerini yalnızca başarılı yazmadan sonra
            // ilerlettiği için bir sonraki turda yeniden denenir.
            DefaultTimeout = WriteContentionTimeoutSeconds
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
