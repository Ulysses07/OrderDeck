namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Tek bir SMS gönderme soyutlaması. Email tarafındaki <c>IEmailSender</c>
/// ile aynı desen: prod'da Netgsm, dev/test'te log implementasyonu DI ile
/// seçilir (<c>Sms:Provider</c>).
/// </summary>
public interface ISmsSender
{
    /// <summary>
    /// <paramref name="toPhone"/> E.164 (+90XXXXXXXXXX) beklenir; implementasyon
    /// sağlayıcının istediği formata çevirir. Hata durumunda exception fırlatır
    /// (çağıran best-effort try/catch ile sarar).
    /// </summary>
    Task SendAsync(string toPhone, string message, CancellationToken ct = default);
}
