using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Shortcuts;
using OrderDeck.Core.Shortcuts;

namespace OrderDeck.App.ViewModels;

public sealed partial class ShortcutsTabViewModel : ViewModelBase
{
    private readonly ShortcutRegistry _registry;
    private readonly ShortcutBinder _binder;

    [ObservableProperty] private bool _useCustom;

    public ObservableCollection<ShortcutEditRow> Rows { get; } = new();

    public ShortcutsTabViewModel(ShortcutRegistry registry, ShortcutBinder binder)
    {
        _registry = registry;
        _binder = binder;
        _useCustom = registry.UseCustom;
        Reload();
    }

    private void Reload()
    {
        Rows.Clear();
        var active = UseCustom ? _registry.GetCustom() : _registry.Defaults;
        var byCommand = active.ToDictionary(b => b.CommandId, b => b.Chord);

        foreach (var commandId in ShortcutCommand.DisplayNames.Keys)
        {
            byCommand.TryGetValue(commandId, out var chord);
            Rows.Add(new ShortcutEditRow(
                commandId, ShortcutCommand.DisplayNames[commandId], chord));
        }
    }

    partial void OnUseCustomChanged(bool value) => Reload();

    [RelayCommand]
    private void Save()
    {
        if (UseCustom)
        {
            var bindings = Rows
                .Where(r => r.Chord is not null)
                .Select(r => new ShortcutBinding(r.CommandId, r.Chord!))
                .ToList();

            var conflicts = ShortcutRegistry.FindConflicts(bindings);
            if (conflicts.Count > 0)
            {
                var pairs = string.Join("\n",
                    conflicts.Select(c =>
                        $"  • {ShortcutCommand.DisplayNames[c.CommandIdA]}  ↔  {ShortcutCommand.DisplayNames[c.CommandIdB]}"));
                MessageBox.Show(
                    $"Aynı kombinasyona sahip komutlar var:\n{pairs}\n\nLütfen düzeltin.",
                    "Çakışma", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _registry.SaveCustom(bindings, useCustom: true);
        }
        else
        {
            // Persist UseCustom=false flag without changing custom bindings.
            _registry.SaveCustom(_registry.GetCustom(), useCustom: false);
        }

        if (Application.Current?.MainWindow is { } mw)
            _binder.Apply(mw);

        MessageBox.Show("Kısayollar kaydedildi.", "Tamam",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        var confirm = MessageBox.Show(
            "Özel kısayollar silinip varsayılana dönülecek. Emin misin?",
            "Sıfırla", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _registry.ResetCustomToDefaults();
        Reload();
        if (Application.Current?.MainWindow is { } mw)
            _binder.Apply(mw);
    }
}

/// <summary>Settings tab DataGrid satırı. Chord TwoWay bind, UI'da capture button doldurur.</summary>
public sealed partial class ShortcutEditRow : ObservableObject
{
    public string CommandId { get; }
    public string DisplayName { get; }
    [ObservableProperty] private KeyChord? _chord;

    public ShortcutEditRow(string commandId, string displayName, KeyChord? chord)
    {
        CommandId = commandId;
        DisplayName = displayName;
        _chord = chord;
    }
}
