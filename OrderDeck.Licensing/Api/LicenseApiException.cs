namespace OrderDeck.Licensing.Api;

public abstract class LicenseApiException : Exception
{
    public string Code { get; }

    protected LicenseApiException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public sealed class InvalidCredentialsException : LicenseApiException
{
    public InvalidCredentialsException(string message = "E-posta veya şifre yanlış")
        : base("invalid-credentials", message) { }
}

public sealed class EmailNotConfirmedException : LicenseApiException
{
    public EmailNotConfirmedException(string message = "E-posta adresinizi doğrulayın")
        : base("email-not-confirmed", message) { }
}

public sealed class LicenseRevokedException : LicenseApiException
{
    public LicenseRevokedException(string message = "Lisans iptal edilmiş")
        : base("license-revoked", message) { }
}

public sealed class LicenseExpiredException : LicenseApiException
{
    public LicenseExpiredException(string message = "Lisans süresi dolmuş")
        : base("license-expired", message) { }
}

public sealed class SlotFullException : LicenseApiException
{
    public SlotFullException(string message = "Tüm cihaz slotları dolu")
        : base("slot-full", message) { }
}

public sealed class ValidationException : LicenseApiException
{
    public ValidationException(string code, string message) : base(code, message) { }
}

/// <summary>
/// Spec 2026-10-13 §6.6: mod On'da <c>customer-balance/apply</c> kapsamda geri alınmamış bir düşüm bulunca 409
/// <c>scope-has-deduction {transactionId, appliedAmount, productTotal}</c> döner; hiçbir şey yazılmamıştır. İstemci düşümü
/// benimser (miras işte benimsemez). Ek üyeleri eksik aynı ret bugünkü gibi <see cref="ValidationException"/> olur.
/// </summary>
public sealed class ScopeHasDeductionException : LicenseApiException
{
    public const string ProblemCode = "scope-has-deduction";

    /// <summary>Kapsamdaki düşümün işlem kimliği (ledger PK).</summary>
    public Guid TransactionId { get; }

    public decimal AppliedAmount { get; }

    /// <summary>Düşümün yazıldığı satış toplamı (<c>OriginalAmount</c>).</summary>
    public decimal ProductTotal { get; }

    public ScopeHasDeductionException(Guid transactionId, decimal appliedAmount, decimal productTotal, string? message = null)
        : base(ProblemCode, message ?? "Bu satışta geri alınmamış bir bakiye düşümü var.")
    {
        TransactionId = transactionId;
        AppliedAmount = appliedAmount;
        ProductTotal = productTotal;
    }
}

public sealed class LicenseApiNetworkException : LicenseApiException
{
    public LicenseApiNetworkException(string message, Exception? inner = null)
        : base("network", message)
    {
        if (inner is not null) Data["inner"] = inner;
    }
}

public sealed class LicenseApiUnknownException : LicenseApiException
{
    public int StatusCode { get; }
    public LicenseApiUnknownException(int statusCode, string message)
        : base("unknown", message)
    {
        StatusCode = statusCode;
    }
}
