using System.IO;
using Microsoft.Data.Sqlite;

namespace OrderDeck.Core.Storage;

/// <summary>
/// SQLite veritabanı dosyasını dosya sistemi seviyesinde ele alan işlemler.
///
/// Bir SQLite veritabanı tek bir dosya DEĞİLDİR: WAL kipinde işlenmiş (commit
/// edilmiş) veriler bir süre <c>-wal</c> yan dosyasında durur, ana dosyaya
/// ancak checkpoint sırasında geçer. Ana dosyayı <c>File.Copy</c> ile
/// kopyalamak bu yüzden "geçerli ama eski" bir yedek üretir — hata vermez,
/// açılır, sadece son siparişler içinde yoktur. Sessiz veri kaybının tam
/// tanımı bu.
/// </summary>
public static class SqliteFile
{
    /// <summary>
    /// Canlı bir veritabanının tutarlı anlık görüntüsünü <paramref name="destPath"/>
    /// dosyasına yazar. SQLite'ın çevrimiçi yedekleme API'sini kullanır: kaynak
    /// açıkken ve yazılırken bile çalışır, <c>-wal</c> içeriğini de kapsar ve
    /// sonuç tek başına yeterli (yan dosyasız) bir veritabanı dosyasıdır.
    /// </summary>
    /// <remarks>
    /// Havuzlama iki uçta da kapalı: <c>Dispose</c> sonrası dosya tanıtıcısı
    /// havuzda açık kalırsa çağıran taraf dosyayı okuyamaz/silemez (Windows'ta
    /// kilitli kalır).
    /// </remarks>
    public static void Snapshot(string sourcePath, string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);

        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());

        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>
    /// <c>-wal</c> ve <c>-shm</c> yan dosyalarını siler.
    ///
    /// Ana dosyanın üzerine dışarıdan bir veritabanı yazıldığında (geri yükleme)
    /// eski yan dosyalar ZORUNLU olarak temizlenmeli: SQLite kalan WAL'ı yeni
    /// dosyaya aitmiş gibi uygulamaya çalışır ve veritabanını bozar.
    /// </summary>
    public static void DeleteSidecars(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = dbPath + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }
}
