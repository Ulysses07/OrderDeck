namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF tarafindan kullanilan customer balance DTO'lari.</summary>
public sealed record CustomerBalancePreview(
    Guid WpfCustomerId,
    decimal Balance,
    DateTimeOffset UpdatedAt);

/// <summary>Bakiye düşüm isteği. <paramref name="IdempotencyKey"/> çağrı başına
/// bir kez üretilir: dayanıklılık katmanı bu POST'u yeniden denediğinde gövde
/// (dolayısıyla anahtar) aynı kalır ve sunucu bakiyeyi ikinci kez düşürmez.
/// Anahtar aynı zamanda oluşan ledger satırının kimliğidir.
///
/// <para>R4-03: <paramref name="SaleScope"/> satışın <b>kalıcı</b> kimliği —
/// işin <c>ScopeKey</c>'i. Idempotency anahtarı yerel diskte yaşıyor ve yedek
/// geri yüklenince kayboluyor; kapsam sunucuda kaldığından düşüm sonradan
/// yine tanınabiliyor.</para></summary>
public sealed record CustomerBalanceApplyRequest(
    Guid WpfCustomerId,
    decimal Amount,
    decimal ProductTotal,
    Guid? IdempotencyKey = null,
    string? SaleScope = null);

public sealed record CustomerBalanceApplyResponse(
    Guid TransactionId,
    decimal AppliedAmount,
    decimal RemainingBalance);

/// <summary>R4-03: bir kapsamda sunucuda duran, geri alınmamış düşüm. Yerel
/// anahtar kaybolmuş olsa bile satış bununla tanınır.</summary>
public sealed record CustomerBalanceScope(
    Guid TransactionId,
    decimal AppliedAmount,
    decimal ProductTotal,
    DateTimeOffset CreatedAt);

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
