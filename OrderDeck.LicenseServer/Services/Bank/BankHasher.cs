using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// IBAN/VKN için HMAC-SHA256 (spec §7). Düz SHA yetersiz: IBAN'ın entropisi düşük,
/// sızan tablo sözlükle çözülür. Anahtar yalnız sunucuda; hash yalnız "aynı mı" sorusuna
/// cevap verir (hafıza araması), geri dönüş yoktur.
/// </summary>
public sealed class BankHasher
{
    private readonly byte[] _key;

    public BankHasher(IOptions<BankOptions> opt)
    {
        var key = opt.Value.HashKey;
        if (string.IsNullOrWhiteSpace(key) || key.Length < 16)
            throw new InvalidOperationException(
                "OrderDeck:Bank:HashKey boş ya da 16 karakterden kısa — IBAN hash'i güvensiz olur.");
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>Boşluk/küçük harf farkı hash'i değiştirmez; boş değer null döner.</summary>
    public string? HashIban(string? iban)
    {
        var norm = NormalizeIban(iban);
        return norm is null ? null : Hmac(norm);
    }

    public string? HashTaxId(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return null;
        var digits = new string(taxId.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : Hmac(digits);
    }

    public static string? NormalizeIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var compact = new string(iban.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return compact.Length < 8 ? null : compact;
    }

    /// <summary>`TR12…345` — yalnız görüntü; 8 karakterden kısa değer olduğu gibi döner.</summary>
    public static string MaskIban(string? iban)
    {
        var norm = NormalizeIban(iban);
        if (norm is null) return iban ?? "";
        return norm[..4] + "…" + norm[^3..];
    }

    private string Hmac(string value)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(mac);
    }
}
