namespace OrderDeck.LicenseServer.Domain.Bank;

public enum ObifinConnectionStatus { Unverified = 0, Verified = 1, Failed = 2, Disabled = 3 }

/// <summary>Lisans başına Obifin web servis kimliği + çekim imleci (spec §3).
/// Şifre ve API key DataProtection ile şifreli; görünüme yalnız "kayıtlı" bayrağı çıkar.</summary>
public sealed class ObifinConnection
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string BaseUrl { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    public ObifinConnectionStatus Status { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    /// <summary>Görülen en büyük Obifin hareket Id'si; yalnız tam başarılı turda ilerler.</summary>
    public long? LastObifinTransactionId { get; set; }
    public DateTimeOffset? BackfillCompletedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
