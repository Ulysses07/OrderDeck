namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Persisted record of a customer refresh token. Only the SHA-256 hash of the raw
/// token is stored — the raw value is shown to the client exactly once at issuance.
/// On every successful rotation the old row is marked Revoked and ReplacedByTokenHash
/// points at the new row's hash, forming a forensic chain.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>SHA-256 of the raw token, lowercase hex (64 chars).</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Hash of the token that replaced this one (rotation chain pointer).</summary>
    public string? ReplacedByTokenHash { get; set; }

    /// <summary>IP address recorded at issuance — best-effort audit trail.</summary>
    public string? CreatedByIp { get; set; }
}
