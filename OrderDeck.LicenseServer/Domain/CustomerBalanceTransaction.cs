namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Müşteri bakiye ledger satırı (immutable, append-only). CustomerBalance.Balance
/// her zaman SUM(Amount) ile tutar. Audit hem yayıncı hem shopper'a açık.
/// </summary>
public sealed class CustomerBalanceTransaction
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid WpfCustomerId { get; set; }
    public WpfCustomerProjection WpfCustomer { get; set; } = null!;

    /// <summary>+ bakiye eklendi, − kullanıldı / geri alındı.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Transaction türü. Display tarafında karşılığı:
    ///   - "refund-full":     "Hatalı ürün iadesi" (yayıncı kaynaklı, tam tutar)
    ///   - "refund-net":      "Müşteri iadesi (kargo düşülmüş)"
    ///   - "purchase-deduction": "Ödeme isteği — bakiye kullanıldı"
    ///   - "manual-adjustment": "Yayıncı manuel ayar" (+ veya −)
    ///   - "reversal":        "Önceki transaction geri alındı" (yanlış kayıt iptal)
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>Refund'larda orijinal sipariş tutarı (audit). Diğerlerinde null.</summary>
    public decimal? OriginalAmount { get; set; }

    /// <summary>refund-net'te kargo olarak düşülen tutar (audit). Diğerlerinde null.</summary>
    public decimal? ShippingDeducted { get; set; }

    /// <summary>Free text — yayıncı notu, opsiyonel.</summary>
    public string? Reason { get; set; }

    /// <summary>Reversal ise hangi transaction'ı iptal ediyor.</summary>
    public Guid? ReversesTransactionId { get; set; }

    /// <summary>Bu transaction'ı oluşturan yayıncı (Customer) — audit.</summary>
    public Guid CreatedByCustomerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
