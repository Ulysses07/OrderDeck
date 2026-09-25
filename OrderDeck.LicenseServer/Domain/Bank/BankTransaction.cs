namespace OrderDeck.LicenseServer.Domain.Bank;

public enum BankTransactionDirection { Incoming = 0, Outgoing = 1 }

/// <summary>Obifin'den çekilen tek hareket. (LicenseId, ObifinId) tekil → idempotent yazım.
/// Karşı IBAN/VKN yalnız hash + maske (spec §7); ham JSON ve açıklama süreli (§5).</summary>
public sealed class BankTransaction
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public long ObifinId { get; set; }
    public Guid? BankAccountId { get; set; }
    public long ObifinAccountId { get; set; }
    public string BankaKodu { get; set; } = "";
    public BankTransactionDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TL";
    public DateTimeOffset OccurredAt { get; set; }
    public string? Description { get; set; }
    public string? TransactionCode { get; set; }
    public string? CommonType { get; set; }
    public string? BankReference { get; set; }
    public string? CounterpartyIbanHash { get; set; }
    public string? CounterpartyIbanMasked { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyTaxIdHash { get; set; }
    public string? RawJson { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset? DescriptionPurgedAt { get; set; }
}
