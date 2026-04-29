using System.Text.Json;
using FluentAssertions;
using LiveDeck.Licensing.Trial;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveDeck.Licensing.Tests.Trial;

public sealed class ProgramDataTrialStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly ProgramDataTrialStorage _storage;

    public ProgramDataTrialStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "trial.dat");
        _storage = new ProgramDataTrialStorage(_path, NullLogger<ProgramDataTrialStorage>.Instance);
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
    public void Name_is_programdata()
    {
        _storage.Name.Should().Be("programdata");
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
        loaded!.StartedAt.Should().Be(record.StartedAt);
        loaded.ExpiresAt.Should().Be(record.ExpiresAt);
        loaded.HardwareFingerprint.Should().Be("fp");
        loaded.Version.Should().Be(1);
    }

    [Fact]
    public void TryRead_returns_null_when_hmac_tampered()
    {
        _storage.Write(Sample());
        var raw = File.ReadAllText(_path);
        var tampered = raw.Replace("\"hmac\":\"", "\"hmac\":\"00", StringComparison.Ordinal);
        File.WriteAllText(_path, tampered);

        _storage.TryRead().Should().BeNull();
    }

    [Fact]
    public void TryRead_returns_null_when_record_field_tampered()
    {
        _storage.Write(Sample());
        var raw = File.ReadAllText(_path);
        // Replace ExpiresAt year 2026 → 2099 to extend trial; HMAC mismatch
        var tampered = raw.Replace("2026-05-13", "2099-05-13", StringComparison.Ordinal);
        File.WriteAllText(_path, tampered);

        _storage.TryRead().Should().BeNull();
    }

    [Fact]
    public void TryRead_returns_null_for_malformed_json()
    {
        File.WriteAllText(_path, "{not valid json");

        _storage.TryRead().Should().BeNull();
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
