namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper app cihazlarının FCM/APNs token kaydı. Mevcut PushDevice'tan ayrı
/// tablo (FK Shopper'a, yayıncıya değil). (ShopperId, DeviceId) upsert key.
/// </summary>
public sealed class ShopperPushDevice
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public string DeviceId { get; set; } = "";
    public string Platform { get; set; } = "";
    public string PushToken { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
