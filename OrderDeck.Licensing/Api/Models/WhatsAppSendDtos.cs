namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// WPF'in tek mesajlık WhatsApp gönderimi için kullandığı DTO'lar. Sunucudaki
/// LicensesWhatsAppSendController.SendRequest/SendResponse kayıtlarıyla bire bir
/// (JSON camelCase, LicenseApiClient.JsonOpts ile çözülür).
/// </summary>
public sealed record WhatsAppSendRequest(string ToPhone, string Text, string? Origin);

/// <summary>
/// <see cref="Ok"/> false iken <see cref="ErrorCode"/> nedeni taşır:
/// <c>window_closed</c> (24s pencere kapalı), <c>no_account</c> (lisansa bağlı
/// WhatsApp hesabı yok), <c>bad_phone</c>, ya da Meta'nın döndürdüğü hata kodu.
/// HTTP durumu bu durumlarda da 200'dür.
/// </summary>
public sealed record WhatsAppSendResponse(
    bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);
