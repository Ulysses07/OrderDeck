namespace OrderDeck.Core.Sales;

/// <summary>
/// Preset cancellation reasons surfaced in the customer detail dialog. Stored
/// verbatim in Label.CancelReason so reports can group / count by reason
/// without parsing localised text. The free-form variant prefixes "custom:"
/// followed by the operator's text — keeps the column self-contained without
/// adding a separate "details" field.
/// </summary>
public static class CancelReasonCodes
{
    public const string Customer       = "customer";        // Müşteri vazgeçti
    public const string WrongProduct   = "wrong-product";   // Yanlış ürün / fiyat
    public const string Duplicate      = "duplicate";       // Mükerrer kayıt
    public const string OutOfStock     = "out-of-stock";    // Stok kalmadı
    public const string CustomPrefix   = "custom:";         // Özel sebep (free-form)

    /// <summary>Returns the operator-facing Turkish label for a stored reason
    /// code, or the free-form text after "custom:" when applicable.</summary>
    public static string LocalisedDisplay(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "";
        if (reason.StartsWith(CustomPrefix)) return reason[CustomPrefix.Length..];
        return reason switch
        {
            Customer       => "Müşteri vazgeçti",
            WrongProduct   => "Yanlış ürün / fiyat",
            Duplicate      => "Mükerrer kayıt",
            OutOfStock     => "Stok kalmadı",
            _              => reason
        };
    }
}
