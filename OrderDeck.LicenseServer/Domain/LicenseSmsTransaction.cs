namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Lisans SMS kredi ledger satırı (immutable, append-only). <see cref="LicenseSmsBalance"/>
/// .CreditsRemaining her zaman SUM(Amount) ile tutar.
/// </summary>
public sealed class LicenseSmsTransaction
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>+ kredi eklendi (satış/iade), − kullanıldı (rezerve).</summary>
    public int Amount { get; set; }

    /// <summary>
    /// Transaction türü:
    ///   - "purchase":          admin kredi yükledi (satış)
    ///   - "send-reserve":      kampanya oluşturuldu, kredi rezerve edildi (−)
    ///   - "send-refund":       kampanyada başarısız/atlanan alıcılar iade (+)
    ///   - "manual-adjustment": admin manuel ayar (+ veya −)
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>Free text — admin notu / kampanya referansı, opsiyonel.</summary>
    public string? Reason { get; set; }

    /// <summary>Bu transaction'ı oluşturan yayıncı (Customer). Admin işlemlerinde null.</summary>
    public Guid? CreatedByCustomerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
