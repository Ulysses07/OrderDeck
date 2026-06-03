namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// WPF tarafının /api/panel/support-requests endpoint'leri için DTO'ları.
/// Server: PanelSupportRequestsController.SupportRequestDto ile aynı alanlar.
/// </summary>
public sealed record SupportRequestDto(
    Guid Id,
    Guid LicenseId,
    Guid ShopperId,
    string ShopperName,
    string ShopperPhone,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record IssueTempPasswordResponse(string TempPassword);
