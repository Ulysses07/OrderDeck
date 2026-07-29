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
/// <para><see cref="Template"/> verilirse sunucu kararı kendisi verir: 24 saatlik
/// pencere AÇIKSA <see cref="Text"/> (ücretsiz servis mesajı), KAPALIYSA şablon
/// gider. Kararın sunucuda olması şart — pencere durumu orada tutuluyor ve iki
/// yol tek <see cref="IdempotencyKey"/> altında kalıyor; istemci önce metni
/// deneyip sonra şablona düşseydi bir yeniden-deneme ikisini birden
/// gönderebilirdi. Şablon yoksa eski davranış: <c>window_closed</c> döner.</para>
public sealed record WhatsAppSendRequest(
    string ToPhone, string Text, string? Origin, Guid? IdempotencyKey = null,
    WhatsAppTemplateRef? Template = null);

/// <summary>Meta'da onaylı şablonun kimliği + gövde parametreleri.
/// <see cref="BodyParams"/> sırası şablon gövdesindeki <c>{{1}}</c>, <c>{{2}}</c>…
/// ile birebir eşleşmek zorunda. Değerler boş olamaz ve satır sonu/sekme
/// taşıyamaz — Meta reddediyor.</summary>
public sealed record WhatsAppTemplateRef(
    string Name, string LanguageCode, IReadOnlyList<string> BodyParams);

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
