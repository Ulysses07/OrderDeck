namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>Gap'in nedeni. NoCandidate/AmbiguousCandidates onay anındaki aday seçiminden gelir. UnlinkedByAdmin: onayda
/// harekete bağlanan dekontun bağını admin kaldırdı; dekont ölçümden düşmesin diye açık gap'e döner. Gap çözümüyle
/// bağlanmış dekontun kaldırılmasında gap'in kendisi yeniden açılır, onay anındaki nedeni korunur. String saklanır: yeni
/// değer göç istemez.</summary>
public enum PaymentMatchGapReason { NoCandidate = 0, AmbiguousCandidates = 1, UnlinkedByAdmin = 2 }

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
