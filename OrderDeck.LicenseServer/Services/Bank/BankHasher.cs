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
    /// <summary>VKN 10 rakam — bundan kısa bir değer vergi kimliği değildir.</summary>
    public const int MinTaxIdDigits = 10;

    private readonly byte[] _key;

    public BankHasher(IOptions<BankOptions> opt)
    {
        var key = opt.Value.HashKey;
        // Spec §7: HMAC anahtarı 32+ bayt. Karakter değil BAYT sayılır (UTF-8). Anahtar mesaja girmez.
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException(
                "OrderDeck:Bank:HashKey boş ya da 32 bayttan kısa — IBAN hash'i güvensiz olur.");
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>Boşluk/küçük harf farkı hash'i değiştirmez; boş değer null döner.</summary>
    public string? HashIban(string? iban)
    {
        var norm = NormalizeIban(iban);
        return norm is null ? null : Hmac(norm);
    }

    /// <summary>Yalnız rakamlar sayılır. VKN 10, TCKN 11 rakamdır; <see cref="MinTaxIdDigits"/>'ten
    /// kısa kalıntı ("0", "-", 9 rakam) null döner — hash'i olsaydı sonraki eşleştirici onu
    /// kimlik kanıtı sanabilirdi.</summary>
    public string? HashTaxId(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return null;
        var digits = new string(taxId.Where(char.IsAsciiDigit).ToArray());
        return digits.Length < MinTaxIdDigits ? null : Hmac(digits);
    }

    public static string? NormalizeIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var compact = Compact(iban);
        return compact.Length < 8 ? null : compact;
    }

    /// <summary>`TR12…345` — yalnız görüntü. Ham girdi ASLA dönmez: boş/boşluk → "",
    /// 8 karakterden kısa değer normalize (boşluksuz, büyük harf) hâliyle döner.</summary>
    public static string MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return "";
        var compact = Compact(iban);
        return compact.Length < 8 ? compact : compact[..4] + "…" + compact[^3..];
    }

    private static string Compact(string value)
        => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private string Hmac(string value)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(mac);
    }
}
