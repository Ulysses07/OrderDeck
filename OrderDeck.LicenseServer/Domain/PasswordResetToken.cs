namespace LiveDeck.LicenseServer.Domain;

/// <summary>
/// Single-use password reset token. TTL enforced in PasswordResetService (1h default).
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
