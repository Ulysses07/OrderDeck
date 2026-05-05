using System.Collections.Generic;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class AnimationPickerViewModelTests
{
    private static IReadOnlyList<AnimationCatalogEntry> Two() => new[]
    {
        new AnimationCatalogEntry("wheel", "Çark", "Klasik", "klasik", "wheel/thumbnail.svg"),
        new AnimationCatalogEntry("slot-machine", "Slot", "Kazino", "klasik", "slot-machine/thumbnail.svg"),
    };

    [Fact]
    public void Loaded_animations_match_seeded_catalog()
    {
        var vm = new AnimationPickerViewModel();
        vm.LoadAnimations(Two());
        vm.Animations.Should().HaveCount(2);
        vm.Animations[0].Id.Should().Be("wheel");
    }

    [Fact]
    public void SelectedId_change_fires_PropertyChanged()
    {
        var vm = new AnimationPickerViewModel();
        vm.LoadAnimations(Two());
        vm.SelectedId = "wheel";

        var changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedId)) changes++; };

        vm.SelectedId = "slot-machine";

        changes.Should().Be(1);
        vm.SelectedId.Should().Be("slot-machine");
    }

    [Fact]
    public void Setting_unknown_id_does_not_change_selection()
    {
        var vm = new AnimationPickerViewModel();
        vm.LoadAnimations(Two());
        vm.SelectedId = "wheel";

        vm.SelectedId = "phantom";

        vm.SelectedId.Should().Be("wheel");
    }
}
