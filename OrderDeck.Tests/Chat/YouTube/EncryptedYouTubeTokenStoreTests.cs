using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.Chat.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// DPAPI-backed store tests. Each test gets its own temp directory so we can
/// run them in parallel without colliding on filenames. The store hashes keys
/// to filenames internally so we don't need to worry about path-safe keys here.
/// </summary>
public class EncryptedYouTubeTokenStoreTests : IDisposable
{
    private readonly string _folder;
    private readonly EncryptedYouTubeTokenStore _store;

    public EncryptedYouTubeTokenStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(),
            "orderdeck-yt-token-test-" + Guid.NewGuid().ToString("N"));
        _store = new EncryptedYouTubeTokenStore(_folder);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed record StoredToken(string AccessToken, string RefreshToken, long ExpiresAtUnix);

    [Fact]
    public async Task Store_then_Get_round_trips_complex_object()
    {
        var key = "user@example.com-Google.Apis.Auth.OAuth2.Responses.TokenResponse";
        var token = new StoredToken("at_abc", "rt_xyz", 1_700_000_000);

        await _store.StoreAsync(key, token);
        var roundTripped = await _store.GetAsync<StoredToken>(key);

        roundTripped.Should().NotBeNull();
        roundTripped!.AccessToken.Should().Be("at_abc");
        roundTripped.RefreshToken.Should().Be("rt_xyz");
        roundTripped.ExpiresAtUnix.Should().Be(1_700_000_000);
    }

    [Fact]
    public async Task Get_returns_default_for_missing_key()
    {
        var roundTripped = await _store.GetAsync<StoredToken>("never-stored");
        roundTripped.Should().BeNull();
    }

    [Fact]
    public async Task Delete_removes_a_single_key()
    {
        var token = new StoredToken("a", "b", 0);
        await _store.StoreAsync("key1", token);
        await _store.StoreAsync("key2", token);

        await _store.DeleteAsync<StoredToken>("key1");

        (await _store.GetAsync<StoredToken>("key1")).Should().BeNull();
        (await _store.GetAsync<StoredToken>("key2")).Should().NotBeNull();
    }

    [Fact]
    public async Task Clear_removes_every_key()
    {
        var token = new StoredToken("a", "b", 0);
        await _store.StoreAsync("k1", token);
        await _store.StoreAsync("k2", token);
        await _store.StoreAsync("k3", token);

        await _store.ClearAsync();

        (await _store.GetAsync<StoredToken>("k1")).Should().BeNull();
        (await _store.GetAsync<StoredToken>("k2")).Should().BeNull();
        (await _store.GetAsync<StoredToken>("k3")).Should().BeNull();
    }

    [Fact]
    public async Task Tampered_blob_is_silently_dropped_and_treated_as_missing()
    {
        // Simulate corruption / tamper: write garbage to a file the store
        // would create. The hashed filename for this key is deterministic.
        var key = "tamper-test";
        await _store.StoreAsync(key, new StoredToken("a", "b", 0));

        // Stomp every file in the folder with random bytes — DPAPI will fail
        // to decrypt and the store should treat the read as a fresh state.
        foreach (var f in Directory.EnumerateFiles(_folder))
            File.WriteAllBytes(f, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var result = await _store.GetAsync<StoredToken>(key);
        result.Should().BeNull("tampered blobs should look the same as 'never stored'");
    }
}
