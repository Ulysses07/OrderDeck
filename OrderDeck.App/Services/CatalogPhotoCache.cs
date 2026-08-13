using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OrderDeck.App.Services;

/// <summary>
/// Katalog kapak fotoğraflarının disk önbelleği.
///
/// Anahtar R2 nesne anahtarı (<c>{licenseId:N}/products/{productId:N}/x.img</c>)
/// — yani <b>eğik çizgi içeriyor</b> ve doğrudan dosya adı olamaz. Dosya adı
/// anahtarın SHA-256 özeti: hem düzleştirir hem uzunluk sınırını kaldırır hem
/// de <c>..</c> gibi bir şeyin köke kaçmasını yapısal olarak imkânsız kılar.
///
/// Sunucudan gelen indirme adresi 5 dakikada geçersizleşiyor; kalıcı olan tek
/// şey anahtar, o yüzden önbellek anahtarla adresleniyor. Fotoğraf değişince
/// nesne anahtarı da değişir (panel yeni bir GUID üretiyor), dolayısıyla
/// bayat içerik dönme ihtimali yok — eskisi <see cref="Prune"/> ile düşer.
///
/// <b>İş parçacığı güvenli DEĞİL:</b> <see cref="Save"/> ve <see cref="Prune"/>
/// tek senkron turu içinde sırayla çağrılmalı. NEDEN: <see cref="Prune"/>'un
/// koruma kümesinde yalnız <c>{özet}.img</c> adları var, sürmekte olan bir
/// <see cref="Save"/>'in <c>.tmp</c> dosyası değil — paralel koşarlarsa
/// temizlik geçiciyi silip <c>File.Move</c>'u
/// <see cref="FileNotFoundException"/> ile düşürür.
/// </summary>
public sealed class CatalogPhotoCache
{
    private const string Extension = ".img";
    private const string TempExtension = ".tmp";

    private readonly string _root;

    /// <param name="root">
    /// Yalnız test için. Üretimde null → %LOCALAPPDATA%\OrderDeck\catalog-photos.
    /// </param>
    public CatalogPhotoCache(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrderDeck", "catalog-photos");
    }

    public bool Has(string? objectKey) => ResolveAbsolute(objectKey) is not null;

    /// <summary>
    /// Baytları anahtarın dosyasına yazar.
    ///
    /// NEDEN önce geçici dosya: doğrudan hedefe yazarken <b>yazma yarıda
    /// kesilirse</b> (uygulama çöker, disk dolar) geriye yarım dosya kalır ve
    /// <see cref="Has"/> onu sonsuza kadar "var" sayar — anahtar değişmediği
    /// için de kimse yeniden indirmez. Geçiciye yazıp
    /// <see cref="File.Move(string, string, bool)"/> ile yerine koyunca hedef
    /// dosya ya tamamen eski ya tamamen yeni olur. Geçici dosya <em>aynı
    /// klasörde</em> duruyor: farklı bölümler arası taşıma atomik değildir.
    ///
    /// Kapsam dışı: gelen baytların <em>doğruluğu</em>. Eksik ama "başarılı"
    /// biten bir indirme burada da bozuk olarak, üstelik atomik biçimde
    /// önbelleğe girer; bu sınıf içerik bütünlüğü doğrulamaz.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Anahtar boş/boşluk. NEDEN sessizce yok saymak yerine fırlatıyoruz:
    /// <see cref="ResolveAbsolute"/> boş anahtarda null döndüğü için yazılan
    /// dosyayı <see cref="Has"/> asla göremez, <see cref="Prune"/> ise onu
    /// canlı sayar → her turda yeniden indirilen, hiç silinmeyen yetim dosya.
    /// Anahtar DTO'dan gelir; boşsa bu çağıranın hatasıdır, görünsün.
    /// </exception>
    public void Save(string objectKey, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        Directory.CreateDirectory(_root);

        var fileName = FileNameFor(objectKey);
        // Geçici ad anahtar başına sabit: yarım kalan denemeler birikmez,
        // sonraki tur aynı dosyanın üstüne yazar.
        var temp = Path.Combine(_root, fileName + TempExtension);

        File.WriteAllBytes(temp, bytes);
        File.Move(temp, Path.Combine(_root, fileName), overwrite: true);
    }

    /// <summary>Önbellekteki dosyanın tam yolu; yoksa null (view placeholder gösterir).</summary>
    public string? ResolveAbsolute(string? objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        var full = Path.Combine(_root, FileNameFor(objectKey));
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Canlı anahtar listesinde olmayan dosyaları siler. Katalogdan düşen ürünün
    /// fotoğrafı sonsuza kadar diskte kalmasın diye her senkron turunda çağrılır.
    ///
    /// Boş/null anahtarlar elenir: fotoğrafsız ürün istisna değil kural, ve
    /// listedeki tek bir null bütün temizlik turunu düşürmemeli. Zaten
    /// <see cref="Save"/> boş anahtarı reddettiği için böyle bir anahtarın
    /// diskte karşılığı olamaz — elemek hiçbir canlı dosyayı riske atmaz.
    /// </summary>
    public void Prune(IEnumerable<string?> liveKeys)
    {
        if (!Directory.Exists(_root)) return;

        // OrdinalIgnoreCase kasıtlı: FileNameFor her zaman küçük harf hex
        // ürettiği için kümede harf farkı oluşamaz, ama karşılaştırdığımız
        // taraf DOSYA SİSTEMİNDEN geliyor ve Windows adları büyük/küçük harf
        // ayırmaz. Ordinal'e çekmek, diskteki adın harf durumu bir şekilde
        // değişirse (kopyalama aracı, eski sürüm) canlı dosyayı sildirir.
        var keep = liveKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => FileNameFor(k!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Desen hem ".img" hem de yarım kalmış ".img.tmp" dosyalarını yakalar;
        // geçiciler hiçbir zaman canlı listede olmadığı için hepsi düşer.
        foreach (var file in Directory.EnumerateFiles(_root, "*" + Extension + "*").ToList())
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            // Dosya kilitliyse (Image hâlâ bağlı) atla: temizlik bir sonraki
            // turda yeniden denenir, önbellek tutarlılığı bundan etkilenmez.
            // Windows'ta kilit IOException yerine UnauthorizedAccessException
            // olarak da yüzeye çıkabiliyor; ikisi de turu düşürmemeli.
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string FileNameFor(string objectKey)
        => Convert.ToHexString(
               SHA256.HashData(Encoding.UTF8.GetBytes(objectKey))).ToLowerInvariant()
           + Extension;
}
