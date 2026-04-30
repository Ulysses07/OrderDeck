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
    {
        // Normalize phone: strip +, space, dash
        var normalizedPhone = e164Phone
            .Replace("+", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty);

        // Build message with 3 labeled lines
        var message = $"Kullanıcı adı: {username}\nAd Soyad: {fullName}\nAdres: {address}";

        // Encode message for URL
        var encodedMessage = Uri.EscapeDataString(message);

        // Return wa.me link
        return $"https://wa.me/{normalizedPhone}?text={encodedMessage}";
    }
}
