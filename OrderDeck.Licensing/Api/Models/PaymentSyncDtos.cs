namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF → LicenseServer push: tek payment item.</summary>
public sealed record SyncPaymentItem(
    Guid Id,
    string PayerName,
    decimal Amount,
    DateTimeOffset PaidAt,
    string ReferansNo,
    string? PdfHash,
    string? ShipmentDirective = null);   // Kargo PR E. "normal" | "hold" | "recipientpays" | null=normal

public sealed record SyncPaymentsRequest(IReadOnlyList<SyncPaymentItem> Payments);

/// <summary>Server-authoritative status echo (sync response + since pull aynı şekil).</summary>
public sealed record SyncedPaymentDto(
    Guid Id,
    string Status,                  // "pending" | "approved" | "rejected"
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? RejectedAt,
    string? RejectReason,
    DateTimeOffset UpdatedAt,
    string ShipmentDirective = "normal");   // Kargo PR E. Default "normal" → eski LicenseServer'lar geriye uyumlu.
