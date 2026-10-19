using System.Text;
using System.Text.RegularExpressions;
using OrderDeck.LicenseServer.Services.IntakeForm;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// KVKK Ö-16/Ö-4: havale açıklaması serbest metindir; gönderen içine IBAN ya da T.C. kimlik numarası yazabilir. Kayda alınmadan
/// önce (açıklama sütunu; ham kayıttaki açıklama alanları da aynı yoldan geçer):
/// <list type="bullet">
/// <item>TR IBAN — bitişik ya da tek boşluklu gruplarla, büyük/küçük harf, önünde ya da arkasında bitişik harf olsa da —
/// <see cref="BankHasher.MaskIban"/> biçimine (<c>TR12…345</c>) çevrilir. Kalıp <c>TR</c> + 2 rakam + 5 rakam (banka kodu) + 17
/// alfasayısal; sınır yok, yanlış eşleşmeyi mod-97 (<see cref="BankHasher.HasValidIbanChecksum"/>) eler. Sözcük dizileri ve
/// kullanıcı adları eşleşmez — eşleştirici açıklamadaki kullanıcı adına bakıyor. Kontrol hanesi tutmayan dizi (yanlış yazılmış
/// IBAN) maskelenmez: kalıbı sınırsız bırakmanın bedeli. Tutmayan adaydan sonra arama bir karakter ileriden sürer: önündeki
/// "TR" + 7 rakam gerçek IBAN'ın başını yutup onu düz bırakmasın.</item>
/// <item>Rakama bitişik olmayan 11 haneli rakam dizisi (TCKN, cep telefonu) <see cref="ElevenDigitMask"/> olur. Tek başına
/// duran dizi (komşusu boşluk, noktalama ya da metin sınırı) her zaman maskelenir. Harfe bitişik dizi kullanıcı adının parçası
/// olabilir; yalnız TCKN kontrol hanelerini tutuyorsa (<see cref="TcknValidator"/>) maskelenir: kimlik numarasının en doğal
/// yazımları (<c>TC</c>/<c>TCKN</c>/<c>TCNO</c> önekli, isme bitişik) harfe bitişik. TCKN 0 ile başlamadığı için kullanıcı
/// adındaki "0" ile başlayan telefon hiç etkilenmez; harfe bitişik rastgele 11 hane yaklaşık %1 olasılıkla (9/1000) TCKN gibi
/// görünüp maskelenir — IBAN'daki mod-97 ile aynı türden, gizlilik yönünde bedel. '_' ya da '.' harf değildir: onlarla ayrılmış
/// 11 hane maskelenir; o kullanıcı adı açıklamadan eşleşmez.</item>
/// </list>
/// Maske kırpmadan ÖNCE uygulanır: sütun sınırı IBAN'ın ortasına düşerse kırpılmış baş hiçbir kalıba uymaz. Saf ve
/// idempotent: maskelenmiş metin yeniden maskelenince değişmez (bank-data-cleanup buna dayanır).
/// </summary>
public static partial class BankDescriptionMasker
{
    public const string ElevenDigitMask = "***********";

    // Harfler açıkça yazılır (IgnoreCase yok): büyük/küçük harf eşdeğerlik tablosu ASCII dışı harfleri (ör. Kelvin işareti)
    // [A-Za-z]'ye katmasın. Rakam [0-9]: kontrol hanesi denetimi yalnız ASCII rakamı tanır.
    [GeneratedRegex(@"[Tt][Rr]\s?[0-9]\s?[0-9](?:\s?[0-9]){5}(?:\s?[0-9A-Za-z]){17}", RegexOptions.CultureInvariant)]
    private static partial Regex TrIban();

    // Sınır yalnız rakam; harf komşuluğu eşleşmeden sonra MaskElevenDigits'te tartılır.
    [GeneratedRegex(@"(?<!\p{N})\d{11}(?!\p{N})", RegexOptions.CultureInvariant)]
    private static partial Regex ElevenDigits();

    public static string? Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return MaskElevenDigits(MaskIbans(text));
    }

    private static string MaskElevenDigits(string text)
        => ElevenDigits().Replace(text, m =>
        {
            var letterBefore = m.Index > 0 && char.IsLetter(text[m.Index - 1]);
            var end = m.Index + m.Length;
            var letterAfter = end < text.Length && char.IsLetter(text[end]);
            // Harfe bitişik dizi kullanıcı adının parçası olabilir: yalnız TCKN kontrol hanelerini tutuyorsa maskelenir.
            return (letterBefore || letterAfter) && TcknValidator.Validate(m.Value) is not null ? m.Value : ElevenDigitMask;
        });

    private static string MaskIbans(string text)
    {
        StringBuilder? sb = null;
        var copied = 0;
        var m = TrIban().Match(text);
        while (m.Success)
        {
            var compact = string.Concat(m.Value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
            if (BankHasher.HasValidIbanChecksum(compact))
            {
                (sb ??= new StringBuilder(text.Length)).Append(text, copied, m.Index - copied).Append(BankHasher.MaskIban(compact));
                copied = m.Index + m.Length;
                m = TrIban().Match(text, copied);
            }
            else
            {
                // Regex.Replace tutmayan adayın sonundan sürerdi; adayın içinde başlayan gerçek IBAN gözden kaçardı.
                m = TrIban().Match(text, m.Index + 1);
            }
        }
        return sb is null ? text : sb.Append(text, copied, text.Length - copied).ToString();
    }
}
