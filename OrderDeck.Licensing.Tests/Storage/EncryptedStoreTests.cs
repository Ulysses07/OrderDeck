using FluentAssertions;
using OrderDeck.Licensing.Storage;
using Xunit;

namespace OrderDeck.Licensing.Tests.Storage;

public sealed class EncryptedStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly EncryptedStore _store;

    public EncryptedStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OrderDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new EncryptedStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record Sample(string Name, int Value);

    [Fact]
    public void Save_then_TryLoad_roundtrips_object()
    {
        var path = Path.Combine(_dir, "sample.dat");
        var original = new Sample("hello", 42);

        _store.Save(path, original);
        var loaded = _store.TryLoad<Sample>(path);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("hello");
        loaded.Value.Should().Be(42);
    }

    [Fact]
    public void TryLoad_returns_null_when_file_missing()
    {
        var path = Path.Combine(_dir, "missing.dat");
        var loaded = _store.TryLoad<Sample>(path);
        loaded.Should().BeNull();
    }

    [Fact]
    public void TryLoad_deletes_corrupted_file_and_returns_null()
    {
        var path = Path.Combine(_dir, "corrupt.dat");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });

        var loaded = _store.TryLoad<Sample>(path);

        loaded.Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Save_creates_parent_directory_if_missing()
    {
        var nestedDir = Path.Combine(_dir, "nested", "deep");
        var path = Path.Combine(nestedDir, "sample.dat");

        _store.Save(path, new Sample("x", 1));

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Saved_payload_is_not_plain_json()
    {
        var path = Path.Combine(_dir, "encrypted.dat");
        _store.Save(path, new Sample("secret-value", 999));

        var raw = File.ReadAllBytes(path);
        var asUtf8 = System.Text.Encoding.UTF8.GetString(raw);
        asUtf8.Should().NotContain("secret-value");
        asUtf8.Should().NotContain("999");
    }

    [Fact]
    public void Delete_removes_file_when_present_and_is_idempotent()
    {
        var path = Path.Combine(_dir, "to-delete.dat");
        _store.Save(path, new Sample("x", 1));
        File.Exists(path).Should().BeTrue();

        _store.Delete(path);
        File.Exists(path).Should().BeFalse();

        // Idempotent: second delete throws nothing
        _store.Delete(path);
    }
}
