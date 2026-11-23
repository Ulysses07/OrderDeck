using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Settings;

/// <summary>
/// Ayar dosyası yazımının dayanıklılığı. Eskiden <c>File.WriteAllText</c>
/// kullanılıyordu: dosyayı önce sıfırlar, sonra yazar. O aralıkta elektrik
/// kesilirse geriye çoğu zaman sıfır baytlık bir settings.json kalıyor, Load
/// açılışta JsonException fırlatıyor ve uygulama bir daha hiç açılmıyordu.
/// </summary>
public sealed class SettingsStoreDurabilityTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"orderdeck-durability-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(
                     Path.GetDirectoryName(_path)!,
                     Path.GetFileName(_path) + "*"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        new SettingsStore(_path).Save(new AppSettings { PrinterName = "Zebra" });

        File.Exists(_path + ".tmp").Should().BeFalse(
            because: "geçici dosya yerine geçtikten sonra ortada kalmamalı");
    }

    [Fact]
    public void Second_save_keeps_previous_version_as_backup()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { PrinterName = "ilk" });
        store.Save(new AppSettings { PrinterName = "ikinci" });

        File.Exists(_path + ".bak").Should().BeTrue();
        File.ReadAllText(_path + ".bak").Should().Contain("ilk");
        File.ReadAllText(_path).Should().Contain("ikinci");
    }

    [Fact]
    public void Corrupt_file_falls_back_to_backup()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { PrinterName = "Zebra ZD220" });
        store.Save(new AppSettings { PrinterName = "Zebra ZD220", OverlayPort = 5001 });

        // Kesintinin bıraktığı hâl: sıfır baytlık dosya.
        File.WriteAllText(_path, "");

        var loaded = store.Load();

        loaded.PrinterName.Should().Be("Zebra ZD220",
            because: "yedek bir önceki sürümü tutuyor, varsayılana düşmemeli");
    }

    [Fact]
    public void Corrupt_file_without_backup_is_quarantined_not_deleted()
    {
        File.WriteAllText(_path, "{ bu json deg");

        var loaded = new SettingsStore(_path).Load();

        loaded.OverlayPort.Should().Be(4747, because: "varsayılanlarla açılabilmeli");

        var quarantined = Directory.GetFiles(
            Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*");
        quarantined.Should().ContainSingle(
            because: "elle girilmiş ayarlar kurtarılabilir olsun diye bozuk dosya silinmez");
        File.ReadAllText(quarantined.Single()).Should().Be("{ bu json deg");
    }

    /// <summary>
    /// <c>File.Replace</c> eski <c>.bak</c>'ın üstüne yazar ve Windows, üzerinde açık tutamaç olan bir
    /// dosyanın üstüne yeniden adlandırmaya izin vermez — tutamaç yalnız öznitelik okuyan türden olsa
    /// bile. Yedeğe o an bakan herhangi bir süreç (Gezgin, antivirüs, dizin oluşturucu, yedekleme aracı)
    /// kaydı <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> (0x80070497) ile düşürüyordu: bakış anlık olduğu
    /// hâlde kayıt bütünüyle kayboluyordu. Ölçüm (2026-11-18, geliştirme makinesi, dosyalar TEMP
    /// kökünde): ardışık kayıtların ~%0,1'i, yükten bağımsız — aşağıdaki iki eşzamanlılık testi bu
    /// yüzden ara sıra düşüyordu (32 kayıtlık olan, yinelenen koşuların ~%3'ünde).
    ///
    /// <para>Buradaki "öbür süreç" aynı süreçten açılmış ikinci bir tutamaç: çekirdek ayırt etmez, hata
    /// birebir aynı. Tutamaç, deponun ilk çakışma uyarısında bırakılır — sabit bekleme yok, sıra
    /// deterministik.</para>
    /// </summary>
    [Fact]
    public void Save_retries_while_another_process_looks_at_the_backup()
    {
        var setup = new SettingsStore(_path) { ReplaceRetryBudget = TestPatience.Upper };
        setup.Save(new AppSettings { PrinterName = "ilk" });
        setup.Save(new AppSettings { PrinterName = "ikinci" });     // .bak artık var ("ilk")

        using var onlooker = new FileStream(
            _path + ".bak", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var log = new ReleaseOnWarning(onlooker);
        var store = new SettingsStore(_path, log) { ReplaceRetryBudget = TestPatience.Upper };

        store.Save(new AppSettings { PrinterName = "üçüncü" });

        log.Warnings.Should().ContainSingle(
            because: "çakışma sahada günlükten görülebilmeli, ama kayıt başına bir kez");
        store.Load().PrinterName.Should().Be("üçüncü");
        File.ReadAllText(_path + ".bak").Should().Contain("ikinci",
            because: "yeniden deneme yedeği atlamaz: bir önceki sürüm yine .bak'ta");
        File.Exists(_path + ".tmp").Should().BeFalse();
    }

    /// <summary>
    /// Yeniden deneme sınırlıdır: tutamaç bırakılmazsa kayıt bütçe dolunca asıl hatayla düşer, kilidi
    /// süresiz tutmaz — ve yarım kalan kayıt ayar dosyasını bozmaz (asıl dosya da yedek de eski hâlinde).
    /// Bütçenin kendisi sınandığı için burada <see cref="TestPatience.Upper"/>'a çekilmez, kısaltılır.
    /// </summary>
    [Fact]
    public async Task Save_gives_up_once_the_retry_budget_is_spent()
    {
        var setup = new SettingsStore(_path) { ReplaceRetryBudget = TestPatience.Upper };
        setup.Save(new AppSettings { PrinterName = "ilk" });
        setup.Save(new AppSettings { PrinterName = "ikinci" });

        var store = new SettingsStore(_path) { ReplaceRetryBudget = TimeSpan.FromMilliseconds(50) };

        using (new FileStream(
                   _path + ".bak", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var save = Task.Run(() => store.Save(new AppSettings { PrinterName = "üçüncü" }));
            var waiting = () => save.WaitAsync(TestPatience.Upper);

            await waiting.Should().ThrowAsync<IOException>(
                because: "bütçe dolunca hata yüzeye çıkmalı; kayıt sonsuza dek beklememeli");
        }

        setup.Load().PrinterName.Should().Be("ikinci",
            because: "başarısız kayıt asıl dosyaya dokunmamış olmalı");
        File.ReadAllText(_path + ".bak").Should().Contain("ilk");
    }

    /// <summary>
    /// Y-07: Save üç adımlı (geçici dosyaya yaz → diske indir → yerine geçir)
    /// ve bu adımlar atomik değil. Kilitsiz hâlde iki hosted service aynı anda
    /// kaydettiğinde ikisi de aynı sabit <c>.tmp</c> yolunu <c>FileShare.None</c>
    /// ile açmaya çalışıyor: biri IOException alıyor ve o servisin sync cursor'u
    /// sessizce kaydedilmemiş oluyor — sahada aynı kayıtların tekrar çekilmesi
    /// demek.
    /// </summary>
    [Fact]
    public void Concurrent_saves_do_not_throw_and_leave_a_readable_file()
    {
        // Bütçe: 64 ardışık kayıt, yedeğe bakan gerçek bir sürece denk gelebilir (bkz. yukarıdaki test).
        var store = new SettingsStore(_path) { ReplaceRetryBudget = TestPatience.Upper };
        store.Save(new AppSettings { PrinterName = "başlangıç" });

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        System.Threading.Tasks.Parallel.For(0, 64, i =>
        {
            try { store.Save(new AppSettings { PrinterName = $"yazıcı-{i}" }); }
            catch (Exception ex) { exceptions.Add(ex); }
        });

        exceptions.Should().BeEmpty(
            because: "eşzamanlı kayıtlar birbirinin geçici dosyasına çarpmamalı");

        File.Exists(_path + ".tmp").Should().BeFalse();
        store.Load().PrinterName.Should().StartWith("yazıcı-",
            because: "son yazan kazanmalı, dosya yarım kalmamalı");
    }

    /// <summary>
    /// Okuma da kilit altında olmalı: bir Save'in <c>File.Replace</c> anına denk
    /// gelen okuma dosyayı bulamayıp bozuk sanabilir ve karantinaya alabilirdi.
    /// </summary>
    [Fact]
    public void Loads_during_concurrent_saves_never_fall_back_to_defaults()
    {
        // Bütçe: yukarıdaki testle aynı gerekçe (32 ardışık kayıt).
        var store = new SettingsStore(_path) { ReplaceRetryBudget = TestPatience.Upper };
        store.Save(new AppSettings { PrinterName = "başlangıç" });

        var loadedDefaults = 0;

        System.Threading.Tasks.Parallel.For(0, 64, i =>
        {
            if (i % 2 == 0)
            {
                store.Save(new AppSettings { PrinterName = $"yazıcı-{i}" });
            }
            else
            {
                var s = store.Load();
                if (string.IsNullOrEmpty(s.PrinterName))
                    System.Threading.Interlocked.Increment(ref loadedDefaults);
            }
        });

        loadedDefaults.Should().Be(0,
            because: "yazma sırasında yapılan okuma varsayılanlara düşmemeli");

        Directory.GetFiles(
                Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*")
            .Should().BeEmpty(because: "sağlam dosya karantinaya alınmamalı");
    }

    /// <summary>
    /// Deponun çakışma uyarısını "öbür süreç işini bitirdi" anı olarak kullanır: ilk uyarıda yabancı
    /// tutamacı bırakır, sıradaki deneme başarılı olur. Uyarıları ayrıca toplar.
    /// </summary>
    private sealed class ReleaseOnWarning(IDisposable foreignHandle) : ILogger<SettingsStore>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Warnings.Add(formatter(state, exception));
            foreignHandle.Dispose();
        }
    }
}
