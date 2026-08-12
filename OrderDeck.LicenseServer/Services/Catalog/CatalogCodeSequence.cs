using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Otomatik ürün kodu üretir: A1 → A2 → … → A999 → B1 → … → Z999 → AA1.
///
/// Mevcut kodların EN YÜKSEĞİNDEN devam eder; boşlukları doldurmaz. Gerekçe:
/// arşivlenen ürünün kodu tekrar kullanılırsa eski siparişler yanlış ürünü
/// gösterir. Ürün kartı silinemediği için "en yüksek" taraması güvenli.
///
/// Önek ayrıca saklanmıyor: yayıncı elle bir kod yazdığında (örn. "B1") sayaç
/// kendiliğinden oradan devam eder. "Önek ayarlanabilir" gereksinimi böyle
/// karşılanıyor — yeni tablo gerekmiyor.
/// </summary>
public static class CatalogCodeSequence
{
    public const int MaxNumber = 999;

    private static readonly Regex Pattern =
        new("^([A-Z]+)([0-9]{1,3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Next(IEnumerable<string> existingCodes)
    {
        string? bestPrefix = null;
        var bestNumber = 0;

        foreach (var raw in existingCodes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var match = Pattern.Match(raw.Trim().ToUpperInvariant());
            if (!match.Success) continue;

            var prefix = match.Groups[1].Value;
            var number = int.Parse(match.Groups[2].Value);

            if (bestPrefix is null || Compare(prefix, number, bestPrefix, bestNumber) > 0)
            {
                bestPrefix = prefix;
                bestNumber = number;
            }
        }

        if (bestPrefix is null) return "A1";

        return bestNumber < MaxNumber
            ? bestPrefix + (bestNumber + 1)
            : NextPrefix(bestPrefix) + "1";
    }

    /// <summary>Önce önek uzunluğu, sonra önek, sonra sayı. "AA2" &gt; "Z5".</summary>
    private static int Compare(string prefixA, int numberA, string prefixB, int numberB)
    {
        if (prefixA.Length != prefixB.Length) return prefixA.Length - prefixB.Length;

        var byPrefix = string.CompareOrdinal(prefixA, prefixB);
        return byPrefix != 0 ? byPrefix : numberA - numberB;
    }

    /// <summary>A→B, Z→AA, AZ→BA — Excel sütun mantığı.</summary>
    private static string NextPrefix(string prefix)
    {
        var chars = prefix.ToCharArray();

        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] != 'Z')
            {
                chars[i]++;
                return new string(chars);
            }

            chars[i] = 'A';
        }

        return "A" + new string(chars);
    }
}
