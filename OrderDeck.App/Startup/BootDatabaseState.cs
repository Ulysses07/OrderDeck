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

    private BootDatabaseState(bool isMissingOrTiny, string? integrityError)
    {
        IsMissingOrTiny = isMissingOrTiny;
        IntegrityError = integrityError;
    }

    /// <summary>Açılış anında dosya yok muydu ya da eşiğin altında mıydı.</summary>
    public bool IsMissingOrTiny { get; }

    /// <summary>
    /// Açılışta <c>PRAGMA quick_check</c>'in verdiği hata; sağlamsa (ya da
    /// ölçülecek dosya yoksa) <c>null</c>.
    ///
    /// Neden ölçülüyor: bozulma bugüne dek ancak bir sorgu patladığında,
    /// çoğu zaman yayının ortasında görülüyordu. Açılışta bir kez bakmak
    /// sahadaki log dökümüne "veritabanı o gün zaten bozuktu" sinyalini
    /// koyuyor. Ölçüm <b>bilgi amaçlı</b>: açılışı engellemiyor, çünkü bozuk
    /// bir dosyadan da çoğu zaman veri okunabiliyor ve operatörün elindeki
    /// tek kopya o.
    /// </summary>
    public string? IntegrityError { get; }

    /// <summary>
    /// Dosyayı bir kez okur ve kararı dondurur. <c>AppHost</c> ctor'unun EN
    /// BAŞINDA, veritabanına dokunan hiçbir iş yapılmadan önce çağrılmalı;
    /// başka bir noktadan çağrılırsa ölçtüğü şey migration'ın kendi
    /// yarattığı boş şema olur.
    /// </summary>
    public static BootDatabaseState Capture(string databaseFile)
    {
        var missingOrTiny =
            !File.Exists(databaseFile) ||
            new FileInfo(databaseFile).Length < TinyThresholdBytes;

        // Olmayan/boş dosyada quick_check'in söyleyeceği bir şey yok; üstelik
        // ilk açılışta gereksiz bir "bozuk" uyarısı üretirdi.
        if (missingOrTiny) return new BootDatabaseState(true, null);

        OrderDeck.Core.Storage.SqliteFile.IsIntactDatabase(databaseFile, out var error);
        return new BootDatabaseState(false, error);
    }
}
