using System.IO;

namespace OrderDeck.App.Startup;

/// <summary>
/// Yerel veritabanı dosyasının açılış anındaki durumu — <b>migration
/// koşmadan ÖNCE</b> ölçülmüş hâli.
///
/// NEDEN ANLIK GÖRÜNTÜ, NEDEN DOSYAYA SONRADAN BAKILAMIYOR:
/// <c>AppHost</c> ctor'unun sonunda <c>MigrationRunner.Run()</c> çağrılıyor
/// ve SQLite bağlantısı <c>ReadWriteCreate</c> ile açıldığı için dosya
/// yoksa yaratılıp şema kuruluyor. Ctor ise <c>App.OnStartup</c>'ta
/// <see cref="StartupFlow"/>'dan önce koşuyor. Yani akış dosyaya baktığında
/// dosya HER ZAMAN var ve boş şemayla bile <see cref="TinyThresholdBytes"/>
/// eşiğinin çok üstünde (ölçüm: bu depodaki migration'lar boş bir dosyada
/// koşturulduğunda 184.320 bayt). Sonuç: "yeni bilgisayar / silinmiş disk"
/// durumu hiç görülemiyordu ve bulut yedeği operatöre hiç önerilmiyordu.
/// </summary>
public sealed class BootDatabaseState
{
    /// <summary>
    /// Bu boyutun altındaki dosya "yok" sayılır (bayt).
    ///
    /// Dosyanın varlığını sormak yetmiyor: yarıda kesilmiş bir geri
    /// yüklemeden ya da diskteki bir bozulmadan geriye 0 baytlık (veya
    /// yalnızca SQLite başlığı kadar) bir dosya kalabiliyor. Eşik o durumu
    /// da yakalamak için var.
    /// </summary>
    public const long TinyThresholdBytes = 10240;

    private BootDatabaseState(bool isMissingOrTiny) => IsMissingOrTiny = isMissingOrTiny;

    /// <summary>Açılış anında dosya yok muydu ya da eşiğin altında mıydı.</summary>
    public bool IsMissingOrTiny { get; }

    /// <summary>
    /// Dosyayı bir kez okur ve kararı dondurur. <c>AppHost</c> ctor'unun EN
    /// BAŞINDA, veritabanına dokunan hiçbir iş yapılmadan önce çağrılmalı;
    /// başka bir noktadan çağrılırsa ölçtüğü şey migration'ın kendi
    /// yarattığı boş şema olur.
    /// </summary>
    public static BootDatabaseState Capture(string databaseFile) =>
        new(!File.Exists(databaseFile) ||
            new FileInfo(databaseFile).Length < TinyThresholdBytes);
}
