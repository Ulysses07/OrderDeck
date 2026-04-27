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
        settings.LabelWidthMm.Should().Be(60);
        settings.LabelHeightMm.Should().Be(30);
        settings.LabelFontFamily.Should().Be("Arial");
        settings.PrinterName.Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);
        var original = new AppSettings
        {
            OverlayPort = 5000,
            ChatTheme = "neon",
            ParserHighConfidence = 75,
            ParserLowConfidence = 40,
            PrinterName = "Zebra ZD220",
            LabelWidthMm = 75,
            LabelHeightMm = 40,
            LabelGapMm = 3,
            LabelFontFamily = "Segoe UI",
            LabelUserFontSize = 16,
            LabelMessageFontSize = 13
        };

        store.Save(original);
        var reloaded = store.Load();

        reloaded.OverlayPort.Should().Be(5000);
        reloaded.ChatTheme.Should().Be("neon");
        reloaded.ParserHighConfidence.Should().Be(75);
        reloaded.ParserLowConfidence.Should().Be(40);
        reloaded.PrinterName.Should().Be("Zebra ZD220");
        reloaded.LabelWidthMm.Should().Be(75);
        reloaded.LabelHeightMm.Should().Be(40);
        reloaded.LabelGapMm.Should().Be(3);
        reloaded.LabelFontFamily.Should().Be("Segoe UI");
        reloaded.LabelUserFontSize.Should().Be(16);
        reloaded.LabelMessageFontSize.Should().Be(13);

        File.Delete(path);
    }
}
