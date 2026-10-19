using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// IBAN için HMAC-SHA256 (spec §7). Düz SHA yetersiz: IBAN'ın entropisi düşük, sızan tablo sözlükle çözülür. Anahtar yalnız
/// sunucuda; özet yalnız "aynı mı" sorusuna cevap verir (hafıza araması). Anahtarı bilmeyen özetten IBAN elde edemez; anahtarı
/// bilen aday IBAN'ları deneyerek bulabilir — özet ve maske bu yüzden kişisel veri sayılır (KVKK Ö-6). VKN/TCKN özetlenmez.
/// </summary>
public sealed class BankHasher
{
    /// <summary>Spec §7: HMAC anahtarı en az bu kadar BAYT (UTF-8) — karakter değil.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>Anahtar yok ya da kısa. Sunucu YİNE açılır (master merge = otomatik prod deploy; eksik bir .env satırı
    /// lisans sunucusunu düşürmemeli): Program.cs açılışta bunu tek bir uyarı olarak loglar ve Obifin çekim/hesap
    /// işlerini zamanlamaz (saklama işi BankHasher istemez, yine koşar); <see cref="BankHasher"/>'ı isteyen her
    /// çözümleme bu mesajla düşer, admin sayfaları yakalayıp gösterir. Anahtar mesaja girmez.</summary>
    public const string DisabledMessage = "Banka modülü kapalı: OrderDeck:Bank:HashKey yok ya da 32 bayttan kısa";

    private readonly byte[] _key;

    public BankHasher(IOptions<BankOptions> opt)
    {
        var key = opt.Value.HashKey;
        if (!IsValidKey(key)) throw new InvalidOperationException(DisabledMessage);
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>Boş/boşluk değil ve en az <see cref="MinKeyBytes"/> bayt (UTF-8). Kurucu ve Program.cs aynı denetimi
    /// kullanır: açılışta "banka modülü açık mı" kararı ile hasher'ın kurulabilmesi ayrışmasın.</summary>
    public static bool IsValidKey([NotNullWhen(true)] string? key)
        => !string.IsNullOrWhiteSpace(key) && Encoding.UTF8.GetByteCount(key) >= MinKeyBytes;

    /// <summary>Boşluk/küçük harf farkı hash'i değiştirmez; boş değer null döner.</summary>
    public string? HashIban(string? iban)
    {
        var norm = NormalizeIban(iban);
        return norm is null ? null : Hmac(norm);
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

    /// <summary>ISO 13616 / ISO 7064 MOD 97-10: boşluksuz, büyük harfli değerin ilk dört karakteri sona alınır, harfler 10–35'e
    /// çevrilir, kalan 1 olmalı. Biçim (ülke, uzunluk) denetlemez — çağıran yapar; ASCII büyük harf/rakam dışı karakterde
    /// (küçük harf dahil) false.</summary>
    public static bool HasValidIbanChecksum(string compactUpper)
    {
        if (compactUpper.Length < 5) return false;
        var remainder = 0;
        foreach (var c in compactUpper[4..] + compactUpper[..4])
        {
            if (char.IsAsciiDigit(c)) remainder = (remainder * 10 + (c - '0')) % 97;
            else if (char.IsAsciiLetterUpper(c)) remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
            else return false;
        }
        return remainder == 1;
    }

    private static string Compact(string value)
        => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private string Hmac(string value)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(mac);
    }
}
