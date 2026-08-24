namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Builds WhatsApp deep links with phone normalization and message encoding.
/// Pure utility — no dependencies.
/// </summary>
public sealed class WhatsAppLinkBuilder
{
    /// <summary>Yayıncı numarası gerçekten bir sohbet açabilir mi?
    /// Boş/eksik numarada <c>Build</c> sessizce <c>https://wa.me/?text=...</c>
    /// üretiyor; o adres WhatsApp'ta hiçbir sohbet açmıyor, müşteri de kaydının
    /// alındığını göremiyor. Çağıran taraf link kurmadan önce bunu sormalı.
    /// Eşik 10 hane: ülke kodsuz TR mobil numaranın uzunluğu.</summary>
    public static bool HasUsablePhone(string? phone)
        => phone is not null && phone.Count(char.IsAsciiDigit) >= 10;

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

    /// <summary>Adresin okunabilir tek satır hâli: "Mahalle Cad. No:5, Kadıköy/İstanbul".
    /// İl/ilçe boşsa (eski kayıt) yalnız serbest metin döner.</summary>
    public static string FormatAddress(string address, string? city, string? district)
    {
        var street = address.Trim();
        var region = string.Join('/', new[] { district?.Trim(), city?.Trim() }
            .Where(s => !string.IsNullOrEmpty(s)));
        if (region.Length == 0) return street;
        return street.Length == 0 ? region : $"{street}, {region}";
    }

    /// <summary>
    /// Multi-platform overload. Draft lists each provided platform username,
    /// then Ad Soyad + Adres (+ optional Telefon). Email and TCKN are
    /// intentionally excluded from the WhatsApp draft (form-only fields).
    /// </summary>
    public string Build(
        string e164Phone,
        string? youTubeUsername, string? instagramUsername,
        string? facebookUsername, string? tikTokUsername,
        string fullName, string address, string? phoneFromCustomer,
        string? city = null, string? district = null)
    {
        var normalizedPhone = e164Phone
            .Replace("+", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty);

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(youTubeUsername)) lines.Add($"YouTube: {youTubeUsername.Trim()}");
        if (!string.IsNullOrWhiteSpace(instagramUsername)) lines.Add($"Instagram: {instagramUsername.Trim()}");
        if (!string.IsNullOrWhiteSpace(facebookUsername)) lines.Add($"Facebook: {facebookUsername.Trim()}");
        if (!string.IsNullOrWhiteSpace(tikTokUsername)) lines.Add($"TikTok: {tikTokUsername.Trim()}");
        lines.Add($"Ad Soyad: {fullName}");
        lines.Add($"Adres: {FormatAddress(address, city, district)}");
        if (!string.IsNullOrWhiteSpace(phoneFromCustomer)) lines.Add($"Telefon: {phoneFromCustomer}");

        var encodedMessage = Uri.EscapeDataString(string.Join("\n", lines));
        return $"https://wa.me/{normalizedPhone}?text={encodedMessage}";
    }
}
