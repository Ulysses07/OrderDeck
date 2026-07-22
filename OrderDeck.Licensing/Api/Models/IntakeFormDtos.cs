namespace OrderDeck.Licensing.Api.Models;

public sealed record IntakeFormConfigDto(
    string Slug,
    string WhatsAppPhone,
    string? CustomTitle,
    bool IsActive,
    string FormUrl);

public sealed record IntakeFormUpsertRequest(
    string Slug,
    string WhatsAppPhone,
    string? CustomTitle,
    bool? IsActive);

public sealed record IntakeFormSubmissionDto(
    Guid Id,
    string Username,
    string FullName,
    string Address,
    string? Phone,
    DateTimeOffset SubmittedAt,
    string? YouTubeUsername = null,
    string? InstagramUsername = null,
    string? FacebookUsername = null,
    string? TikTokUsername = null,
    string? Email = null,
    string? Tckn = null,
    bool WhatsAppConsent = false,
    bool SmsConsent = false,
    string? YouTubeChannelId = null);
