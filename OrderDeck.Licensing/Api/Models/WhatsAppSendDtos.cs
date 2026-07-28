namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// WPF'in tek mesajlık WhatsApp gönderimi için kullandığı DTO'lar. Sunucudaki
/// LicensesWhatsAppSendController.SendRequest/SendResponse kayıtlarıyla bire bir
/// (JSON camelCase, LicenseApiClient.JsonOpts ile çözülür).
///
/// <para><see cref="IdempotencyKey"/> verilirse sunucu aynı anahtarla gelen
/// ikinci isteği yeni gönderim saymaz, ilk sonucu tekrar döner. HttpClient
/// dayanıklılık katmanı 5xx/ağ hatasında POST'u da yeniden dener; gönderim
/// faturalı olduğu için tek tık iki mesaja dönüşmemeli. Çağrı başına yeni bir
/// Guid üretilmeli — yeniden denemeler aynı gövdeyi taşıdığı için anahtar da
/// aynı kalır.</para>
/// </summary>
public sealed record WhatsAppSendRequest(
    string ToPhone, string Text, string? Origin, Guid? IdempotencyKey = null);

/// <summary>
/// <see cref="Ok"/> false iken <see cref="ErrorCode"/> nedeni taşır:
/// <c>window_closed</c> (24s pencere kapalı), <c>no_account</c> (lisansa bağlı
/// WhatsApp hesabı yok), <c>bad_phone</c>, ya da Meta'nın döndürdüğü hata kodu.
/// HTTP durumu bu durumlarda da 200'dür.
/// </summary>
public sealed record WhatsAppSendResponse(
    bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

/// <summary>Sunucunun gövdede taşıdığı gönderilemedi sebepleri.</summary>
public static class WhatsAppSendErrorCodes
{
    /// <summary>Aynı idempotency anahtarıyla bir gönderim hâlâ uçuşta — tekrar deneme.</summary>
    public const string InProgress = "in_progress";
}
