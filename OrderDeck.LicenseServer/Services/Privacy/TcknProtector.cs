using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// TCKN at-rest şifrelemesi. Mevcut Data Protection anahtar halkası (Netgsm /
/// WhatsApp parolalarıyla aynı; anahtarlar SQL'den ayrı yedekleniyor) — SQL
/// yedeği tek başına sızarsa TCKN okunamaz.
///
/// Tek amaç dizesi üç kolon için bilerek ortak (Shoppers.Tc,
/// IntakeFormSubmissions.Tckn, WpfCustomerProjections.Tckn): hepsi aynı tür
/// değer ve birleştirme işi şifreli metni satırlar arasında taşıyor.
/// NetgsmAccountService'teki 'her alana ayrı amaç' kuralının bilinçli
/// istisnası — ayırmak mevcut her satırı çözülemez yapar.
///
/// Açık çağrı, EF değer dönüştürücüsü değil: dönüştürücü model önbelleğine
/// gömülür ve farklı anahtarlı test fabrikaları aynı modeli paylaşınca çözme
/// kırılır. Domain özellikleri bu yüzden *Protected adını taşır; her okuma
/// yeri (2. sürümden itibaren yazma yerleri de) bu sınıfı çağırmak zorunda.
///
/// İKİ SÜRÜMLÜ YAYIN (genişlet/daralt, PR-0a + PR-0b): deploy iş akışı
/// /ready 60 sn içinde başarısız olursa ÖNCEKİ imaja OTOMATİK geri dönüyor —
/// tek adımlı bir yayın olsaydı, geçici bir ağ sorunu bile geri almayı
/// tetikler ve TC'yi şifreli yazan bir imajın ardına, şifreli metni TC diye
/// okuyup WPF'e ve e-Fatura'ya KALICI olarak yazacak eski bir imaj
/// bırakabilirdi. Bu yüzden İKİ YÖNLÜ geri almanın da güvenli olduğu bir
/// sıraya bölündü:
///   PR-0a (bu sürüm, 1. sürüm): her okuma Unprotect çağırır (düz metni VE
///      şifreli metni ikisini de çözer); yazmalar HÂLÂ düz metin.
///      - PR-0b'den PR-0a'ya geri alma GÜVENLİ: bu sürüm PR-0b'nin
///        yazdığı/backfill'in şifrelediği satırları da okuyabiliyor.
///      - PR-0a'dan PR-0'DAN ÖNCEKİ imaja geri alma GÜVENLİ: bu sürüm
///        hiçbir satırı şifreli YAZMIYOR, o yüzden Unprotect'i hiç tanımayan
///        eski imaj karşısına asla şifreli metin çıkmaz.
///   PR-0b (2. sürüm): yazmalar Protect'e geçer ve TcknBackfillJob var olan
///      düz metin satırları arka planda şifreler.
/// PR-0a sahada bir süre durup gözlendikten sonra PR-0b basılır.
///
/// Data Protection öneki (CfDJ8) taşımayan değer düz metindir (eski satır ya
/// da bu sürümün kendi yazdığı satır — ikisi de Unprotect'ten aynı şekilde
/// geçer), olduğu gibi döner.
///
/// Protect önekli (CfDJ8) görünen girdiyi de HER ZAMAN şifreler, asla
/// atlamaz: atlasaydı, sızan bir yedekteki şifreli metin kayıt formuna
/// yapıştırılıp profil ucundan çözdürülebilirdi.
///
/// ÇÖZME KÂHİNİ (decryption oracle) KURALI — PR-0a'da ayrıca geçerli:
/// yazmalar olduğu gibi saklanır, okumalar CfDJ8 önekli değeri çözer.
/// Yapıştırılmış bir yükün (en az ~134 karakter) çözdürülmesini engelleyen
/// şey girdi sınırlarıdır: kayıtta ≤11 karakter (+ CfDJ8 önekini ayrıca
/// reddeden kontrol), PATCH'te ve formda 11 rakam + checksum. Bu sınırları
/// gevşetmek bir çözme kâhini açar.
///
/// Anahtar kaybı: çözülemeyen değer null döner ve uyarı yazılır — TCKN
/// müşteriden yeniden alınabilir; 500 ile akışı kilitlemek daha kötü.
/// </summary>
public sealed class TcknProtector
{
    private const string Purpose = "OrderDeck.Tckn.v1";

    /// <summary>Şifreli metnin kolon sınırı. Varsayılan algoritmada 11 haneli
    /// TCKN ~134 karaktere şifrelenir; en çok ~287 bayt düz metin sığar.
    /// Şifreli metin ASLA kırpılmaz — kırpılan şifreli metin bir daha
    /// çözülemez; sınır düz metne şifrelemeden önce uygulanır.</summary>
    public const int ProtectedMaxLength = 512;

    // 0x09F0C9F0 sihirli başlığının base64url hali.
    private const string PayloadPrefix = "CfDJ8";

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
    public static bool IsLegacyPlaintext(string value)
        => !value.StartsWith(PayloadPrefix, StringComparison.Ordinal);
}
