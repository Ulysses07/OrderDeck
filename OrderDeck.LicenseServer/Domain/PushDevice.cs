namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Registered mobile device for OrderDeck Panel push notifications. A customer
/// may have multiple devices (yayıncı kendi telefonu + çalışan telefonu); fan-out
/// sends to every active row. Stale tokens are pruned by FCM/APNS feedback or
/// by the unregister endpoint (DELETE /api/panel/devices/{token}).
/// </summary>
public sealed class PushDevice
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Capacitor Device.getId() — stable per install, app reset değil.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>"ios" or "android" (lowercase).</summary>
    public string Platform { get; set; } = "";

    /// <summary>APNS device token (iOS) veya FCM token (Android). Plaintext —
    /// FCM/APNS'e ham olarak iletilmesi gerekir, hash'lenmez.</summary>
    public string PushToken { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
