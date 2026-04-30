using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace OrderDeck.LicenseServer.Services.Email;

/// <summary>
/// Stateless HMAC-SHA256 signed unsubscribe tokens. Format:
/// <c>base64url(customerIdBytes).base64url(unixTimeBigEndianBytes).base64url(hmac(payload, key))</c>
/// Key reuse: <c>Jwt:SecretKey</c>. Tokens are not time-bound (issuedAt is audit only).
/// </summary>
public sealed class UnsubscribeTokenSigner
{
    private readonly byte[] _key;

    public UnsubscribeTokenSigner(IConfiguration config)
    {
        var secret = config["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey config value is required for UnsubscribeTokenSigner.");
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string Sign(Guid customerId, DateTimeOffset issuedAt)
    {
        var idBytes = customerId.ToByteArray();
        var timeBytes = BitConverter.GetBytes(issuedAt.ToUnixTimeSeconds());
        if (BitConverter.IsLittleEndian) Array.Reverse(timeBytes);

        var payload = new byte[idBytes.Length + timeBytes.Length];
        Buffer.BlockCopy(idBytes, 0, payload, 0, idBytes.Length);
        Buffer.BlockCopy(timeBytes, 0, payload, idBytes.Length, timeBytes.Length);

        var hmac = HMACSHA256.HashData(_key, payload);

        return $"{Base64UrlEncode(idBytes)}.{Base64UrlEncode(timeBytes)}.{Base64UrlEncode(hmac)}";
    }

    public bool TryVerify(string token, out Guid customerId, out DateTimeOffset issuedAt)
    {
        customerId = Guid.Empty;
        issuedAt = DateTimeOffset.MinValue;

        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            var idBytes = Base64UrlDecode(parts[0]);
            var timeBytes = Base64UrlDecode(parts[1]);
            var providedHmac = Base64UrlDecode(parts[2]);
            if (idBytes.Length != 16) return false;     // Guid = 16 bytes
            if (timeBytes.Length != 8) return false;    // long = 8 bytes

            var payload = new byte[idBytes.Length + timeBytes.Length];
            Buffer.BlockCopy(idBytes, 0, payload, 0, idBytes.Length);
            Buffer.BlockCopy(timeBytes, 0, payload, idBytes.Length, timeBytes.Length);

            var expectedHmac = HMACSHA256.HashData(_key, payload);
            if (!CryptographicOperations.FixedTimeEquals(expectedHmac, providedHmac))
                return false;

            customerId = new Guid(idBytes);

            var beTimeBytes = (byte[])timeBytes.Clone();
            if (BitConverter.IsLittleEndian) Array.Reverse(beTimeBytes);
            var unix = BitConverter.ToInt64(beTimeBytes, 0);
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
