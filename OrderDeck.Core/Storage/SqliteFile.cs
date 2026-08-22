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

    /// <summary>SQLite dosya başlığı: ilk 16 bayt, sonundaki NUL dahil.</summary>
    private static ReadOnlySpan<byte> HeaderMagic => "SQLite format 3\u0000"u8;

    /// <summary>
    /// Dosyanın gerçekten açılabilir, sayfa yapısı tutarlı bir SQLite
    /// veritabanı olduğunu doğrular.
    ///
    /// <para>İki aşamalı: önce 16 baytlık dosya başlığı (ucuz eleme — yanlış
    /// içerik, kırpılmış indirme, HTML hata sayfası buradan döner), sonra
    /// <c>PRAGMA quick_check</c>. <c>quick_check</c> tercih edildi çünkü
    /// <c>integrity_check</c>'in yaptığı pahalı indeks-içerik çapraz
    /// doğrulaması dışında her şeyi yapar ve büyük bir veritabanında
    /// saniyeler yerine milisaniyeler sürer; geri yükleme akışında kullanıcı
    /// bekliyor.</para>
    ///
    /// <para>Bağlantı <c>ReadWrite</c> açılıyor, <c>ReadOnly</c> DEĞİL: WAL
    /// kipindeki bir veritabanını salt-okunur açmak <c>-shm</c> dosyası
    /// yoksa "unable to open database file" ile patlar ve sağlam bir dosyaya
    /// "bozuk" damgası vurur. <c>Create</c> yok — olmayan dosya doğrulamayı
    /// geçmemeli.</para>
    /// </summary>
    /// <param name="error">Başarısızsa insan okuyabilir sebep; başarılıysa null.</param>
    public static bool IsIntactDatabase(string path, out string? error)
    {
        if (!File.Exists(path))
        {
            error = "dosya yok";
            return false;
        }

        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < HeaderMagic.Length)
            {
                error = $"dosya SQLite başlığı için fazla küçük ({fs.Length} bayt)";
                return false;
            }

            Span<byte> head = stackalloc byte[16];
            fs.ReadExactly(head);
            if (!head.SequenceEqual(HeaderMagic))
            {
                error = "SQLite dosya başlığı yok";
                return false;
            }
        }
        catch (IOException ex)
        {
            error = $"dosya okunamadı: {ex.Message}";
            return false;
        }

        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());

            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar() as string;

            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                error = $"quick_check: {result ?? "(sonuç yok)"}";
                return false;
            }
        }
        catch (SqliteException ex)
        {
            error = $"açılamadı: {ex.Message}";
            return false;
        }

        error = null;
        return true;
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
