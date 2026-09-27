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
        foreach (var value in values.OfType<string>().Where(v => v.Length >= MinLength)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Length))
            text = text.Replace(value, Marker, StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
