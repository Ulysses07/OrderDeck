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
    DateTimeOffset SubmittedAt);
