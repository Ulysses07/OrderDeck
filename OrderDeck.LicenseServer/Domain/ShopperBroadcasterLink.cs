namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper ↔ yayıncı (License) N:N pivot. Per-pair Platform + Username, ve
/// (LicenseId, Platform, Username) match sonucu doldurulan WpfCustomerId
/// (yayıncının WPF'teki lokal Customer kaydı GUID'i — null kalabilir,
/// eşleşme retroactive gelir).
/// </summary>
public sealed class ShopperBroadcasterLink
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public Guid? WpfCustomerId { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}
