using System.IO;
using FluentAssertions;
using LiveDeck.Core.Settings;
using Xunit;

namespace LiveDeck.Tests.Settings;

public class SettingsStoreTests
{
    private string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"livedeck-test-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var store = new SettingsStore(CreateTempPath());
        var settings = store.Load();

        settings.OverlayPort.Should().Be(4747);
        settings.CaptureOrderHotkey.Should().Be("F9");
        settings.ParserHighConfidence.Should().Be(80);
        settings.ParserLowConfidence.Should().Be(50);
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);
        var original = new AppSettings
        {
            OverlayPort = 5000,
            CaptureOrderHotkey = "F8",
            ParserHighConfidence = 75,
            ParserLowConfidence = 40,
            EtiketIntegrationEnabled = true
        };

        store.Save(original);
        var reloaded = store.Load();

        reloaded.OverlayPort.Should().Be(5000);
        reloaded.CaptureOrderHotkey.Should().Be("F8");
        reloaded.ParserHighConfidence.Should().Be(75);
        reloaded.ParserLowConfidence.Should().Be(40);
        reloaded.EtiketIntegrationEnabled.Should().BeTrue();

        File.Delete(path);
    }
}
