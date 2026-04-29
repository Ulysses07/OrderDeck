# Faz 3b-1 — Kısayol Sistem Yöneticisi Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 11 komutu kapsayan özelleştirilebilir klavye kısayol sistemi: default profil sabit, kullanıcı özel profili düzenleyebilir, çakışma tespiti, F1 yardım dialog'u, runtime rebind.

**Architecture:** `LiveDeck.Core/Shortcuts/` altında WPF-bağımsız domain (`KeyChord`, `ShortcutCommand`, `ShortcutBinding`, `ShortcutRegistry` + `AppSettings` persistence). `LiveDeck.App/Shortcuts/` altında WPF-spesifik `ShortcutBinder` (registry → MainWindow.InputBindings). `LiveDeck.App/Controls/ShortcutCaptureButton` custom button. Settings dialog'a yeni "Kısayollar" tab'ı + F1 yardım dialog'u.

**Tech Stack:** .NET 10 WPF (existing), CommunityToolkit.Mvvm (existing), `System.Text.Json` (existing). Yeni paket yok.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-3b-1 state:** Faz 3a HEAD `7eb4ae0` + spec commit `c9c3c51`. 87/87 tests passing.

**Spec reference:** `docs/superpowers/specs/2026-04-28-phase-3b-1-shortcut-system-design.md`

---

## Task Index

**Domain (1-3):** KeyChord + ShortcutCommand/Binding + ShortcutRegistry (TDD)
**Settings persistence (4):** AppSettings genişletmesi
**App service + control (5-6):** ShortcutBinder + ShortcutCaptureButton
**ViewModel + Settings tab (7-8):** ShortcutsTabViewModel + Settings dialog tab
**MainShell + F1 dialog (9-10):** new commands + ShortcutHelpDialog
**Wire-up (11):** MainWindow.Loaded + AppHost DI
**Acceptance (12):** Manuel smoke

---

### Task 1: KeyChord (domain primitive, TDD)

**Files:**
- Create: `LiveDeck.Core/Shortcuts/KeyChord.cs`
- Create: `LiveDeck.Tests/Shortcuts/KeyChordTests.cs`

**Context:** WPF-bağımsız tuş kombinasyonu. JSON'da string olarak persist edilir. `Modifiers` `[Flags]` enum, `Key` string (WPF `Key` enum'a Core'dan bağımlı olmamak için). `ToString` canonical sıra: `Ctrl+Shift+Alt+Win+<Key>`. Parse case-insensitive, `+` ayraç, whitespace tolere edilir.

- [ ] **Step 1: Write failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Shortcuts/KeyChordTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Shortcuts;
using Xunit;

namespace LiveDeck.Tests.Shortcuts;

public class KeyChordTests
{
    [Theory]
    [InlineData("F1",                          KeyModifiers.None,                                          "F1")]
    [InlineData("Delete",                      KeyModifiers.None,                                          "Delete")]
    [InlineData("Ctrl+P",                      KeyModifiers.Ctrl,                                          "P")]
    [InlineData("Ctrl+Shift+Delete",           KeyModifiers.Ctrl | KeyModifiers.Shift,                     "Delete")]
    [InlineData("Ctrl+Shift+Alt+Win+P",        KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win, "P")]
    public void Parse_returns_chord_with_correct_modifiers_and_key(
        string input, KeyModifiers expectedMods, string expectedKey)
    {
        var chord = KeyChord.Parse(input);
        chord.Modifiers.Should().Be(expectedMods);
        chord.Key.Should().Be(expectedKey);
    }

    [Theory]
    [InlineData("ctrl+p",         "Ctrl+P")]
    [InlineData("CTRL+SHIFT+s",   "Ctrl+Shift+S")]
    [InlineData("  Ctrl + P  ",   "Ctrl+P")]
    [InlineData("Shift+Ctrl+P",   "Ctrl+Shift+P")]   // canonical re-order
    public void Parse_then_ToString_normalizes_to_canonical_form(string input, string canonical)
    {
        KeyChord.Parse(input).ToString().Should().Be(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]               // modifier-only
    [InlineData("Ctrl+Shift")]         // modifier-only
    [InlineData("+P")]
    public void Parse_throws_on_invalid_or_modifier_only(string input)
    {
        var act = () => KeyChord.Parse(input);
        act.Should().Throw<System.FormatException>();
    }

    [Fact]
    public void TryParse_returns_false_for_invalid_input()
    {
        KeyChord.TryParse("Ctrl+", out _).Should().BeFalse();
        KeyChord.TryParse("",      out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_returns_true_and_sets_chord_for_valid_input()
    {
        KeyChord.TryParse("Ctrl+G", out var chord).Should().BeTrue();
        chord.Should().NotBeNull();
        chord!.ToString().Should().Be("Ctrl+G");
    }

    [Fact]
    public void ToString_uses_canonical_modifier_order_ctrl_shift_alt_win()
    {
        var chord = new KeyChord(
            KeyModifiers.Win | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Ctrl, "P");
        chord.ToString().Should().Be("Ctrl+Shift+Alt+Win+P");
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~KeyChordTests" 2>&1 | tail -10
```

Expected: compile error — `KeyChord`, `KeyModifiers` not found.

- [ ] **Step 3: Implement KeyChord**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Shortcuts/KeyChord.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace LiveDeck.Core.Shortcuts;

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

        var parts = input.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~KeyChordTests" 2>&1 | tail -3
```

Expected: all KeyChord tests pass.

- [ ] **Step 5: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: ~91/91 (87 baseline + 4 KeyChord theory groups, count varies by InlineData).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Shortcuts/KeyChord.cs LiveDeck.Tests/Shortcuts/KeyChordTests.cs
git commit -m "feat(core): add KeyChord domain primitive with Parse/ToString round-trip"
```

---

### Task 2: ShortcutCommand + ShortcutBinding (domain records)

**Files:**
- Create: `LiveDeck.Core/Shortcuts/ShortcutCommand.cs`
- Create: `LiveDeck.Core/Shortcuts/ShortcutBinding.cs`

**Context:** Komut ID sabitleri + UI display name'leri + binding record. Hiç logic yok, sadece data. Test gerekmiyor (records value-equality otomatik).

- [ ] **Step 1: Create ShortcutCommand.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Shortcuts/ShortcutCommand.cs`:

```csharp
using System.Collections.Generic;

namespace LiveDeck.Core.Shortcuts;

/// <summary>
/// Bilinen komut ID'lerinin canonical listesi. Yeni komut eklemek için:
/// 1) yeni const ekle
/// 2) <see cref="DisplayNames"/>'e ekle
/// 3) <c>ShortcutRegistry.BuildDefaults</c>'a ekle
/// 4) <c>ShortcutBinder.GetCommand</c>'a yeni vaka ekle
/// </summary>
public static class ShortcutCommand
{
    public const string Print            = "print";
    public const string DeleteSelected   = "delete-selected";
    public const string ClearQueue       = "clear-queue";
    public const string StartStream      = "start-stream";
    public const string EndStream        = "end-stream";
    public const string StartGiveaway    = "start-giveaway";
    public const string OpenShortcutHelp = "open-shortcut-help";
    public const string OpenSettings     = "open-settings";
    public const string OpenHistory      = "open-history";
    public const string OpenBlacklist    = "open-blacklist";
    public const string OpenCustomers    = "open-customers";

    /// <summary>UI'da gösterilecek Türkçe başlıklar.</summary>
    public static IReadOnlyDictionary<string, string> DisplayNames { get; } = new Dictionary<string, string>
    {
        [Print]            = "Yazdır",
        [DeleteSelected]   = "Seçili etiketi sil",
        [ClearQueue]       = "Kuyruğu temizle",
        [StartStream]      = "Yayını başlat",
        [EndStream]        = "Yayını bitir",
        [StartGiveaway]    = "Çekiliş başlat",
        [OpenShortcutHelp] = "Kısayol yardımı",
        [OpenSettings]     = "Ayarlar",
        [OpenHistory]      = "Yayın geçmişi",
        [OpenBlacklist]    = "Kara liste",
        [OpenCustomers]    = "Müşteriler",
    };
}
```

- [ ] **Step 2: Create ShortcutBinding.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Shortcuts/ShortcutBinding.cs`:

```csharp
namespace LiveDeck.Core.Shortcuts;

/// <summary>Tek bir komut ↔ tuş kombinasyonu eşleşmesi.</summary>
public sealed record ShortcutBinding(string CommandId, KeyChord Chord);
```

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Shortcuts/ShortcutCommand.cs LiveDeck.Core/Shortcuts/ShortcutBinding.cs
git commit -m "feat(core): add ShortcutCommand registry + ShortcutBinding record"
```

---

### Task 3: AppSettings persistence fields

**Files:**
- Modify: `LiveDeck.Core/Settings/AppSettings.cs`

**Context:** `ShortcutRegistry`'nin ihtiyaç duyduğu alanları `AppSettings`'e ekle. JSON serialization mevcut `SettingsStore` aracılığıyla otomatik. Eski config dosyası geriye uyumlu — yeni alanlar default değerlerle çalışır.

- [ ] **Step 1: Add fields**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Settings/AppSettings.cs`. Add new properties at the bottom of the class (just before the closing `}`):

```csharp
    // Shortcuts (Phase 3b-1)
    public bool UseCustomShortcuts { get; set; } = false;

    /// <summary>Custom kısayol profili: command id → chord string ("Ctrl+P", "F1", ...). Null = henüz custom yok.</summary>
    public System.Collections.Generic.Dictionary<string, string>? CustomShortcuts { get; set; }
```

Final file content (full replacement):

```csharp
namespace LiveDeck.Core.Settings;

public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";

    // Printing
    public string? PrinterName { get; set; }
    public int LabelWidthMm  { get; set; } = 60;
    public int LabelHeightMm { get; set; } = 30;
    public int LabelGapMm    { get; set; } = 5;
    public string LabelFontFamily { get; set; } = "Arial";
    public int   LabelUserFontSize  { get; set; } = 14;
    public int   LabelMessageFontSize { get; set; } = 12;

    // Shortcuts (Phase 3b-1)
    public bool UseCustomShortcuts { get; set; } = false;

    /// <summary>Custom kısayol profili: command id → chord string. Null = henüz custom yok.</summary>
    public System.Collections.Generic.Dictionary<string, string>? CustomShortcuts { get; set; }
}
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 3: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: ~91/91 (no regression — new optional fields don't affect existing tests).

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Settings/AppSettings.cs
git commit -m "feat(settings): add UseCustomShortcuts + CustomShortcuts fields"
```

---

### Task 4: ShortcutRegistry (Core service, TDD)

**Files:**
- Create: `LiveDeck.Core/Shortcuts/ShortcutRegistry.cs`
- Create: `LiveDeck.Tests/Shortcuts/ShortcutRegistryTests.cs`

**Context:** Core service. `SettingsStore` üzerinden `AppSettings`'in `UseCustomShortcuts`/`CustomShortcuts` alanlarına bağlı. Defaults sabit kodlu (11 binding). Custom profil null veya boş ise `Defaults` kopyası. Conflict detection static helper.

`SettingsStore.Load()` her çağrıda diskten okur, yeni `AppSettings` instance döner. Registry boot'ta bir kez yükler ve in-memory tutar; `SaveCustom` hem disk hem in-memory güncellemeyi yapar.

- [ ] **Step 1: Write failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Shortcuts/ShortcutRegistryTests.cs`:

```csharp
using System.IO;
using System.Linq;
using FluentAssertions;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Shortcuts;
using Xunit;

namespace LiveDeck.Tests.Shortcuts;

public class ShortcutRegistryTests
{
    private static (ShortcutRegistry Registry, SettingsStore Store, string Path) Fx()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"livedeck-shortcut-test-{System.Guid.NewGuid():N}.json");
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
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ShortcutRegistryTests" 2>&1 | tail -10
```

Expected: compile errors — `ShortcutRegistry` not found.

- [ ] **Step 3: Implement ShortcutRegistry**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Shortcuts/ShortcutRegistry.cs`:

```csharp
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
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ShortcutRegistryTests" 2>&1 | tail -3
```

Expected: 9/9 pass.

- [ ] **Step 5: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: ~100/100 (87 + 4 KeyChord groups + 9 Registry).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Shortcuts/ShortcutRegistry.cs LiveDeck.Tests/Shortcuts/ShortcutRegistryTests.cs
git commit -m "feat(core): add ShortcutRegistry with defaults + custom profile + conflicts"
```

---

### Task 5: ShortcutBinder (App service)

**Files:**
- Create: `LiveDeck.App/Shortcuts/ShortcutBinder.cs`

**Context:** WPF-spesifik adapter. Registry'den binding'leri alıp `MainWindow.InputBindings`'e KeyBinding olarak yazar. `MainShellViewModel`'den ICommand'ları map'ler. Bilinmeyen Key string veya bilinmeyen command id durumunda log warning + skip. State'siz singleton.

`MainShellViewModel`'in `OpenShortcutHelpCommand` ve `DeleteSelectedFromQueueViaShortcutCommand` Task 9'da eklenecek — bu task derler ama o iki komut henüz yok. Geçici olarak `null` döner, Apply skip eder. Task 9 tamamlanınca rebind ile aktive olur.

- [ ] **Step 1: Create ShortcutBinder**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Shortcuts/ShortcutBinder.cs`:

```csharp
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
        ShortcutCommand.DeleteSelected   => GetOptional("DeleteSelectedFromQueueViaShortcutCommand"),
        ShortcutCommand.ClearQueue       => _shell.ClearQueueCommand,
        ShortcutCommand.StartStream      => _shell.StartStreamCommand,
        ShortcutCommand.EndStream        => _shell.EndStreamCommand,
        ShortcutCommand.StartGiveaway    => _shell.StartGiveawayCommand,
        ShortcutCommand.OpenShortcutHelp => GetOptional("OpenShortcutHelpCommand"),
        ShortcutCommand.OpenSettings     => _shell.OpenSettingsCommand,
        ShortcutCommand.OpenHistory      => _shell.OpenStreamHistoryCommand,
        ShortcutCommand.OpenBlacklist    => _shell.OpenBlacklistCommand,
        ShortcutCommand.OpenCustomers    => _shell.OpenCustomerSearchCommand,
        _ => null
    };

    /// <summary>
    /// Reflection-based lookup for commands that may not exist yet during incremental
    /// task execution. Once Task 9 lands, the named properties exist on MainShellViewModel
    /// and this returns the live ICommand. Until then, returns null and the binder skips.
    /// </summary>
    private ICommand? GetOptional(string propertyName)
    {
        var prop = _shell.GetType().GetProperty(propertyName);
        return prop?.GetValue(_shell) as ICommand;
    }

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
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors. (No DI registration yet; happens in Task 11.)

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Shortcuts/ShortcutBinder.cs
git commit -m "feat(app): add ShortcutBinder runtime InputBindings inşacısı"
```

---

### Task 6: ShortcutCaptureButton (custom WPF control)

**Files:**
- Create: `LiveDeck.App/Controls/ShortcutCaptureButton.cs`

**Context:** Click → "tuş bekleniyor" moduna geçer. PreviewKeyDown ilk non-modifier tuşu yakalar. Esc iptal, Backspace temizler, modifier-only basışlar yok sayılır. `Chord` DependencyProperty TwoWay.

- [ ] **Step 1: Create file**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Controls/ShortcutCaptureButton.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiveDeck.Core.Shortcuts;

namespace LiveDeck.App.Controls;

/// <summary>
/// Click → "tuş bekleniyor" moduna; bir sonraki KeyDown chord'u kaydeder
/// (modifier-only basışlar yok sayılır). Esc capture'ı iptal eder, Backspace temizler.
/// </summary>
public sealed class ShortcutCaptureButton : Button
{
    public static readonly DependencyProperty ChordProperty =
        DependencyProperty.Register(
            nameof(Chord), typeof(KeyChord), typeof(ShortcutCaptureButton),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnChordChanged));

    public KeyChord? Chord
    {
        get => (KeyChord?)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    private bool _capturing;
    private Brush? _originalBackground;

    public ShortcutCaptureButton()
    {
        Click += (_, _) => StartCapture();
        PreviewKeyDown += OnKeyDown;
        LostFocus += (_, _) => StopCapture();
        Loaded += (_, _) => UpdateLabel();
    }

    private static void OnChordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShortcutCaptureButton btn) btn.UpdateLabel();
    }

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        _originalBackground = Background;
        Content = "… bekleniyor (Esc)";
        Background = Brushes.DarkOrange;
        Focus();
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        Background = _originalBackground;
        UpdateLabel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        if (e.Key == Key.Escape) { StopCapture(); return; }
        if (e.Key == Key.Back)   { Chord = null; StopCapture(); return; }
        if (IsModifierOnly(e.Key)) return;

        var modifiers = ConvertModifiers(Keyboard.Modifiers);
        Chord = new KeyChord(modifiers, e.Key.ToString());
        StopCapture();
    }

    private void UpdateLabel()
    {
        if (_capturing) return;
        Content = Chord?.ToString() ?? "(atanmadı)";
    }

    private static bool IsModifierOnly(Key k) =>
        k is Key.LeftCtrl  or Key.RightCtrl
          or Key.LeftShift or Key.RightShift
          or Key.LeftAlt   or Key.RightAlt
          or Key.LWin      or Key.RWin
          or Key.System;

    private static KeyModifiers ConvertModifiers(ModifierKeys m)
    {
        var r = KeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control))  r |= KeyModifiers.Ctrl;
        if (m.HasFlag(ModifierKeys.Shift))    r |= KeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Alt))      r |= KeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Windows))  r |= KeyModifiers.Win;
        return r;
    }
}
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Controls/ShortcutCaptureButton.cs
git commit -m "feat(app): add ShortcutCaptureButton custom control with KeyChord binding"
```

---

### Task 7: ShortcutsTabViewModel + ShortcutEditRow

**Files:**
- Create: `LiveDeck.App/ViewModels/ShortcutsTabViewModel.cs`

**Context:** Settings dialog'unun yeni "Kısayollar" tab'ı için VM. UseCustom toggle reload tetikler (ama save'lemez). Save metodu çakışma kontrolü + `ShortcutBinder.Apply(MainWindow)` ile runtime rebind. `ResetToDefaults` confirm + reload. `ShortcutEditRow` her satırın chord'unu TwoWay binding'le tutar.

- [ ] **Step 1: Create file**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/ShortcutsTabViewModel.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.App.Shortcuts;
using LiveDeck.Core.Shortcuts;

namespace LiveDeck.App.ViewModels;

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
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/ShortcutsTabViewModel.cs
git commit -m "feat(app): add ShortcutsTabViewModel + ShortcutEditRow"
```

---

### Task 8: SettingsDialog "Kısayollar" tab + SettingsViewModel property

**Files:**
- Modify: `LiveDeck.App/ViewModels/SettingsViewModel.cs`
- Modify: `LiveDeck.App/Views/SettingsDialog.xaml`

**Context:** SettingsViewModel'e `ShortcutsTab` property'si DI'dan alır. SettingsDialog.xaml'a 3. TabItem (Yazıcı, OBS, Kısayollar). Capture button XAML'de `controls:ShortcutCaptureButton` olarak referans, namespace import gerekir.

- [ ] **Step 1: Add ShortcutsTab to SettingsViewModel**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/SettingsViewModel.cs`. Add a public property and ctor parameter.

Find the constructor:
```csharp
public SettingsViewModel(AppSettings settings, SettingsStore store)
{
    _liveSettings = settings;
    _store = store;
    _originalOverlayPort = settings.OverlayPort;

    LoadFromSettings();
    LoadInstalledPrinters();
    LoadInstalledFonts();
}
```

Replace with:
```csharp
public ShortcutsTabViewModel ShortcutsTab { get; }

public SettingsViewModel(AppSettings settings, SettingsStore store, ShortcutsTabViewModel shortcutsTab)
{
    _liveSettings = settings;
    _store = store;
    _originalOverlayPort = settings.OverlayPort;
    ShortcutsTab = shortcutsTab;

    LoadFromSettings();
    LoadInstalledPrinters();
    LoadInstalledFonts();
}
```

- [ ] **Step 2: Add Kısayollar tab to SettingsDialog.xaml**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/SettingsDialog.xaml`.

Add the controls namespace to the root `<Window>` element. Find the opening tag and add `xmlns:controls="clr-namespace:LiveDeck.App.Controls"`. Final root tag:

```xml
<Window x:Class="LiveDeck.App.Views.SettingsDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="clr-namespace:LiveDeck.App.Controls"
        Title="Ayarlar" Width="540" Height="540"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
```

Inside the existing `<TabControl>`, after the OBS tab's closing `</TabItem>`, add a new TabItem (just before the TabControl's closing `</TabControl>`):

```xml
<TabItem Header="Kısayollar">
    <DockPanel Margin="12">
        <CheckBox DockPanel.Dock="Top"
                  Content="Özel kısayolları kullan"
                  IsChecked="{Binding ShortcutsTab.UseCustom}"
                  Foreground="White"
                  Margin="0,0,0,8"/>

        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,12,0,0">
            <Button Content="Kaydet"
                    Command="{Binding ShortcutsTab.SaveCommand}"
                    Padding="12,6" FontWeight="Bold"/>
            <Button Content="Varsayılana Sıfırla"
                    Command="{Binding ShortcutsTab.ResetToDefaultsCommand}"
                    Padding="12,6" Margin="8,0,0,0"
                    Foreground="#FFFF6666"/>
        </StackPanel>

        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding ShortcutsTab.Rows}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="200"/>
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="{Binding DisplayName}"
                                       VerticalAlignment="Center"
                                       Foreground="White"/>
                            <controls:ShortcutCaptureButton Grid.Column="1"
                                Chord="{Binding Chord, Mode=TwoWay}"
                                IsEnabled="{Binding DataContext.ShortcutsTab.UseCustom,
                                                     RelativeSource={RelativeSource AncestorType=Window}}"
                                Padding="8,4"/>
                        </Grid>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
    </DockPanel>
</TabItem>
```

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors. (Solution still won't run because AppHost DI for ShortcutsTabViewModel + new ctor parameter doesn't exist yet — Task 11 wires it.)

If build fails because `SettingsViewModel` is resolved by DI somewhere with the old signature, that's expected; Task 11 fixes it. Otherwise, the build fails noisily here and tells us we need Task 11 immediately. **For incremental safety:** add a temporary parameterless overload that throws to keep the existing DI working until Task 11. Skip if build is clean as-is.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/SettingsViewModel.cs LiveDeck.App/Views/SettingsDialog.xaml
git commit -m "feat(app): add Kısayollar tab to SettingsDialog"
```

---

### Task 9: MainShellViewModel new commands + SelectedQueueItem

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs`
- Modify: `LiveDeck.App/Views/MainShellView.xaml`

**Context:** ShortcutBinder'ın ihtiyaç duyduğu iki yeni komut ve TwoWay binding için `SelectedQueueItem` property. Mevcut `RemoveSelectedFromQueueCommand` button'dan `CommandParameter`'la çalışıyor (kuyruğun seçili öğesi); yeni `DeleteSelectedFromQueueViaShortcutCommand` parametresiz çalışır ve VM'in property'sini kullanır.

- [ ] **Step 1: Add SelectedQueueItem + 2 commands to MainShellViewModel**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`. Find the section near other ObservableProperty declarations (around line 36-44 where `_canStartGiveaway` is declared). Add:

```csharp
[ObservableProperty] private LabelViewModel? _selectedQueueItem;
```

Find the `[RelayCommand] private void OpenCustomerSearch()` method. Add two new commands just below it (or anywhere among the RelayCommands; after `OpenCustomerSearch` is conventional):

```csharp
[RelayCommand]
private void DeleteSelectedFromQueueViaShortcut()
{
    if (SelectedQueueItem is null) return;
    _labels.Delete(SelectedQueueItem.Id);
    PrintQueue.Remove(SelectedQueueItem);
    SelectedQueueItem = null;
}

[RelayCommand]
private void OpenShortcutHelp()
{
    var dlg = App.Host.Services.GetRequiredService<Views.ShortcutHelpDialog>();
    dlg.Owner = Application.Current?.MainWindow;
    dlg.ShowDialog();
}
```

- [ ] **Step 2: Bind QueueList SelectedItem in MainShellView.xaml**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml`. Find the QueueList ListBox declaration:

```xml
<ListBox Grid.Row="1"
         x:Name="QueueList"
         ItemsSource="{Binding PrintQueue}"
```

Add `SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"`:

```xml
<ListBox Grid.Row="1"
         x:Name="QueueList"
         ItemsSource="{Binding PrintQueue}"
         SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"
```

The existing `Seçileni Sil` button still uses `CommandParameter="{Binding ElementName=QueueList, Path=SelectedItem}"` — keep that as-is. Both paths (button and shortcut) end up working on the same row; they don't conflict.

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 4: Run full suite (regression)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: ~100/100. No regression on existing tests.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs LiveDeck.App/Views/MainShellView.xaml
git commit -m "feat(app): add SelectedQueueItem + DeleteSelectedFromQueueViaShortcut + OpenShortcutHelp"
```

---

### Task 10: ShortcutHelpDialog (F1)

**Files:**
- Create: `LiveDeck.App/Views/ShortcutHelpDialog.xaml`
- Create: `LiveDeck.App/Views/ShortcutHelpDialog.xaml.cs`

**Context:** F1 ile açılan küçük modal. ShortcutRegistry.GetActive() üzerinden tabloyu render eder. VM yok — code-behind'da projection yapılır.

- [ ] **Step 1: Create XAML**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/ShortcutHelpDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.ShortcutHelpDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Kısayollar" Width="420" Height="440"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Aktif Kısayollar" FontSize="18" FontWeight="Bold"
                   Margin="0,0,0,12"/>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Items}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="160"/>
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="{Binding DisplayName}"
                                       Foreground="White"/>
                            <TextBlock Grid.Column="1" Text="{Binding ChordText}"
                                       Foreground="#FFFFD166" FontFamily="Consolas"
                                       HorizontalAlignment="Right"/>
                        </Grid>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>

        <Button Grid.Row="2" Content="Kapat" Click="OnClose" Padding="14,6"
                HorizontalAlignment="Right" Margin="0,12,0,0"
                IsCancel="True" IsDefault="True"/>
    </Grid>
</Window>
```

- [ ] **Step 2: Create code-behind**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/ShortcutHelpDialog.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LiveDeck.Core.Shortcuts;

namespace LiveDeck.App.Views;

public partial class ShortcutHelpDialog : Window
{
    public ShortcutHelpDialog(ShortcutRegistry registry)
    {
        InitializeComponent();
        DataContext = new
        {
            Items = registry.GetActive()
                .Select(b => new
                {
                    DisplayName = ShortcutCommand.DisplayNames.TryGetValue(b.CommandId, out var n) ? n : b.CommandId,
                    ChordText = b.Chord.ToString()
                })
                .ToList()
        };
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
```

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Views/ShortcutHelpDialog.xaml LiveDeck.App/Views/ShortcutHelpDialog.xaml.cs
git commit -m "feat(app): add ShortcutHelpDialog (F1) with active shortcut table"
```

---

### Task 11: AppHost DI + MainWindow.Loaded hook

**Files:**
- Modify: `LiveDeck.App/AppHost.cs`
- Modify: `LiveDeck.App/MainWindow.xaml.cs`

**Context:** DI'ya yeni servisleri kaydet, MainWindow Loaded event'inde binder'ı uygula.

- [ ] **Step 1: Register services in AppHost**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs`. Add these usings at the top if missing:

```csharp
using LiveDeck.App.Shortcuts;
using LiveDeck.Core.Shortcuts;
```

Locate the existing block of `services.AddSingleton<...>` and `services.AddTransient<...>` registrations. Add a new block (before `Services = services.BuildServiceProvider();`):

```csharp
// Shortcuts (Phase 3b-1)
services.AddSingleton<ShortcutRegistry>();
services.AddSingleton<ShortcutBinder>();
services.AddTransient<ViewModels.ShortcutsTabViewModel>();
services.AddTransient<Views.ShortcutHelpDialog>();
```

- [ ] **Step 2: Hook MainWindow.Loaded**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/MainWindow.xaml.cs`. Replace the file contents:

```csharp
using System.ComponentModel;
using System.Windows;
using LiveDeck.App.Shortcuts;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var binder = App.Host.Services.GetRequiredService<ShortcutBinder>();
        binder.Apply(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // If a giveaway is active, refuse the close and tell the user to finish/cancel it
        // first — the regular EndStream path has the same gate.
        var vm = App.Host.Services.GetService<MainShellViewModel>();
        if (vm is not null && vm.IsGiveawayActive)
        {
            MessageBox.Show(
                "Aktif çekiliş var. Önce çekilişi tamamla veya iptal et.",
                "Çekiliş aktif",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
```

- [ ] **Step 3: Build whole solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 4: Run full test suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: ~100/100 pass.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/AppHost.cs LiveDeck.App/MainWindow.xaml.cs
git commit -m "feat(app): wire ShortcutRegistry/Binder/Help in DI + MainWindow.Loaded apply"
```

---

### Task 12: Manual Acceptance Smoke

**Files:** None — execute and observe.

**Context:** 11 default kısayolu tek tek dene + özelleştirme + çakışma uyarısı + persistence.

- [ ] **Step 1: Start app cleanly**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```

App opens without errors.

- [ ] **Step 2: Default kısayolları test et**

Sırayla aşağıdaki tuşları dene ve her birinin doğru aksiyona yol açtığını doğrula:

| Tuş | Beklenen |
|---|---|
| F1 | Kısayollar dialog açılır, 11 satır listelenir |
| F2 | Ayarlar açılır |
| F3 | Yayın geçmişi açılır |
| F4 | Kara liste açılır |
| F5 | Müşteriler arama dialog'u açılır |
| Ctrl+Shift+S | Yayın başlar |
| Ctrl+G | Çekiliş dialog'u açılır (yayın aktifse) |
| Ctrl+P | Yazdır komutu (kuyruk boşsa no-op) |
| Delete | Kuyrukta seçili etiket varsa siler |
| Ctrl+Shift+Delete | Hepsini temizle (onay sorar) |
| Ctrl+Shift+E | Yayını bitir (onay sorar) |

- [ ] **Step 3: Özelleştirme**

- F2 → Ayarlar → Kısayollar tab'ı.
- "Özel kısayolları kullan" checkbox'ı tikle. Capture button'lar enable olur.
- "Yazdır" satırının capture button'ına bas → "… bekleniyor" görünür.
- Ctrl+Alt+P bas. Button "Ctrl+Alt+P" gösterir.
- Kaydet butonuna bas. "Kısayollar kaydedildi." mesajı görünür, kapan.
- Ana pencerede Ctrl+P artık çalışmaz; Ctrl+Alt+P "Yazdır"ı tetikler.
- F1 → yardım dialog'u "Yazdır → Ctrl+Alt+P" gösterir.

- [ ] **Step 4: Çakışma uyarısı**

- F2 → Kısayollar → "Yazdır"a F2 ata (Ayarlar ile çakışır).
- Kaydet → MessageBox: "Aynı kombinasyona sahip komutlar var: Yazdır ↔ Ayarlar".
- Çakışmayı düzelt (Yazdır'a Ctrl+P geri ata).
- Kaydet → başarı mesajı.

- [ ] **Step 5: Sıfırla**

- F2 → Kısayollar → "Varsayılana Sıfırla" → onay → tüm chord'lar default'a döner.
- Kaydet → ana pencerede default kısayollar tekrar çalışır.

- [ ] **Step 6: Persistence**

- Uygulamayı kapat.
- Tekrar aç.
- F2 → Kısayollar tab'ı: UseCustom flag ve Custom profil korunmuş.
- Ana pencerede aktif profil (default veya custom) çalışıyor.

- [ ] **Step 7: Commit smoke log (optional)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add docs/smoke-tests/2026-04-28-phase-3b-1-smoke.md
git commit -m "docs: phase 3b-1 shortcut system smoke test results"
```

---

## Self-Review

**Spec coverage check:**

| Spec section | Plan task |
|---|---|
| §3.1 ShortcutCommand | Task 2 |
| §3.2 KeyChord (Parse/ToString/TryParse) | Task 1 |
| §3.3 ShortcutBinding + ShortcutRegistry (defaults, custom, conflicts, persistence) | Tasks 2 + 4 |
| §3.4 AppSettings genişletmesi | Task 3 |
| §3.5 ShortcutBinder | Task 5 |
| §3.6 MainShellViewModel commands (DeleteSelectedFromQueueViaShortcut, OpenShortcutHelp, SelectedQueueItem) | Task 9 |
| §3.7 ShortcutCaptureButton | Task 6 |
| §3.8 ShortcutsTabViewModel | Task 7 |
| §3.9 SettingsDialog Kısayollar tab + SettingsViewModel.ShortcutsTab | Task 8 |
| §3.10 ShortcutHelpDialog | Task 10 |
| §3.11 MainWindow Loaded hook | Task 11 |
| §3.12 AppHost DI | Task 11 |
| §4 Hata yönetimi | Distributed (corrupt JSON in Task 4 LoadCustom; modifier-only/Esc/Backspace in Task 6; conflict warning in Task 7; binder skip in Task 5) |
| §5 Test stratejisi (~10 yeni test) | Tasks 1 (4 theory groups → ~14 tests) + Task 4 (9 tests) |
| §8 Kabul kriterleri (manuel smoke 11/11) | Task 12 |

All sections covered.

**Placeholder scan:** Searched for "TBD", "TODO", "implement later", "fill in", "appropriate", "similar to". None found. Every code step has actual code. Task 8 Step 3 mentions "If build fails" with a specific recovery path — this is genuine optional handling, not a placeholder.

**Type consistency check:**

- `KeyChord(KeyModifiers Modifiers, string Key)` — defined Task 1, used in Tasks 2, 4, 5, 6, 7. Consistent.
- `ShortcutBinding(string CommandId, KeyChord Chord)` — defined Task 2, used in Tasks 4, 5, 7. Consistent.
- `ShortcutCommand` constants — defined Task 2 with exact 11 string IDs; same IDs used in `ShortcutRegistry.BuildDefaults` (Task 4) and `ShortcutBinder.GetCommand` (Task 5). Consistent.
- `ShortcutRegistry` API: `Defaults`, `UseCustom`, `GetActive()`, `GetCustom()`, `SaveCustom(IReadOnlyList<ShortcutBinding>, bool)`, `ResetCustomToDefaults()`, `FindConflicts(...)` — defined Task 4, used Tasks 5, 7, 10. Consistent.
- `ShortcutBinder.Apply(Window)` — defined Task 5, used Tasks 7 (rebind on save) + 11 (initial apply). Consistent.
- `ShortcutsTabViewModel.Rows / UseCustom / SaveCommand / ResetToDefaultsCommand` — defined Task 7, bound by Task 8 XAML. Consistent.
- `ShortcutEditRow.CommandId / DisplayName / Chord` — defined Task 7, bound by Task 8 XAML. Consistent.
- `MainShellViewModel.SelectedQueueItem`, `DeleteSelectedFromQueueViaShortcutCommand`, `OpenShortcutHelpCommand` — defined Task 9, consumed by Task 5 (`GetOptional` reflection lookup) and Task 9 (XAML SelectedItem binding). Consistent.
- `ShortcutHelpDialog(ShortcutRegistry)` ctor — defined Task 10, resolved via DI in Task 11 AppHost registration. Consistent.

All consistent.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-28-phase-3b-1-shortcut-system.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Hangisi?
