namespace OrderDeck.LicenseServer.Domain;

public sealed class AdminUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Ardışık başarısız giriş sayısı; başarılı girişte sıfırlanır.
    /// Kilit süresi dolduğunda KORUNUR — tırmanan kilidi besleyen şey bu.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>Bu ana kadar parola doğrulanmaz. Argon2 çağrısı bu kontrolden
    /// SONRA geldiği için alan aynı zamanda kaynak tavanı görevi görür.</summary>
    public DateTimeOffset? LockedOutUntil { get; set; }
}
