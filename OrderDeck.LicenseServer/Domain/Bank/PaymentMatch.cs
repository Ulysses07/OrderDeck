namespace OrderDeck.LicenseServer.Domain.Bank;

public enum PaymentMatchLayer { None = 0, UsernameInDescription = 1, IbanMemory = 2, NameAmount = 3 }

public enum PaymentMatchStatus
{
    Proposed = 0,
    NoProposal = 1,
    ConfirmedByHuman = 2,
    Contradicted = 3,
    ManualOnly = 4
}

/// <summary>Gölge mod önerisi (spec §3/§6). Hiçbir zaman Payment yaratmaz/değiştirmez.</summary>
public sealed class PaymentMatch
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid BankTransactionId { get; set; }
    public BankTransaction BankTransaction { get; set; } = null!;
    public Guid? ProposedWpfCustomerId { get; set; }
    public PaymentMatchLayer Layer { get; set; }
    public decimal Confidence { get; set; }
    public string? Evidence { get; set; }
    public PaymentMatchStatus Status { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? ActualWpfCustomerId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
