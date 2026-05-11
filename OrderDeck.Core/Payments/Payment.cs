namespace OrderDeck.Core.Payments;

public enum PaymentStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>
/// Müşterinin yayıncıya banka transferi olarak yaptığı ödemenin lokal kaydı.
/// Manuel girilebilir (PR C — DekontEkleDialog) veya gelecekte customer mobile
/// app upload'undan gelebilir. Periyodik olarak <c>PaymentSyncService</c>
/// tarafından LicenseServer'a push'lanır.
///
/// SyncedAt null iken outbox kuyruğunda. Server confirm sonrası dolar.
/// Status değişimi (mobile onay/red) reverse sync ile geri gelir (sonraki faz).
/// </summary>
public sealed record Payment(
    string Id,
    string PayerName,
    decimal Amount,
    long PaidAt,
    string ReferansNo,
    string? PdfHash,
    PaymentStatus Status,
    long CreatedAt,
    long UpdatedAt,
    long? SyncedAt,
    long? ApprovedAt,
    long? RejectedAt,
    string? RejectReason);
