namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Gönderilen SMS'in İYS sınıfı. Netgsm tarafında <c>iysfilter</c> değerini
/// belirler: hizmet mesajı (OTP) İYS kontrolünden muaf, ticari ileti (kampanya)
/// İYS ret listesinden geçmek ZORUNDA. Tek global filtre kullanmak ya OTP'yi
/// İYS-retli kullanıcıya ulaştırmaz ya da ticari mesajı kontrolsüz gönderir —
/// ikisi de kabul edilemez, o yüzden tür mesaj başına taşınır.
/// </summary>
public enum SmsKind
{
    /// <summary>Hizmet/bilgilendirme mesajı (OTP, parola sıfırlama) —
    /// İYS filtresi "0", ret listesi kontrolü yok.</summary>
    Transactional,

    /// <summary>Ticari ileti (toplu kampanya) — İYS filtresi "11" (B2C),
    /// Netgsm alıcıyı İYS onay/ret kaydına göre eler.</summary>
    Commercial,
}
