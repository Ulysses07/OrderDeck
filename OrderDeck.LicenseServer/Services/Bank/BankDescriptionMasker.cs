using System.Text;
using System.Text.RegularExpressions;

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
/// <item>Harf ya da rakama bitişik OLMAYAN 11 haneli rakam dizisi (TCKN, cep telefonu) <see cref="ElevenDigitMask"/> olur.
/// Harfe bitişik dizi kullanıcı adının parçası olabilir: maskelenmez (bedeli: isme bitişik yazılmış TCKN düz kalır).</item>
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

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{11}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex ElevenDigits();

    public static string? Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return ElevenDigits().Replace(MaskIbans(text), ElevenDigitMask);
    }

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
