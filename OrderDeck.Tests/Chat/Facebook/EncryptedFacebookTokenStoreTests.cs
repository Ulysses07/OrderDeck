using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.Chat.Facebook;
using Xunit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// DPAPI-backed token store tests. Each test gets its own temp folder so
/// parallel runs can't collide on <c>facebook-token.bin</c>.
/// </summary>
public class EncryptedFacebookTokenStoreTests : IDisposable
{
    private readonly string _folder;
    private readonly EncryptedFacebookTokenStore _store;

    public EncryptedFacebookTokenStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(),
            "orderdeck-fb-token-test-" + Guid.NewGuid().ToString("N"));
        _store = new EncryptedFacebookTokenStore(_folder);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Save_then_Load_roundtrips_all_fields()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(60);
        var bundle = new FacebookTokenBundle(
            UserAccessToken: "EAA_user_abc",
            PageId: "111222333",
            PageAccessToken: "EAA_page_xyz",
            PageName: "Mezat Dünyası",
            ExpiresAt: expires);

        await _store.SaveAsync(bundle);
        var loaded = await _store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded!.UserAccessToken.Should().Be(bundle.UserAccessToken);
        loaded.PageId.Should().Be(bundle.PageId);
        loaded.PageAccessToken.Should().Be(bundle.PageAccessToken);
        loaded.PageName.Should().Be(bundle.PageName);
        // JSON roundtrip preserves DateTimeOffset to the same instant, but
        // representation can differ — compare as instants.
        loaded.ExpiresAt.Should().NotBeNull();
        loaded.ExpiresAt!.Value.ToUnixTimeSeconds().Should().Be(expires.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Load_when_no_blob_returns_null()
    {
        var loaded = await _store.LoadAsync();
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Clear_removes_the_blob()
    {
        await _store.SaveAsync(new FacebookTokenBundle("u", "p", "pa", "n", null));
        (await _store.LoadAsync()).Should().NotBeNull();

        await _store.ClearAsync();
        (await _store.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Load_when_blob_is_corrupted_returns_null_and_clears_file()
    {
        // Overwrite with garbage that won't decrypt or parse as JSON.
        var path = Path.Combine(_folder, "facebook-token.bin");
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(path, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        var loaded = await _store.LoadAsync();

        loaded.Should().BeNull();
        File.Exists(path).Should().BeFalse(
            "tampered/restored-from-other-machine blobs should be silently dropped");
    }

    [Fact]
    public async Task ExpiresAt_null_roundtrips_as_null()
    {
        // Older Meta accounts hand out user tokens that effectively don't expire —
        // we model that as null in the bundle.
        var bundle = new FacebookTokenBundle("u", "p", "pa", "n", ExpiresAt: null);

        await _store.SaveAsync(bundle);
        var loaded = await _store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded!.ExpiresAt.Should().BeNull();
    }
}
