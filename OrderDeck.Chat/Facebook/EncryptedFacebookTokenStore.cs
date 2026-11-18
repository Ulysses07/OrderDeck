using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// DPAPI-encrypted persistence for <see cref="FacebookTokenBundle"/>. Mirrors
/// <c>EncryptedYouTubeTokenStore</c> but typed (single blob, no IDataStore key
/// indirection) because we own the Facebook OAuth flow end-to-end and don't
/// need to be polymorphic over Google's storage API.
///
/// Tampered / cross-machine blobs are silently dropped → caller treats it as
/// "not connected, re-auth needed", matching the YouTube failure mode.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EncryptedFacebookTokenStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;

    public EncryptedFacebookTokenStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "facebook-token.bin");
    }

    public Task SaveAsync(FacebookTokenBundle bundle, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(bundle, JsonOpts);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var cipher = ProtectedData.Protect(plaintext, optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        // Atomik ve kalıcı yazım. NEDEN: hedefe doğrudan yazarken yazma yarıda
        // kesilirse geriye yarım blob kalır; LoadAsync onu çözemez, dosyayı siler
        // ve bütün Sayfaların bağlantısı kopar. Geçiciye yazıp yerine taşıyınca
        // hedef ya tamamen eski ya tamamen yenidir. Güvence süreç çökmesini,
        // yazım hatasını ve güç kesintisini kapsar. Sonuncusu diske indirme
        // sayesindedir: baytlar işletim sistemi önbelleğinde bırakılıp taşınsaydı,
        // kesintiden sonra hedef ad yeni ama içeriği boş bir dosyayı
        // gösterebilirdi (emsal: OrderDeck.Core'daki SettingsStore.WriteThrough).
        // Geçici dosya AYNI klasörde: bölümler arası taşıma atomik değildir. Adı
        // sabit: yarım kalan deneme birikmez, sonraki yazım üstüne yazar;
        // ClearAsync aynı adı siler.
        var temp = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(cipher, 0, cipher.Length);
                stream.Flush(flushToDisk: true);
            }
            // Akış taşımadan önce kapanır: paylaşımsız açık dosya taşınamaz.
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch { /* best effort */ }
            throw;
        }
        return Task.CompletedTask;
    }

    public Task<FacebookTokenBundle?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return Task.FromResult<FacebookTokenBundle?>(null);

        try
        {
            var cipher = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(cipher, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plaintext);
            var bundle = JsonSerializer.Deserialize<FacebookTokenBundle>(json, JsonOpts);
            return Task.FromResult(bundle);
        }
        catch (CryptographicException)
        {
            // Tampered / restored from another machine — discard.
            TryDelete();
            return Task.FromResult<FacebookTokenBundle?>(null);
        }
        catch (JsonException)
        {
            // Schema drift — discard.
            TryDelete();
            return Task.FromResult<FacebookTokenBundle?>(null);
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        // SaveAsync'in geçici dosyası da silinir. NEDEN: süreç geçiciyi yazıp
        // yerine taşıyamadan ölürse şifreli demet orada kalır; yalnız ana dosya
        // silinseydi "Bağlantıyı Kaldır"dan sonra diskte token artığı kalırdı.
        // Önce artık, sonra ana dosya: araya kesinti girerse bağlantı görünür
        // kalır ve yeniden kaldırılır, gizli bir artık kalmaz. Dosya yoksa
        // File.Delete sessizce geçer; silinemezse ana dosyanın silinmesi bundan
        // etkilenmez.
        try { File.Delete(_path + ".tmp"); }
        catch { /* best effort */ }
        TryDelete();
        return Task.CompletedTask;
    }

    private void TryDelete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* best effort */ }
    }
}
