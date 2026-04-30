using FluentAssertions;
using LiveDeck.Licensing.Storage;
using Xunit;

namespace LiveDeck.Licensing.Tests.Storage;

public sealed class AuthStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly AuthStore _store;

    public AuthStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "auth.dat");
        _store = new AuthStore(new EncryptedStore(), _path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IsPresent_is_false_when_no_file()
    {
        _store.IsPresent.Should().BeFalse();
    }

    [Fact]
    public void Load_returns_null_when_no_file()
    {
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_roundtrips_record()
    {
        var record = new AuthRecord(
            CustomerId: Guid.NewGuid(),
            Email: "user@example.com",
            Name: "Test User",
            Token: "header.payload.signature",
            TokenExpiresAt: DateTimeOffset.UtcNow.AddDays(7));

        _store.Save(record);
        _store.IsPresent.Should().BeTrue();

        var loaded = _store.Load();
        loaded.Should().NotBeNull();
        loaded!.CustomerId.Should().Be(record.CustomerId);
        loaded.Email.Should().Be(record.Email);
        loaded.Name.Should().Be(record.Name);
        loaded.Token.Should().Be(record.Token);
        loaded.TokenExpiresAt.Should().BeCloseTo(record.TokenExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Clear_removes_file()
    {
        _store.Save(new AuthRecord(Guid.NewGuid(), "a", "b", "t", DateTimeOffset.UtcNow));
        _store.IsPresent.Should().BeTrue();

        _store.Clear();

        _store.IsPresent.Should().BeFalse();
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Clear_is_idempotent_when_no_file()
    {
        _store.Clear();
        _store.IsPresent.Should().BeFalse();
    }
}
