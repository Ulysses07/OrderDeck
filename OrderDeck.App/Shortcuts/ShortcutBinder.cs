using System;
using System.Windows;
using System.Windows.Input;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Shortcuts;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Shortcuts;

/// <summary>
/// ShortcutRegistry'deki aktif binding'leri MainWindow.InputBindings'e runtime'da uygular.
/// State'siz; her Apply çağrısı Window.InputBindings'i temizleyip yeniden inşa eder.
/// </summary>
public sealed class ShortcutBinder
{
    private readonly ShortcutRegistry _registry;
    private readonly MainShellViewModel _shell;
    private readonly ILogger<ShortcutBinder> _log;

    public ShortcutBinder(ShortcutRegistry registry, MainShellViewModel shell,
        ILogger<ShortcutBinder>? log = null)
    {
        _registry = registry;
        _shell = shell;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ShortcutBinder>.Instance;
    }

    /// <summary>Window.InputBindings koleksiyonunu temizler ve registry.GetActive()'e göre yeniden inşa eder.</summary>
    public void Apply(Window window)
    {
        window.InputBindings.Clear();
        foreach (var binding in _registry.GetActive())
        {
            if (!Enum.TryParse<Key>(binding.Chord.Key, ignoreCase: true, out var wpfKey))
            {
                _log.LogWarning("Unknown WPF Key '{Key}' for command '{Cmd}', skipping",
                    binding.Chord.Key, binding.CommandId);
                continue;
            }
            var cmd = GetCommand(binding.CommandId);
            if (cmd is null)
            {
                _log.LogWarning("Unknown command id '{Cmd}', skipping", binding.CommandId);
                continue;
            }
            window.InputBindings.Add(new KeyBinding(cmd, wpfKey, ConvertModifiers(binding.Chord.Modifiers)));
        }
    }

    private ICommand? GetCommand(string commandId) => commandId switch
    {
        ShortcutCommand.Print            => _shell.PrintCommand,
        ShortcutCommand.DeleteSelected   => _shell.DeleteSelectedFromQueueViaShortcutCommand,
        ShortcutCommand.ClearQueue       => _shell.ClearQueueCommand,
        ShortcutCommand.StartStream      => _shell.StartStreamCommand,
        ShortcutCommand.EndStream        => _shell.EndStreamCommand,
        ShortcutCommand.StartGiveaway    => _shell.StartGiveawayCommand,
        ShortcutCommand.OpenShortcutHelp => _shell.OpenShortcutHelpCommand,
        ShortcutCommand.OpenSettings     => _shell.OpenSettingsCommand,
        ShortcutCommand.OpenHistory      => _shell.OpenStreamHistoryCommand,
        ShortcutCommand.OpenBlacklist    => _shell.OpenBlacklistCommand,
        ShortcutCommand.OpenCustomers    => _shell.OpenCustomerSearchCommand,
        _ => null
    };

    private static ModifierKeys ConvertModifiers(KeyModifiers mods)
    {
        var r = ModifierKeys.None;
        if (mods.HasFlag(KeyModifiers.Ctrl))  r |= ModifierKeys.Control;
        if (mods.HasFlag(KeyModifiers.Shift)) r |= ModifierKeys.Shift;
        if (mods.HasFlag(KeyModifiers.Alt))   r |= ModifierKeys.Alt;
        if (mods.HasFlag(KeyModifiers.Win))   r |= ModifierKeys.Windows;
        return r;
    }
}
