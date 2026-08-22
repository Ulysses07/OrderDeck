using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Startup;
using OrderDeck.Core.Storage;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Bulut yedeğinden geri yükleme önerisinin koşulu. STA GEREKMİYOR:
/// <see cref="BootDatabaseState"/> yalnız dosya sistemine bakıyor.
///
/// NEDEN BU TESTLER: koşul üretimde hiç true dönmedi. Dosya
/// <c>AppHost</c> ctor'unun sonundaki <c>MigrationRunner.Run()</c>
/// tarafından yaratılıyor, akış ise ctor'dan sonra koşuyor — yani akış
/// baktığında dosya hep var ve boş şemayla bile eşiğin çok üstünde.
/// Bilgisayarını değiştiren operatöre yedek hiç önerilmedi. Aşağıdaki
/// <c>Bos_sema_esigin_ustunde_kaliyor</c> testi kök nedeni ölçerek
/// kilitliyor: o test yeşil kaldığı sürece "dosyaya sonradan bakalım"
/// çözümü yanlış olmaya devam eder.
/// </summary>
public class BootDatabaseStateTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "od-bootdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Yeni_kurulumda_dosya_yokken_true()
    {
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");

            BootDatabaseState.Capture(db).IsMissingOrTiny.Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Dosya_var_ama_esigin_altindaysa_true()
    {
        // Yarıda kesilmiş geri yükleme / bozulmuş dosya senaryosu: dosya
        // var, içi işe yaramaz. Eşik bunun için duruyor.
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");
            File.WriteAllBytes(db, new byte[BootDatabaseState.TinyThresholdBytes - 1]);

            BootDatabaseState.Capture(db).IsMissingOrTiny.Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Sifir_bayt_dosya_true()
    {
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");
            File.WriteAllBytes(db, []);

            BootDatabaseState.Capture(db).IsMissingOrTiny.Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Esik_boyutundaki_dosya_false()
    {
        // Sınırın kendisi "yeterli" tarafında: karşılaştırma < eşik.
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");
            File.WriteAllBytes(db, new byte[BootDatabaseState.TinyThresholdBytes]);

            BootDatabaseState.Capture(db).IsMissingOrTiny.Should().BeFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Gercek_boyutlu_dosya_false()
    {
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");
            File.WriteAllBytes(db, new byte[512 * 1024]);

            BootDatabaseState.Capture(db).IsMissingOrTiny.Should().BeFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Bos_sema_esigin_ustunde_kaliyor()
    {
        // KÖK NEDEN. Migration boş bir dosyada koşunca dosyayı hem yaratıyor
        // hem eşiğin üstüne çıkarıyor; bu yüzden ölçüm migration'dan ÖNCE
        // yapılmak zorunda.
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");

            BootDatabaseState.Capture(db).IsMissingOrTiny.Should().BeTrue();

            new MigrationRunner(new SqliteConnectionFactory(db)).Run();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Ölçüldü (2026-08-10, bu depodaki migration seti): 184.320 bayt.
            new FileInfo(db).Length.Should().BeGreaterThan(BootDatabaseState.TinyThresholdBytes);
            BootDatabaseState.Capture(db).IsMissingOrTiny.Should().BeFalse();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* SQLite handle */ } }
    }

    // ── Açılış bütünlük telemetrisi (O-07) ──────────────────────────────────

    [Fact]
    public void Saglam_veritabaninda_butunluk_hatasi_yok()
    {
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");
            new MigrationRunner(new SqliteConnectionFactory(db)).Run();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            BootDatabaseState.Capture(db).IntegrityError.Should().BeNull();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Bozuk_veritabani_acilista_raporlaniyor()
    {
        // Bozulma bugüne dek ancak bir sorgu patladığında, çoğu zaman yayının
        // ortasında görülüyordu. Açılışta bir kez bakmak sahadaki log
        // dökümüne sinyal koyuyor.
        var dir = TempDir();
        try
        {
            var db = Path.Combine(dir, "orderdeck.db");
            File.WriteAllBytes(db, new byte[512 * 1024]);

            var state = BootDatabaseState.Capture(db);

            state.IsMissingOrTiny.Should().BeFalse("dosya boyutça yeterli");
            state.IntegrityError.Should().NotBeNull("ama açılabilir bir veritabanı değil");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Dosya_yokken_butunluk_hatasi_uretilmiyor()
    {
        // İlk açılışta operatöre "veritabanın bozuk" demek yanlış olurdu.
        var dir = TempDir();
        try
        {
            BootDatabaseState.Capture(Path.Combine(dir, "orderdeck.db"))
                .IntegrityError.Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppHost_yakalanmis_durumu_tek_ornek_olarak_veriyor()
    {
        // Bu test yalnız TEKİLLİĞİ ölçüyor: her çözümleme aynı örneği verir,
        // yani ölçüm bir kez yapılıp saklanır. Ölçümün migration'dan ÖNCE
        // yapıldığını bu test GÖSTEREMEZ (tembel bir fabrika da aynı örneği
        // döndürürdü) — sıranın kanıtı Bos_sema_esigin_ustunde_kaliyor.
        using var host = new global::OrderDeck.App.AppHost();

        var first = host.Services.GetRequiredService<BootDatabaseState>();
        var second = host.Services.GetRequiredService<BootDatabaseState>();

        first.Should().NotBeNull();
        first.Should().BeSameAs(second);
    }
}
