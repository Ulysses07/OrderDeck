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

public sealed record SmsCreateRequest(string MessageBody);

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
