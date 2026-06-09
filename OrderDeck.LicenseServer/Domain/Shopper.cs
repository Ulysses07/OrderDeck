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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
