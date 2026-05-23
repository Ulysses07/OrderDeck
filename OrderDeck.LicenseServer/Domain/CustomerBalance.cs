namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncı bazlı müşteri bakiyesi. (LicenseId, WpfCustomerId) composite key.
/// Balance her zaman SUM(CustomerBalanceTransaction.Amount) ile tutar (ledger
/// invariant'ı). Bu satır cache amaçlı — query'leri hızlandırmak için.
///
/// Iade/refund senaryoları:
///   - Hatalı ürün (yayıncı kaynaklı): tam tutar bakiye olarak eklenir
///   - Müşteri iadesi: tutar - kargo bakiye olarak eklenir
///   - Yayıncı WhatsApp "Ödeme iste" anında: balance düşülür (purchase-deduction)
/// </summary>
public sealed class CustomerBalance
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>WpfCustomerProjection.Id — yayıncının WPF lokal müşteri kaydı.</summary>
    public Guid WpfCustomerId { get; set; }
    public WpfCustomerProjection WpfCustomer { get; set; } = null!;

    /// <summary>Anlık bakiye. Negatif olamaz (server validation).</summary>
    public decimal Balance { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
