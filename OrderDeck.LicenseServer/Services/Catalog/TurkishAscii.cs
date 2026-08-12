namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Türkçe harflerin ASCII karşılıkları — <b>tek kaynak</b>.
///
/// Neden ayrı bir sınıf: aynı katlama tablosuna iki ayrı iş ihtiyaç duyuyor —
/// barkoda girecek kod parçası (<see cref="AxisCodeDeriver"/>, Code128 yalnız
/// ASCII kodlar) ve arama normalleştirmesi (<see cref="SearchNormalizer"/>,
/// "tisort" yazan kullanıcı "Tişört"ü bulmalı). Tablo iki yere kopyalanırsa
/// sessizce ayrışır ve ayrışmayı hiçbir test göstermez: her iki taraf da kendi
/// kopyasıyla tutarlı kalır, yalnız birbirleriyle tutarsız olur.
/// </summary>
public static class TurkishAscii
{
    /// <summary>
    /// Büyük harfe çevrilmiş bir karakteri ASCII karşılığına indirger;
    /// karşılığı olmayanı olduğu gibi döner.
    /// </summary>
    public static char Fold(char upper) => upper switch
    {
        'Ç' => 'C',
        'Ğ' => 'G',
        'İ' => 'I',      // U+0130 — ToUpperInvariant bunu korur
        '\u0131' => 'I', // ı — ToUpperInvariant U+0131'i küçük bırakır
        'Ö' => 'O',
        'Ş' => 'S',
        'Ü' => 'U',
        _ => upper,
    };
}
