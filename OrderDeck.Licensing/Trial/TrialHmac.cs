using System.Security.Cryptography;
using System.Text;

namespace LiveDeck.Licensing.Trial;

/// <summary>
/// HMAC-SHA256 wrapper for ProgramData tamper detection. The embedded key is
/// reverse-engineerable — this is obfuscation, not real security. The goal is
/// to deter casual file editing, not stop a determined attacker.
/// </summary>
internal static class TrialHmac
{
    // 32 random bytes generated at implementation time. Do not change without
    // a schema migration: existing ProgramData records would be rejected.
    private static readonly byte[] Key =
    {
        0x4C, 0x44, 0x54, 0x52, 0x49, 0x41, 0x4C, 0x21,
        0x9F, 0x3B, 0x6E, 0x82, 0xC5, 0x14, 0xAA, 0x77,
        0xD2, 0x68, 0x05, 0x91, 0x3C, 0xBE, 0x4F, 0x76,
        0x1A, 0x8D, 0xE0, 0x52, 0x6B, 0xF4, 0x97, 0x23
    };

    /// <summary>Canonical input format: "{StartedAtIso}|{ExpiresAtIso}|{HardwareFingerprint}|{Version}".</summary>
    public static string Compute(TrialRecord record)
    {
        var canonical = $"{record.StartedAt:O}|{record.ExpiresAt:O}|{record.HardwareFingerprint}|{record.Version}";
        using var hmac = new HMACSHA256(Key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>True when the supplied hex MAC matches the freshly computed one.</summary>
    public static bool Verify(TrialRecord record, string mac) =>
        string.Equals(Compute(record), mac, StringComparison.OrdinalIgnoreCase);
}
