namespace OrderDeck.LicenseServer.Domain.Bank;

public enum PaymentMatchGapReason { NoCandidate = 0, AmbiguousCandidates = 1 }

/// <summary>İnsan kararı var, banka hareketi bulunamadı (gecikme / başka hesap) — ölçüm (spec §3).</summary>
public sealed class PaymentMatchGap
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public Guid PaymentId { get; set; }
    public PaymentMatchGapReason Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ResolvedBankTransactionId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
