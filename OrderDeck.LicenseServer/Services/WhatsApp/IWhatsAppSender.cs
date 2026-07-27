using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// WhatsApp Cloud API giden mesaj soyutlaması. <b>Tenant-aware:</b> gönderim
/// kimlikleri (Phone Number ID + Access Token) her çağrıda <see cref="WhatsAppSendContext"/>
/// ile verilir — çağıran, tenant'ın kimliklerini (DB veya config default) çözer.
/// Böylece sender tek bir tenant'a bağlı değildir.
/// </summary>
public interface IWhatsAppSender
{
    /// <summary>Serbest-metin (service) mesajı. Yalnız açık 24s pencere içinde
    /// geçerli — pencere kontrolü çağıranda yapılır.</summary>
    Task<WhatsAppSendResult> SendTextAsync(
        WhatsAppSendContext ctx, string toPhoneE164, string text, CancellationToken ct = default);

    /// <summary>Onaylı template mesajı (pencere-dışı business-initiated için).</summary>
    Task<WhatsAppSendResult> SendTemplateAsync(
        WhatsAppSendContext ctx, string toPhoneE164, WhatsAppTemplate template, CancellationToken ct = default);
}

/// <summary>Bir gönderimin hangi tenant/numara adına yapılacağı: Phone Number ID
/// + o tenant'ın Access Token'ı.</summary>
public sealed record WhatsAppSendContext(string PhoneNumberId, string AccessToken);

/// <summary>Onaylı template referansı + gövde parametreleri (sıralı).</summary>
public sealed record WhatsAppTemplate(string Name, string LanguageCode, IReadOnlyList<string> BodyParams);

/// <summary>Gönderim sonucu. Başarılıysa <see cref="MessageId"/> (wamid) dolu;
/// değilse <see cref="ErrorCode"/>/<see cref="ErrorMessage"/>.</summary>
public sealed record WhatsAppSendResult(bool Ok, string? MessageId, string? ErrorCode, string? ErrorMessage)
{
    public static WhatsAppSendResult Success(string messageId) => new(true, messageId, null, null);
    public static WhatsAppSendResult Failure(string? code, string? message) => new(false, null, code, message);
}
