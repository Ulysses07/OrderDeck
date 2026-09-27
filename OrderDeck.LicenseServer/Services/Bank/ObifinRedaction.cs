using System.Text;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Obifin'e gönderilen değerlerin geri yankılandığı metni (Obifin/banka hata mesajı, vekil/debug sayfası gövdesi)
/// LastError'a, günlüğe ya da ekrana gitmeden önce maskeler. Bağlantı servisi ve istemci aynı kuralı kullanır.</summary>
internal static class ObifinRedaction
{
    /// <summary>Maskelenen değerin yerine geçer.</summary>
    public const string Marker = "[gizli]";

    /// <summary>Bundan kısa değer maskelenmez: iki harflik bir değeri her geçtiği yerde gizlemek metni okunmaz yapar, bu
    /// uzunlukta bir değer de kimlik sayılmaz.</summary>
    public const int MinLength = 3;

    /// <summary><paramref name="values"/>'daki her değerin (en az <see cref="MinLength"/> karakter) metindeki her geçişini
    /// <see cref="Marker"/> yapar — sıralı, harf duyarsız. Uzun değer önce: kısa bir değer uzunun parçasıysa önce o
    /// değiştirilseydi uzun değerin kalanı açıkta kalırdı.</summary>
    public static string Redact(string text, IEnumerable<string?> values)
    {
        foreach (var value in Maskable(values))
            text = text.Replace(value, Marker, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>Tanı kopyası: <paramref name="text"/>'in ilk <paramref name="maxChars"/> karakteri, <paramref name="values"/>
    /// maskelenmiş olarak. Tüm gövde taranmaz: metin önce <paramref name="maxChars"/> + en uzun değer kadar dilimlenir.
    /// Sınırdan ÖNCE başlayan bir geçiş sınırı aşsa da TAMAMI maskelenir; sınırda ya da sonra başlayan hiçbir karakter
    /// çıktıya girmez. Kesme, maskeli metnin uzunluğuna değil kaynak konumuna göredir: maskelenen değerler metni
    /// kısaltınca dilimin sonundaki yarım bir değerin başı sınırın içine kayıp açığa çıkamaz. Harf duyarsız; aynı
    /// konumda uzun değer önce.</summary>
    public static string RedactHead(string text, IEnumerable<string?> values, int maxChars)
    {
        var masked = Maskable(values).ToList();
        var longest = masked.Count == 0 ? 0 : masked[0].Length;
        var window = text.Length > maxChars + longest ? text[..(maxChars + longest)] : text;
        var result = new StringBuilder(Math.Min(window.Length, maxChars));
        var i = 0;
        while (i < window.Length && i < maxChars)
        {
            var hit = MatchAt(window, i, masked);
            if (hit is null) { result.Append(window[i]); i++; }
            else { result.Append(Marker); i += hit.Length; }
        }
        return result.ToString();
    }

    /// <summary><paramref name="text"/>'in <paramref name="index"/> konumunda başlayan ilk (en uzun) değer; yoksa null.</summary>
    private static string? MatchAt(string text, int index, List<string> values)
    {
        var rest = text.AsSpan(index);
        foreach (var v in values)
            if (rest.StartsWith(v, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    private static IEnumerable<string> Maskable(IEnumerable<string?> values)
        => values.OfType<string>().Where(v => v.Length >= MinLength)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Length);
}
