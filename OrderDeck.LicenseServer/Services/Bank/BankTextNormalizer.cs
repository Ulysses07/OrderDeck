using System.Globalization;
using System.Text;

namespace OrderDeck.LicenseServer.Services.Bank;

public readonly record struct TokenizedText(IReadOnlyList<string> Tokens, string Joined);

/// <summary>Spec §6 normalizasyonu. Deterministik ve kültür-bağımsız: bankadan gelen açıklama ile
/// yayıncının kaydettiği kullanıcı adı aynı fonksiyondan geçer, aksi halde "İ/ı" gibi farklar eşleşmeyi kırar.</summary>
public static class BankTextNormalizer
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        // Önce uyumluluk ayrıştırması (NFKD): "Ş" → "S"+U+0327, "é" → "e"+U+0301, tam genişlikli "Ａ" → "A",
        // "ﬁ" → "fi". Böylece hem ayrıştırılmış (NFD) girdi hem Türkçe dışı aksanlar aynı ASCII harfe iner;
        // elle tutulan tabloda yalnız ayrıştırması olmayan "ı" kalır.
        var s = DecomposeKd(input);
        var lower = s.ToLower(Tr); // tr-TR: 'I' → 'ı'; 'İ' zaten "I"+U+0307 olarak ayrıştı
        var sb = new StringBuilder(lower.Length);
        var lastSpace = true;
        foreach (var ch in lower)
        {
            // Birleşik işaret harfin parçasıdır, ayırıcı değil. Biçim karakteri (Cf) görünmezdir, kelimeyi bölmez:
            // yumuşak tire U+00AD, sıfır genişlikli boşluk/birleştirici U+200B/U+200D, yön işareti U+200E/F,
            // sözcük birleştirici U+2060, BOM U+FEFF.
            if (CharUnicodeInfo.GetUnicodeCategory(ch)
                is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                continue;
            var c = ch == 'ı' ? 'i' : ch;
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c); lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' '); lastSpace = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>NFKD ayrıştırması, hiç atmadan. Eşleşmemiş vekil ya da U+FFFE ayrıştırmayı patlatır
    /// (ArgumentException); böyle birim UTF-16 sınırından yarıya kesilmiş emojiden gelir (ObifinPollJob.Trim,
    /// IntakeForm FB adı [..64]). Eşleştirici asla atmamalı: geçersiz birim ayırıcı (boşluk) sayılır, kalanı yine
    /// ayrıştırılır. Mutlu yol değişmez; temizlik yalnız istisnada, tek dizi ayırarak yapılır.</summary>
    private static string DecomposeKd(string input)
    {
        try
        {
            return input.IsNormalized(NormalizationForm.FormKD) ? input : input.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            var chars = input.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                    i++; // tam vekil çifti (ör. bütün emoji) geçerlidir, dokunma
                else if (char.IsSurrogate(chars[i]) || chars[i] == '\uFFFE')
                    chars[i] = ' ';
            }
            return new string(chars).Normalize(NormalizationForm.FormKD);
        }
    }

    public static TokenizedText Tokenize(string? input)
    {
        var norm = Normalize(input);
        var tokens = norm.Length == 0 ? Array.Empty<string>() : norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new TokenizedText(tokens, string.Concat(tokens));
    }

    /// <summary>Kullanıcı adının bitişik anahtarı ("aysegul34") — alt dize araması için.</summary>
    public static string UsernameKey(string? username) => Tokenize(username).Joined;

    public static IReadOnlyList<string> UsernameTokens(string? username)
    {
        // "gul34" değil "gul","34": harf/rakam sınırında da böl ki açıklamadaki "gul 34" ile eşleşsin.
        var result = new List<string>();
        foreach (var tok in Tokenize(username).Tokens)
        {
            var start = 0;
            for (var i = 1; i <= tok.Length; i++)
            {
                if (i == tok.Length || char.IsDigit(tok[i]) != char.IsDigit(tok[i - 1]))
                { result.Add(tok[start..i]); start = i; }
            }
        }
        return result;
    }
}
