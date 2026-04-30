using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveDeck.Licensing.Storage;

/// <summary>
/// JSON + DPAPI (current-user scope). Tampered or cross-user files are deleted
/// on load and surface as <c>null</c> — caller treats this as fresh state.
/// </summary>
public sealed class EncryptedStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, JsonOpts);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var cipher = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, cipher);
    }

    public T? TryLoad<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            var cipher = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (CryptographicException)
        {
            // Tampered or written by a different DPAPI principal — start fresh.
            TryDeleteFile(path);
            return null;
        }
        catch (JsonException)
        {
            // Decrypts but doesn't deserialize — schema drift, treat as fresh.
            TryDeleteFile(path);
            return null;
        }
    }

    public void Delete(string path)
    {
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
