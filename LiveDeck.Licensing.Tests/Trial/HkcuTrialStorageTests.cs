using FluentAssertions;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Trial;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.Licensing.Tests.Trial;

public sealed class HkcuTrialStorageTests : IDisposable
{
    private readonly LicensingOptions _opts;
    private readonly HkcuTrialStorage _storage;

    public HkcuTrialStorageTests()
    {
        _opts = new LicensingOptions
        {
            TrialRegistrySubKey = $"Software\\LiveDeckTests\\Trial-{Guid.NewGuid():N}"
        };
        _storage = new HkcuTrialStorage(Options.Create(_opts), NullLogger<HkcuTrialStorage>.Instance);
    }

    public void Dispose()
    {
        try { _storage.Clear(); } catch { }
        try
        {
            using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\LiveDeckTests", writable: true);
            parent?.DeleteSubKeyTree("Trial-" + _opts.TrialRegistrySubKey.Substring(_opts.TrialRegistrySubKey.LastIndexOf("Trial-") + 6), throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static TrialRecord Sample() => new(
        StartedAt: new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero),
        ExpiresAt: new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
        HardwareFingerprint: "fp",
        Version: 1);

    [Fact]
    public void Name_is_hkcu()
    {
        _storage.Name.Should().Be("hkcu");
    }

    [Fact]
    public void TryRead_returns_null_when_subkey_missing()
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
    public void Clear_removes_subkey()
    {
        _storage.Write(Sample());
        _storage.TryRead().Should().NotBeNull();

        _storage.Clear();

        _storage.TryRead().Should().BeNull();
    }
}
