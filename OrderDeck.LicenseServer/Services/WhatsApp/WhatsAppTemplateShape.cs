using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Şablon gövdesindeki yer tutucuların çözümlenmesi.
///
/// <para>HTTP'den ayrı duruyor çünkü asıl incelik burada: yanlış sayıda parametre
/// göndermek Meta'dan 132000 ile döner ve şablon <b>ücretli</b> olduğu için
/// yayıncı parasını denemelere yatırır.</para>
/// </summary>
public static class WhatsAppTemplateShape
{
    /// <summary>Yer tutucunun kendisi — içi rakam mı isim mi, ayrımı çağıran yapar.</summary>
    private static readonly Regex Placeholder =
        new(@"\{\{(.*?)\}\}", RegexOptions.CultureInvariant);

    public const string NamedParams =
        "Bu şablon isimli değişken kullanıyor; panel yalnız {{1}}, {{2}} biçimini gönderebiliyor.";

    public const string GappedParams =
        "Şablonun değişken numaraları 1'den başlayarak sırayla gitmiyor.";

    public const string HeaderMedia =
        "Şablonun başlığında görsel/belge var; panel yalnız metin başlıklı şablon gönderebiliyor.";

    public const string HeaderVariable =
        "Şablonun başlığında değişken var; panel yalnız gövde değişkenlerini doldurabiliyor.";

    public const string ButtonVariable =
        "Şablonun butonu değişken istiyor; panel bu tür şablonu gönderemiyor.";

    public const string AuthCategory =
        "Doğrulama (authentication) şablonları ayrı bir gönderim biçimi istiyor.";

    /// <summary>
    /// Gövdedeki konumsal parametre sayısı.
    /// </summary>
    /// <returns><c>Unsupported</c> doluysa gövde bizim gönderebileceğimiz
    /// biçimde değil ve <c>Count</c> anlamsızdır.</returns>
    public static (int Count, string? Unsupported) CountBodyParams(string bodyText)
    {
        var matches = Placeholder.Matches(bodyText);
        if (matches.Count == 0) return (0, null);

        var indexes = new SortedSet<int>();
        foreach (Match m in matches)
        {
            var inner = m.Groups[1].Value.Trim();
            // Meta 2024'ten beri {{musteri_adi}} gibi isimli değişkene de izin
            // veriyor. Gönderenimiz konumsal dizi yolluyor; isimli şablonda o
            // dizi sessizce yanlış yere oturmaz, Meta reddeder — ama ücretli
            // denemeye bırakmak yerine burada eliyoruz.
            if (!int.TryParse(inner, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return (0, NamedParams);
            }
            indexes.Add(n);
        }

        // 1..n bitişik olmalı. Meta bunu kendi de zorluyor ama liste bizim
        // verimiz değil: boşluklu bir dizide ({{1}}, {{3}}) yayıncının girdiği
        // değerler bir sıra kayar ve yanlış bilgi müşteriye gider.
        var expected = 1;
        foreach (var n in indexes)
        {
            if (n != expected++) return (0, GappedParams);
        }

        return (indexes.Count, null);
    }
}
