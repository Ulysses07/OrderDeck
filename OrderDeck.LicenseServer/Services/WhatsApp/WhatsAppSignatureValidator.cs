using System.Security.Cryptography;
using System.Text;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Meta webhook imza doğrulaması: <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> =
/// HMAC-SHA256(App Secret, <b>ham istek gövdesi</b>).
///
/// <para><b>Ham gövde şart:</b> JSON'u deserialize edip yeniden serialize etmek
/// byte'ları değiştirir ve imza tutmaz — bu yüzden controller gövdeyi string
/// olarak okuyup buraya verir.</para>
///
/// <para>Karşılaştırma <see cref="CryptographicOperations.FixedTimeEquals"/> ile
/// sabit zamanlıdır (timing attack ile imza tahmini engellenir).</para>
/// </summary>
public static class WhatsAppSignatureValidator
{
    private const string Prefix = "sha256=";

    /// <summary>İmza geçerliyse true. Secret veya header boşsa <b>false</b> —
    /// yapılandırılmamış sunucu imzasız isteği kabul etmemeli.</summary>
    public static bool IsValid(string? headerValue, string rawBody, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(appSecret)) return false;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        if (!headerValue.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var provided = headerValue.AsSpan(Prefix.Length).Trim();
        if (provided.Length != 64) return false;

        Span<byte> providedBytes = stackalloc byte[32];
        if (!TryParseHex(provided, providedBytes)) return false;

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(appSecret),
            Encoding.UTF8.GetBytes(rawBody ?? string.Empty),
            expected);

        return CryptographicOperations.FixedTimeEquals(expected, providedBytes);
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            var hi = FromHexDigit(hex[i * 2]);
            var lo = FromHexDigit(hex[(i * 2) + 1]);
            if (hi < 0 || lo < 0) return false;
            output[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int FromHexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
