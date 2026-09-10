namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF "Toplu SMS" ekranının kullandığı DTO'lar. Server tarafındaki
/// LicensesSmsBalanceController / LicensesSmsCampaignsController response/request
/// kayıtlarıyla bire bir (JSON camelCase, LicenseApiClient.JsonOpts ile çözülür).</summary>
public sealed record SmsBalanceResponse(
    int CreditsRemaining,
    DateTimeOffset UpdatedAt);

public sealed record SmsPreviewRequest(string MessageBody);

public sealed record SmsPreviewResponse(
    int RecipientCount,
    int SegmentsPerMessage,
    int TotalCredits,
    int CreditsRemaining,
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
    int CreditsRefunded,
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
    int CreditsRefunded,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
