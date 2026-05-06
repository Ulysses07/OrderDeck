using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Settings;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class CreateGiveawayDialogViewModelAnimationTests
{
    private static AppSettings SettingsWith(string defaultId)
    {
        var s = new AppSettings();
        s.GiveawayAnimation.DefaultId = defaultId;
        return s;
    }

    [Fact]
    public void SelectedAnimationId_defaults_to_settings_DefaultId()
    {
        var vm = new NewGiveawayDialogViewModel(SettingsWith("bingo"));

        vm.SelectedAnimationId.Should().Be("bingo");
    }

    [Fact]
    public void SelectedAnimationId_can_be_changed()
    {
        var vm = new NewGiveawayDialogViewModel(SettingsWith("bingo"));
        var changes = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedAnimationId)) changes++;
        };

        vm.SelectedAnimationId = "wheel";

        vm.SelectedAnimationId.Should().Be("wheel");
        changes.Should().Be(1);
    }
}
