using System;
using System.IO;
using System.Linq;

namespace OrderDeck.App.Services;

/// <summary>
/// Ürün fotoğraflarının dosya deposu.
///
/// NEDEN kopyalıyoruz: operatör fotoğrafı İndirilenler/Masaüstü'nden seçiyor.
/// O yolu veritabanına yazsak dosya taşınınca kart sessizce boşalırdı.
/// Dosya <c>%LOCALAPPDATA%\OrderDeck\products\</c> altına alınır; tabloya
/// yalnız DOSYA ADI yazılır — mutlak yol değil, çünkü kullanıcı profili
/// (makine değişimi, profil taşıma) yolu geçersiz kılar.
///
/// Kapsam notu: R2'ye yükleme / panelden görsel yönetimi stok projesine ait
/// (spec §9.1). Burası kasıtlı olarak yerel ve aptal.
/// </summary>
public sealed class ProductPhotoStore
{
    private readonly string _root;

    /// <param name="root">
    /// Yalnız test için. Üretimde null → %LOCALAPPDATA%\OrderDeck\products.
    /// (WebView2 klasöründeki aynı kural: exe dizini salt-okunur olabilir.)
    /// </param>
    public ProductPhotoStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrderDeck", "products");
    }

    /// <summary>
    /// <paramref name="sourcePath"/>'i depoya kopyalar, tabloya yazılacak
    /// göreli adı döner. Aynı kodun önceki fotoğrafı (uzantısı ne olursa
    /// olsun) silinir — yoksa klasör her düzenlemede şişer ve
    /// <see cref="ResolveAbsolute"/> hangisini seçeceğini bilemez.
    /// </summary>
    public string Save(string code, string sourcePath)
    {
        var key = Normalize(code);
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";

        Directory.CreateDirectory(_root);
        Delete(code);

        var fileName = key + ext;
        File.Copy(sourcePath, Path.Combine(_root, fileName), overwrite: true);
        return fileName;
    }

    /// <summary>
    /// Göreli adı açılabilir mutlak yola çevirir. Dosya yoksa, ad boşsa ya da
    /// kök dışına çıkıyorsa <c>null</c> — çağıran placeholder gösterir.
    /// </summary>
    public string? ResolveAbsolute(string? relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName)) return null;

        var full = Path.GetFullPath(Path.Combine(_root, relativeName));
        var rootFull = Path.GetFullPath(_root);

        // Ayraç garantisi: "products" kökü ise "products-eski\x.jpg" yolu
        // StartsWith("products") kontrolünü geçer. Sona ayraç ekleyerek
        // kardeş-dizin kaçağını kapatıyoruz.
        var rootWithSep = rootFull.TrimEnd(Path.DirectorySeparatorChar,
                                            Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        // Bozuk/kötü niyetli bir satır ("..\..\windows\win.ini") kökten
        // kaçmasın; kart rastgele dosya açan bir pencereye dönüşmemeli.
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return null;

        return File.Exists(full) ? full : null;
    }

    /// <summary>Bir ürün koduna ait fotoğrafı (varsa) siler.</summary>
    public void Delete(string code)
    {
        if (!Directory.Exists(_root)) return;
        var key = Normalize(code);
        foreach (var f in Directory.EnumerateFiles(_root, key + ".*").ToList())
        {
            try { File.Delete(f); } catch (IOException) { /* dosya kilitli: kartı düşürme */ }
        }
    }

    private static string Normalize(string code)
    {
        var cleaned = new string((code ?? "").Trim()
            .Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "_" : cleaned.ToLowerInvariant();
    }
}
