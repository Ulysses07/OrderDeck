namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ödeme satırının doğduğu yer (<see cref="Payment"/>; banka havalesi spec'i 2026-10-10 §2.3). String saklanır, ad
/// değişmez.
/// </summary>
public enum PaymentSource
{
    /// <summary>Dekont. Sütunun DB varsayılanı da budur: bu sütunu bilmeyen imajın INSERT'i ona düşer.</summary>
    Receipt = 0,

    /// <summary>Onaylanan banka havalesi.</summary>
    Bank = 1,
}

/// <summary>
/// Ödemeyi kimin hangi yoldan onayladığı (banka havalesi spec'i 2026-10-10 §2.3). Boş: bilinmiyor ya da onaylanmadı.
/// String saklanır, ad değişmez.
/// </summary>
public enum PaymentApprovalSource
{
    /// <summary>İnsan dekontu onayladı.</summary>
    ReceiptHuman = 0,

    /// <summary>İnsan havaleye "Evet" dedi; "Evet"le onaylanan dekont da budur.</summary>
    BankHuman = 1,

    /// <summary>Otomatik onay kuralı (§5).</summary>
    BankAuto = 2,
}
