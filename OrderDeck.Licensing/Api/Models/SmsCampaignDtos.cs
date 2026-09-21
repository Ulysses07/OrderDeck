namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF "Toplu SMS" ekranının kullandığı DTO'lar. Server tarafındaki
/// LicensesSmsCampaignsController response/request kayıtlarıyla bire bir
/// (JSON camelCase, LicenseApiClient.JsonOpts ile çözülür).</summary>
public sealed record SmsPreviewRequest(string MessageBody);

/// <summary>`Sufficient` = Netgsm kurulumu doğrulanmış mı (server alan adını
/// eski istemciler için koruyor); `CreditsRemaining` JSON'da hâlâ gelir, artık
/// parse edilmez (bilinmeyen alanlar yok sayılır). `TotalCredits` = alıcı ×
/// segment (Netgsm segment başına ücretlendirir; "kaça mal olur" göstergesi).</summary>
public sealed record SmsPreviewResponse(
    int RecipientCount,
    int SegmentsPerMessage,
    int TotalCredits,
    bool Sufficient);

/// <summary>ClientRequestId (F09): gönderim eylemi başına üretilen idempotency
/// anahtarı. Resilience handler'ın retry'ı veya kullanıcı tekrar denemesi aynı
/// anahtarı taşır; server ikinci kampanya açmak yerine ilkinin yanıtını döner.</summary>
public sealed record SmsCreateRequest(string MessageBody, Guid? ClientRequestId = null);

public sealed record SmsCreateResponse(
    Guid CampaignId,
    int RecipientCount,
    int TotalCredits);

public sealed record SmsCampaignStatusResponse(
    Guid CampaignId,
    string Status,
    int RecipientCount,
    int Sent,
    int Failed,
    int Skipped,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record SmsCampaignListItem(
    Guid CampaignId,
    string Status,
    string MessagePreview,
    int RecipientCount,
    int Sent,
    int Failed,
    int Skipped,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
