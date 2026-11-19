using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderDeck.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> from a JSON file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Ayar dosyasına erişimi süreç içinde tekleştirir.
    ///
    /// <para><b>Neden gerekli.</b> <see cref="Save"/> sabit bir <c>.tmp</c>
    /// yoluna yazıp <c>File.Replace</c> ile yerine geçiriyor. Bu üç adım
    /// (yaz → diske indir → yer değiştir) atomik DEĞİL: iki hosted service
    /// aynı anda kaydederse ikisi de aynı <c>.tmp</c>'yi açmaya çalışır —
    /// <c>FileShare.None</c> yüzünden biri <c>IOException</c> alır ve o
    /// servisin cursor'u sessizce kaydedilmez, ya da biri diğerinin henüz
    /// yer değiştirmemiş geçici dosyasını ezer. Sahada bu, sync cursor'unun
    /// geri gitmesi (aynı kayıtların tekrar çekilmesi) demek.</para>
    ///
    /// <para><b>Neden static.</b> DI'da tek örnek var ama testler ve
    /// <c>AppHost</c>'un ayrı <c>Load()</c> çağrıları aynı dosyaya bakan
    /// başka örnekler üretebiliyor. Kilit dosyayı korumalı, nesneyi değil.
    /// Kaydetme saniyede bir kez bile olmadığından tek global kilidin
    /// maliyeti ölçülemez.</para>
    ///
    /// <para><b>Neyi kapatmaz.</b> Süreçler arası yarışı. Uygulamanın ikinci
    /// örneği yok (<c>App.xaml.cs</c> mutex'i), ama dosyalara dışarıdan bakan
    /// süreçler var (Gezgin, antivirüs, dizin oluşturucu); onların anlık
    /// tutamacını <see cref="MoveIntoPlace"/> karşılıyor.</para>
    /// </summary>
    private static readonly Lock FileGate = new();

    /// <summary>Yeniden denemeler arasındaki bekleme 1 ms'den başlar, ikiye katlanarak en çok buraya
    /// kadar büyür.</summary>
    private static readonly TimeSpan MaxRetryPause = TimeSpan.FromMilliseconds(50);

    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly ILogger _log;

    public SettingsStore(string filePath, ILogger<SettingsStore>? logger = null)
    {
        _filePath = filePath;
        _backupPath = filePath + ".bak";
        _log = logger ?? (ILogger)NullLogger<SettingsStore>.Instance;
    }

    /// <summary>
    /// Yerine geçirme adımı "dosya şu an başka bir süreçte" hatası verdiğinde en çok ne kadar
    /// süre yeniden denenir. Süre dolunca asıl hata olduğu gibi fırlar. Testler değiştirir.
    /// </summary>
    public TimeSpan ReplaceRetryBudget { get; init; } = TimeSpan.FromSeconds(2);

    public AppSettings Load()
    {
        // Okuma da kilit altında: bir Save'in File.Replace anına denk gelen
        // okuma dosyayı bulamayabilir ve bozuk sanıp karantinaya alabilirdi.
        lock (FileGate)
        {
            return LoadCore();
        }
    }

    private AppSettings LoadCore()
    {
        if (TryRead(_filePath, out var settings)) return settings;

        // Ana dosya okunamadı. Yedek, Save'in her başarılı yazımda bıraktığı bir
        // önceki sürüm: en fazla bir değişiklik geride, sıfırdan başlamaktan çok iyi.
        if (File.Exists(_backupPath) && TryRead(_backupPath, out var fromBackup))
        {
            _log.LogWarning(
                "Ayar dosyası okunamadı, yedekten ({BackupPath}) yüklendi", _backupPath);
            return fromBackup;
        }

        // İkisi de gitti. Bozuk dosyayı SİLMİYORUZ: yazıcı adı, YouTube anahtarı,
        // kargo eşiği gibi elle girilmiş değerler içinde ve büyük ihtimalle
        // kurtarılabilir. Kenara alıp varsayılanlarla açılıyoruz — uygulamanın hiç
        // açılmaması, ayarların varsayılana dönmesinden daha kötü.
        if (File.Exists(_filePath))
        {
            var quarantine = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_filePath, quarantine);
                _log.LogError(
                    "Ayar dosyası bozuk ve yedeği de yok; {Quarantine} olarak saklandı, " +
                    "varsayılan ayarlarla devam ediliyor", quarantine);
            }
            catch (IOException ex)
            {
                _log.LogError(ex, "Bozuk ayar dosyası kenara alınamadı: {Path}", _filePath);
            }
        }

        return new AppSettings();
    }

    /// <summary>Verilen nesneyi olduğu gibi yazar. <b>Load-üzerinde-değiştir-Save
    /// deseni için KULLANMA</b> — elindeki kopya bayatsa başka bir bileşenin bu
    /// arada kaydettiği alanları ezersin (N04); onun yerine <see cref="Update"/>.
    /// Bu metot yalnız "elimdeki nesne gerçeğin kendisi" durumları için:
    /// ilk oluşturma ve test kurulumları.</summary>
    public void Save(AppSettings settings)
    {
        lock (FileGate)
        {
            SaveCore(settings);
        }
    }

    /// <summary>
    /// N04 (2026-09-10 denetimi): atomik oku-değiştir-yaz. Diskteki EN GÜNCEL
    /// kopya kilit altında yüklenir, <paramref name="mutate"/> yalnız kendi
    /// alanlarını değiştirir ve sonuç aynı kilit altında yazılır.
    ///
    /// <para><b>Neden gerekli.</b> <see cref="Load"/> ve <see cref="Save"/> tek
    /// tek kilitli ama aralarındaki süre değil: bir servis Load'la aldığı bütün
    /// nesneyi dakikalar sonra Save'lediğinde, arada başka bir bileşenin
    /// kaydettiği alanları (ör. sync imleçleri) eski değerleriyle ezer. Sahada
    /// bu, ayar ekranında Kaydet'e basmanın müşteri-projeksiyon imlecini
    /// uygulama açılışındaki değere döndürmesi demekti — sonraki turda binlerce
    /// kayıt boşuna yeniden çekilirdi.</para>
    ///
    /// <para>Çağıran, bellekte tuttuğu canlı kopyayı da aynı değerlerle
    /// güncel tutmalı (bkz. sync servislerindeki kullanım).</para>
    /// </summary>
    /// <returns>Diske yazılan güncel nesne.</returns>
    public AppSettings Update(Action<AppSettings> mutate)
    {
        lock (FileGate)
        {
            var settings = LoadCore();
            mutate(settings);
            SaveCore(settings);
            return settings;
        }
    }

    private void SaveCore(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, Options);

        // Eskiden doğrudan File.WriteAllText'ti: dosyayı önce sıfırlar, sonra yazar.
        // O aralıkta elektrik kesilirse ya da uygulama çökerse geriye yarım —
        // çoğu zaman sıfır baytlık — bir settings.json kalıyordu ve Load açılışta
        // JsonException fırlatıyordu. Yani uygulama bir daha hiç açılmıyordu.
        //
        // Şimdi önce geçici dosyaya yazılıp diske indiriliyor, sonra tek adımda
        // yerine geçiyor. Kesinti hangi anda olursa olsun settings.json ya eski
        // ya yeni içeriktir; asla yarım değildir.
        var tempPath = _filePath + ".tmp";
        WriteThrough(tempPath, json);
        MoveIntoPlace(tempPath);
    }

    /// <summary>
    /// Geçici dosyayı asıl dosyanın yerine geçirir; dosyalardan biri o an başka bir sürecin
    /// elindeyse <see cref="ReplaceRetryBudget"/> dolana kadar yeniden dener.
    ///
    /// <para><b>Neden gerekli.</b> <c>File.Replace</c> eski <c>.bak</c>'ın üstüne yazmak zorunda
    /// ve Windows, üzerinde açık tutamaç olan bir dosyanın üstüne yeniden adlandırmaya izin
    /// vermiyor — tutamaç yalnız öznitelik okuyan türden olsa bile. Yani yedeğe o an bakan
    /// herhangi bir süreç (Gezgin penceresi, antivirüs, dizin oluşturucu, yedekleme aracı) kaydı
    /// <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> ile düşürüyordu. <see cref="FileGate"/> bunu
    /// kapatamaz: çakışan taraf başka bir süreç. Bakış anlık: ölçülen 40 örneğin hepsinde çağrıyı
    /// hemen yinelemek yetti (2026-11-18, geliştirme makinesi, dosyalar TEMP kökünde: ardışık
    /// kayıtların ~%0,1'i; tam paket yükü altında da aynı oran).</para>
    ///
    /// <para><b>Hangi hatalar.</b> Yalnız "dosya şu an kullanımda" ailesi
    /// (<see cref="IsHeldByAnotherProcess"/>). Yol yok, disk dolu, yetki yok gibi kalıcı hatalar
    /// beklemeden fırlar.</para>
    /// </summary>
    private void MoveIntoPlace(string tempPath)
    {
        var waited = Stopwatch.StartNew();
        var pause = TimeSpan.FromMilliseconds(1);
        var warned = false;

        while (true)
        {
            try
            {
                // Her denemede yeniden bakılır: File.Replace ikinci adımında kalırsa
                // (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2) asıl dosya .bak adına taşınmış, yerinde
                // hiçbir şey kalmamış olur — o hâlde sıradaki denemenin işi düz taşımadır.
                if (File.Exists(_filePath))
                {
                    // File.Replace hem yer değiştirmeyi atomik yapar hem de eskisini
                    // .bak olarak bırakır — Load'ın kurtarma yolu buna dayanıyor.
                    File.Replace(tempPath, _filePath, _backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _filePath);
                }

                return;
            }
            catch (IOException ex) when (IsHeldByAnotherProcess(ex) && waited.Elapsed < ReplaceRetryBudget)
            {
                if (!warned)
                {
                    // Kayıt başına bir kez: sahada ne sıklıkta olduğu günlükten okunabilsin.
                    warned = true;
                    _log.LogWarning(ex,
                        "Ayar dosyası yerine geçirilemedi: dosyalardan birini başka bir süreç tutuyor; " +
                        "en çok {Budget} boyunca yeniden denenecek ({Path})",
                        ReplaceRetryBudget, _filePath);
                }

                Thread.Sleep(pause);
                pause = pause * 2 < MaxRetryPause ? pause * 2 : MaxRetryPause;
            }
        }
    }

    /// <summary>
    /// Win32 hata kodu "dosya şu an başka bir tutamaçta" ailesinden mi? (HRESULT = 0x8007 + kod.)
    /// Deterministik deneyle eşlendi: eski <c>.bak</c> tutuluyorsa 1175; asıl ya da geçici dosya
    /// paylaşımsız tutuluyorsa 32. 1176 ve 1177, <c>ReplaceFile</c>'ın aynı sebeple ikinci adımında
    /// kaldığı belgelenmiş hâller.
    /// </summary>
    private static bool IsHeldByAnotherProcess(IOException ex) => ex.HResult is
        unchecked((int)0x80070020)      // ERROR_SHARING_VIOLATION (32)
        or unchecked((int)0x80070497)   // ERROR_UNABLE_TO_REMOVE_REPLACED (1175)
        or unchecked((int)0x80070498)   // ERROR_UNABLE_TO_MOVE_REPLACEMENT (1176)
        or unchecked((int)0x80070499);  // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 (1177)

    /// <summary>
    /// Diske gerçekten yazar. <c>File.WriteAllText</c> işletim sistemi önbelleğine
    /// bırakıp döner; <c>Flush(true)</c> ise donanıma inmesini bekler. Bunu
    /// yapmazsak "atomik yer değiştirme" kâğıt üstünde kalırdı: yeni dosya henüz
    /// diskte yokken eskisinin üstüne geçmiş olabilirdi.
    /// </summary>
    private static void WriteThrough(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    private static bool TryRead(string path, out AppSettings settings)
    {
        settings = new AppSettings();
        if (!File.Exists(path)) return false;

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (parsed is null) return false;   // dosya içeriği tam olarak "null"
            settings = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
