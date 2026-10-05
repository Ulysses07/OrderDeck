using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// TCKN at-rest şifrelemesi. Mevcut Data Protection anahtar halkası (Netgsm /
/// WhatsApp parolalarıyla aynı; anahtarlar SQL'den ayrı yedekleniyor) — SQL
/// yedeği tek başına sızarsa TCKN okunamaz.
///
/// Açık çağrı, EF değer dönüştürücüsü değil: dönüştürücü model önbelleğine
/// gömülür ve farklı anahtarlı test fabrikaları aynı modeli paylaşınca çözme
/// kırılır. Domain özellikleri bu yüzden *Protected adını taşır; her
/// okuma/yazma yeri bu sınıfı çağırmak zorunda.
///
/// Geçiş dönemi: Data Protection öneki (CfDJ8) taşımayan değer eski düz
/// metindir, olduğu gibi döner (TcknBackfillHostedService onları şifreler).
/// Düz metin desteği bir sonraki sürümde kaldırılacak.
///
/// Anahtar kaybı: çözülemeyen değer null döner ve uyarı yazılır — TCKN
/// müşteriden yeniden alınabilir; 500 ile akışı kilitlemek daha kötü.
/// </summary>
public sealed class TcknProtector
{
    public const string Purpose = "OrderDeck.Tckn.v1";
    /// <summary>Şifreli metnin kolon sınırı.</summary>
    public const int ProtectedMaxLength = 512;

    private readonly IDataProtector _protector;
    private readonly ILogger<TcknProtector> _log;

    public TcknProtector(IDataProtectionProvider provider, ILogger<TcknProtector> log)
    {
        _protector = provider.CreateProtector(Purpose);
        _log = log;
    }

    public string? Protect(string? plain)
        => string.IsNullOrWhiteSpace(plain) ? null : _protector.Protect(plain.Trim());

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        if (IsLegacyPlaintext(stored)) return stored;
        try
        {
            return _protector.Unprotect(stored);
        }
        catch (CryptographicException ex)
        {
            _log.LogWarning(ex, "TCKN çözülemedi (anahtar kaybı ya da bozuk değer); boş dönülüyor");
            return null;
        }
    }

    /// <summary>Data Protection yükleri sabit sihirli başlıkla (0x09F0C9F0)
    /// başlar; base64url'de bu her zaman "CfDJ8". Bu önekle başlamayan her
    /// değer eski düz metindir — biçimi ne olursa olsun (kayıt akışı TC'yi
    /// doğrulamadan yazdığı için 11 hane garanti değil).</summary>
    private const string PayloadPrefix = "CfDJ8";

    public static bool IsLegacyPlaintext(string value)
        => !value.StartsWith(PayloadPrefix, StringComparison.Ordinal);
}
