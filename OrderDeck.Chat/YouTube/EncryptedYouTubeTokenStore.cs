using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Apis.Util.Store;

namespace OrderDeck.Chat.YouTube;

/// <summary>
/// Implements Google's <see cref="IDataStore"/> on top of DPAPI (current-user
/// scope) so the YouTube OAuth refresh token never lives on disk in plaintext.
///
/// Pattern mirrors <c>OrderDeck.Licensing.Storage.EncryptedStore</c>: each key
/// is stored as one file under a per-process directory, ProtectedData wraps
/// the JSON, tampered or cross-user blobs are silently deleted so the caller
/// treats the missing token as "user must re-connect". This keeps us aligned
/// with the YouTube API audit's Limited Use requirements (no plaintext
/// long-lived credentials at rest).
///
/// Filenames are hex-encoded SHA1 of the key — Google's keys can contain
/// characters that aren't safe on Windows file systems (".", colons, etc.),
/// and the original key isn't load-bearing for us, only the token blob is.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EncryptedYouTubeTokenStore : IDataStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null, // Google library names properties manually
    };

    private readonly string _folder;

    public EncryptedYouTubeTokenStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var path = PathForKey(key);
        var json = JsonSerializer.Serialize(value, JsonOpts);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var cipher = ProtectedData.Protect(plaintext, optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, cipher);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathForKey(key);
        if (!File.Exists(path))
            return Task.FromResult<T>(default!);

        try
        {
            var cipher = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(cipher, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plaintext);
            var value = JsonSerializer.Deserialize<T>(json, JsonOpts);
            return Task.FromResult(value!);
        }
        catch (CryptographicException)
        {
            // Tampered / restored from another machine — discard.
            TryDelete(path);
            return Task.FromResult<T>(default!);
        }
        catch (JsonException)
        {
            // Schema drift between Google library versions — discard.
            TryDelete(path);
            return Task.FromResult<T>(default!);
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        TryDelete(PathForKey(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_folder))
        {
            foreach (var f in Directory.EnumerateFiles(_folder))
                TryDelete(f);
        }
        return Task.CompletedTask;
    }

    private string PathForKey(string key)
    {
        // SHA1 is fine here — we're not authenticating, just sanitising the
        // filename. Length 40 hex chars stays well within MAX_PATH.
        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) hex.Append(b.ToString("x2"));
        return Path.Combine(_folder, hex.ToString());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
