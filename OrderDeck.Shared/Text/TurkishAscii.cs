namespace OrderDeck.Shared.Text;

/// <summary>
/// Türkçe harflerin ASCII karşılıkları — <b>tek kaynak</b>.
///
/// Neden ayrı bir sınıf: aynı katlama tablosuna birden çok iş ihtiyaç duyuyor —
/// barkoda girecek kod parçası (<c>AxisCodeDeriver</c>, Code128 yalnız ASCII
/// kodlar), arama normalleştirmesi (<c>SearchNormalizer</c>, "tisort" yazan
/// kullanıcı "Tişört"ü bulmalı) ve WPF tarafındaki yorum eşleştirmesi. Tablo
/// kopyalanırsa sessizce ayrışır ve ayrışmayı hiçbir test göstermez: her taraf
/// kendi kopyasıyla tutarlı kalır, yalnız birbirleriyle tutarsız olur.
///
/// Neden <c>OrderDeck.Shared</c>'da: sunucu ile WPF arasında ortak assembly yok;
/// WPF'in yerel katalog kopyası sunucunun ürettiği <c>NameSearch</c> değerini
/// saklıyor, iğne başka bir tablodan geçerse yerel arama sessizce bozulur.
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
