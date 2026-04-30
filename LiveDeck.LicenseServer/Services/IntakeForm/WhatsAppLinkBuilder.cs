namespace LiveDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Builds WhatsApp deep links with phone normalization and message encoding.
/// Pure utility — no dependencies.
/// </summary>
public sealed class WhatsAppLinkBuilder
{
    /// <summary>
    /// Builds a WhatsApp deep link URL with encoded message.
    /// Phone format: strips +, space, dash.
    /// Message: 3 lines (Kullanıcı adı / Ad Soyad / Adres) joined with newlines.
    /// </summary>
    public string Build(string e164Phone, string username, string fullName, string address)
        => Build(e164Phone, username, fullName, address, null);

    /// <summary>
    /// Phase 4g overload — appends customer's WhatsApp/phone (E.164) as a 4th line ("Telefon: ...")
    /// to the broadcaster's message when provided.
    /// </summary>
    public string Build(string e164Phone, string username, string fullName, string address, string? phoneFromCustomer)
    {
        // Normalize phone: strip +, space, dash
        var normalizedPhone = e164Phone
            .Replace("+", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty);

        // Build message with 3 labeled lines (+ optional Telefon line)
        var message = $"Kullanıcı adı: {username}\nAd Soyad: {fullName}\nAdres: {address}";
        if (!string.IsNullOrWhiteSpace(phoneFromCustomer))
        {
            message += $"\nTelefon: {phoneFromCustomer}";
        }

        // Encode message for URL
        var encodedMessage = Uri.EscapeDataString(message);

        // Return wa.me link
        return $"https://wa.me/{normalizedPhone}?text={encodedMessage}";
    }
}
