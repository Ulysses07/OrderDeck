using System.IO;
using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Shortcuts;
using Xunit;

namespace OrderDeck.Tests.Shortcuts;

public class ShortcutRegistryTests
{
    private static (ShortcutRegistry Registry, SettingsStore Store, string Path) Fx()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"orderdeck-shortcut-test-{System.Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        var registry = new ShortcutRegistry(store);
        return (registry, store, path);
    }

    [Fact]
    public void Defaults_contains_eleven_known_commands()
    {
        var (reg, _, path) = Fx();
        try
        {
            reg.Defaults.Should().HaveCount(11);
            reg.Defaults.Select(b => b.CommandId).Should().BeEquivalentTo(new[]
            {
                ShortcutCommand.Print, ShortcutCommand.DeleteSelected, ShortcutCommand.ClearQueue,
                ShortcutCommand.StartStream, ShortcutCommand.EndStream, ShortcutCommand.StartGiveaway,
                ShortcutCommand.OpenShortcutHelp, ShortcutCommand.OpenSettings, ShortcutCommand.OpenHistory,
                ShortcutCommand.OpenBlacklist, ShortcutCommand.OpenCustomers,
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Defaults_have_unique_chords_so_FindConflicts_is_empty()
    {
        var (reg, _, path) = Fx();
        try
        {
            ShortcutRegistry.FindConflicts(reg.Defaults).Should().BeEmpty();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetActive_returns_defaults_when_UseCustom_false()
    {
        var (reg, _, path) = Fx();
        try
        {
            reg.UseCustom.Should().BeFalse();
            reg.GetActive().Should().BeEquivalentTo(reg.Defaults);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveCustom_with_useCustom_true_persists_to_disk_and_GetActive_returns_custom()
    {
        var (reg, store, path) = Fx();
        try
        {
            var custom = new[]
            {
                new ShortcutBinding(ShortcutCommand.Print, new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Alt, "P")),
            };
            reg.SaveCustom(custom, useCustom: true);

            reg.UseCustom.Should().BeTrue();
            reg.GetActive().Should().BeEquivalentTo(custom);

            // Reload from disk to verify persistence
            var fresh = new ShortcutRegistry(store);
            fresh.UseCustom.Should().BeTrue();
            fresh.GetCustom().Should().BeEquivalentTo(custom);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveCustom_with_useCustom_false_persists_flag_but_keeps_custom_bindings()
    {
        var (reg, store, path) = Fx();
        try
        {
            var custom = new[]
            {
                new ShortcutBinding(ShortcutCommand.Print, new KeyChord(KeyModifiers.Ctrl, "P")),
            };
            reg.SaveCustom(custom, useCustom: false);

            reg.UseCustom.Should().BeFalse();
            reg.GetActive().Should().BeEquivalentTo(reg.Defaults);   // active = defaults
            reg.GetCustom().Should().BeEquivalentTo(custom);          // custom retained
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ResetCustomToDefaults_overwrites_custom_with_defaults()
    {
        var (reg, _, path) = Fx();
        try
        {
            var custom = new[]
            {
                new ShortcutBinding(ShortcutCommand.Print, new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Alt, "P")),
            };
            reg.SaveCustom(custom, useCustom: true);

            reg.ResetCustomToDefaults();

            reg.GetCustom().Should().BeEquivalentTo(reg.Defaults);
            reg.UseCustom.Should().BeTrue();   // flag unchanged
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FindConflicts_returns_pair_when_two_commands_share_chord()
    {
        var bindings = new[]
        {
            new ShortcutBinding(ShortcutCommand.Print,            new KeyChord(KeyModifiers.Ctrl, "P")),
            new ShortcutBinding(ShortcutCommand.OpenSettings,     new KeyChord(KeyModifiers.Ctrl, "P")),   // conflict!
            new ShortcutBinding(ShortcutCommand.OpenShortcutHelp, new KeyChord(KeyModifiers.None, "F1")),
        };

        var conflicts = ShortcutRegistry.FindConflicts(bindings);

        conflicts.Should().HaveCount(1);
        var pair = conflicts[0];
        new[] { pair.CommandIdA, pair.CommandIdB }.Should().BeEquivalentTo(new[]
            { ShortcutCommand.Print, ShortcutCommand.OpenSettings });
    }

    [Fact]
    public void FindConflicts_empty_when_all_chords_unique()
    {
        var bindings = new[]
        {
            new ShortcutBinding(ShortcutCommand.Print,        new KeyChord(KeyModifiers.Ctrl, "P")),
            new ShortcutBinding(ShortcutCommand.OpenSettings, new KeyChord(KeyModifiers.None, "F2")),
        };

        ShortcutRegistry.FindConflicts(bindings).Should().BeEmpty();
    }

    [Fact]
    public void Boot_with_corrupt_chord_string_falls_back_to_defaults_for_that_entry()
    {
        var (reg1, store, path) = Fx();
        try
        {
            // Manually write a settings file with one bad chord
            var badSettings = new AppSettings
            {
                UseCustomShortcuts = true,
                CustomShortcuts = new System.Collections.Generic.Dictionary<string, string>
                {
                    [ShortcutCommand.Print] = "Ctrl+P",
                    [ShortcutCommand.OpenSettings] = "Foo+Bar+Baz",   // unparseable
                }
            };
            store.Save(badSettings);

            var fresh = new ShortcutRegistry(store);
            var custom = fresh.GetCustom();

            custom.Should().Contain(b => b.CommandId == ShortcutCommand.Print);
            custom.Should().NotContain(b => b.CommandId == ShortcutCommand.OpenSettings);   // skipped
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
