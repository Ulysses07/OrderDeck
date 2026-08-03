using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// 81 il ve 973 ilçenin resmî listesi. Kayıt formundaki il/ilçe açılır
/// listelerini besler ve sunucu tarafında gönderilen değerin gerçekten
/// listede olduğunu doğrular.
///
/// Neden liste: e-Fatura toplu yükleme şablonunda "Alıcı Şehir" ve
/// "Alıcı İlçe" ayrı kolonlar. Serbest metin adreste bunlar ayrıştırılamıyor
/// ("Merkez", kısaltmalar, yazım hataları) ve entegratör satırı reddediyor.
///
/// Veri gömülü kaynak (<c>Data/IlIlce.json</c>) olarak taşınıyor; form da
/// listeyi buradan basıyor, yani tek kaynak.
/// </summary>
public static class TurkeyRegions
{
    /// <summary>İl adları, Türkçe alfabetik sırada.</summary>
    public static IReadOnlyList<string> Cities { get; }

    /// <summary>İl → ilçeler (Türkçe alfabetik). Arama büyük/küçük harfe duyarsız.</summary>
    public static FrozenDictionary<string, IReadOnlyList<string>> Districts { get; }

    static TurkeyRegions()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{typeof(TurkeyRegions).Namespace}.Data.IlIlce.json";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Gömülü kaynak bulunamadı: {name}");

        var rows = JsonSerializer.Deserialize<Row[]>(
                stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("IlIlce.json ayrıştırılamadı.");

        Cities = rows.Select(r => r.Il).ToArray();
        Districts = rows.ToFrozenDictionary(
            r => r.Il,
            r => (IReadOnlyList<string>)r.Ilceler,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Listede eşleşen ili döndürür (yazım/harf farkını düzelterek), yoksa null.</summary>
    public static string? MatchCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        var key = Fold(city);
        return Cities.FirstOrDefault(c => Fold(c) == key);
    }

    /// <summary>Verilen ile ait eşleşen ilçeyi döndürür, yoksa null.</summary>
    public static string? MatchDistrict(string? city, string? district)
    {
        if (string.IsNullOrWhiteSpace(district)) return null;
        var matchedCity = MatchCity(city);
        if (matchedCity is null) return null;
        if (!Districts.TryGetValue(matchedCity, out var list)) return null;
        var key = Fold(district);
        return list.FirstOrDefault(d => Fold(d) == key);
    }

    /// <summary>
    /// Karşılaştırma anahtarı. <c>OrdinalIgnoreCase</c> yetmiyor: Türkçe'de
    /// "istanbul" ile "İstanbul" ordinal olarak EŞLEŞMEZ (i↔İ ve ı↔I ayrı
    /// harfler). i ailesinin dördünü tek harfe indirip küçültüyoruz — böylece
    /// hangi klavye/otomatik düzeltme ne yazarsa yazsın liste değeri bulunur.
    /// Çakışma yok: i/ı farkıyla ayrışan iki il ya da aynı ildeki iki ilçe
    /// listede bulunmuyor.
    /// </summary>
    private static string Fold(string s) =>
        s.Trim().Replace('İ', 'i').Replace('I', 'i').Replace('ı', 'i').ToLowerInvariant();

    private sealed record Row(string Il, string[] Ilceler);
}
