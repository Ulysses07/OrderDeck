# Faz 3b-1 — Kısayol Sistem Yöneticisi (Tasarım)

**Hedef:** 11 komutu kapsayan özelleştirilebilir klavye kısayol sistemi. Default profil sabit, kullanıcı Özel profili düzenleyebilir, çakışma tespiti, F1 yardım dialog'u.

**Kapsam:** Core domain (ShortcutCommand/KeyChord/ShortcutRegistry), App katmanı (ShortcutCaptureButton + ShortcutBinder + Settings tab + F1 dialog), AppSettings persistence.

**Pre-Faz-3b-1 state:** Faz 3a HEAD `7eb4ae0`. 87/87 test geçer.

---

## 1. Bağlam

**Sorun 1 — kısayol yok:** Yayıncı yayın sırasında kuyruktan etiket silmek, yazdırmak, çekiliş başlatmak için fareye uzanmak zorunda. Tek elin sürekli klavyede olduğu workflow'da bu yorucu.

**Sorun 2 — basit hardcoded kısayollar yetmez:** Kullanıcılar kendi alıştıkları tuşlara mahkum olmak istemez (örn. Ctrl+P bazıları için "ana kayıt" anlamına gelir). Özelleştirilebilir bir sistem gerek.

**Sorun 3 — mevcut altyapı yok:** WPF `InputBinding` mekanizması statik tanımlı. Runtime'da yeniden bağlanabilen, persistence'lı bir wrapper gerek.

---

## 2. Mimari

Yeni proje yok. Yeni dış bağımlılık yok. Mevcut katmanlara aşağıdaki dosyalar eklenir:

| Katman | Dosya | Eylem |
|---|---|---|
| Domain | `LiveDeck.Core/Shortcuts/ShortcutCommand.cs` | Yeni — komut ID sabitleri + DisplayName map |
| Domain | `LiveDeck.Core/Shortcuts/KeyChord.cs` | Yeni — `(Modifiers, Key)` record + Parse/ToString |
| Domain | `LiveDeck.Core/Shortcuts/ShortcutBinding.cs` | Yeni — `(CommandId, KeyChord)` record |
| Domain | `LiveDeck.Core/Shortcuts/ShortcutRegistry.cs` | Yeni — defaults + custom + persistence + conflict |
| Settings | `LiveDeck.Core/Settings/AppSettings.cs` | Modify — `UseCustomShortcuts`, `CustomShortcuts` alanları |
| App — control | `LiveDeck.App/Controls/ShortcutCaptureButton.cs` | Yeni — KeyDown yakalayan custom Button |
| App — service | `LiveDeck.App/Shortcuts/ShortcutBinder.cs` | Yeni — registry → MainWindow.InputBindings |
| App — VM | `LiveDeck.App/ViewModels/ShortcutsTabViewModel.cs` | Yeni — Settings tab'ı için VM + ShortcutEditRow |
| App — view | `LiveDeck.App/Views/SettingsDialog.xaml` | Modify — yeni "Kısayollar" tab'ı |
| App — VM | `LiveDeck.App/ViewModels/SettingsViewModel.cs` | Modify — `ShortcutsTab` property |
| App — F1 | `LiveDeck.App/Views/ShortcutHelpDialog.xaml` + `.xaml.cs` | Yeni — referans tablosu |
| App — main | `LiveDeck.App/MainWindow.xaml.cs` | Modify — Loaded event'inde `ShortcutBinder.Apply(this)` |
| App — main | `LiveDeck.App/MainWindow.xaml` | Modify — boş `<Window.InputBindings>` placeholder |
| App — VM | `LiveDeck.App/ViewModels/MainShellViewModel.cs` | Modify — yeni `OpenShortcutHelp` + `DeleteSelectedFromQueueViaShortcut` commands + `SelectedQueueItem` ObservableProperty |
| App — view | `LiveDeck.App/Views/MainShellView.xaml` | Modify — QueueList SelectedItem two-way bind |
| AppHost | `LiveDeck.App/AppHost.cs` | Modify — `ShortcutRegistry`, `ShortcutBinder`, `ShortcutsTabViewModel`, `ShortcutHelpDialog` kayıtları |
| Tests | `LiveDeck.Tests/Shortcuts/KeyChordTests.cs` | Yeni — Parse/TryParse/ToString round-trip |
| Tests | `LiveDeck.Tests/Shortcuts/ShortcutRegistryTests.cs` | Yeni — defaults, save/load, conflict, reset |

**Dosya sayısı:** 13 yeni, 6 modify.

---

## 3. Detaylar

### 3.1 ShortcutCommand (Core)

```csharp
namespace LiveDeck.Core.Shortcuts;

/// <summary>Bilinen komut ID'lerinin canonical listesi. Yeni komut eklemek için:
/// 1) yeni const ekle, 2) DisplayNames'e ekle, 3) ShortcutRegistry.BuildDefaults'a ekle,
/// 4) ShortcutBinder.GetCommand'a yeni vaka ekle.</summary>
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

    /// <summary>UI'da gösterilecek Türkçe başlıklar. Bilinmeyen ID için fallback yok.</summary>
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

### 3.2 KeyChord (Core)

```csharp
namespace LiveDeck.Core.Shortcuts;

[Flags]
public enum KeyModifiers
{
    None  = 0,
    Ctrl  = 1,
    Shift = 2,
    Alt   = 4,
    Win   = 8
}

/// <summary>
/// WPF-bağımsız tuş kombinasyonu. JSON'da string olarak persist edilir
/// ("Ctrl+Shift+P", "F1", "Delete").
/// </summary>
public sealed record KeyChord(KeyModifiers Modifiers, string Key)
{
    public override string ToString();              // "Ctrl+Shift+P"
    public static KeyChord Parse(string input);     // throws FormatException
    public static bool TryParse(string input, out KeyChord chord);
}
```

`ToString` modifier sırası: `Ctrl, Shift, Alt, Win`. Parse case-insensitive, `+` ayraç. Whitespace tolere edilir. Boş key → `FormatException`.

### 3.3 ShortcutBinding + ShortcutRegistry (Core)

```csharp
public sealed record ShortcutBinding(string CommandId, KeyChord Chord);

public sealed class ShortcutRegistry
{
    private readonly SettingsStore _settings;

    public IReadOnlyList<ShortcutBinding> Defaults { get; }   // sabit, salt-okunur
    public bool UseCustom { get; private set; }

    public ShortcutRegistry(SettingsStore settings);

    public IReadOnlyList<ShortcutBinding> GetActive();        // UseCustom ? Custom : Defaults
    public IReadOnlyList<ShortcutBinding> GetCustom();        // her zaman custom (UseCustom'tan bağımsız)

    public void SaveCustom(IReadOnlyList<ShortcutBinding> bindings, bool useCustom);
    public void ResetCustomToDefaults();

    /// <summary>Aynı KeyChord'u kullanan komut çiftleri. Boş = çakışma yok.</summary>
    public static IReadOnlyList<(string CommandIdA, string CommandIdB)> FindConflicts(
        IReadOnlyList<ShortcutBinding> bindings);

    private static IReadOnlyList<ShortcutBinding> BuildDefaults();
}
```

**Defaults tablosu (sabit kodlanmış):**

| CommandId | KeyChord |
|---|---|
| `print` | `Ctrl+P` |
| `delete-selected` | `Delete` |
| `clear-queue` | `Ctrl+Shift+Delete` |
| `start-stream` | `Ctrl+Shift+S` |
| `end-stream` | `Ctrl+Shift+E` |
| `start-giveaway` | `Ctrl+G` |
| `open-shortcut-help` | `F1` |
| `open-settings` | `F2` |
| `open-history` | `F3` |
| `open-blacklist` | `F4` |
| `open-customers` | `F5` |

**Persistence:**
- `AppSettings.UseCustomShortcuts: bool` (default `false`)
- `AppSettings.CustomShortcuts: Dictionary<string,string>?` (key = command id, value = chord string)
- `SaveCustom` → `_settings.Save()` ile diske JSON yazılır
- Boot'ta `LoadCustom` → corrupt JSON ya da unparseable chord → o entry skip + log warning, registry boot'a devam eder

### 3.4 AppSettings genişletmesi

```csharp
public sealed record AppSettings(
    /* mevcut alanlar... */,
    bool UseCustomShortcuts = false,
    Dictionary<string, string>? CustomShortcuts = null);
```

`SettingsStore.Load()` mevcut JSON'u okur; eski sürüm dosyası (alanlar yoksa) default değerlerle çalışır — geriye uyumluluk garanti.

### 3.5 ShortcutBinder (App)

```csharp
namespace LiveDeck.App.Shortcuts;

public sealed class ShortcutBinder
{
    private readonly ShortcutRegistry _registry;
    private readonly MainShellViewModel _shell;

    public ShortcutBinder(ShortcutRegistry registry, MainShellViewModel shell);

    /// <summary>Window'un InputBindings koleksiyonunu temizler ve registry.GetActive()'e göre yeniden inşa eder.</summary>
    public void Apply(Window window);

    private System.Windows.Input.ICommand? GetCommand(string commandId);
}
```

`GetCommand` switch:
- `print` → `_shell.PrintCommand`
- `delete-selected` → `_shell.DeleteSelectedFromQueueViaShortcutCommand` (yeni; aşağıda)
- `clear-queue` → `_shell.ClearQueueCommand`
- `start-stream` → `_shell.StartStreamCommand`
- `end-stream` → `_shell.EndStreamCommand`
- `start-giveaway` → `_shell.StartGiveawayCommand`
- `open-shortcut-help` → `_shell.OpenShortcutHelpCommand` (yeni)
- `open-settings` → `_shell.OpenSettingsCommand`
- `open-history` → `_shell.OpenStreamHistoryCommand`
- `open-blacklist` → `_shell.OpenBlacklistCommand`
- `open-customers` → `_shell.OpenCustomerSearchCommand`
- _ → null (skip)

`Apply`'da her binding için:
```csharp
if (!Enum.TryParse<Key>(b.Chord.Key, ignoreCase: true, out var k)) { log warn; continue; }
var cmd = GetCommand(b.CommandId);
if (cmd is null) { log warn; continue; }
window.InputBindings.Add(new KeyBinding(cmd, k, ConvertModifiers(b.Chord.Modifiers)));
```

### 3.6 MainShellViewModel değişiklikleri

```csharp
[ObservableProperty] private LabelViewModel? _selectedQueueItem;

[RelayCommand]
private void DeleteSelectedFromQueueViaShortcut()
{
    if (SelectedQueueItem is null) return;
    _labels.Delete(SelectedQueueItem.Id);
    PrintQueue.Remove(SelectedQueueItem);
}

[RelayCommand]
private void OpenShortcutHelp()
{
    var dlg = App.Host.Services.GetRequiredService<Views.ShortcutHelpDialog>();
    dlg.Owner = Application.Current?.MainWindow;
    dlg.ShowDialog();
}
```

`MainShellView.xaml` QueueList: `SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"` eklenir. Mevcut `RemoveSelectedFromQueueCommand` `CommandParameter="{Binding ElementName=QueueList, Path=SelectedItem}"` aynen kalır (button'dan çağrılıyor); yeni shortcut command parametresiz çalışır (VM'in property'sini kullanır).

### 3.7 ShortcutCaptureButton (App)

WPF custom Button. Click → "tuş bekleniyor" moduna; PreviewKeyDown ilk non-modifier basışı yakalar; `Chord` DependencyProperty'si TwoWay binding ile güncellenir.

- Esc → capture iptal (chord değişmez)
- Backspace → chord null'a (boş)
- Modifier-only tuş (Ctrl, Shift, Alt, Win) → yok say
- LostFocus → capture iptal

Etiket içeriği:
- Capture modunda: `"… bekleniyor (Esc)"`, sarı arka plan
- Normal modda: `Chord?.ToString() ?? "(atanmadı)"`

### 3.8 ShortcutsTabViewModel + Settings tab

```csharp
public sealed partial class ShortcutsTabViewModel : ViewModelBase
{
    private readonly ShortcutRegistry _registry;
    private readonly ShortcutBinder _binder;       // Save sonrası rebind için

    [ObservableProperty] private bool _useCustom;
    public ObservableCollection<ShortcutEditRow> Rows { get; } = new();

    public ShortcutsTabViewModel(ShortcutRegistry registry, ShortcutBinder binder);

    [RelayCommand] private void Save();
    [RelayCommand] private void ResetToDefaults();

    partial void OnUseCustomChanged(bool value);
}

public sealed partial class ShortcutEditRow : ObservableObject
{
    public string CommandId { get; }
    public string DisplayName { get; }
    [ObservableProperty] private KeyChord? _chord;
}
```

`Save`:
1. UseCustom=true ise: `Rows`'tan `bindings` listesi oluştur (null chord'lar dışarıda); `FindConflicts` → çakışma varsa MessageBox uyarı + iptal
2. `registry.SaveCustom(bindings, useCustom)`
3. `binder.Apply(Application.Current.MainWindow)` — runtime rebind

`ResetToDefaults`: Confirm dialog → `registry.ResetCustomToDefaults()` → reload Rows + binder rebind.

`OnUseCustomChanged`: Rows'u GetActive() (custom veya defaults) ile yeniden doldur. **Burada save yapılmaz** — sadece UI yenilenmesi; user "Kaydet" basana kadar persistence değişmez.

### 3.9 SettingsDialog "Kısayollar" tab'ı

```xml
<TabItem Header="Kısayollar">
  <DockPanel Margin="12">
    <CheckBox DockPanel.Dock="Top"
              Content="Özel kısayolları kullan"
              IsChecked="{Binding ShortcutsTab.UseCustom}"
              Margin="0,0,0,8"/>
    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,12,0,0">
      <Button Content="Kaydet"
              Command="{Binding ShortcutsTab.SaveCommand}"
              Padding="12,6" FontWeight="Bold"/>
      <Button Content="Varsayılana Sıfırla"
              Command="{Binding ShortcutsTab.ResetToDefaultsCommand}"
              Padding="12,6" Margin="8,0,0,0"/>
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
              <TextBlock Grid.Column="0" Text="{Binding DisplayName}" VerticalAlignment="Center"/>
              <controls:ShortcutCaptureButton
                  Grid.Column="1" Chord="{Binding Chord, Mode=TwoWay}"
                  IsEnabled="{Binding DataContext.ShortcutsTab.UseCustom,
                                       RelativeSource={RelativeSource AncestorType=Window}}"/>
            </Grid>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>
  </DockPanel>
</TabItem>
```

### 3.10 ShortcutHelpDialog (F1)

Küçük modal: aktif kısayolları liste halinde gösterir. ItemsControl `registry.GetActive()` üzerinden:

```
┌─────────────────────────────────────┐
│ Aktif Kısayollar                    │
├─────────────────────────────────────┤
│ Yazdır                  Ctrl+P      │
│ Seçili etiketi sil      Delete      │
│ ...                                 │
├─────────────────────────────────────┤
│                            [Kapat]  │
└─────────────────────────────────────┘
```

VM yok — code-behind constructor'da `registry.GetActive()` projection'u DataContext olur.

### 3.11 MainWindow değişiklikleri

```csharp
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

    // mevcut OnClosing aynen kalır
}
```

`MainWindow.xaml`'a `<Window.InputBindings/>` placeholder eklemek opsiyonel — boş zaten.

### 3.12 AppHost DI

```csharp
// Shortcuts (Phase 3b-1)
services.AddSingleton<ShortcutRegistry>();
services.AddSingleton<ShortcutBinder>();
services.AddTransient<ViewModels.ShortcutsTabViewModel>();
services.AddTransient<Views.ShortcutHelpDialog>();
```

`ShortcutRegistry` singleton (settings cache); `ShortcutBinder` singleton (state'siz, sadece `_registry` + `_shell` ref'leri).

`SettingsViewModel` ctor `ShortcutsTabViewModel` parametresi alır (Settings dialog açıldığında hazır).

---

## 4. Hata Yönetimi

| Senaryo | Davranış |
|---|---|
| Boot'ta corrupt JSON `CustomShortcuts` | Try/catch — defaults yüklenir, log warning |
| Boot'ta unparseable chord (örn. `"Foo+Bar"`) | O entry skip, geri kalanlar yüklenir, log warning |
| Capture button modifier-only basış | Yok sayılır, capture sürer |
| Capture button Esc | Capture iptal, chord değişmez |
| Capture button Backspace | `Chord = null`, "(atanmadı)" |
| Capture button focus kaybı | Capture iptal (LostFocus) |
| Save'de chord null olan komut | Filtrelenir, o komut runtime'da kısayolsuz olur |
| Save'de iki komut aynı KeyChord | MessageBox uyarı + Save iptal |
| Binder bilinmeyen Key string (`Enum.TryParse<Key>` fail) | Skip + log warning |
| Binder bilinmeyen command id | Skip + log warning |
| Settings tab `UseCustom = false`, capture button | `IsEnabled=false`, kullanıcı düzenleyemez |
| Multiple Settings dialog open (impossible — modal) | n/a |
| F1 basılı tutulduğunda spam dialog | WPF `KeyBinding` repeat'i throttle etmez; pratik problem değil çünkü dialog modal — ikinci F1 yok sayılır |

---

## 5. Test Stratejisi

### 5.1 Yeni unit testler

| Test | Dosya |
|---|---|
| `KeyChord_round_trips_through_parse_and_tostring` | `KeyChordTests.cs` |
| `KeyChord_parse_is_case_insensitive_and_tolerates_whitespace` | `KeyChordTests.cs` |
| `KeyChord_parse_rejects_empty_or_modifier_only` | `KeyChordTests.cs` |
| `KeyChord_tostring_uses_canonical_modifier_order` | `KeyChordTests.cs` |
| `Registry_returns_defaults_when_UseCustom_false` | `ShortcutRegistryTests.cs` |
| `Registry_returns_custom_when_UseCustom_true` | `ShortcutRegistryTests.cs` |
| `Registry_SaveCustom_persists_to_AppSettings` | `ShortcutRegistryTests.cs` |
| `Registry_ResetCustomToDefaults_overwrites_custom_with_defaults` | `ShortcutRegistryTests.cs` |
| `FindConflicts_returns_pairs_with_same_chord` | `ShortcutRegistryTests.cs` |
| `FindConflicts_empty_when_all_unique` | `ShortcutRegistryTests.cs` |

~10 yeni test. 87 → ~97.

### 5.2 Test scope dışı

- WPF `ShortcutCaptureButton` davranışı (UI control, manuel test)
- `ShortcutBinder.Apply` sonrası KeyBinding'in gerçekten tetiklendiği (manuel test)
- Settings tab UI render/binding (manuel test)

### 5.3 Manuel kabul testi

| # | Senaryo |
|---|---|
| 1 | Default profille başlat → 11 kısayol çalışır (her birini test et) |
| 2 | F1 → yardım dialog'u tabloyu gösterir |
| 3 | F2 → Ayarlar → Kısayollar tab'ında 11 satır default chord'larla |
| 4 | "Özel kısayolları kullan" tikle → capture button'lar enable olur |
| 5 | Bir capture button → Ctrl+Alt+P → chord güncellenir |
| 6 | İki komuta aynı chord ata → Kaydet → MessageBox çakışma uyarısı |
| 7 | Çakışmayı düzelt → Kaydet → kapatıldı, F1 yardım dialog'u yeni chord'u gösterir |
| 8 | Yeni chord ile o komut çalışır |
| 9 | Eski default chord o komut için ARTIK çalışmaz |
| 10 | "Varsayılana Sıfırla" → confirm → reload → tüm chord'lar default'a döner |
| 11 | Uygulamayı kapat + aç → custom profile + UseCustom flag persist olmuş |

---

## 6. YAGNI (yapılmayanlar)

- Sınırsız profil + import/export (sadece Default + Custom)
- Context-sensitive routing (hepsi MainWindow global)
- Sistem-genişliğinde global hotkey (sadece app focus iken)
- Çakışma otomatik çözüm (sadece warning, save block)
- Kısayolu disable etme (chord null = disable, ek UI yok)
- Mouse button bindings (sadece klavye)
- Chord sequence (Ctrl+K Ctrl+P gibi VS Code style; tek kombinasyon yeterli)
- Per-locale farklı default'lar (tek default set)

---

## 7. Phase 3b-1 Sonrası Açık Bırakılanlar

- **Phase 3b-2:** Kuyrukta toplu seçim (SelectionMode=Extended) + multi-delete + ek kısayollar (Ctrl+A, Enter chat→queue)
- **Phase 3c:** Stream Deck SDK (komut envanterini yeniden kullanır)
- **İleride:** Multi-profile, import/export, global hotkey, chord sequence

---

## 8. Kabul Kriterleri

- ✅ Build temiz, 0 warning, 0 error
- ✅ ~97/97 test pass (87 baseline + 10 new)
- ✅ 11 default kısayol çalışıyor (manuel smoke 11/11)
- ✅ Settings → Kısayollar tab'ında özelleştirme + Kaydet → kısayollar değişiyor
- ✅ F1 yardım dialog'u aktif kısayol tablosunu gösteriyor
- ✅ Çakışma tespit edilip MessageBox ile uyarılıyor
- ✅ Custom profil ve UseCustom flag uygulamayı kapatıp açtıktan sonra korunuyor
- ✅ Faz 3a davranışları regrese olmamış
