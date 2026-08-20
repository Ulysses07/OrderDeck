using System;
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

    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly ILogger _log;

    public SettingsStore(string filePath, ILogger<SettingsStore>? logger = null)
    {
        _filePath = filePath;
        _backupPath = filePath + ".bak";
        _log = logger ?? (ILogger)NullLogger<SettingsStore>.Instance;
    }

    public AppSettings Load()
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

    public void Save(AppSettings settings)
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
    }

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
