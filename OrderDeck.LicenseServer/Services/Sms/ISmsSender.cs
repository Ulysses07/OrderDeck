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
    /// (çağıran best-effort try/catch ile sarar). <paramref name="kind"/>
    /// İYS sınıfını belirler — ticari kampanya <see cref="SmsKind.Commercial"/>
    /// ile gönderilmek ZORUNDA (İYS ret listesi kontrolü).
    /// </summary>
    Task SendAsync(string toPhone, string message, SmsKind kind, CancellationToken ct = default);
}
