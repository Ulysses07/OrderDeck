namespace OrderDeck.LicenseServer.Domain.Bank;

public enum BankConnectionStatus { Active = 0, Failed = 1, Removed = 2 }

/// <summary>Obifin'de açılmış banka API kaydı. Banka web servis kimlikleri SAKLANMAZ —
/// admin girer, sunucu Obifin'e iletir, unutur (spec §3).</summary>
public sealed class BankConnection
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid ObifinConnectionId { get; set; }
    public ObifinConnection ObifinConnection { get; set; } = null!;
    public string BankaKodu { get; set; } = "";
    public long BankaApiId { get; set; }
    public string Label { get; set; } = "";
    public BankConnectionStatus Status { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
