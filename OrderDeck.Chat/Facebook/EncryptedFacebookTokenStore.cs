using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// DPAPI-encrypted persistence for <see cref="FacebookTokenBundle"/>. Mirrors
/// <c>EncryptedYouTubeTokenStore</c> but typed (single blob, no IDataStore key
/// indirection) because we own the Facebook OAuth flow end-to-end and don't
/// need to be polymorphic over Google's storage API.
///
/// Tampered / cross-machine blobs are silently dropped → caller treats it as
/// "not connected, re-auth needed", matching the YouTube failure mode.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EncryptedFacebookTokenStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;

    public EncryptedFacebookTokenStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "facebook-token.bin");
    }

    public Task SaveAsync(FacebookTokenBundle bundle, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(bundle, JsonOpts);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var cipher = ProtectedData.Protect(plaintext, optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, cipher);
        return Task.CompletedTask;
    }

    public Task<FacebookTokenBundle?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return Task.FromResult<FacebookTokenBundle?>(null);

        try
        {
            var cipher = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(cipher, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plaintext);
            var bundle = JsonSerializer.Deserialize<FacebookTokenBundle>(json, JsonOpts);
            return Task.FromResult(bundle);
        }
        catch (CryptographicException)
        {
            // Tampered / restored from another machine — discard.
            TryDelete();
            return Task.FromResult<FacebookTokenBundle?>(null);
        }
        catch (JsonException)
        {
            // Schema drift — discard.
            TryDelete();
            return Task.FromResult<FacebookTokenBundle?>(null);
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        TryDelete();
        return Task.CompletedTask;
    }

    private void TryDelete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* best effort */ }
    }
}
