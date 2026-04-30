using FluentAssertions;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Trial;
using Xunit;

namespace OrderDeck.Licensing.Tests.Trial;

public sealed class LocalAppDataTrialStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly LocalAppDataTrialStorage _storage;

    public LocalAppDataTrialStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OrderDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "trial.dat");
        _storage = new LocalAppDataTrialStorage(new EncryptedStore(), _path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TrialRecord Sample() => new(
        StartedAt: new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero),
        ExpiresAt: new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
        HardwareFingerprint: "fp",
        Version: 1);

    [Fact]
    public void Name_is_localappdata()
    {
        _storage.Name.Should().Be("localappdata");
    }

    [Fact]
    public void TryRead_returns_null_when_file_missing()
    {
        _storage.TryRead().Should().BeNull();
    }

    [Fact]
    public void Write_then_TryRead_roundtrips_record()
    {
        var record = Sample();
        _storage.Write(record);

        var loaded = _storage.TryRead();
        loaded.Should().NotBeNull();
        loaded!.HardwareFingerprint.Should().Be("fp");
        loaded.Version.Should().Be(1);
    }

    [Fact]
    public void Saved_payload_is_dpapi_encrypted_not_plain_json()
    {
        _storage.Write(new TrialRecord(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14), "secret-fp-value", 1));
        var raw = File.ReadAllBytes(_path);
        var asUtf8 = System.Text.Encoding.UTF8.GetString(raw);
        asUtf8.Should().NotContain("secret-fp-value");
    }

    [Fact]
    public void Clear_removes_file()
    {
        _storage.Write(Sample());
        File.Exists(_path).Should().BeTrue();
        _storage.Clear();
        File.Exists(_path).Should().BeFalse();
    }
}
