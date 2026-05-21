namespace OrderDeck.Licensing.Api.Models;

/// <summary>Server response for GET and PUT /api/panel/shopper-code (Faz 0c-1).</summary>
public sealed record ShopperCodeResponse(
    string? Code,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CanChangeAt,
    Guid LicenseId);

public sealed record SetShopperCodeRequest(string Code);
