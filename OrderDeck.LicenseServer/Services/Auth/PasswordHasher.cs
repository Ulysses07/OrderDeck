using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace LiveDeck.LicenseServer.Services.Auth;

/// <summary>
/// Argon2id password hasher. OWASP 2024 parameters: m=65536 KB, t=4 iterations, p=2 lanes.
/// Format: $argon2id$v=19$m=65536,t=4,p=2$&lt;salt-base64&gt;$&lt;hash-base64&gt;
/// </summary>
public sealed class PasswordHasher
{
    private const int MemoryKb = 65536;
    private const int Iterations = 4;
    private const int Parallelism = 2;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string hashString, string password)
    {
        try
        {
            var parts = hashString.Split('$');
            if (parts.Length != 6) return false;
            if (parts[1] != "argon2id") return false;
            var salt = Convert.FromBase64String(parts[4]);
            var expectedHash = Convert.FromBase64String(parts[5]);
            var actualHash = Compute(password, salt);
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = Parallelism,
            Iterations = Iterations,
            MemorySize = MemoryKb,
        };
        return argon2.GetBytes(HashSize);
    }
}
