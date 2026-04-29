using FluentAssertions;
using LiveDeck.Licensing.Storage;
using Xunit;

namespace LiveDeck.Licensing.Tests.Storage;

public sealed class LicenseStateStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly LicenseStateStore _store;

    public LicenseStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new LicenseStateStore(new EncryptedStore(), Path.Combine(_dir, "license.dat"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_returns_null_when_no_file()
    {
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_roundtrips_record()
    {
        var record = new LicenseRecord(
            LicenseKey: "LDK-XYZ",
            SkuCode: "STD",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(365),
            RemainingDaysAtLastCheck: 365,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: DateTimeOffset.UtcNow,
            LastKnownStatus: "Active");

        _store.Save(record);
        var loaded = _store.Load();

        loaded.Should().NotBeNull();
        loaded!.LicenseKey.Should().Be("LDK-XYZ");
        loaded.SkuCode.Should().Be("STD");
        loaded.RemainingDaysAtLastCheck.Should().Be(365);
        loaded.LastKnownStatus.Should().Be("Active");
    }

    [Fact]
    public void Clear_removes_file()
    {
        _store.Save(new LicenseRecord("LDK", "STD", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "x"));
        _store.IsPresent.Should().BeTrue();

        _store.Clear();

        _store.IsPresent.Should().BeFalse();
    }
}
