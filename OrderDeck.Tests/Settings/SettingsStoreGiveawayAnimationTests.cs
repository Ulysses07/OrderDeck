using System.IO;
using FluentAssertions;
using OrderDeck.Core.Settings;
using Xunit;

namespace OrderDeck.Tests.Settings;

public class SettingsStoreGiveawayAnimationTests
{
    [Fact]
    public void Save_then_Load_round_trips_GiveawayAnimation_block()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"orderdeck-anim-{System.Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        try
        {
            var settings = new AppSettings();
            settings.GiveawayAnimation.DefaultId = "wheel";
            settings.GiveawayAnimation.Volume = 0.5;
            settings.GiveawayAnimation.MutedMode = true;

            store.Save(settings);
            var loaded = store.Load();

            loaded.GiveawayAnimation.DefaultId.Should().Be("wheel");
            loaded.GiveawayAnimation.Volume.Should().Be(0.5);
            loaded.GiveawayAnimation.MutedMode.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Fresh_AppSettings_has_default_animation_block()
    {
        var s = new AppSettings();

        s.GiveawayAnimation.Should().NotBeNull();
        s.GiveawayAnimation.DefaultId.Should().Be("wheel");
        s.GiveawayAnimation.Volume.Should().BeApproximately(0.7, 0.001);
        s.GiveawayAnimation.MutedMode.Should().BeFalse();
    }
}
