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
        var lower = input.ToLower(Tr);
        var sb = new StringBuilder(lower.Length);
        var lastSpace = true;
        foreach (var ch in lower)
        {
            var c = ch switch
            {
                'ı' => 'i', 'i' => 'i', 'ş' => 's', 'ğ' => 'g', 'ü' => 'u', 'ö' => 'o', 'ç' => 'c', 'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => ch,
            };
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                sb.Append(c); lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' '); lastSpace = true;
            }
        }
        return sb.ToString().Trim();
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
