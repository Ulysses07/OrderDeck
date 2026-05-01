namespace OrderDeck.LicenseServer.Services.Email;

/// <summary>
/// Masks license keys for emails / logs / external display so a leaked transport
/// (mail server compromise, log dump) doesn't hand out activation-ready keys.
/// Keeps the prefix and last 4 chars so the customer still recognises which
/// license the message refers to. Unsuitable for the "key issued" email itself —
/// that one delivery has to send the full key.
/// </summary>
public static class LicenseKeyMasker
{
    /// <summary>Keep `LDK-` (or whatever prefix lives before the first dash) and
    /// the last 4 chars; replace the middle with bullet runs.
    /// LDK-A1B2C3D4E5F6789012345678ABCDEF12 → LDK-••••...••••EF12</summary>
    public static string Mask(string? licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey)) return string.Empty;

        // If the key is unusually short, just return last-4 with dots prefix.
        if (licenseKey.Length <= 8) return new string('•', Math.Max(0, licenseKey.Length - 4))
            + licenseKey[^Math.Min(4, licenseKey.Length)..];

        var dashIdx = licenseKey.IndexOf('-');
        var prefix = dashIdx > 0 && dashIdx < 8 ? licenseKey[..(dashIdx + 1)] : string.Empty;
        var suffix = licenseKey[^4..];
        // Fixed-length 24-bullet middle keeps every masked rendering visually
        // identical even if key length grows in the future.
        return $"{prefix}{new string('•', 24)}{suffix}";
    }
}
