namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Self-service parola sıfırlama için SMS ile gönderilen tek kullanımlık kod.
/// Kod plaintext saklanmaz — <c>PasswordHasher</c> (Argon2id) ile hash'lenir.
/// Verify'da <see cref="AttemptCount"/> artar, 5'i aşınca satır geçersiz.
/// Rate-limit ve global günlük tavan sorguları bu tablo üzerinden yapılır.
/// </summary>
public sealed class ShopperPasswordResetCode
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;

    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? RequestIp { get; set; }
}
