namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF tarafindan kullanilan customer balance DTO'lari.</summary>
public sealed record CustomerBalancePreview(
    Guid WpfCustomerId,
    decimal Balance,
    DateTimeOffset UpdatedAt);

public sealed record CustomerBalanceApplyRequest(
    Guid WpfCustomerId,
    decimal Amount,
    decimal ProductTotal);

public sealed record CustomerBalanceApplyResponse(
    Guid TransactionId,
    decimal AppliedAmount,
    decimal RemainingBalance);

// Panel endpoint'leri ile uyumlu DTO'lar (WPF'in /api/panel/customers/{id}/balance
// kullanması için).
public sealed record CustomerBalanceDto(
    Guid WpfCustomerId,
    Guid LicenseId,
    decimal Balance,
    DateTimeOffset UpdatedAt);

public sealed record CustomerBalanceTransactionDto(
    Guid Id,
    decimal Amount,
    string Kind,
    decimal? OriginalAmount,
    decimal? ShippingDeducted,
    string? Reason,
    Guid? ReversesTransactionId,
    DateTimeOffset CreatedAt);

public sealed record CustomerBalanceDetailsResponse(
    CustomerBalanceDto Balance,
    CustomerBalanceTransactionDto[] Transactions);

public sealed record RefundFullRequest(decimal Amount, string? Reason);

public sealed record RefundNetRequest(
    decimal OriginalAmount,
    decimal ShippingDeducted,
    string? Reason);
