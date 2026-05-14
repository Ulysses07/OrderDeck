namespace OrderDeck.LicenseServer.Services.Push;

/// <summary>
/// Mobile push notification fan-out. Implementations: <see cref="StubNotificationSender"/>
/// (log-only, default in dev/test) ya da gerçek FCM/APNS gönderici (Faz 2'de
/// `FcmNotificationSender` eklenecek — service account JSON env'den okur).
///
/// Sender her zaman fire-and-forget kullanılır (sync hook'ları içinden);
/// transient hata fırlatmamalı, log'la yetinmeli — push başarısız olsa bile
/// sync controller başarılı dönmelidir.
/// </summary>
public interface INotificationSender
{
    /// <summary>
    /// Belirli bir customer'ın tüm kayıtlı device'larına push gönderir.
    /// Stale token'lar (FCM 404 / APNS BadDeviceToken) sessizce drop edilir.
    /// </summary>
    /// <param name="customerId">Hedef Customer.Id (License sahibinin Guid'i).</param>
    /// <param name="title">Bildirim başlığı (kısa, &lt;= 50 char).</param>
    /// <param name="body">Bildirim gövdesi (&lt;= 200 char).</param>
    /// <param name="data">Optional click payload — örn. {"type":"payment","id":"..."}.</param>
    Task SendToCustomerAsync(
        Guid customerId,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default);
}
