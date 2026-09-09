namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Müşteri (shopper) app kullanıcısı. WPF'teki Customer entity'si (yayıncı)
/// ile karıştırılmamalı — bu, alışveriş yapan son kullanıcı. Telefon global
/// unique kimlik; bir shopper birden çok yayıncıya bağlı olabilir
/// (ShopperBroadcasterLink üzerinden).
/// </summary>
public sealed class Shopper
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";        // E.164, global unique
    public string PasswordHash { get; set; } = ""; // bcrypt
    public string Address { get; set; } = "";
    public string? Email { get; set; }
    public string? Tc { get; set; }                // KVKK: AES at-rest (Faz 0b'de)

    public bool NotificationsEnabledBroadcast { get; set; } = true;
    public bool NotificationsEnabledOrders { get; set; } = true;
    public bool NotificationsEnabledPayments { get; set; } = true;

    /// <summary>
    /// Ticari/kampanya SMS izni (ticari elektronik ileti onayı). Opt-in: kayıt
    /// ekranındaki açık onay kutusundan gelir, varsayılan izinsiz (false).
    /// Shopper uygulama profilinden açıp kapatabilir. Yayıncı toplu SMS alıcı
    /// listesi yalnızca SmsConsent=true shopper'lardan türetilir.
    /// </summary>
    public bool SmsConsent { get; set; }

    /// <summary>Son ONAY anı. İspat yükü hizmet sağlayıcıda (6563) ve İYS
    /// onay yüklemesi tarih istiyor — boolean tek başına ispat değil.
    /// Yalnızca false→true GEÇİŞİNDE yazılır; aynı değerin tekrar PUT'lanması
    /// tarihi ezmez (ispat tarihi kaymasın). null + SmsConsent=true =
    /// bu alanlardan ÖNCE alınmış onay (tarih tahmini için CreatedAt kullan,
    /// #127 öncesi otomatik opt-in ayrımına dikkat).</summary>
    public DateTimeOffset? SmsConsentAt { get; set; }

    /// <summary>Onayın alındığı kanal: "register" (kayıt ekranı kutusu) /
    /// "profile" (profil ekranından açma). İYS kaynak alanına gider.</summary>
    public string? SmsConsentSource { get; set; }

    /// <summary>Son RET anı (true→false geçişi). İYS'ye RET push'u ve
    /// "opt-out'u ne zaman işledik" ispatı için.</summary>
    public DateTimeOffset? SmsConsentRevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
