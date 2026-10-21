using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Gelen IBAN'ın niteliği; banka hesabı başına sayılır (KVKK Ö-7). Değer yazılmaz, yalnız sınıf.</summary>
public enum IbanQuality { Empty = 0, Masked = 1, Full = 2 }

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

    /// <summary>Yalnız geçerli IBAN özetlenir (<see cref="NormalizeIban"/>); boşluk/küçük harf farkı özeti değiştirmez; boş,
    /// maskeli ya da geçersiz değer null döner.</summary>
    public string? HashIban(string? iban)
    {
        var norm = NormalizeIban(iban);
        return norm is null ? null : Hmac(norm);
    }

    /// <summary>
    /// Yalnız tam ve geçerli IBAN normalize edilir (KVKK Ö-7): boşluk atılır, büyük harfe çevrilir; yalnız ASCII harf ve rakam;
    /// 2 harf ülke + 2 rakam kontrol; uzunluk 15–34, TR'de tam 26; ISO 7064 MOD 97-10 kalanı 1. Aksi hâlde null. Maskeli değer
    /// (ör. <c>TR12****…</c>) özetlenseydi aynı ilk ve son karakterleri taşıyan farklı gönderenler aynı özete düşer, IBAN
    /// hafızası farklı kişileri tek müşteriye bağlardı. TR BBAN'ı standarda göre harf içerebilir: "TR'den sonra yalnız rakam"
    /// koşulu BİLEREK yok.
    /// </summary>
    public static string? NormalizeIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var compact = Compact(iban);
        if (compact.Length < MinIbanLength || compact.Length > MaxIbanLength) return null;
        foreach (var c in compact)
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)) return null;
        if (!char.IsAsciiLetterUpper(compact[0]) || !char.IsAsciiLetterUpper(compact[1])
            || !char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3])) return null;
        if (compact.StartsWith("TR", StringComparison.Ordinal) && compact.Length != TrIbanLength) return null;
        return HasValidIbanChecksum(compact) ? compact : null;
    }

    /// <summary>Boş → <see cref="IbanQuality.Empty"/>; geçerli → <see cref="IbanQuality.Full"/>; dolu ama geçersiz (maskeli,
    /// kısaltılmış, bozuk, hesap numarası) → <see cref="IbanQuality.Masked"/>.</summary>
    public static IbanQuality Classify(string? iban)
        => string.IsNullOrWhiteSpace(iban) ? IbanQuality.Empty
            : NormalizeIban(iban) is null ? IbanQuality.Masked : IbanQuality.Full;

    /// <summary>IBAN olmayan değerin maskesi: bir değerin geldiği görünür, hiçbir karakteri görünmez (KVKK Ö-6).</summary>
    public const string ShortValueMask = "…";

    /// <summary>En kısa geçerli IBAN uzunluğu (ISO 13616; ör. Norveç 15). Bundan kısa değer IBAN olamaz.</summary>
    public const int MinIbanLength = 15;

    /// <summary>En uzun IBAN (ISO 13616).</summary>
    public const int MaxIbanLength = 34;

    /// <summary>TR IBAN'ı: TR + 2 kontrol + 5 banka kodu + 1 rezerv + 16 hesap = 26.</summary>
    public const int TrIbanLength = 26;

    /// <summary>`TR12…345` — yalnız görüntü, YALNIZ geçerli IBAN için (<see cref="Classify"/> = Full). Ham girdi ASLA dönmez:
    /// boş/boşluk → ""; maskeli, kısaltılmış, bozuk ya da hesap numarası → <see cref="ShortValueMask"/> (KVKK Ö-6: IBAN olmayan
    /// değerin hiçbir karakteri "maske" sütununda görünmez; 15+ karakterlik hesap numarası dahil).</summary>
    public static string MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return "";
        var normalized = NormalizeIban(iban);
        return normalized is null ? ShortValueMask : normalized[..4] + "…" + normalized[^3..];
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
