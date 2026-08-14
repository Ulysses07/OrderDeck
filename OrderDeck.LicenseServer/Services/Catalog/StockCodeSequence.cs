using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Stok kodu üreteci: <c>SK00001</c>, <c>SK00002</c>…
///
/// <para>Kod <b>sistemin</b>; operatör göremez, değiştiremez. Yayında söylenen
/// kod ayrı (<c>ProductBroadcastCode</c>). Bu ayrım olmadan tek kod iki işi
/// birden yapmak zorundaydı: hem kalıcı stok kimliği hem operatörün yayında
/// beğendiği kısa ad — ve ikisi çeliştiğinde etiket ile yayın ayrışıyordu.</para>
///
/// <para>Sayaç <b>en büyük + 1</b>; boşluk doldurulmaz.</para>
/// </summary>
public static class StockCodeSequence
{
    public const string Prefix = "SK";
    public const int Digits = 5;

    // 5+ hane: SK99999'dan sonrası altı haneye taşar ve o kodlar da okunabilmeli.
    private static readonly Regex Pattern =
        new(@"^SK([0-9]{5,})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Verilen kod kümesindeki en büyük numaranın bir fazlası. Kalıba uymayan
    /// kayıtlar (eski elle yazılmış <c>A1</c> gibi) sessizce atlanır — bu
    /// bilinçli: göç öncesinden kalan bir kod üretimi kilitlememeli.
    /// </summary>
    public static string Next(IEnumerable<string?> existing)
    {
        long max = 0;

        foreach (var raw in existing)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var match = Pattern.Match(raw.Trim().ToUpperInvariant());
            if (!match.Success) continue;

            if (!long.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number)) continue;

            if (number > max) max = number;
        }

        return Format(max + 1);
    }

    /// <summary>
    /// Numarayı koda çevirir. <c>PadLeft</c> kullanılıyor, sabit genişlikli
    /// biçim değil: 99999 aşıldığında kesmek yerine altı haneye taşsın.
    /// </summary>
    public static string Format(long number) =>
        Prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
}
