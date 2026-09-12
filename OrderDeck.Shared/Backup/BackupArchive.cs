using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OrderDeck.Shared.Backup;

/// <summary>
/// Masaüstü geri yüklemesi ile sunucudaki tatbikatın ORTAK yedek sözleşmesi.
///
/// <para>İkisi de aynı arşivi inceliyor ama farklı sorular soruyordu: masaüstü
/// arşivin <b>kökündeki</b> <c>orderdeck.db</c> girdisini açıyor, tatbikat ise
/// "arşivde bulduğun ilk <c>*.db</c>" diyordu. Arşive <c>foreign.db</c> adıyla
/// sağlam ama OrderDeck'e ait olmayan tek tabloluk bir SQLite konulduğunda
/// tatbikat YEŞİL dönüyor, gerçek geri yükleme ise çöküyordu — yani alarm
/// tam da uyarması gereken yedekte susuyordu. İki taraf artık aynı dosyaya
/// bakmak zorunda.</para>
///
/// <para>İkinci ayrım: "açılabilir SQLite" ile "OrderDeck veritabanı" aynı şey
/// değil. <c>PRAGMA integrity_check</c> yabancı bir şemayı kusursuz sayar.
/// Aynı yabancı dosya <c>orderdeck.db</c> adıyla konulduğunda geri yükleme
/// BAŞARILI dönüyor ve aktif veritabanının — Customer tablosu dahil — yerine
/// geçiyordu. Bu yüzden kimlik de doğrulanıyor: <c>_meta</c> şema sürümü ve
/// uygulamanın çekirdek tabloları.</para>
/// </summary>
public static class BackupArchive
{
    /// <summary>
    /// Yedek zip'inin KÖKÜNDEKİ veritabanı girdisi. <c>BackupService</c> bu
    /// adla yazıyor, <c>RestoreService</c> ve <c>BackupViewerService</c> bu adla
    /// arıyor; tatbikat da başka bir dosyaya bakmamalı.
    /// </summary>
    public const string DatabaseEntryName = "orderdeck.db";

    /// <summary>
    /// Tanınan en düşük şema sürümü. 2, <c>Label</c> tablosunun geldiği göç:
    /// altındaki bir sürüm sahada yok ve zaten çekirdek tabloları taşımıyor.
    /// </summary>
    public const int MinimumSchemaVersion = 2;

    /// <summary>
    /// Dosyanın OrderDeck'e ait olduğunu kanıtlayan çekirdek tablolar. Liste
    /// bilerek kısa: burası şema denetimi değil <b>kimlik</b> denetimi —
    /// eksik bir sütun değil, tamamen yabancı bir veritabanı aranıyor.
    /// </summary>
    public static IReadOnlyList<string> RequiredTables { get; } =
        new[] { "_meta", "Customer", "StreamSession", "Label" };

    /// <summary>
    /// Dosyanın bir OrderDeck veritabanı olduğunu doğrular. SQLite bütünlüğünü
    /// SORMAZ — çağıran onu ayrıca (masaüstünde <c>SqliteFile.IsIntactDatabase</c>,
    /// tatbikatta <c>PRAGMA integrity_check</c>) yapıyor ve iki başarısızlığın
    /// sebebi farklı raporlanmalı.
    /// </summary>
    /// <param name="error">Başarısızsa insan okuyabilir sebep; başarılıysa null.</param>
    public static bool IsOrderDeckDatabase(string path, out string? error)
    {
        try
        {
            // ReadWrite, ReadOnly DEĞİL: WAL kipindeki bir veritabanını
            // salt-okunur açmak -shm yan dosyası yoksa patlar ve sağlam bir
            // yedeğe "yabancı" damgası vurur (bkz. SqliteFile.IsIntactDatabase).
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            conn.Open();

            foreach (var table in RequiredTables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = $name";
                cmd.Parameters.AddWithValue("$name", table);
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    error = $"OrderDeck veritabanı değil: '{table}' tablosu yok";
                    return false;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT SchemaVersion FROM _meta WHERE Id = 1";
                var raw = cmd.ExecuteScalar();
                if (raw is null or DBNull)
                {
                    error = "OrderDeck veritabanı değil: _meta şema sürümü satırı yok";
                    return false;
                }

                var version = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                if (version < MinimumSchemaVersion)
                {
                    error =
                        $"desteklenmeyen şema sürümü ({version}); " +
                        $"en az {MinimumSchemaVersion} bekleniyor";
                    return false;
                }
            }
        }
        catch (SqliteException ex)
        {
            error = $"veritabanı okunamadı: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }
}
