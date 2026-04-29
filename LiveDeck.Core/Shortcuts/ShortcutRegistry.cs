using System.Collections.Generic;
using System.Linq;
using LiveDeck.Core.Settings;

namespace LiveDeck.Core.Shortcuts;

/// <summary>
/// Komut ↔ kısayol haritasının runtime sahibi. Defaults sabit; UseCustom flag'i true ise
/// custom profil aktif. Persistence <see cref="SettingsStore"/> üzerinden AppSettings'in
/// UseCustomShortcuts + CustomShortcuts alanlarına yazılır.
/// </summary>
public sealed class ShortcutRegistry
{
    private readonly SettingsStore _settings;
    private List<ShortcutBinding> _custom;

    /// <summary>Sabit kodlanmış varsayılan profil. Salt-okunur.</summary>
    public IReadOnlyList<ShortcutBinding> Defaults { get; }

    /// <summary>Aktif profil custom mu?</summary>
    public bool UseCustom { get; private set; }

    public ShortcutRegistry(SettingsStore settings)
    {
        _settings = settings;
        Defaults = BuildDefaults();
        (_custom, UseCustom) = LoadCustom(settings, Defaults);
    }

    public IReadOnlyList<ShortcutBinding> GetActive() =>
        UseCustom ? _custom.AsReadOnly() : Defaults;

    public IReadOnlyList<ShortcutBinding> GetCustom() => _custom.AsReadOnly();

    public void SaveCustom(IReadOnlyList<ShortcutBinding> bindings, bool useCustom)
    {
        _custom = bindings.ToList();
        UseCustom = useCustom;

        var current = _settings.Load();
        current.UseCustomShortcuts = useCustom;
        current.CustomShortcuts = _custom.ToDictionary(b => b.CommandId, b => b.Chord.ToString());
        _settings.Save(current);
    }

    public void ResetCustomToDefaults()
    {
        _custom = Defaults.ToList();
        var current = _settings.Load();
        current.UseCustomShortcuts = UseCustom;
        current.CustomShortcuts = _custom.ToDictionary(b => b.CommandId, b => b.Chord.ToString());
        _settings.Save(current);
    }

    /// <summary>Aynı KeyChord'u kullanan komut çiftlerini bulur. Boş = çakışma yok.
    /// Aynı chord N komutta paylaşılırsa C(N,2) çift döndürür.</summary>
    public static IReadOnlyList<(string CommandIdA, string CommandIdB)> FindConflicts(
        IReadOnlyList<ShortcutBinding> bindings)
    {
        var conflicts = new List<(string, string)>();
        for (int i = 0; i < bindings.Count; i++)
        {
            for (int j = i + 1; j < bindings.Count; j++)
            {
                if (bindings[i].Chord == bindings[j].Chord)
                    conflicts.Add((bindings[i].CommandId, bindings[j].CommandId));
            }
        }
        return conflicts;
    }

    private static List<ShortcutBinding> BuildDefaults() => new()
    {
        new ShortcutBinding(ShortcutCommand.Print,            new KeyChord(KeyModifiers.Ctrl,                                  "P")),
        new ShortcutBinding(ShortcutCommand.DeleteSelected,   new KeyChord(KeyModifiers.None,                                  "Delete")),
        new ShortcutBinding(ShortcutCommand.ClearQueue,       new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Shift,             "Delete")),
        new ShortcutBinding(ShortcutCommand.StartStream,      new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Shift,             "S")),
        new ShortcutBinding(ShortcutCommand.EndStream,        new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Shift,             "E")),
        new ShortcutBinding(ShortcutCommand.StartGiveaway,    new KeyChord(KeyModifiers.Ctrl,                                  "G")),
        new ShortcutBinding(ShortcutCommand.OpenShortcutHelp, new KeyChord(KeyModifiers.None,                                  "F1")),
        new ShortcutBinding(ShortcutCommand.OpenSettings,     new KeyChord(KeyModifiers.None,                                  "F2")),
        new ShortcutBinding(ShortcutCommand.OpenHistory,      new KeyChord(KeyModifiers.None,                                  "F3")),
        new ShortcutBinding(ShortcutCommand.OpenBlacklist,    new KeyChord(KeyModifiers.None,                                  "F4")),
        new ShortcutBinding(ShortcutCommand.OpenCustomers,    new KeyChord(KeyModifiers.None,                                  "F5")),
    };

    private static (List<ShortcutBinding> Custom, bool UseCustom) LoadCustom(
        SettingsStore settings, IReadOnlyList<ShortcutBinding> defaults)
    {
        var s = settings.Load();
        if (s.CustomShortcuts is null || s.CustomShortcuts.Count == 0)
            return (defaults.ToList(), s.UseCustomShortcuts);

        var custom = new List<ShortcutBinding>();
        foreach (var (commandId, chordStr) in s.CustomShortcuts)
        {
            if (KeyChord.TryParse(chordStr, out var chord) && chord is not null)
                custom.Add(new ShortcutBinding(commandId, chord));
            // else: skip (corrupt entry)
        }
        return (custom, s.UseCustomShortcuts);
    }
}
