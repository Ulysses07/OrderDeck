namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Mevcut <see cref="RefreshToken"/> (yayıncı/Customer) pattern'inin shopper
/// karşılığı. Raw token client'a bir kez gösterilir, DB'de yalnız SHA-256 hash
/// saklanır. Rotation chain için <see cref="ReplacedByTokenHash"/>.
/// </summary>
public sealed class ShopperRefreshToken
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;

    /// <summary>SHA-256 of the raw token, lowercase hex (64 chars).</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? CreatedByIp { get; set; }
}
