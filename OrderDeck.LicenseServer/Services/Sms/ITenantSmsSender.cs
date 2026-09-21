namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Bir kiracının Netgsm kimlikleri. Şifre ÇÖZÜLMÜŞ hâlde taşınır —
/// yalnız bu record'un ömrü boyunca bellekte; asla loglanmaz, asla saklanmaz.</summary>
public sealed record TenantSmsCredentials(string UserCode, string Password, string Header);

/// <summary>
/// Kiracı (yayıncı) kimlikleriyle TİCARİ SMS gönderimi — §1.2. Merkezi
/// <see cref="ISmsSender"/>'dan ayrı arayüz olması kasıtlı: tek arayüze kimlik
/// parametresi eklemek "yanlış marka altında ticari SMS" hatasını çalışma-anı
/// kontrolüne indirger; ayrı arayüz onu yapısal olarak imkânsız kılar.
/// <see cref="SmsKind"/> parametresi YOK: bu yol tanımı gereği Commercial,
/// iysfilter daima ticari filtre ile gider.
/// </summary>
public interface ITenantSmsSender
{
    /// <summary>Tek alıcıya ticari SMS. Dönüş: Netgsm <c>jobid</c> (yanıtta
    /// yoksa null) — ileride rapor mutabakatı için saklanır (§3.4 karar 3).
    /// Temiz ret → <see cref="NetgsmSmsException"/>; ağ hatası → ham istisna
    /// (ayrım sözleşmesi o sınıfın doc'unda).</summary>
    Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default);
}
