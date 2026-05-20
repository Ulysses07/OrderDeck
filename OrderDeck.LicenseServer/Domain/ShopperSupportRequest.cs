namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper'ın yayıncıya manuel destek talebi (Faz 0b-1: forgot-password için).
/// Faz 0c'de yayıncı paneli "Destek talepleri" bölümünde gösterilir; yayıncı
/// WhatsApp'tan manuel cevap verir. Kind = "forgot-password" şimdilik tek tür.
/// </summary>
public sealed class ShopperSupportRequest
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string Kind { get; set; } = "";   // "forgot-password"
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
