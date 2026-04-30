using System;
using System.Collections.Generic;
using System.Text;

namespace OrderDeck.Core.Shortcuts;

[Flags]
public enum KeyModifiers
{
    None  = 0,
    Ctrl  = 1,
    Shift = 2,
    Alt   = 4,
    Win   = 8,
}

/// <summary>
/// WPF-bağımsız tuş kombinasyonu. JSON'da string olarak persist edilir.
/// Format: "Ctrl+Shift+Alt+Win+&lt;Key&gt;" — canonical modifier order.
/// </summary>
public sealed record KeyChord(KeyModifiers Modifiers, string Key)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl))  sb.Append("Ctrl+");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(KeyModifiers.Alt))   sb.Append("Alt+");
        if (Modifiers.HasFlag(KeyModifiers.Win))   sb.Append("Win+");
        sb.Append(Key);
        return sb.ToString();
    }

    public static KeyChord Parse(string input)
    {
        if (!TryParse(input, out var chord))
            throw new FormatException($"Invalid key chord: '{input}'");
        return chord!;
    }

    public static bool TryParse(string input, out KeyChord? chord)
    {
        chord = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.StartsWith('+') || trimmed.EndsWith('+')) return false;

        var rawParts = trimmed.Split('+', StringSplitOptions.TrimEntries);
        // Empty segment between '+' is invalid (e.g. "Ctrl++P", "Ctrl+ +P").
        foreach (var p in rawParts)
            if (p.Length == 0) return false;

        var parts = rawParts;
        if (parts.Length == 0) return false;

        var mods = KeyModifiers.None;
        string? key = null;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            switch (part.ToLowerInvariant())
            {
                case "ctrl":  case "control": mods |= KeyModifiers.Ctrl;  continue;
                case "shift":                  mods |= KeyModifiers.Shift; continue;
                case "alt":                    mods |= KeyModifiers.Alt;   continue;
                case "win":   case "windows":  mods |= KeyModifiers.Win;   continue;
            }
            // First non-modifier part is the key; further parts are invalid.
            if (key is not null) return false;
            key = NormalizeKey(part);
        }

        if (key is null) return false;   // modifier-only

        chord = new KeyChord(mods, key);
        return true;
    }

    /// <summary>Title-case key tokens so "delete" → "Delete", "p" → "P". F-keys preserved as-is.</summary>
    private static string NormalizeKey(string raw)
    {
        if (raw.Length == 0) return raw;
        if (raw.Length == 1) return raw.ToUpperInvariant();
        return char.ToUpperInvariant(raw[0]) + raw.Substring(1).ToLowerInvariant();
    }
}
