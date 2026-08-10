# Faz 4a — Açılış Durumları Shell'in İçine (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kalan üç modal pencereyi (`LoginDialog`, `RestoreDialog`, `FirstRunWizard`) tam-ekran shell durumlarına çevirmek ve açılış sırasını tersine döndürmek — pencere önce açılır, lisans/yedek/sihirbaz kontrolleri onun içinde koşar.

**Architecture:** `MainWindow` artık `AppRootView`'ı barındırır. `AppRootView` iki katmanlı bir `Grid`'dir: `ShellHost` (gate'ler geçilene kadar BOŞ bir `ContentControl`) ve `GateHost` (opak, hep üstte bir `Border`). Gate'ler `AppGateStack` üzerinden yığın olarak açılır — `DrawerStack` kalıbının modal ve basitleştirilmiş kardeşi. Açılış kararları `App.xaml.cs`'ten `StartupFlow`'a taşınır; `IStartupGates` (UI) ve `IStartupEnvironment` (servisler) arkasında durduğu için STA/WPF olmadan test edilir.

**Kritik kısıt:** Geri yükleme durumu **veritabanı yokken** koşar (`!File.Exists(dbFile) || Length < 10240`). O anda `MainShellViewModel` kurulamaz. Bu yüzden gate'ler shell'in ÜSTÜNE bindirilen bir katman değil; `ShellHost` gate'ler geçilene kadar gerçekten boş kalır.

**Tech Stack:** WPF (`net10.0-windows`), CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, xUnit + STA test harness (`ThemeTestHost`).

**Spec:** [docs/superpowers/specs/2026-08-10-arayuz-faz4-acilis-durumlari-design.md](../specs/2026-08-10-arayuz-faz4-acilis-durumlari-design.md)

---

## Dosya Yapısı

**Yeni — altyapı**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.App/Services/Gates/AppGate.cs` | Tek gate örneği: içerik + kapanışı bekleyen `Task<bool>` |
| `OrderDeck.App/Services/Gates/IAppGateService.cs` | Gate açmanın tek yolu; ViewModel'ler buna bağlanır |
| `OrderDeck.App/Services/Gates/AppGateStack.cs` | Açık gate'lerin yığını; `GateHost`'un DataContext'i |
| `OrderDeck.App/Views/AppRootView.xaml(.cs)` | `ShellHost` + `GateHost` iki katmanlı kök |

**Yeni — ekranlar** (`OrderDeck.App/Views/Gates/`)

| Dosya | Kaynağı |
|---|---|
| `GateBrand.xaml(.cs)` | Beş gate'in paylaştığı marka işareti (sol raydakinin büyütülmüşü) |
| `BootGate.xaml(.cs)` | Yeni — bugün bu aralıkta ekran boş |
| `LoginGate.xaml(.cs)` | `Views/LoginDialog.xaml` |
| `RestoreGate.xaml(.cs)` | `Views/RestoreDialog.xaml` |
| `FirstRunGate.xaml(.cs)` | `Views/FirstRunWizard.xaml` |
| `SessionRecoveryGate.xaml(.cs)` | Bugünkü `MessageBox.Show(..., YesNoCancel)` |

**Yeni — akış** (`OrderDeck.App/Startup/`)

| Dosya | Sorumluluk |
|---|---|
| `StartupFlow.cs` | Saf karar makinesi; UI ve servis bilmez |
| `IStartupGates.cs` | Gate gösterme sözleşmesi |
| `IStartupEnvironment.cs` | Lisans/DB/yedek/oturum/servis sözleşmesi |
| `RestoreOutcome.cs` | Geri yükleme sonucu (`Skipped` / `Restored`) |
| `SessionRecoveryChoice.cs` | Kurtarma kararı (`Exit` / `Continue` / `EndSession`) |
| `WpfStartupGates.cs` | `IStartupGates`'in `AppGateStack` üzerinden gerçeklenmesi |
| `WpfStartupEnvironment.cs` | `IStartupEnvironment`'ın gerçek servislerle gerçeklenmesi + arka plan servis ömrü |

**Silinen:** `Views/LoginDialog.xaml(.cs)`, `Views/RestoreDialog.xaml(.cs)`, `Views/FirstRunWizard.xaml(.cs)`

**Değişen:** `App.xaml.cs`, `MainWindow.xaml(.cs)`, `AppHost.cs`, `Themes/Metrics.xaml` (gate ölçüleri), `Themes/Controls.xaml` (yeni `OD.ProgressBar`), `ViewModels/AccountDialogViewModel.cs`, `ViewModels/FirstRunWizardViewModel.cs`

**Dokunulmayan:** `ViewModels/LoginDialogViewModel.cs`, `ViewModels/RestoreDialogViewModel.cs` — adlarındaki "Dialog" tarihsel, yeniden adlandırma bu fazın işi değil (24 dosya + testler etkilenirdi).

**Testler:** `OrderDeck.Tests/App/AppGateStackTests.cs` (yeni), `GateCompositionTests.cs` (yeni), `StartupFlowTests.cs` (yeni), `ControlsThemeTests.cs` (genişliyor)

---

## Task 0: Dal aç

- [ ] **Step 1: `master`'dan yeni dal**

Faz 4a'nın tüm commit'leri bu dalda toplanır (Task 11 Step 7 buradan push
ediyor). Spec commit'i `27dd8e1` zaten `master`'da duruyor.

```bash
git checkout master
git checkout -b feat/arayuz-faz4a-acilis-durumlari
git status --short
```

Beklenen: dal değişti, çalışma ağacında bu fazla ilgisiz duran dosyalar
(`.claude/launch.json`, `.gitignore`, `docs/` taslakları) **hiçbir commit'e
girmeyecek** — her adımda dosyalar tek tek `git add` ediliyor, `git add -A`
yok.

---

## Task 1: `AppGate` + `AppGateStack` altyapısı

`DrawerStack`'in modal kardeşi. Fark: gate'ler modal olduğu için yalnız `Top` çizilir — `IsTop`/opaklık yok, başlık şeridi yok, `Title` yok.

**Files:**
- Create: `OrderDeck.App/Services/Gates/AppGate.cs`
- Create: `OrderDeck.App/Services/Gates/IAppGateService.cs`
- Create: `OrderDeck.App/Services/Gates/AppGateStack.cs`
- Test: `OrderDeck.Tests/App/AppGateStackTests.cs`

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.Tests/App/AppGateStackTests.cs`:

```csharp
using OrderDeck.App.Services.Gates;

namespace OrderDeck.Tests.App;

/// <summary>
/// Gate yığınının sözleşmesi (Faz 4a altyapısı).
///
/// NEDEN: bu yığın açılıştaki üç <c>ShowDialog()</c>'un yerini alıyor.
/// Pencerenin bloklamasını WPF garanti ediyordu; burada o garantiyi bu sınıf
/// veriyor. Bozulursa açılış ya kilitlenir ya da shell hiç kurulmaz.
///
/// STA gerekmiyor: yığın saf CLR. Görsel katman ayrı test ediliyor
/// (GateCompositionTests).
/// </summary>
public class AppGateStackTests
{
    private static object Content(AppGate _) => new object();

    [Fact]
    public void Show_opens_the_layer_and_puts_the_gate_on_top()
    {
        var stack = new AppGateStack();

        stack.ShowAsync(Content);

        Assert.True(stack.IsOpen);
        Assert.Single(stack.Items);
        Assert.NotNull(stack.Top!.Content);
    }

    [Fact]
    public void Content_factory_receives_the_gate_it_will_live_in()
    {
        var stack = new AppGateStack();
        AppGate? seen = null;

        stack.ShowAsync(g => { seen = g; return new object(); });

        Assert.Same(stack.Top, seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Closing_completes_the_task_with_the_given_result(bool confirmed)
    {
        var stack = new AppGateStack();
        var pending = stack.ShowAsync(Content);

        Assert.False(pending.IsCompleted);
        stack.Top!.Close(confirmed);

        Assert.Equal(confirmed, await pending);
        Assert.Empty(stack.Items);
        Assert.False(stack.IsOpen);
    }

    [Fact]
    public async Task Second_close_is_ignored()
    {
        var stack = new AppGateStack();
        var pending = stack.ShowAsync(Content);
        var gate = stack.Top!;

        gate.Close(true);
        gate.Close(false);

        Assert.True(await pending);
    }

    [Fact]
    public async Task Nested_gates_stack_and_unwind_in_order()
    {
        // FirstRunGate → LoginGate zinciri: üstteki kapanınca sihirbaz geri gelir.
        var stack = new AppGateStack();
        var outer = stack.ShowAsync(Content);
        var outerGate = stack.Top!;
        var inner = stack.ShowAsync(Content);
        var innerGate = stack.Top!;

        Assert.Equal(2, stack.Items.Count);
        Assert.NotSame(outerGate, innerGate);

        innerGate.Close(true);

        Assert.True(await inner);
        Assert.False(outer.IsCompleted);
        Assert.Same(outerGate, stack.Top);
    }

    [Fact]
    public async Task Closing_a_lower_gate_cancels_the_ones_it_opened()
    {
        var stack = new AppGateStack();
        var outer = stack.ShowAsync(Content);
        var outerGate = stack.Top!;
        var inner = stack.ShowAsync(Content);

        outerGate.Close(true);

        Assert.False(await inner);
        Assert.True(await outer);
        Assert.Empty(stack.Items);
    }
}
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~AppGateStackTests`
Beklenen: derleme hatası — `OrderDeck.App.Services.Gates` ad alanı yok.

- [ ] **Step 3: `AppGate` yaz**

`OrderDeck.App/Services/Gates/AppGate.cs`:

```csharp
namespace OrderDeck.App.Services.Gates;

/// <summary>
/// Tek bir açılış durumunun (gate) canlı örneği: içerik + kapanışı bekleyen
/// görev.
///
/// <see cref="OrderDeck.App.Services.Drawers.Drawer"/>'ın modal kardeşi. İki
/// fark var ve ikisi de gate'lerin modal olmasından geliyor:
/// · <c>Title</c> yok — gate'in başlık şeridi yok, ekranın tamamı içerik.
/// · <c>IsTop</c> yok — yalnız en üstteki çizilir, alttakiler soluklaşmaz.
///
/// Dönen bool eski <c>ShowDialog() == true</c> ile birebir aynı anlamda.
/// </summary>
public sealed class AppGate
{
    // RunContinuationsAsynchronously: Close() UI thread'inden çağrılıyor.
    // Bayrak olmasa await eden gövde Close()'un İÇİNDEN, yığın daha kendini
    // toparlamadan devam ederdi.
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closed;

    internal AppGate() { }

    /// <summary>Ekrana çizilen görsel içerik. Yığın, gate'i listeye eklemeden
    /// hemen önce doldurur (fabrika gate'in kendisini almalı ki içerik
    /// <see cref="Close"/>'u tutabilsin).</summary>
    public object? Content { get; internal set; }

    /// <summary>Kapanınca tamamlanır. true = onay, false = iptal.</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>
    /// Kapanış BURADAN başlar; yığın <see cref="Closed"/>'ı dinleyip kendini
    /// günceller. İkinci çağrı sessizce yok sayılır — yığın üstteki gate'i
    /// iptal ederken o gate zaten kapanıyor olabilir.
    /// </summary>
    public void Close(bool confirmed)
    {
        if (_closed) return;
        _closed = true;
        // Önce yığından düş, sonra sonucu ver: await eden kod uyandığında
        // ekranda kapanmış bir gate görmesin.
        Closed?.Invoke(this);
        _completion.TrySetResult(confirmed);
    }

    internal event Action<AppGate>? Closed;
}
```

- [ ] **Step 4: `IAppGateService` yaz**

`OrderDeck.App/Services/Gates/IAppGateService.cs`:

```csharp
namespace OrderDeck.App.Services.Gates;

/// <summary>
/// Tam-ekran açılış durumu açmanın tek yolu. Spec §6: "hiçbir şey pop-up
/// değil" — açılıştaki üç modal pencere bu katmana taşındı.
///
/// Üretimdeki uygulaması <see cref="AppGateStack"/> (aynı nesne hem servis hem
/// de GateHost'un bağlandığı yığın — arada üçüncü bir sınıf yok).
/// </summary>
public interface IAppGateService
{
    /// <summary>
    /// Gate'i açar ve kapanmasını bekler. true = onaylanarak kapandı.
    ///
    /// Başlık parametresi YOK: gate'in şeridi yok, başlığı içeriğin kendisi
    /// taşıyor.
    /// </summary>
    Task<bool> ShowAsync(Func<AppGate, object> buildContent);
}
```

- [ ] **Step 5: `AppGateStack` yaz**

`OrderDeck.App/Services/Gates/AppGateStack.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrderDeck.App.Services.Gates;

/// <summary>
/// Açık gate'lerin yığını. Tek örnek (singleton): hem
/// <see cref="IAppGateService"/> olarak verilir hem de AppRootView'daki
/// GateHost'un DataContext'i olur.
///
/// NEDEN YIĞIN, tek yuva değil: sihirbazın 2. adımı lisans için LoginGate
/// açıyor ve kapanınca AYNI adıma dönmesi gerekiyor. Tek slot bunu ifade
/// edemez.
///
/// Thread: WPF UI thread'i. Kilit yok; ObservableCollection zaten tek
/// thread'e bağlı.
/// </summary>
public sealed class AppGateStack : IAppGateService, INotifyPropertyChanged
{
    private readonly ObservableCollection<AppGate> _items = new();

    public AppGateStack() => Items = new ReadOnlyObservableCollection<AppGate>(_items);

    /// <summary>Alttan üste sıralı açık gate'ler. Yalnız <see cref="Top"/>
    /// çizilir; liste kapanış sırasını yönetmek için tutuluyor.</summary>
    public ReadOnlyObservableCollection<AppGate> Items { get; }

    /// <summary>Ekranda görünen gate. GateHost buna bağlanır.</summary>
    public AppGate? Top => _items.Count == 0 ? null : _items[^1];

    /// <summary>Gate katmanı görünür mü? False ise shell'in önü açılır.</summary>
    public bool IsOpen => _items.Count > 0;

    public Task<bool> ShowAsync(Func<AppGate, object> buildContent)
    {
        var gate = new AppGate();
        gate.Content = buildContent(gate);
        gate.Closed += OnGateClosed;
        _items.Add(gate);
        Refresh();
        return gate.Completion;
    }

    private void OnGateClosed(AppGate gate)
    {
        var index = _items.IndexOf(gate);
        if (index < 0) return;

        // Bir gate kapanırken ONUN AÇTIKLARI ekranda kalamaz: üsttekiler iptal
        // edilir. Her biri buraya yeniden girip kendini listeden düşürdüğü için
        // döngü bittiğinde index hâlâ geçerli.
        for (var i = _items.Count - 1; i > index; i--)
            _items[i].Close(false);

        gate.Closed -= OnGateClosed;
        _items.RemoveAt(index);
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Top));
        OnPropertyChanged(nameof(IsOpen));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 6: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~AppGateStackTests`
Beklenen: 7 test geçer (2 `[Theory]` satırı dahil).

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.App/Services/Gates OrderDeck.Tests/App/AppGateStackTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): gate yığını altyapısı (Faz 4a)

Açılıştaki üç ShowDialog()'un yerini alacak modal katmanın çekirdeği.
DrawerStack kalıbı, iki sadeleştirmeyle: gate modal olduğu için Title ve
IsTop yok — yalnız en üstteki çizilir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `OD.ProgressBar` stili

`BootGate` ve `LoginGate` belirsiz ilerleme çubuğu istiyor. `Controls.xaml`'de `ProgressBar` anahtarı YOK; `DarkControls.xaml`'de de örtük `ProgressBar` stili yok — bugünkü giriş penceresindeki çubuk Windows'un varsayılan yeşil Aero çubuğu. Spec §9: "Style'lar tüketildikleri fazda yazılır" → burada yazılıyor.

**Files:**
- Modify: `OrderDeck.App/Themes/Metrics.xaml` (yeni `OD.Layout.ProgressHeight`)
- Modify: `OrderDeck.App/Themes/Controls.xaml` (dosya sonu, `</ResourceDictionary>` öncesi)
- Test: `OrderDeck.Tests/App/ControlsThemeTests.cs`

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.Tests/App/ControlsThemeTests.cs` dosyasının sonuna, son `}` öncesine ekle:

```csharp
    [Fact]
    public void Controls_defines_the_progress_bar_style()
    {
        // BootGate ve LoginGate belirsiz çubuk kullanıyor. Anahtar yoksa
        // Windows'un varsayılan YEŞİL Aero çubuğu çizilir — kapalı palet dışı.
        var error = ThemeTestHost.Run(dict =>
        {
            var style = Assert.IsType<Style>(dict["OD.ProgressBar"]);
            Assert.Equal(typeof(System.Windows.Controls.ProgressBar), style.TargetType);
        }, "Controls.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Metrics_defines_the_progress_height()
    {
        var error = ThemeTestHost.Run(
            dict => Assert.True(Assert.IsType<double>(dict["OD.Layout.ProgressHeight"]) > 0),
            "Metrics.xaml");

        Assert.Null(error);
    }
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ControlsThemeTests`
Beklenen: iki yeni test FAIL — `ResourceReferenceKeyNotFoundException: 'OD.ProgressBar'` ve `'OD.Layout.ProgressHeight'`.

- [ ] **Step 3: Ölçü token'ını ekle**

`OrderDeck.App/Themes/Metrics.xaml`, `OD.Layout.ButtonHeight` satırının hemen altına:

```xml
  <sys:Double x:Key="OD.Layout.ProgressHeight">3</sys:Double> <!-- belirsiz ilerleme çubuğu -->
```

- [ ] **Step 4: Stili yaz**

`OrderDeck.App/Themes/Controls.xaml`, dosya sonundaki `</ResourceDictionary>` satırının HEMEN ÜSTÜNE:

```xml
    <!-- Belirsiz ilerleme çubuğu (BootGate "Hazırlanıyor", LoginGate "meşgul").
         Varsayılan Aero şablonu YEŞİL çizer ve kapalı paletin dışında kalır;
         ayrıca DarkControls'te de örtük karşılığı yok. Determinate mod da
         çalışsın diye PART_Indicator korunuyor, ama bugün yalnız
         IsIndeterminate kullanılıyor.

         Belirsiz animasyon: dar bir parlak blok rayın soluna girip sağından
         çıkıyor. TranslateTransform üzerinden, Width'e dokunmadan — layout
         her karede yeniden ölçülmesin. -->
    <Style x:Key="OD.ProgressBar" TargetType="ProgressBar">
        <Setter Property="Height" Value="{StaticResource OD.Layout.ProgressHeight}"/>
        <Setter Property="Background" Value="{StaticResource OD.Brush.Surface2}"/>
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Accent}"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ProgressBar">
                    <Border x:Name="Track"
                            Background="{TemplateBinding Background}"
                            CornerRadius="{StaticResource OD.Radius.Full}"
                            ClipToBounds="True">
                        <Grid>
                            <Rectangle x:Name="PART_Track" Fill="Transparent"/>
                            <Rectangle x:Name="PART_Indicator"
                                       HorizontalAlignment="Left"
                                       Fill="{TemplateBinding Foreground}"
                                       RadiusX="{StaticResource OD.Radius.Full}"
                                       RadiusY="{StaticResource OD.Radius.Full}"/>
                            <Rectangle x:Name="Pulse"
                                       Width="80"
                                       HorizontalAlignment="Left"
                                       Visibility="Collapsed"
                                       Fill="{TemplateBinding Foreground}"
                                       RadiusX="{StaticResource OD.Radius.Full}"
                                       RadiusY="{StaticResource OD.Radius.Full}">
                                <Rectangle.RenderTransform>
                                    <TranslateTransform x:Name="PulseShift" X="-80"/>
                                </Rectangle.RenderTransform>
                            </Rectangle>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsIndeterminate" Value="True">
                            <Setter TargetName="PART_Indicator" Property="Visibility" Value="Collapsed"/>
                            <Setter TargetName="Pulse" Property="Visibility" Value="Visible"/>
                            <Trigger.EnterActions>
                                <BeginStoryboard x:Name="PulseLoop">
                                    <Storyboard RepeatBehavior="Forever">
                                        <DoubleAnimation Storyboard.TargetName="PulseShift"
                                                         Storyboard.TargetProperty="X"
                                                         From="-80" To="440"
                                                         Duration="0:0:1.2"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <StopStoryboard BeginStoryboardName="PulseLoop"/>
                            </Trigger.ExitActions>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 5: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProgressBar_style_exists`
Beklenen: PASS.

Ayrıca `ThemeMetricsTests` hâlâ geçmeli: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ThemeMetricsTests`

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/Themes/Controls.xaml OrderDeck.App/Themes/Metrics.xaml OrderDeck.Tests/App/ControlsThemeTests.cs
git commit -m "$(cat <<'EOF'
feat(theme): OD.ProgressBar stili

Belirsiz ilerleme çubuğunun anahtarlı karşılığı yoktu; ne Controls.xaml'de
ne DarkControls.xaml'de. Bugünkü giriş penceresindeki çubuk Windows'un
varsayılan YEŞİL Aero çubuğu — kapalı paletin dışında. BootGate ve LoginGate
tüketeceği için spec §9 gereği bu fazda yazıldı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `AppRootView` — iki katmanlı kök

**Files:**
- Create: `OrderDeck.App/Views/AppRootView.xaml`
- Create: `OrderDeck.App/Views/AppRootView.xaml.cs`
- Test: `OrderDeck.Tests/App/GateCompositionTests.cs`

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.Tests/App/GateCompositionTests.cs`:

```csharp
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.Views;

namespace OrderDeck.Tests.App;

/// <summary>
/// Gate katmanı gerçekten çiziliyor mu? (Faz 3'teki
/// MainShellViewCompositionTests kalıbı.)
///
/// NEDEN TEK [Fact]: her Fact kendi STA thread'ini açıyor. Hepsini tek
/// thread'de kurmak hem hızlı hem de "process başına tek Application"
/// kuralını en az zorlayan yol.
/// </summary>
public class GateCompositionTests
{
    [Fact]
    public void Gate_layer_composes()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var gates = new AppGateStack();
            var root = new AppRootView(gates);

            // Gate yokken katman kapalı, shell yuvası boş.
            Assert.False(root.IsShellMounted);

            // Shell yuvası doldurulabiliyor.
            root.MountShell(new Border());
            Assert.True(root.IsShellMounted);
        });
        Assert.Null(error);
    }
}
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: derleme hatası — `AppRootView` yok.

- [ ] **Step 3: `AppRootView.xaml` yaz**

`OrderDeck.App/Views/AppRootView.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.AppRootView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Uygulamanın kökü. İki katman, tek Grid hücresi.

         NEDEN SHELL BAŞTAN KURULMUYOR: geri yükleme durumu VERİTABANI YOKKEN
         koşuyor (App.xaml.cs'teki dbMissingOrTiny kontrolü). O anda
         MainShellViewModel kurulamaz — Dapper sorguları boş dosyaya çarpar.
         Bu yüzden gate'ler shell'in üstüne bindirilen bir katman DEĞİL;
         ShellHost gate'ler geçilene kadar gerçekten BOŞ kalıyor.

         Çalışırken (hesap değiştirme) ise tersi geçerli: shell zaten kurulu,
         GateHost onun ÜSTÜNE doluyor. Shell unload olmadığı için yayın
         sürerken sohbet paneli, sayaçlar ve çekmece yığını yerinde kalıyor. -->
    <Grid>
        <ContentControl x:Name="ShellHost"/>

        <!-- Border, ContentControl DEĞİL: ContentControl'ün varsayılan şablonu
             çıplak bir ContentPresenter, Background'u ÇİZMEZ. Opak zemin ve
             tıklama engelleme (modallik) Border'dan geliyor. -->
        <Border x:Name="GateHost"
                Background="{StaticResource OD.Brush.Bg}"
                Visibility="{Binding IsOpen, Converter={StaticResource BoolToVisibleConverter}}">
            <ContentPresenter Content="{Binding Top.Content}"/>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 4: `AppRootView.xaml.cs` yaz**

`OrderDeck.App/Views/AppRootView.xaml.cs`:

```csharp
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;

namespace OrderDeck.App.Views;

/// <summary>
/// MainWindow'un tek çocuğu. Gate katmanını ve shell yuvasını taşır.
/// </summary>
public partial class AppRootView : UserControl
{
    public AppRootView(AppGateStack gates)
    {
        InitializeComponent();
        DataContext = gates;
    }

    /// <summary>Shell kuruldu mu? MainWindow.OnClosing bunu soruyor: gate
    /// aşamasındayken MainShellViewModel'i DI'dan çekmek onu KURAR ve
    /// veritabanı henüz yokken patlar.</summary>
    public bool IsShellMounted => ShellHost.Content is not null;

    /// <summary>Gate'ler geçildikten sonra bir kez çağrılır.</summary>
    public void MountShell(object shell) => ShellHost.Content = shell;
}
```

- [ ] **Step 5: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/Views/AppRootView.xaml OrderDeck.App/Views/AppRootView.xaml.cs OrderDeck.Tests/App/GateCompositionTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): AppRootView — shell yuvası + gate katmanı

Kritik kısıt yorumda: geri yükleme DB yokken koşuyor, o anda
MainShellViewModel kurulamaz. Bu yüzden ShellHost gate'ler geçilene kadar
BOŞ kalıyor; gate katmanı shell'in üstüne bindirilen bir overlay değil.

GateHost Border, ContentControl değil: ContentControl'ün varsayılan şablonu
Background çizmiyor, opak zemin ve tıklama engelleme oradan gelemezdi.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Gate ölçü token'ları + `GateBrand` + `BootGate`

Beş gate'in ortak kabı burada doğuyor: ortalanmış sütun + marka işareti. `BootGate` en küçük tüketicisi olduğu için ikisi birlikte yazılıyor.

**Files:**
- Modify: `OrderDeck.App/Themes/Metrics.xaml`
- Create: `OrderDeck.App/Views/Gates/GateBrand.xaml(.cs)`
- Create: `OrderDeck.App/Views/Gates/BootGate.xaml(.cs)`
- Test: `OrderDeck.Tests/App/GateCompositionTests.cs`

- [ ] **Step 1: Testi genişlet (kırmızı)**

`OrderDeck.Tests/App/GateCompositionTests.cs` içindeki `Gate_layer_composes` gövdesinde `root.MountShell(new Border());` satırından SONRA, `});` öncesine ekle:

```csharp
            // BootGate gate katmanına gerçekten oturuyor mu?
            var pending = gates.ShowAsync(_ => new BootGate());
            Assert.IsType<BootGate>(gates.Top!.Content);
            Assert.True(gates.IsOpen);

            gates.Top.Close(false);
            Assert.False(gates.IsOpen);
            Assert.True(pending.IsCompleted);
```

Dosyanın başındaki `using` listesine ekle:

```csharp
using OrderDeck.App.Views.Gates;
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: derleme hatası — `BootGate` yok.

- [ ] **Step 3: Ölçü token'larını ekle**

`OrderDeck.App/Themes/Metrics.xaml`:

`OD.Pad.Bottom3` satırının HEMEN ÜSTÜNE:

```xml
  <Thickness x:Key="OD.Pad.Bottom2" Left="0" Top="0"  Right="0" Bottom="4"/>
  <Thickness x:Key="OD.Pad.Bottom4" Left="0" Top="0"  Right="0" Bottom="12"/>
```

`OD.Pad.Top6` satırının HEMEN ALTINA:

```xml
  <Thickness x:Key="OD.Pad.Top7"    Left="0" Top="24" Right="0" Bottom="0"/>
```

`OD.Pad.X5` satırının HEMEN ALTINA:

```xml
  <!-- Sol kenarında ikon taşıyan input: sol dolgu ikona yer açıyor.
       (Giriş ekranındaki e-posta ve şifre kutuları.) -->
  <Thickness x:Key="OD.Pad.InputWithIcon" Left="34" Top="8" Right="12" Bottom="8"/>
```

`OD.Layout.AppStartHeight` satırının HEMEN ALTINA:

```xml
  <!-- Açılış durumları (gate'ler): tam ekranda ortalanan tek sütun.
       440 seçildi çünkü bugünkü giriş penceresinin iç genişliği 420-2*28=364
       idi ve dar geliyordu; 440 aynı hissi verip lisans listesine nefes
       bırakıyor. Sihirbaz daha geniş, kendi ölçüsü var. -->
  <sys:Double x:Key="OD.Layout.GateColumn">440</sys:Double>
  <sys:Double x:Key="OD.Layout.GateColumnWide">760</sys:Double>  <!-- sihirbaz -->
  <sys:Double x:Key="OD.Layout.GateBrandMark">48</sys:Double>
  <sys:Double x:Key="OD.Layout.GateListMaxHeight">260</sys:Double>
```

- [ ] **Step 4: `GateBrand` yaz**

`OrderDeck.App/Views/Gates/GateBrand.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.Gates.GateBrand"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Sol raydaki marka işaretinin büyütülmüş hâli (ShellSidebar.xaml).
         Beş gate de bunu kullanıyor; kopyalanmasın diye kendi UserControl'ü.
         Vektör logo varlığı YOK — Assets/Brand yalnız üçüncü taraf platform
         ikonlarını taşıyor, orderdeck.ico da pencere ikonu. -->
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
        <Border Width="{StaticResource OD.Layout.GateBrandMark}"
                Height="{StaticResource OD.Layout.GateBrandMark}"
                CornerRadius="{StaticResource OD.Radius.Lg}"
                Background="{StaticResource OD.Brush.Accent}">
            <TextBlock Text="OD"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       FontFamily="{StaticResource OD.Font.Display}"
                       FontSize="{StaticResource OD.Font.F3}"
                       FontWeight="Bold"
                       Foreground="{StaticResource OD.Brush.OnAccent}"/>
        </Border>
        <TextBlock Text="OrderDeck"
                   VerticalAlignment="Center"
                   Margin="{StaticResource OD.Pad.Left4}"
                   FontFamily="{StaticResource OD.Font.Display}"
                   FontSize="{StaticResource OD.Font.F3}"
                   Foreground="{StaticResource OD.Brush.Text}"/>
    </StackPanel>
</UserControl>
```

`OrderDeck.App/Views/Gates/GateBrand.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace OrderDeck.App.Views.Gates;

/// <summary>Gate ekranlarının ortak marka işareti.</summary>
public partial class GateBrand : UserControl
{
    public GateBrand() => InitializeComponent();
}
```

- [ ] **Step 5: `BootGate` yaz**

`OrderDeck.App/Views/Gates/BootGate.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.Gates.BootGate"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:gates="clr-namespace:OrderDeck.App.Views.Gates">
    <!-- Lisans kontrolü koşarken görünen ekran. BUGÜN BU ARALIKTA HİÇBİR ŞEY
         YOK: pencere daha açılmadığı için operatör birkaç saniye boş masaüstüne
         bakıyor ve uygulamanın açılıp açılmadığını bilmiyor.

         Çıkış düğmesi yok — kendiliğinden geçer (spec §3.2 tablosu). -->
    <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center"
                Width="{StaticResource OD.Layout.GateColumn}">
        <gates:GateBrand/>

        <ProgressBar Style="{StaticResource OD.ProgressBar}"
                     IsIndeterminate="True"
                     Margin="{StaticResource OD.Pad.Top7}"/>

        <TextBlock Text="Hazırlanıyor…"
                   Style="{StaticResource OD.Text.Hint}"
                   HorizontalAlignment="Center"
                   Margin="{StaticResource OD.Pad.Top4}"/>
    </StackPanel>
</UserControl>
```

`OrderDeck.App/Views/Gates/BootGate.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace OrderDeck.App.Views.Gates;

/// <summary>Lisans başlatma koşarken görünen açılış ekranı.</summary>
public partial class BootGate : UserControl
{
    public BootGate() => InitializeComponent();
}
```

- [ ] **Step 6: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: PASS.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.App/Themes/Metrics.xaml OrderDeck.App/Views/Gates OrderDeck.Tests/App/GateCompositionTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): GateBrand + BootGate

Açılışta lisans kontrolü koşarken bugün ekranda hiçbir şey yok — pencere
henüz açılmıyor. BootGate o boşluğu dolduruyor.

GateBrand sol raydaki marka işaretinin büyütülmüş hâli; beş gate de onu
kullanacağı için kendi UserControl'ü oldu.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `LoginGate`

`LoginDialog.xaml`'in dört modu (giriş / kayıt / e-posta onayı / lisans seçimi) aynen taşınır. `LoginDialogViewModel` **değişmez** — adındaki "Dialog" tarihsel, yeniden adlandırma bu fazın işi değil.

**Files:**
- Create: `OrderDeck.App/Views/Gates/LoginGate.xaml(.cs)`
- Test: `OrderDeck.Tests/App/GateCompositionTests.cs`

**Dönüşüm tablosu** — kaynak `OrderDeck.App/Views/LoginDialog.xaml`:

| Kaynak satır | Bugün | Yerine |
|---|---|---|
| 1-14 | `Window` 420×500 + yerel `BoolToVis` | `UserControl`; uygulama geneli `BoolToVisibleConverter` |
| 23-39 | 52×52 mavi degrade kutu + `DropShadowEffect` (`#FF5B8DEF`/`#FF4A77D4`) + "OrderDeck" | `<gates:GateBrand/>` |
| 40-42 | `FontSize="12"` alt başlık | `Style="{StaticResource OD.Text.Hint}"` |
| 47, 60, 84, 86, 88, 90 | `Margin="0,0,0,4"` + `OD.Fg.Secondary` etiket | `OD.Text.Label` + `OD.Pad.Bottom2` |
| 51, 64 | `Padding="34,7,8,7"` | `OD.Pad.InputWithIcon` |
| 85, 87, 89, 91 | `Padding="8,7"` | düşer — `OD.TextBox` kendi dolgusunu veriyor |
| 49, 62, 85, 87, 89, 91 | `Margin="0,0,0,12"` | `OD.Pad.Bottom4` |
| 52-58, 65-71 | `Viewbox` 16×16, `Margin="10,0,0,0"`, `Stroke="{StaticResource OD.Fg.Secondary}"` | `Path` verisi AYNEN; ölçü → `OD.Icon.Md`, margin → `OD.Pad.Left4`, stroke → `OD.Brush.TextDim` |
| 73-75, 92-94, 130-132 | `Background="{StaticResource OD.Accent}" Foreground="White" Padding="0,9"` | `OD.Button.Primary` |
| 76-79, 95-98, 108-111 | `Hyperlink Foreground="{StaticResource OD.Accent}"` | `OD.Button.Ghost` düğme (`Hyperlink` düşer — anahtarlı karşılığı yok, `DarkControls`'e yaslanırdı) |
| 107 | çıplak `Button` | `OD.Button.Secondary` |
| 114-119 | "500px pencereye sığmıyor" yorumu | düşer — tam ekranda o kısıt yok; yerine kaydırıcı |
| 127-129 | `ListBox DisplayMemberPath="LicenseKey"` | `ListBox` + kart `ItemTemplate`, `SelectedItem="{Binding Selected}"` korunur |
| 137 | `Foreground="#FFF87171"` | `OD.Brush.Accent` |
| 139-140 | çıplak `ProgressBar Height="2"` | `Style="{StaticResource OD.ProgressBar}"` |
| — | (yoktu; pencerenin çarpısı vardı) | alt satırda çıkış düğmesi: açılışta **Çıkış**, çalışırken **Vazgeç** |

- [ ] **Step 1: Testi genişlet (kırmızı)**

`GateCompositionTests.cs`, `Gate_layer_composes` gövdesinin sonuna (`});` öncesi):

```csharp
            // LoginGate DataContext'siz de çizilebilmeli: bu testin ölçtüğü şey
            // kaynak çözümlemesi. StaticResource anahtarlarından biri yanlışsa
            // XamlParseException atar; binding'ler sessizce boş kalır.
            var loginPending = gates.ShowAsync(g => LoginGate.Create(g, vm: null, isStartupGate: true));
            Assert.IsType<LoginGate>(gates.Top!.Content);
            gates.Top.Close(false);
            Assert.True(loginPending.IsCompleted);
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: derleme hatası — `LoginGate` yok.

- [ ] **Step 3: `LoginGate.xaml` yaz**

`OrderDeck.App/Views/Gates/LoginGate.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.Gates.LoginGate"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:gates="clr-namespace:OrderDeck.App.Views.Gates">
    <!-- LoginDialog.xaml'in (420x500 Window) gate karşılığı.

         Dört mod aynen taşındı; LoginDialogViewModel'e dokunulmadı. Sınıf
         adındaki "Dialog" tarihsel — yeniden adlandırmak dört dosyaya yayılırdı
         ve bu fazın işi değil.

         İKİ BARINDIRMA, TEK VIEW: aynı ekran hem açılışta (ShellHost boşken)
         hem çalışırken (hesap değiştirme, shell altta) kullanılıyor. Tek fark
         alttaki çıkış düğmesinin METNİ; davranış ikisinde de Close(false).
         Anlamı StartupFlow veriyor — açılışta false gelirse Shutdown(). -->
    <Grid HorizontalAlignment="Center" VerticalAlignment="Center"
          Width="{StaticResource OD.Layout.GateColumn}">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>  <!-- marka -->
            <RowDefinition Height="Auto"/>  <!-- mod gövdesi -->
            <RowDefinition Height="Auto"/>  <!-- hata + meşgul -->
            <RowDefinition Height="Auto"/>  <!-- çıkış -->
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0">
            <gates:GateBrand/>
            <TextBlock Text="Yayıncı hesabınla giriş yap"
                       Style="{StaticResource OD.Text.Hint}"
                       HorizontalAlignment="Center"
                       Margin="{StaticResource OD.Pad.Top3}"/>
        </StackPanel>

        <!-- ── Giriş ──────────────────────────────────────────────────── -->
        <StackPanel Grid.Row="1" Margin="{StaticResource OD.Pad.Top7}"
                    Visibility="{Binding IsLoginMode, Converter={StaticResource BoolToVisibleConverter}}">
            <TextBlock Text="E-posta" Style="{StaticResource OD.Text.Label}"
                       Margin="{StaticResource OD.Pad.Bottom2}"/>
            <Grid Margin="{StaticResource OD.Pad.Bottom4}">
                <TextBox Style="{StaticResource OD.TextBox}"
                         Padding="{StaticResource OD.Pad.InputWithIcon}"
                         Text="{Binding Email, UpdateSourceTrigger=PropertyChanged}"/>
                <Viewbox Width="{StaticResource OD.Icon.Md}" Height="{StaticResource OD.Icon.Md}"
                         HorizontalAlignment="Left" Margin="{StaticResource OD.Pad.Left4}"
                         IsHitTestVisible="False">
                    <Path Width="24" Height="24"
                          Stroke="{StaticResource OD.Brush.TextDim}" StrokeThickness="1.8"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                          Data="M3,5 h18 a0,0 0 0 1 0,0 v14 h-18 V5 Z M4,7.5 L12,13 L20,7.5"/>
                </Viewbox>
            </Grid>

            <TextBlock Text="Şifre" Style="{StaticResource OD.Text.Label}"
                       Margin="{StaticResource OD.Pad.Bottom2}"/>
            <Grid Margin="{StaticResource OD.Pad.Bottom4}">
                <PasswordBox x:Name="LoginPassword" PasswordChanged="OnLoginPasswordChanged"
                             Padding="{StaticResource OD.Pad.InputWithIcon}"/>
                <Viewbox Width="{StaticResource OD.Icon.Md}" Height="{StaticResource OD.Icon.Md}"
                         HorizontalAlignment="Left" Margin="{StaticResource OD.Pad.Left4}"
                         IsHitTestVisible="False">
                    <Path Width="24" Height="24"
                          Stroke="{StaticResource OD.Brush.TextDim}" StrokeThickness="1.8"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                          Data="M5,11 h14 v9.5 h-14 V11 Z M8,11 V7.5 a4,4 0 0 1 8,0 V11"/>
                </Viewbox>
            </Grid>

            <Button Content="Giriş yap" Command="{Binding SubmitLoginCommand}"
                    Style="{StaticResource OD.Button.Primary}"
                    IsDefault="True"
                    Margin="{StaticResource OD.Pad.Top3}"/>
            <Button Content="Hesap oluştur" Command="{Binding SwitchToRegisterCommand}"
                    Style="{StaticResource OD.Button.Ghost}"
                    HorizontalAlignment="Center"
                    Margin="{StaticResource OD.Pad.Top4}"/>
        </StackPanel>

        <!-- ── Kayıt ──────────────────────────────────────────────────── -->
        <StackPanel Grid.Row="1" Margin="{StaticResource OD.Pad.Top7}"
                    Visibility="{Binding IsRegisterMode, Converter={StaticResource BoolToVisibleConverter}}">
            <TextBlock Text="Ad Soyad" Style="{StaticResource OD.Text.Label}"
                       Margin="{StaticResource OD.Pad.Bottom2}"/>
            <TextBox Style="{StaticResource OD.TextBox}" Margin="{StaticResource OD.Pad.Bottom4}"
                     Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}"/>

            <TextBlock Text="E-posta" Style="{StaticResource OD.Text.Label}"
                       Margin="{StaticResource OD.Pad.Bottom2}"/>
            <TextBox Style="{StaticResource OD.TextBox}" Margin="{StaticResource OD.Pad.Bottom4}"
                     Text="{Binding Email, UpdateSourceTrigger=PropertyChanged}"/>

            <TextBlock Text="Şifre (en az 8 karakter)" Style="{StaticResource OD.Text.Label}"
                       Margin="{StaticResource OD.Pad.Bottom2}"/>
            <PasswordBox x:Name="RegisterPassword" Margin="{StaticResource OD.Pad.Bottom4}"
                         PasswordChanged="OnRegisterPasswordChanged"/>

            <TextBlock Text="Şifre tekrar" Style="{StaticResource OD.Text.Label}"
                       Margin="{StaticResource OD.Pad.Bottom2}"/>
            <PasswordBox x:Name="RegisterPasswordConfirm" Margin="{StaticResource OD.Pad.Bottom4}"
                         PasswordChanged="OnRegisterPasswordConfirmChanged"/>

            <Button Content="Kayıt ol" Command="{Binding SubmitRegisterCommand}"
                    Style="{StaticResource OD.Button.Primary}"
                    Margin="{StaticResource OD.Pad.Top3}"/>
            <Button Content="Giriş ekranına dön" Command="{Binding SwitchToLoginCommand}"
                    Style="{StaticResource OD.Button.Ghost}"
                    HorizontalAlignment="Center"
                    Margin="{StaticResource OD.Pad.Top4}"/>
        </StackPanel>

        <!-- ── E-posta onayı bekleniyor ───────────────────────────────── -->
        <StackPanel Grid.Row="1" Margin="{StaticResource OD.Pad.Top7}"
                    Visibility="{Binding IsConfirmPendingMode, Converter={StaticResource BoolToVisibleConverter}}">
            <TextBlock TextWrapping="Wrap"
                       Style="{StaticResource OD.Text.Hint}"
                       Margin="{StaticResource OD.Pad.Bottom5}"
                       Text="E-posta adresine doğrulama linki gönderdik. Linke tıklayıp hesabını aktifleştir, sonra giriş yap."/>
            <Button Content="Linki tekrar gönder" Command="{Binding ResendCommand}"
                    Style="{StaticResource OD.Button.Secondary}"/>
            <Button Content="Giriş ekranına dön" Command="{Binding SwitchToLoginCommand}"
                    Style="{StaticResource OD.Button.Ghost}"
                    HorizontalAlignment="Center"
                    Margin="{StaticResource OD.Pad.Top4}"/>
        </StackPanel>

        <!-- ── Lisans seçimi ──────────────────────────────────────────────
             Pencerede liste sabit yükseklikliydi ve "aktive et" düğmesini
             500px'lik NoResize pencerenin dışına itiyordu (eski dosyadaki
             uzun not). Tam ekranda o kısıt yok: liste kaydırıcıya bağlandı,
             düğme her zaman görünür. -->
        <Grid Grid.Row="1" Margin="{StaticResource OD.Pad.Top7}"
              Visibility="{Binding IsLicenseSelectionMode, Converter={StaticResource BoolToVisibleConverter}}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Text="Bu makineye aktive edilecek lisansı seç:"
                       Style="{StaticResource OD.Text.Label}"
                       Margin="{StaticResource OD.Pad.Bottom4}"/>

            <ListBox Grid.Row="1"
                     MaxHeight="{StaticResource OD.Layout.GateListMaxHeight}"
                     ItemsSource="{Binding Licenses}"
                     SelectedItem="{Binding Selected}"
                     Background="Transparent" BorderThickness="0"
                     ScrollViewer.VerticalScrollBarVisibility="Auto">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding LicenseKey}"
                                   Style="{StaticResource OD.Text.Mono}"
                                   Margin="{StaticResource OD.Pad.2}"/>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>

            <Button Grid.Row="2" Content="Bu makineye aktive et"
                    Command="{Binding ActivateSelectedCommand}"
                    Style="{StaticResource OD.Button.Primary}"
                    IsDefault="True"
                    Margin="{StaticResource OD.Pad.Top4}"/>
        </Grid>

        <!-- ── Hata + meşgul ──────────────────────────────────────────── -->
        <StackPanel Grid.Row="2" Margin="{StaticResource OD.Pad.Top5}">
            <TextBlock Text="{Binding ErrorMessage}" TextWrapping="Wrap"
                       Style="{StaticResource OD.Text.Hint}"
                       Foreground="{StaticResource OD.Brush.Accent}"
                       Visibility="{Binding HasError, Converter={StaticResource BoolToVisibleConverter}}"/>
            <ProgressBar Style="{StaticResource OD.ProgressBar}"
                         IsIndeterminate="True"
                         Margin="{StaticResource OD.Pad.Top3}"
                         Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibleConverter}}"/>
        </StackPanel>

        <!-- ── Çıkış ──────────────────────────────────────────────────── -->
        <Button Grid.Row="3" x:Name="ExitButton"
                Style="{StaticResource OD.Button.Ghost}"
                HorizontalAlignment="Center"
                Margin="{StaticResource OD.Pad.Top6}"
                Click="OnExit"/>
    </Grid>
</UserControl>
```

> **`ListBox` notu:** `ListBox`/`ListBoxItem`'ın anahtarlı karşılığı henüz yok; bu tek kullanım Faz 4b'ye kadar `DarkControls.xaml`'in örtük stiline yaslanıyor. `Background="Transparent" BorderThickness="0"` bilerek: örtük stilin kendi çerçevesi gate zemininde kutu gibi duruyordu.

- [ ] **Step 4: `LoginGate.xaml.cs` yaz**

`OrderDeck.App/Views/Gates/LoginGate.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// LoginDialog'un gate karşılığı. Aynı view iki bağlamda kullanılıyor:
/// açılışta (ShellHost boş) ve çalışırken (hesap değiştirme, shell altta).
/// Tek fark çıkış düğmesinin metni.
///
/// <paramref name="vm"/> null olabiliyor: kompozisyon testi bu view'ı
/// servissiz çiziyor (ölçtüğü şey kaynak çözümlemesi).
/// </summary>
public partial class LoginGate : UserControl
{
    private readonly LoginDialogViewModel? _vm;
    private readonly AppGate _gate;

    private LoginGate(AppGate gate, LoginDialogViewModel? vm, bool isStartupGate)
    {
        InitializeComponent();
        _gate = gate;
        _vm = vm;
        DataContext = vm;

        // Açılışta iptal = uygulamadan çıkış (StartupFlow false'u Shutdown'a
        // çeviriyor). Çalışırken iptal = sadece bu ekranı kapat, shell altta
        // duruyor. Davranış aynı, anlatım farklı.
        ExitButton.Content = isStartupGate ? "Çıkış" : "Vazgeç";

        if (vm is not null)
            vm.RequestClose += OnRequestClose;
    }

    /// <summary>Fabrika: içerik gate'in kendisini alır (yığın kalıbı).</summary>
    public static LoginGate Create(AppGate gate, LoginDialogViewModel? vm, bool isStartupGate)
        => new(gate, vm, isStartupGate);

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.RequestClose -= OnRequestClose;
        _gate.Close(true);
    }

    private void OnExit(object sender, RoutedEventArgs e) => _gate.Close(false);

    private void OnLoginPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Password = LoginPassword.Password;
    }

    private void OnRegisterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Password = RegisterPassword.Password;
    }

    private void OnRegisterPasswordConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.PasswordConfirm = RegisterPasswordConfirm.Password;
    }
}
```

- [ ] **Step 5: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/Views/Gates/LoginGate.xaml OrderDeck.App/Views/Gates/LoginGate.xaml.cs OrderDeck.Tests/App/GateCompositionTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): LoginGate — giriş penceresi tam ekrana

Dört mod (giriş/kayıt/e-posta onayı/lisans seçimi) aynen taşındı,
LoginDialogViewModel'e dokunulmadı. Mavi degrade "OD" kutusu ve #FFF87171
hata rengi token'lara döndü.

Aynı view iki bağlamda kullanılıyor: açılışta ve çalışırken hesap
değiştirirken. Tek fark çıkış düğmesinin metni — giriş ekranını iki genişlikte
iki kere tasarlamamak için.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `RestoreGate`

`RestoreDialogViewModel` **değişmiyor**: `RestoreCompleted` ve `RestoreCompletedEvent` zaten var; gate "tamamlandı" durumuna o bayrakla geçiyor. Yeniden başlatma kararını gate değil `StartupFlow` veriyor (gate `true` ile kapanır → `IStartupEnvironment.RequestRestart()`), böylece akış headless test edilebilir kalıyor.

**Files:**
- Create: `OrderDeck.App/Views/Gates/RestoreGate.xaml(.cs)`
- Test: `OrderDeck.Tests/App/GateCompositionTests.cs`

- [ ] **Step 1: Testi genişlet (kırmızı)**

`GateCompositionTests.cs`, `Gate_layer_composes` gövdesinin sonuna:

```csharp
            var restorePending = gates.ShowAsync(g => RestoreGate.Create(g, vm: null));
            Assert.IsType<RestoreGate>(gates.Top!.Content);
            gates.Top.Close(false);
            Assert.True(restorePending.IsCompleted);
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: derleme hatası — `RestoreGate` yok.

- [ ] **Step 3: `RestoreGate.xaml` yaz**

`OrderDeck.App/Views/Gates/RestoreGate.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.Gates.RestoreGate"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:gates="clr-namespace:OrderDeck.App.Views.Gates">
    <!-- RestoreDialog.xaml'in gate karşılığı. (O pencere Background'ı HİÇ
         vermiyordu; DarkControls'ün örtük Window stili de tam tipe
         uygulanmadığı için zemin beyaz kalıyordu.)

         Değişenler: 📅 emojisi "Aylık" çipe döndü (spec §10: emoji ikon 0),
         #25D366 / DarkBlue / Gray token'a döndü.

         "Tamamlandı" durumu YENİ: eskiden geri yükleme bitince MessageBox
         çıkıyor, uygulama kapanıyor ve operatörün elle yeniden açması
         gerekiyordu. -->
    <Grid HorizontalAlignment="Center" VerticalAlignment="Center"
          Width="{StaticResource OD.Layout.GateColumn}">

        <!-- ── Seçim durumu ───────────────────────────────────────────── -->
        <Grid Visibility="{Binding RestoreCompleted, Converter={StaticResource BoolToCollapsedConverter}}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <StackPanel Grid.Row="0">
                <gates:GateBrand/>
                <TextBlock Style="{StaticResource OD.Text.Hint}"
                           TextWrapping="Wrap"
                           TextAlignment="Center"
                           Margin="{StaticResource OD.Pad.Top3}"
                           Text="Bu bilgisayarda veri bulunamadı. Buluttaki yedeklerden birini geri yükleyebilirsin."/>
            </StackPanel>

            <ListBox Grid.Row="1"
                     MaxHeight="{StaticResource OD.Layout.GateListMaxHeight}"
                     Margin="{StaticResource OD.Pad.Top7}"
                     ItemsSource="{Binding AvailableBackups}"
                     SelectedItem="{Binding SelectedBackup}"
                     Background="Transparent" BorderThickness="0"
                     HorizontalContentAlignment="Stretch"
                     ScrollViewer.VerticalScrollBarVisibility="Auto">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="{StaticResource OD.Pad.2}">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="{Binding CreatedAt, StringFormat='{}{0:dd MMM yyyy HH:mm}'}"
                                           FontFamily="{StaticResource OD.Font.Sans}"
                                           FontSize="{StaticResource OD.Font.F2}"
                                           FontWeight="Bold"
                                           Foreground="{StaticResource OD.Brush.Text}"/>
                                <!-- Eski 📅 emojisinin yerine metin çip -->
                                <Border Margin="{StaticResource OD.Pad.Left4}"
                                        Padding="{StaticResource OD.Pad.X2}"
                                        CornerRadius="{StaticResource OD.Radius.Xs}"
                                        Background="{StaticResource OD.Brush.AmberTint}"
                                        BorderBrush="{StaticResource OD.Brush.AmberTintBorder}"
                                        BorderThickness="1"
                                        VerticalAlignment="Center"
                                        Visibility="{Binding IsMonthlyMilestone, Converter={StaticResource BoolToVisibleConverter}}">
                                    <TextBlock Text="Aylık" Style="{StaticResource OD.Text.Micro}"/>
                                </Border>
                            </StackPanel>
                            <TextBlock Style="{StaticResource OD.Text.Hint}"
                                       Margin="{StaticResource OD.Pad.Top3}">
                                <Run Text="{Binding SizeBytes, StringFormat='{}{0:N0} bayt', Mode=OneWay}"/>
                                <Run Text="·"/>
                                <Run Text="{Binding MachineName, Mode=OneWay}"/>
                            </TextBlock>
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>

            <TextBlock Grid.Row="2" Text="{Binding StatusMessage}"
                       Style="{StaticResource OD.Text.Hint}"
                       Foreground="{StaticResource OD.Brush.Info}"
                       TextWrapping="Wrap"
                       Margin="{StaticResource OD.Pad.Top4}"/>

            <StackPanel Grid.Row="3" Margin="{StaticResource OD.Pad.Top6}">
                <Button Content="En son yedeği kullan"
                        Command="{Binding RestoreLatestCommand}"
                        Style="{StaticResource OD.Button.Primary}"
                        IsDefault="True"/>
                <Button Content="Seçileni geri yükle"
                        Command="{Binding RestoreSelectedCommand}"
                        Style="{StaticResource OD.Button.Secondary}"
                        Margin="{StaticResource OD.Pad.Top3}"/>
                <Button Content="Atla, yeni başlat"
                        Style="{StaticResource OD.Button.Ghost}"
                        HorizontalAlignment="Center"
                        Margin="{StaticResource OD.Pad.Top4}"
                        Click="OnSkip"/>
            </StackPanel>
        </Grid>

        <!-- ── Tamamlandı durumu ──────────────────────────────────────── -->
        <StackPanel Visibility="{Binding RestoreCompleted, Converter={StaticResource BoolToVisibleConverter}}">
            <gates:GateBrand/>
            <TextBlock Text="Geri yükleme tamamlandı"
                       HorizontalAlignment="Center"
                       Margin="{StaticResource OD.Pad.Top7}"
                       FontFamily="{StaticResource OD.Font.Display}"
                       FontSize="{StaticResource OD.Font.F3}"
                       Foreground="{StaticResource OD.Brush.Success}"/>
            <TextBlock Style="{StaticResource OD.Text.Hint}"
                       TextAlignment="Center" TextWrapping="Wrap"
                       Margin="{StaticResource OD.Pad.Top3}"
                       Text="Verilerin yerine kondu. OrderDeck'in yeniden başlaması gerekiyor."/>
            <Button Content="Yeniden Başlat"
                    Style="{StaticResource OD.Button.Primary}"
                    Margin="{StaticResource OD.Pad.Top6}"
                    Click="OnRestart"/>
        </StackPanel>
    </Grid>
</UserControl>
```

- [ ] **Step 4: `RestoreGate.xaml.cs` yaz**

`OrderDeck.App/Views/Gates/RestoreGate.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// RestoreDialog'un gate karşılığı. İki durum tek view'da: yedek seçimi ve
/// "tamamlandı".
///
/// Yeniden başlatmayı BURASI yapmıyor — gate true ile kapanıyor, kararı
/// StartupFlow veriyor (IStartupEnvironment.RequestRestart). Akışın tamamı
/// böylece headless test edilebiliyor.
/// </summary>
public partial class RestoreGate : UserControl
{
    private readonly AppGate _gate;

    private RestoreGate(AppGate gate, RestoreDialogViewModel? vm)
    {
        InitializeComponent();
        _gate = gate;
        DataContext = vm;
    }

    public static RestoreGate Create(AppGate gate, RestoreDialogViewModel? vm)
        => new(gate, vm);

    private void OnSkip(object sender, RoutedEventArgs e) => _gate.Close(false);

    private void OnRestart(object sender, RoutedEventArgs e) => _gate.Close(true);
}
```

- [ ] **Step 5: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/Views/Gates/RestoreGate.xaml OrderDeck.App/Views/Gates/RestoreGate.xaml.cs OrderDeck.Tests/App/GateCompositionTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): RestoreGate — geri yükleme tam ekrana

Üç eylem korundu. 📅 emojisi "Aylık" çipe döndü (spec §10), #25D366 /
DarkBlue / Gray token'a döndü.

"Tamamlandı" durumu yeni: eskiden MessageBox çıkıp uygulama kapanıyor,
operatörün elle yeniden açması gerekiyordu. Artık aynı ekran tek "Yeniden
Başlat" düğmesine dönüşüyor. Yeniden başlatma kararı gate'te değil
StartupFlow'da — akış headless test edilebilsin diye.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `FirstRunGate`

Sihirbaz altı adımıyla olduğu gibi taşınıyor; VM'in adım mantığına
DOKUNULMUYOR. Tek davranış değişikliği 2. adımdaki "Lisansı etkinleştir":
bugün `LoginDialog` penceresini `ShowDialog()` ile açıyor, artık aynı
`LoginGate`'i sihirbazın ÜSTÜNE yığıyor ve kapanınca operatör bıraktığı
adımda kalıyor (spec §4.4).

Bu gate `OD.Layout.GateColumnWide` (760) kullanıyor — diğer dördü 440. Sebep:
5. adım Chrome eklentisi kurulum rehberi, dört kutu ve iki sütunlu satırlar
taşıyor; 440'a sıkıştırılırsa her satır sarılıp adım listesi okunmaz oluyor.

**Files:**
- Create: `OrderDeck.App/Views/Gates/FirstRunGate.xaml`
- Create: `OrderDeck.App/Views/Gates/FirstRunGate.xaml.cs`
- Modify: `OrderDeck.App/ViewModels/FirstRunWizardViewModel.cs:109-116`
- Test: `OrderDeck.Tests/App/GateCompositionTests.cs`

**`FirstRunWizard.xaml` → `FirstRunGate.xaml` dönüşüm tablosu**

| Eski satır | Eski | Yeni |
|---|---|---|
| 1-14 | `Window` (780×600, `ResizeMode=NoResize`) + zorunlu `Background`/`Foreground` | `UserControl`; zemin `GateHost` Border'ından geliyor, açıklama notu düşüyor |
| 23-30 | `🎁 OrderDeck'e Hoş Geldin` + `#FFFFD166` | `<gates:GateBrand/>` + `OD.Text.Title` (emoji düştü — spec §10) |
| 26 | adım etiketi `#FFAAAAAA` | `OD.Text.Hint` |
| 36 | `Border Background="#FF222222" CornerRadius="6" Padding="24"` | `Style="{StaticResource OD.Card}"` |
| 40,58,80,94,109 | adım başlığı `FontSize="18" FontWeight="SemiBold"` | `OD.Text.Section` |
| 41,59,81,95,110 | gövde `Foreground="#FFE0E0E0" LineHeight="22"` | `OD.Text.Label` |
| 51,87,99 | ipucu `#FFAAAAAA` | `OD.Text.Hint` |
| 62,98,114,133,144,156 | iç kutu `Background="#FF1A1A1A" CornerRadius="4" Padding="16"` | `Style="{StaticResource OD.Panel}"` + `OD.Pad.5` |
| 84,116,135,146,158 | alt başlık `#FFFFD166` | `OD.Text.Section` |
| 86 | `TextBox FontSize="14" Padding="8"` | `Style="{StaticResource OD.TextBox}"` |
| 100 | `ⓘ` işareti | düştü; kutu zaten ayrılmış (spec §10) |
| 125,196-197 | `FontFamily="Consolas, monospace" FontSize="11"` | `OD.Text.Mono` |
| 172-176 | doğrulama sonucu `#FFAAAAAA` → `#FF4ADE80` tetikleyicisi | `OD.Brush.TextMute` → `OD.Brush.Success`, tetikleyici aynen kalıyor |
| 190 | `🎉 Hazırsın` `#FF4ADE80` | `Hazırsın` + `OD.Text.Title`, `Foreground="{StaticResource OD.Brush.Success}"` |
| 206-213 | "Daha sonra hallederim" `Foreground="#FFAAAAAA" Background=Transparent BorderThickness=0` | `Style="{StaticResource OD.Button.Ghost}"` |
| 216-219 | "Geri" `Padding="14,6"` | `OD.Button.Secondary` |
| 221-225 | "İleri" | `OD.Button.Primary` |
| 227-232 | "Bitir" `Background="#FF22C55E" Foreground="White"` | `OD.Button.Primary` (yeşil tonu paletin dışında — spec §10 kapalı küme) |

Adım panellerinin metinleri, `Visibility` bağlamaları ve komut adları
BİREBİR aynı kalıyor.

- [ ] **Step 1: Testi genişlet (kırmızı)**

`GateCompositionTests.cs`, `Gate_layer_composes` gövdesinin sonuna:

```csharp
            var wizardPending = gates.ShowAsync(g => FirstRunGate.Create(g, vm: null));
            Assert.IsType<FirstRunGate>(gates.Top!.Content);
            gates.Top.Close(true);
            Assert.True(wizardPending.IsCompleted);
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: derleme hatası — `FirstRunGate` yok.

- [ ] **Step 3: `FirstRunGate.xaml` yaz**

`OrderDeck.App/Views/Gates/FirstRunGate.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.Gates.FirstRunGate"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:gates="clr-namespace:OrderDeck.App.Views.Gates">
    <!-- FirstRunWizard.xaml'in gate karşılığı.

         O pencerenin başındaki uzun "Background BURADA verilmek zorunda"
         notu ARTIK GEÇERSİZ: örtük Window stilinin türetilmiş tipe
         uygulanmaması sorunu pencere kalmayınca ortadan kalktı — zemini
         GateHost Border'ı veriyor.

         Altı adım, adım metinleri ve VM bağlamaları aynen korundu; yalnız
         renk/ölçü token'a, emoji (🎁 🎉 ⓘ) metne döndü (spec §10).

         Genişlik 760 (GateColumnWide): 5. adımın kurulum rehberi 440'a
         sığmıyor. -->
    <Grid Width="{StaticResource OD.Layout.GateColumnWide}"
          HorizontalAlignment="Center"
          Margin="{StaticResource OD.Pad.7}">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>   <!-- marka + adım etiketi -->
            <RowDefinition Height="*"/>      <!-- adım içeriği -->
            <RowDefinition Height="Auto"/>   <!-- gezinme -->
        </Grid.RowDefinitions>

        <DockPanel Grid.Row="0" Margin="{StaticResource OD.Pad.Bottom5}">
            <TextBlock DockPanel.Dock="Right"
                       Text="{Binding StepLabel}"
                       Style="{StaticResource OD.Text.Hint}"
                       VerticalAlignment="Bottom"/>
            <gates:GateBrand/>
        </DockPanel>

        <Border Grid.Row="1" Style="{StaticResource OD.Card}">
            <Grid>
                <!-- Adım 1: Hoş geldin -->
                <StackPanel Visibility="{Binding IsStep1, Converter={StaticResource BoolToVisibleConverter}}">
                    <TextBlock Text="Kuruluma başlıyoruz" Style="{StaticResource OD.Text.Section}"/>
                    <TextBlock Style="{StaticResource OD.Text.Label}"
                               Text="Bu sihirbaz seni 5 adımda OrderDeck'i yayına hazır hale getirir:"/>
                    <StackPanel Margin="{StaticResource OD.Pad.Top5}">
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• Lisans etkinleştirme"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• YouTube kanal ayarı (opsiyonel)"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• Yazıcı ayarları (opsiyonel)"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• Chrome eklentisi kurulumu"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• Tamam, hazırsın!"/>
                    </StackPanel>
                    <TextBlock Style="{StaticResource OD.Text.Hint}"
                               Margin="{StaticResource OD.Pad.Top5}"
                               Text="Tüm adımlar daha sonra Ayarlar sayfasından da değiştirilebilir. Atlamak istediğin adımları geçebilirsin."/>
                </StackPanel>

                <!-- Adım 2: Lisans -->
                <StackPanel Visibility="{Binding IsStep2, Converter={StaticResource BoolToVisibleConverter}}">
                    <TextBlock Text="Lisans" Style="{StaticResource OD.Text.Section}"/>
                    <TextBlock Style="{StaticResource OD.Text.Label}"
                               Text="OrderDeck'i kullanmak için lisansı etkinleştir veya 14 günlük denemeyi başlat."/>
                    <Border Style="{StaticResource OD.Panel}"
                            Padding="{StaticResource OD.Pad.5}"
                            Margin="{StaticResource OD.Pad.Top5}">
                        <DockPanel>
                            <!-- Düğme ÖNCE: DockPanel'de son çocuk kalan alanı
                                 doldurur, durum metni sağa taşmasın diye. -->
                            <Button DockPanel.Dock="Right"
                                    Content="Lisansı etkinleştir"
                                    Command="{Binding ActivateLicenseCommand}"
                                    Style="{StaticResource OD.Button.Primary}"
                                    Visibility="{Binding IsLicenseActivated, Converter={StaticResource BoolToCollapsedConverter}}"/>
                            <TextBlock Text="{Binding LicenseStatusText}"
                                       Style="{StaticResource OD.Text.Section}"
                                       Margin="0"
                                       VerticalAlignment="Center"/>
                        </DockPanel>
                    </Border>
                </StackPanel>

                <!-- Adım 3: YouTube -->
                <StackPanel Visibility="{Binding IsStep3, Converter={StaticResource BoolToVisibleConverter}}">
                    <TextBlock Text="YouTube Kanal Bağlantısı" Style="{StaticResource OD.Text.Section}"/>
                    <TextBlock Style="{StaticResource OD.Text.Label}"
                               Text="YouTube canlı yayınlarındaki chat mesajlarını OrderDeck'e çekmek için kanal handle'ını gir. Boş bırakırsan sadece Instagram/TikTok için Chrome eklentisi ile çalışır."/>
                    <TextBlock Text="Kanal handle'ı veya URL'si"
                               Style="{StaticResource OD.Text.Label}"
                               Margin="{StaticResource OD.Pad.Top5}"/>
                    <TextBox Text="{Binding YouTubeHandle, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource OD.TextBox}"/>
                    <TextBlock Style="{StaticResource OD.Text.Hint}"
                               Text="Örnek: @orderdeck veya https://youtube.com/@orderdeck"/>
                </StackPanel>

                <!-- Adım 4: Yazıcı -->
                <StackPanel Visibility="{Binding IsStep4, Converter={StaticResource BoolToVisibleConverter}}">
                    <TextBlock Text="Yazıcı Ayarları" Style="{StaticResource OD.Text.Section}"/>
                    <TextBlock Style="{StaticResource OD.Text.Label}"
                               Text="Etiket yazdırma için kullanacağın yazıcının ayarları Ayarlar → Yazıcı sekmesinden yapılır. Şimdi atlayabilir, sonra ayarlayabilirsin — yayın sırasında ayar dışındaki tüm özellikler çalışır."/>
                    <Border Style="{StaticResource OD.Panel}"
                            Padding="{StaticResource OD.Pad.5}"
                            Margin="{StaticResource OD.Pad.Top5}">
                        <TextBlock Style="{StaticResource OD.Text.Hint}"
                                   Margin="0"
                                   Text="Yazıcı seçimi ve etiket boyutu Ayarlar sayfasında yapılır. Sihirbaz bittikten sonra sol raydan Ayarlar'ı aç."/>
                    </Border>
                </StackPanel>

                <!-- Adım 5: Chrome eklentisi -->
                <ScrollViewer Visibility="{Binding IsStep5, Converter={StaticResource BoolToVisibleConverter}}"
                              VerticalScrollBarVisibility="Auto">
                    <StackPanel>
                        <TextBlock Text="Chrome Eklentisi Kurulumu" Style="{StaticResource OD.Text.Section}"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}"
                                   Text="Instagram / TikTok canlı yayın chat'i için Chrome eklentisini kurman gerekiyor. (YouTube ve Facebook eklenti olmadan, doğrudan OrderDeck'ten çalışır.) Aşağıdaki adımları sırasıyla uygula:"/>

                        <Border Style="{StaticResource OD.Panel}"
                                Padding="{StaticResource OD.Pad.5}"
                                Margin="{StaticResource OD.Pad.Top5}">
                            <StackPanel>
                                <TextBlock Text="1. Eklenti klasörünü hazırla"
                                           Style="{StaticResource OD.Text.Section}"/>
                                <DockPanel>
                                    <Button DockPanel.Dock="Right"
                                            Content="Klasörü Aç"
                                            Command="{Binding OpenExtensionFolderCommand}"
                                            Style="{StaticResource OD.Button.Secondary}"/>
                                    <TextBlock Text="{Binding ExtensionPath}"
                                               Style="{StaticResource OD.Text.Mono}"
                                               TextWrapping="Wrap"
                                               VerticalAlignment="Center"/>
                                </DockPanel>
                            </StackPanel>
                        </Border>

                        <Border Style="{StaticResource OD.Panel}"
                                Padding="{StaticResource OD.Pad.5}"
                                Margin="{StaticResource OD.Pad.Top4}">
                            <StackPanel>
                                <TextBlock Text="2. Chrome'da chrome://extensions sayfasını aç"
                                           Style="{StaticResource OD.Text.Section}"/>
                                <Button Content="Chrome'da Eklentiler Sayfasını Aç"
                                        Command="{Binding OpenChromeExtensionsPageCommand}"
                                        Style="{StaticResource OD.Button.Secondary}"
                                        HorizontalAlignment="Left"/>
                            </StackPanel>
                        </Border>

                        <Border Style="{StaticResource OD.Panel}"
                                Padding="{StaticResource OD.Pad.5}"
                                Margin="{StaticResource OD.Pad.Top4}">
                            <StackPanel>
                                <TextBlock Text="3. Sayfada şu adımları uygula"
                                           Style="{StaticResource OD.Text.Section}"/>
                                <TextBlock Style="{StaticResource OD.Text.Label}" Text="a) Sağ üstte Geliştirici modu'nu aç"/>
                                <TextBlock Style="{StaticResource OD.Text.Label}" Text="b) Sol üstte Paketlenmemiş öğe yükle butonuna bas"/>
                                <TextBlock Style="{StaticResource OD.Text.Label}" Text="c) Açılan klasör seçicide adım 1'deki Extension klasörünü seç"/>
                                <TextBlock Style="{StaticResource OD.Text.Label}" Text="d) Eklenti listesinde OrderDeck Chat Bridge görünmeli"/>
                            </StackPanel>
                        </Border>

                        <Border Style="{StaticResource OD.Panel}"
                                Padding="{StaticResource OD.Pad.5}"
                                Margin="{StaticResource OD.Pad.Top4}">
                            <StackPanel>
                                <TextBlock Text="4. Bağlantıyı doğrula"
                                           Style="{StaticResource OD.Text.Section}"/>
                                <DockPanel>
                                    <Button DockPanel.Dock="Right"
                                            Content="Doğrula"
                                            Command="{Binding VerifyExtensionAsyncCommand}"
                                            Style="{StaticResource OD.Button.Secondary}"/>
                                    <TextBlock Text="{Binding ExtensionVerifyResult}"
                                               TextWrapping="Wrap"
                                               VerticalAlignment="Center">
                                        <TextBlock.Style>
                                            <Style TargetType="TextBlock" BasedOn="{StaticResource OD.Text.Hint}">
                                                <Setter Property="Margin" Value="0"/>
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding IsExtensionConnected}" Value="True">
                                                        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Success}"/>
                                                        <Setter Property="FontWeight" Value="SemiBold"/>
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </TextBlock.Style>
                                    </TextBlock>
                                </DockPanel>
                            </StackPanel>
                        </Border>
                    </StackPanel>
                </ScrollViewer>

                <!-- Adım 6: Bitti -->
                <StackPanel Visibility="{Binding IsStep6, Converter={StaticResource BoolToVisibleConverter}}">
                    <TextBlock Text="Hazırsın"
                               Style="{StaticResource OD.Text.Title}"
                               Foreground="{StaticResource OD.Brush.Success}"
                               Margin="{StaticResource OD.Pad.Bottom4}"/>
                    <TextBlock Style="{StaticResource OD.Text.Label}"
                               Text="OrderDeck kuruldu ve yayına hazır. Şimdi yapabileceklerin:"/>
                    <StackPanel Margin="{StaticResource OD.Pad.Top5}">
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• Üst şeritten Yayın Başlat düğmesine bas"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• OBS'de browser source URL'si:"/>
                        <TextBlock Style="{StaticResource OD.Text.Mono}" Text="http://localhost:4747/overlay/chat"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• Çekiliş animasyonu:"/>
                        <TextBlock Style="{StaticResource OD.Text.Mono}" Text="http://localhost:4747/overlay/giveaway"/>
                        <TextBlock Style="{StaticResource OD.Text.Label}" Text="• Sorun çıkarsa: Ayarlar → Logları Aç"/>
                    </StackPanel>
                </StackPanel>
            </Grid>
        </Border>

        <DockPanel Grid.Row="2" Margin="{StaticResource OD.Pad.Top5}">
            <Button DockPanel.Dock="Left"
                    Content="Daha sonra hallederim"
                    Command="{Binding SkipFirstRunCommand}"
                    Style="{StaticResource OD.Button.Ghost}"
                    Visibility="{Binding IsStep6, Converter={StaticResource BoolToCollapsedConverter}}"/>

            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                <Button Content="Geri"
                        Command="{Binding BackCommand}"
                        Style="{StaticResource OD.Button.Secondary}"
                        IsEnabled="{Binding CanGoBack}"/>
                <Button Content="İleri"
                        Command="{Binding NextCommand}"
                        Style="{StaticResource OD.Button.Primary}"
                        Margin="{StaticResource OD.Pad.Left4}"
                        Visibility="{Binding IsStep6, Converter={StaticResource BoolToCollapsedConverter}}"
                        IsDefault="True"/>
                <Button Content="Bitir"
                        Command="{Binding FinishCommand}"
                        Style="{StaticResource OD.Button.Primary}"
                        Margin="{StaticResource OD.Pad.Left4}"
                        Visibility="{Binding IsStep6, Converter={StaticResource BoolToVisibleConverter}}"
                        IsDefault="True"/>
            </StackPanel>

            <TextBlock/>
        </DockPanel>
    </Grid>
</UserControl>
```

- [ ] **Step 4: `FirstRunGate.xaml.cs` yaz**

`OrderDeck.App/Views/Gates/FirstRunGate.xaml.cs`:

```csharp
using System;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// İlk açılış sihirbazı — tam ekran gate.
///
/// Gate <c>true</c> ile kapanırsa sihirbaz BİTİRİLDİ, <c>false</c> ile
/// kapanırsa atlandı. Ayrımı VM'in <see cref="FirstRunWizardViewModel.IsStep6"/>
/// bayrağından okuyoruz: hem "Bitir" hem "Daha sonra hallederim" aynı
/// <c>RequestClose</c> olayını yükseltiyor, tek ayırt edici o.
/// </summary>
public partial class FirstRunGate : UserControl
{
    private readonly AppGate _gate;
    private readonly FirstRunWizardViewModel? _vm;

    private FirstRunGate(AppGate gate, FirstRunWizardViewModel? vm)
    {
        InitializeComponent();
        _gate = gate;
        _vm = vm;
        DataContext = vm;
        if (vm is not null) vm.RequestClose += OnRequestClose;
    }

    /// <summary>vm null geçilebiliyor: GateCompositionTests ekranı hiçbir
    /// servis kurmadan render edip kaynak anahtarlarını doğruluyor.</summary>
    public static FirstRunGate Create(AppGate gate, FirstRunWizardViewModel? vm)
        => new(gate, vm);

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.RequestClose -= OnRequestClose;
        _gate.Close(_vm?.IsStep6 ?? false);
    }
}
```

- [ ] **Step 5: `ActivateLicense`'ı gate'e çevir**

`OrderDeck.App/ViewModels/FirstRunWizardViewModel.cs`, 109-116. satırları şununla
değiştir:

```csharp
    [RelayCommand]
    private async Task ActivateLicenseAsync()
    {
        // Faz 4a: LoginDialog penceresi yok. Aynı LoginGate ekranı sihirbazın
        // ÜSTÜNE yığılıyor; kapanınca yığın operatörü bıraktığı adıma geri
        // bırakıyor (AppGateStack yığın olduğu için — spec §4.4).
        //
        // isStartupGate:false → çıkış düğmesi "Çıkış" değil "Vazgeç";
        // vazgeçmek uygulamayı kapatmıyor, sihirbaza dönüyor.
        var gates = _services.GetRequiredService<Services.Gates.IAppGateService>();
        var loginVm = _services.GetRequiredService<LoginDialogViewModel>();
        await gates.ShowAsync(g => Views.Gates.LoginGate.Create(g, loginVm, isStartupGate: false));
        UpdateLicenseStepStatus();
    }
```

XAML bağlaması DEĞİŞMİYOR: CommunityToolkit `Async` sonekini atıyor, üretilen
komut adı yine `ActivateLicenseCommand`.

- [ ] **Step 6: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: PASS.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.App/Views/Gates/FirstRunGate.xaml OrderDeck.App/Views/Gates/FirstRunGate.xaml.cs OrderDeck.App/ViewModels/FirstRunWizardViewModel.cs OrderDeck.Tests/App/GateCompositionTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): FirstRunGate — kurulum sihirbazı tam ekrana

Altı adım, metinler ve VM bağlamaları aynen korundu. 🎁 🎉 ⓘ emojileri ve
altı sabit hex düştü (spec §10). Genişlik 760: 5. adımın kurulum rehberi
440'lık sütuna sığmıyor.

ActivateLicense artık pencere açmıyor, LoginGate'i sihirbazın üstüne
yığıyor — kapanınca operatör 2. adımda kalıyor. Pencere modelinde bu
"dialog üstüne dialog" demekti; yığın bunu doğal ifade ediyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: `SessionRecoveryGate`

Bugün bu ekran hiç yok: `App.xaml.cs:193-233` `MessageBox.Show(..., YesNoCancel)`
çağırıyor. Evet/Hayır/İptal'in hangisinin ne demek olduğu başlıktan
anlaşılmıyor ve yayının ne zaman başladığı görünmüyor. Gate üç düğmeyi
adıyla gösteriyor (spec §4.5).

**Files:**
- Create: `OrderDeck.App/Views/Gates/SessionRecoveryGate.xaml`
- Create: `OrderDeck.App/Views/Gates/SessionRecoveryGate.xaml.cs`
- Test: `OrderDeck.Tests/App/GateCompositionTests.cs`

Bu gate'in üç sonucu var, `AppGate.Close(bool)` ise ikili. Sonucu gate'in
kendi `Choice` özelliğinden okuyoruz; `WpfStartupGates` gate kapandıktan
sonra onu okuyup `SessionRecoveryChoice`'a çeviriyor (Task 9). Enum
`OrderDeck.App.Startup`'ta tanımlı — gate ile akış aynı sonuç tipini
paylaşsın diye.

- [ ] **Step 1: Testi genişlet (kırmızı)**

`GateCompositionTests.cs`, `Gate_layer_composes` gövdesinin sonuna:

```csharp
            var recoveryPending = gates.ShowAsync(g => SessionRecoveryGate.Create(g, session: null));
            var recoveryGate = Assert.IsType<SessionRecoveryGate>(gates.Top!.Content);
            Assert.Equal(SessionRecoveryChoice.Exit, recoveryGate.Choice);
            gates.Top.Close(true);
            Assert.True(recoveryPending.IsCompleted);
```

Varsayılan `Exit`: gate hiç seçim yapılmadan kapanırsa (üst gate zorla
kapatılırsa) en güvenli sonuç uygulamanın açılmaması, yarım bir shell'e
düşmemesi.

`GateCompositionTests.cs` using bloğuna ekle:

```csharp
using OrderDeck.App.Startup;
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: derleme hatası — `SessionRecoveryGate` ve `SessionRecoveryChoice` yok.

- [ ] **Step 3: `SessionRecoveryChoice`'ı yaz**

`OrderDeck.App/Startup/SessionRecoveryChoice.cs`:

```csharp
namespace OrderDeck.App.Startup;

/// <summary>
/// Yarım kalmış yayın oturumu bulunduğunda operatörün verdiği karar.
///
/// Ayrı dosyada: hem gate (View) hem StartupFlow (karar makinesi) bunu
/// kullanıyor ve ikisinin birbirini tanıması gerekmiyor.
/// </summary>
public enum SessionRecoveryChoice
{
    /// <summary>Seçim yapılmadan kapandı — uygulama açılmasın.</summary>
    Exit = 0,
    /// <summary>Oturuma devam et, sayaçlar kaldığı yerden işlesin.</summary>
    Continue,
    /// <summary>Oturumu kapat, yeni bir yayına temiz başla.</summary>
    EndSession
}
```

- [ ] **Step 4: `SessionRecoveryGate.xaml` yaz**

`OrderDeck.App/Views/Gates/SessionRecoveryGate.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.Gates.SessionRecoveryGate"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:gates="clr-namespace:OrderDeck.App.Views.Gates">
    <!-- Bugünkü MessageBox(YesNoCancel)'ın karşılığı.

         NEDEN GATE: MessageBox'ta düğmeler "Evet / Hayır / İptal" yazıyor,
         hangisinin yayını bitirdiği başlıktaki uzun metinden çıkarılmak
         zorunda. Burada üçü de eylemin adını taşıyor ve oturumun ne zaman
         başladığı görünüyor.

         Metinler kod-arkasında dolduruluyor (tarih biçimi + opsiyonel
         başlık), bu yüzden bağlama yok. -->
    <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center"
                Width="{StaticResource OD.Layout.GateColumn}">
        <gates:GateBrand/>

        <TextBlock Text="Yarım kalmış yayın var"
                   Style="{StaticResource OD.Text.Title}"
                   Margin="{StaticResource OD.Pad.Top7}"/>

        <Border Style="{StaticResource OD.Panel}"
                Padding="{StaticResource OD.Pad.5}"
                Margin="{StaticResource OD.Pad.Top5}">
            <StackPanel>
                <TextBlock x:Name="SessionTitle"
                           Style="{StaticResource OD.Text.Section}"/>
                <TextBlock x:Name="SessionStarted"
                           Style="{StaticResource OD.Text.Hint}"
                           Margin="0"/>
            </StackPanel>
        </Border>

        <TextBlock Style="{StaticResource OD.Text.Label}"
                   Margin="{StaticResource OD.Pad.Top5}"
                   Text="Uygulama önceki açılışta kapanmadan sonlandı. Bu oturuma devam edebilir ya da kapatıp temiz başlayabilirsin."/>

        <Button Content="Devam et"
                Style="{StaticResource OD.Button.Primary}"
                Margin="{StaticResource OD.Pad.Top5}"
                IsDefault="True"
                Click="OnContinue"/>
        <Button Content="Yayını bitir"
                Style="{StaticResource OD.Button.Secondary}"
                Margin="{StaticResource OD.Pad.Top3}"
                Click="OnEndSession"/>
        <Button Content="Çıkış"
                Style="{StaticResource OD.Button.Ghost}"
                HorizontalAlignment="Center"
                Margin="{StaticResource OD.Pad.Top4}"
                Click="OnExit"/>
    </StackPanel>
</UserControl>
```

- [ ] **Step 5: `SessionRecoveryGate.xaml.cs` yaz**

`OrderDeck.App/Views/Gates/SessionRecoveryGate.xaml.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.Startup;
using OrderDeck.Core.Sessions;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// Yarım kalmış yayın oturumu için üç yollu karar ekranı.
///
/// <see cref="AppGate.Close(bool)"/> ikili olduğu için sonuç burada
/// tutuluyor; akış gate kapandıktan sonra <see cref="Choice"/>'ı okuyor.
/// </summary>
public partial class SessionRecoveryGate : UserControl
{
    private readonly AppGate _gate;

    /// <summary>Operatörün kararı. Seçim yapılmadan kapanırsa
    /// <see cref="SessionRecoveryChoice.Exit"/> kalır — yarım bir shell'e
    /// düşmektense açılmamak daha güvenli.</summary>
    public SessionRecoveryChoice Choice { get; private set; } = SessionRecoveryChoice.Exit;

    private SessionRecoveryGate(AppGate gate, StreamSession? session)
    {
        InitializeComponent();
        _gate = gate;

        SessionTitle.Text = string.IsNullOrWhiteSpace(session?.Title)
            ? "Adsız yayın"
            : session!.Title;

        SessionStarted.Text = session is null
            ? "—"
            : "Başlangıç: " + DateTimeOffset
                .FromUnixTimeSeconds(session.StartedAt)
                .LocalDateTime
                .ToString("dd MMMM HH:mm", new CultureInfo("tr-TR"));
    }

    /// <summary>session null geçilebiliyor: GateCompositionTests ekranı
    /// hiçbir servis kurmadan render ediyor.</summary>
    public static SessionRecoveryGate Create(AppGate gate, StreamSession? session)
        => new(gate, session);

    private void OnContinue(object sender, RoutedEventArgs e) => Decide(SessionRecoveryChoice.Continue);

    private void OnEndSession(object sender, RoutedEventArgs e) => Decide(SessionRecoveryChoice.EndSession);

    private void OnExit(object sender, RoutedEventArgs e) => Decide(SessionRecoveryChoice.Exit);

    private void Decide(SessionRecoveryChoice choice)
    {
        Choice = choice;
        _gate.Close(choice != SessionRecoveryChoice.Exit);
    }
}
```

(`StreamSession` = `OrderDeck.Core.Sessions.StreamSession`,
`OrderDeck.Core/Sessions/StreamSession.cs:5` — doğrulandı.)

- [ ] **Step 6: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~GateCompositionTests`
Beklenen: PASS.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.App/Startup/SessionRecoveryChoice.cs OrderDeck.App/Views/Gates/SessionRecoveryGate.xaml OrderDeck.App/Views/Gates/SessionRecoveryGate.xaml.cs OrderDeck.Tests/App/GateCompositionTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): SessionRecoveryGate — yarım yayın kararı tam ekrana

MessageBox(YesNoCancel) yerine üç adlandırılmış düğme: Devam et / Yayını
bitir / Çıkış. Oturumun başlangıç saati ve başlığı artık görünüyor —
MessageBox'ta ikisi de yoktu.

Üç sonuç ikili Close()'a sığmadığı için karar gate'in Choice özelliğinde
duruyor; varsayılanı Exit, çünkü seçim yapılmadan kapanan bir gate'ten
sonra shell'i kurmak yarım duruma yol açar.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: `StartupFlow` — açılış sırasının karar makinesi

Bugün açılış sırası `App.xaml.cs:112-343`'te düz akış hâlinde duruyor ve
test edilemiyor: her adımı ya bir `ShowDialog()` ya bir `MessageBox` ya da
gerçek bir servis çağrısı. Faz 4a bu sırayı **saf bir sınıfa** taşıyor;
UI ve servisler iki arayüzün arkasında kalıyor, böylece "lisans yoksa
girişe git, giriş iptal edilirse shell'i KURMA" gibi kurallar STA'sız,
DB'siz, pencere açmadan test edilebiliyor.

Sıra **değişmiyor** — bugünkü sıranın birebir aynısı, yalnız
`ShowDialog()`'lar `await` oldu ve en sona `MountShell()` eklendi.

**Files:**
- Create: `OrderDeck.App/Startup/RestoreOutcome.cs`
- Create: `OrderDeck.App/Startup/IStartupGates.cs`
- Create: `OrderDeck.App/Startup/IStartupEnvironment.cs`
- Create: `OrderDeck.App/Startup/StartupFlow.cs`
- Test: `OrderDeck.Tests/App/StartupFlowTests.cs`

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.Tests/App/StartupFlowTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Startup;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.Tests.App;

/// <summary>
/// Açılış sırasının kuralları. STA GEREKMİYOR: StartupFlow ne pencere ne
/// servis tanıyor, ikisi de arayüz arkasında.
///
/// NEDEN BU TESTLER: bu sıra bugüne kadar hiç test edilemedi ve iki kez
/// sessizce bozuldu (StartupUri'nin OnStartup'tan sonra koşması, restore
/// sonrası uygulamanın kapanıp açılmaması). Kurallar artık burada kilitli.
/// </summary>
public class StartupFlowTests
{
    private static StreamSession Session(string id = "s1") =>
        new(id, "Akşam yayını", 1_700_000_000, null, new[] { "youtube" }, null);

    private static StartupFlow Build(FakeStartupGates gates, FakeStartupEnvironment env) =>
        new(gates, env, NullLogger<StartupFlow>.Instance);

    [Fact]
    public async Task Licensed_and_clean_start_mounts_the_shell()
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment();

        await Build(gates, env).RunAsync();

        Assert.True(gates.BootShown);
        Assert.False(gates.LoginShown);
        Assert.True(env.ShellMounted);
        Assert.False(env.ShutdownRequested);
        Assert.False(env.RestartRequested);
    }

    [Fact]
    public async Task Cancelled_login_shuts_down_without_mounting_the_shell()
    {
        var gates = new FakeStartupGates { LoginResult = false };
        var env = new FakeStartupEnvironment { HasLicense = false };

        await Build(gates, env).RunAsync();

        Assert.True(gates.LoginShown);
        Assert.True(env.ShutdownRequested);
        Assert.False(env.ShellMounted);
    }

    [Fact]
    public async Task Successful_login_continues_to_the_shell()
    {
        var gates = new FakeStartupGates { LoginResult = true };
        var env = new FakeStartupEnvironment { HasLicense = false };

        await Build(gates, env).RunAsync();

        Assert.True(env.ShellMounted);
        Assert.False(env.ShutdownRequested);
    }

    [Fact]
    public async Task Completed_restore_restarts_instead_of_mounting_the_shell()
    {
        var gates = new FakeStartupGates { RestoreResult = RestoreOutcome.Restored };
        var env = new FakeStartupEnvironment
        {
            DatabaseMissing = true,
            Backups = new[] { new BackupMetadata(Guid.NewGuid(), 4096, DateTimeOffset.UtcNow, false, "PC") }
        };

        await Build(gates, env).RunAsync();

        Assert.True(env.RestartRequested);
        Assert.False(env.ShellMounted);
    }

    [Fact]
    public async Task Skipped_restore_continues_to_the_shell()
    {
        var gates = new FakeStartupGates { RestoreResult = RestoreOutcome.Skipped };
        var env = new FakeStartupEnvironment
        {
            DatabaseMissing = true,
            Backups = new[] { new BackupMetadata(Guid.NewGuid(), 4096, DateTimeOffset.UtcNow, false, "PC") }
        };

        await Build(gates, env).RunAsync();

        Assert.False(env.RestartRequested);
        Assert.True(env.ShellMounted);
    }

    [Fact]
    public async Task Restore_gate_is_skipped_when_there_are_no_backups()
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { DatabaseMissing = true, Backups = Array.Empty<BackupMetadata>() };

        await Build(gates, env).RunAsync();

        Assert.False(gates.RestoreShown);
        Assert.True(env.ShellMounted);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task First_run_gate_follows_the_persisted_flag(bool completed, bool expectShown)
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { HasCompletedFirstRun = completed };

        await Build(gates, env).RunAsync();

        Assert.Equal(expectShown, gates.FirstRunShown);
    }

    [Fact]
    public async Task Session_recovery_exit_shuts_down()
    {
        var gates = new FakeStartupGates { RecoveryResult = SessionRecoveryChoice.Exit };
        var env = new FakeStartupEnvironment { ActiveSession = Session() };

        await Build(gates, env).RunAsync();

        Assert.True(env.ShutdownRequested);
        Assert.False(env.ShellMounted);
        Assert.Null(env.EndedSessionId);
    }

    [Fact]
    public async Task Session_recovery_end_closes_the_session_and_continues()
    {
        var gates = new FakeStartupGates { RecoveryResult = SessionRecoveryChoice.EndSession };
        var env = new FakeStartupEnvironment { ActiveSession = Session("abc") };

        await Build(gates, env).RunAsync();

        Assert.Equal("abc", env.EndedSessionId);
        Assert.True(env.ShellMounted);
    }

    [Fact]
    public async Task Session_recovery_continue_leaves_the_session_open()
    {
        var gates = new FakeStartupGates { RecoveryResult = SessionRecoveryChoice.Continue };
        var env = new FakeStartupEnvironment { ActiveSession = Session("abc") };

        await Build(gates, env).RunAsync();

        Assert.Null(env.EndedSessionId);
        Assert.True(env.ShellMounted);
    }

    [Fact]
    public async Task Background_service_failure_leaves_the_shell_unmounted()
    {
        // Port çakışması: bugün MessageBox + Shutdown(). Ortam false döndürüp
        // kapatmayı kendi üstleniyor; akışın tek işi shell'i KURMAMAK.
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { BackgroundServicesStart = false };

        await Build(gates, env).RunAsync();

        Assert.False(env.ShellMounted);
    }

    [Fact]
    public async Task License_initialization_failure_does_not_stop_startup()
    {
        // Bugünkü davranış: hata loglanır, akış devam eder (çevrimdışı
        // makinede uygulama yine de açılmalı).
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { LicenseInitThrows = true };

        await Build(gates, env).RunAsync();

        Assert.True(env.ShellMounted);
    }

    // ── Sahteler ──────────────────────────────────────────────────────

    private sealed class FakeStartupGates : IStartupGates
    {
        public bool BootShown, LoginShown, RestoreShown, FirstRunShown, RecoveryShown;
        public bool LoginResult = true;
        public RestoreOutcome RestoreResult = RestoreOutcome.Skipped;
        public SessionRecoveryChoice RecoveryResult = SessionRecoveryChoice.Continue;

        public async Task ShowBootAsync(Func<Task> work)
        {
            BootShown = true;
            await work();
        }

        public Task<bool> ShowLoginAsync(bool isStartupGate)
        {
            LoginShown = true;
            return Task.FromResult(LoginResult);
        }

        public Task<RestoreOutcome> ShowRestoreAsync(IReadOnlyList<BackupMetadata> backups)
        {
            RestoreShown = true;
            return Task.FromResult(RestoreResult);
        }

        public Task ShowFirstRunAsync()
        {
            FirstRunShown = true;
            return Task.CompletedTask;
        }

        public Task<SessionRecoveryChoice> ShowSessionRecoveryAsync(StreamSession session)
        {
            RecoveryShown = true;
            return Task.FromResult(RecoveryResult);
        }
    }

    private sealed class FakeStartupEnvironment : IStartupEnvironment
    {
        public bool HasLicense { get; set; } = true;
        public bool HasCompletedFirstRun { get; set; } = true;
        public bool DatabaseMissing { get; set; }
        public bool LicenseInitThrows { get; set; }
        public bool BackgroundServicesStart { get; set; } = true;
        public IReadOnlyList<BackupMetadata> Backups { get; set; } = Array.Empty<BackupMetadata>();
        public StreamSession? ActiveSession { get; set; }

        public bool ShellMounted, ShutdownRequested, RestartRequested;
        public string? EndedSessionId;

        public Task InitializeLicenseAsync() =>
            LicenseInitThrows
                ? Task.FromException(new InvalidOperationException("lisans sunucusuna ulaşılamadı"))
                : Task.CompletedTask;

        public bool IsDatabaseMissingOrTiny() => DatabaseMissing;

        public Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync() => Task.FromResult(Backups);

        public StreamSession? GetActiveSession() => ActiveSession;

        public void EndSession(string sessionId) => EndedSessionId = sessionId;

        public Task<bool> StartBackgroundServicesAsync() => Task.FromResult(BackgroundServicesStart);

        public void MountShell() => ShellMounted = true;

        public void RequestShutdown() => ShutdownRequested = true;

        public void RequestRestart() => RestartRequested = true;
    }
}
```

- [ ] **Step 2: Testin kırmızı olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StartupFlowTests`
Beklenen: derleme hatası — `StartupFlow`, `IStartupGates`, `IStartupEnvironment`,
`RestoreOutcome` yok.

- [ ] **Step 3: `RestoreOutcome`'ı yaz**

`OrderDeck.App/Startup/RestoreOutcome.cs`:

```csharp
namespace OrderDeck.App.Startup;

/// <summary>Geri yükleme gate'inin sonucu.</summary>
public enum RestoreOutcome
{
    /// <summary>Operatör atladı; boş veritabanıyla devam.</summary>
    Skipped = 0,
    /// <summary>Yedek indirildi ve yazıldı; uygulama yeniden başlamalı.</summary>
    Restored
}
```

- [ ] **Step 4: `IStartupGates`'i yaz**

`OrderDeck.App/Startup/IStartupGates.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Startup;

/// <summary>
/// Açılış sırasının UI tarafı. StartupFlow bu arayüzün ardında ne
/// olduğunu bilmez; üretimde <c>WpfStartupGates</c> gate yığınına basar,
/// testte sahte bir kayıt tutucu.
/// </summary>
public interface IStartupGates
{
    /// <summary>
    /// Açılış ekranını gösterir, <paramref name="work"/> bitene kadar
    /// bekler, sonra ekranı kapatır. İşin fırlattığı hata AYNEN yukarı
    /// çıkar — gate kapandıktan sonra.
    ///
    /// <c>Func&lt;Task&gt;</c>, <c>Task</c> değil: iş ekran görünmeden
    /// başlamasın.
    /// </summary>
    Task ShowBootAsync(Func<Task> work);

    /// <summary>true = lisans alındı. isStartupGate:false runtime'da
    /// (hesap değiştirme / sihirbaz) kullanılır.</summary>
    Task<bool> ShowLoginAsync(bool isStartupGate);

    Task<RestoreOutcome> ShowRestoreAsync(IReadOnlyList<BackupMetadata> backups);

    Task ShowFirstRunAsync();

    Task<SessionRecoveryChoice> ShowSessionRecoveryAsync(StreamSession session);
}
```

- [ ] **Step 5: `IStartupEnvironment`'ı yaz**

`OrderDeck.App/Startup/IStartupEnvironment.cs`:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Startup;

/// <summary>
/// Açılış sırasının servis tarafı: lisans, veritabanı, yedek, oturum,
/// arka plan servisleri ve uygulama ömrü.
///
/// StartupFlow'un servisleri doğrudan çağırmamasının sebebi test değil
/// yalnızca: bu arayüz aynı zamanda "açılışta neye dokunuluyor"un tam
/// listesi. Yeni bir açılış adımı buraya bir üye eklemeden yazılamaz.
/// </summary>
public interface IStartupEnvironment
{
    Task InitializeLicenseAsync();

    /// <summary>Lisans başlatıldıktan SONRA okunur.</summary>
    bool HasLicense { get; }

    /// <summary>Yerel DB yok ya da 10 KB'ın altında (boş şema).</summary>
    bool IsDatabaseMissingOrTiny();

    Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync();

    bool HasCompletedFirstRun { get; }

    StreamSession? GetActiveSession();

    void EndSession(string sessionId);

    /// <summary>
    /// Overlay + köprü + hosted service'leri başlatır. false = ölümcül
    /// hata (port çakışması); kapatmayı uygulama KENDİ yapar, akışın işi
    /// yalnızca shell'i kurmamaktır.
    /// </summary>
    Task<bool> StartBackgroundServicesAsync();

    /// <summary>Shell'i kök görünüme yerleştirir — akışın son adımı.</summary>
    void MountShell();

    void RequestShutdown();

    /// <summary>Uygulamayı kapatıp yeniden açar (geri yükleme sonrası).</summary>
    void RequestRestart();
}
```

- [ ] **Step 6: `StartupFlow`'u yaz**

`OrderDeck.App/Startup/StartupFlow.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Startup;

/// <summary>
/// Açılış sırası. Bugün <c>App.OnStartup</c> içinde düz akış hâlinde duran
/// ve hiç test edilemeyen mantığın tamamı burada.
///
/// SIRA DEĞİŞMEDİ: lisans → giriş → geri yükleme → sihirbaz → oturum
/// kurtarma → arka plan servisleri. Tek eklenen son adım
/// <see cref="IStartupEnvironment.MountShell"/>: pencere artık en başta
/// açıldığı için shell'in ne zaman kurulacağına burası karar veriyor.
///
/// Üç blok (geri yükleme, sihirbaz, oturum kurtarma) hatayı yutup devam
/// ediyor — bugünkü davranışın aynısı. Gerekçe: hiçbiri uygulamanın
/// açılmasını engelleyecek kadar kritik değil; bulut erişilemiyorsa
/// operatör yine de yayın yapabilmeli.
/// </summary>
public sealed class StartupFlow
{
    private readonly IStartupGates _gates;
    private readonly IStartupEnvironment _env;
    private readonly ILogger<StartupFlow> _log;

    public StartupFlow(IStartupGates gates, IStartupEnvironment env, ILogger<StartupFlow> log)
    {
        _gates = gates;
        _env = env;
        _log = log;
    }

    public async Task RunAsync()
    {
        // ── Lisans ────────────────────────────────────────────────────
        try
        {
            await _gates.ShowBootAsync(_env.InitializeLicenseAsync);
        }
        catch (Exception ex)
        {
            // Çevrimdışı makinede uygulama yine de açılmalı; durum
            // OfflineGrace'e düşer.
            _log.LogError(ex, "License initialization failed");
        }

        if (!_env.HasLicense)
        {
            var licensed = await _gates.ShowLoginAsync(isStartupGate: true);
            if (!licensed)
            {
                _env.RequestShutdown();
                return;
            }
        }

        // ── Geri yükleme ──────────────────────────────────────────────
        try
        {
            if (_env.IsDatabaseMissingOrTiny())
            {
                var backups = await _env.ListBackupsAsync();
                if (backups.Count > 0 &&
                    await _gates.ShowRestoreAsync(backups) == RestoreOutcome.Restored)
                {
                    // Yazılan DB'nin süreç boyunca açık tutulan bağlantılarla
                    // tutarlı olmasının tek yolu yeniden başlamak.
                    _env.RequestRestart();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore auto-prompt failed (non-fatal)");
        }

        // ── İlk açılış sihirbazı ──────────────────────────────────────
        try
        {
            if (!_env.HasCompletedFirstRun)
                await _gates.ShowFirstRunAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "First-run wizard failed (non-fatal)");
        }

        // ── Yarım kalmış yayın ────────────────────────────────────────
        try
        {
            var active = _env.GetActiveSession();
            if (active is not null)
            {
                var choice = await _gates.ShowSessionRecoveryAsync(active);
                if (choice == SessionRecoveryChoice.Exit)
                {
                    _env.RequestShutdown();
                    return;
                }
                if (choice == SessionRecoveryChoice.EndSession)
                    _env.EndSession(active.Id);
                // Continue → oturum açık kalıyor; MainShellViewModel
                // ReloadQueueFromActiveSession ile kuyruğu geri yüklüyor.
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Session recovery prompt failed (non-fatal)");
        }

        // ── Arka plan servisleri ve shell ─────────────────────────────
        if (!await _env.StartBackgroundServicesAsync())
            return;

        _env.MountShell();
    }
}
```

- [ ] **Step 7: Testin yeşil olduğunu gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StartupFlowTests`
Beklenen: 13 test PASS.

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.App/Startup OrderDeck.Tests/App/StartupFlowTests.cs
git commit -m "$(cat <<'EOF'
feat(shell): açılış sırasını StartupFlow'a taşı

Sıra aynı (lisans → giriş → geri yükleme → sihirbaz → oturum kurtarma →
servisler), ama artık App.OnStartup'ın içinde düz akış değil, iki arayüzün
ardında saf bir karar makinesi. Böylece STA'sız, DB'siz, pencere açmadan
test edilebiliyor — bu sıra bugüne kadar hiç test edilemedi ve iki kez
sessizce bozuldu.

Tek yeni adım MountShell(): pencere en başta açıldığı için shell'in ne
zaman kurulacağına artık akış karar veriyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Bağlama — açılış sırası tersine

Fazın kilit taşı: `MainWindow` ARTIK ÖNCE açılıyor, üç `ShowDialog()` ve
bir `MessageBox` gate'lere dönüşüyor, eski üç pencere siliniyor.

Bu görevde TDD adımı yok — burada yeni davranış yazılmıyor, Task 1-9'da
yazılmış parçalar birbirine takılıyor. Doğrulama derleme + tüm test paketi
(Task 11) ve elle açılış turu.

**Files:**
- Create: `OrderDeck.App/Startup/WpfStartupGates.cs`
- Create: `OrderDeck.App/Startup/WpfStartupEnvironment.cs`
- Modify: `OrderDeck.App/MainWindow.xaml`, `OrderDeck.App/MainWindow.xaml.cs`
- Modify: `OrderDeck.App/AppHost.cs:519-528`
- Modify: `OrderDeck.App/App.xaml.cs:72-75,112-343,472-500`
- Modify: `OrderDeck.App/ViewModels/AccountDialogViewModel.cs:35,96-105`
- Delete: `OrderDeck.App/Views/LoginDialog.xaml(.cs)`, `RestoreDialog.xaml(.cs)`, `FirstRunWizard.xaml(.cs)`

- [ ] **Step 1: `WpfStartupGates`'i yaz**

`OrderDeck.App/Startup/WpfStartupGates.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Gates;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Startup;

/// <summary>
/// <see cref="IStartupGates"/>'in gerçek uygulaması: her adımı gate
/// yığınına basar. ViewModel'ler burada çözülüyor — StartupFlow'un DI
/// tanımamasının bedeli bu ince katman.
/// </summary>
public sealed class WpfStartupGates : IStartupGates
{
    private readonly IAppGateService _gates;
    private readonly IServiceProvider _services;

    public WpfStartupGates(IAppGateService gates, IServiceProvider services)
    {
        _gates = gates;
        _services = services;
    }

    public async Task ShowBootAsync(Func<Task> work)
    {
        // ShowAsync içerik kurucusunu SENKRON çağırıyor (AppGateStack), bu
        // yüzden gate referansı await'ten önce elimizde oluyor.
        AppGate? opened = null;
        var pending = _gates.ShowAsync(g =>
        {
            opened = g;
            return new BootGate();
        });

        try
        {
            await work();
        }
        finally
        {
            opened?.Close(true);
            await pending;
        }
    }

    public Task<bool> ShowLoginAsync(bool isStartupGate)
    {
        var vm = _services.GetRequiredService<LoginDialogViewModel>();
        return _gates.ShowAsync(g => LoginGate.Create(g, vm, isStartupGate));
    }

    public async Task<RestoreOutcome> ShowRestoreAsync(IReadOnlyList<BackupMetadata> backups)
    {
        // RestoreDialogViewModel DI'da kayıtlı DEĞİL (eski pencere de elle
        // kuruyordu) ve yedek listesini dışarıdan alması gerekiyor.
        var vm = new RestoreDialogViewModel(_services.GetRequiredService<RestoreService>());
        vm.Populate(backups);

        var restored = await _gates.ShowAsync(g => RestoreGate.Create(g, vm));
        return restored ? RestoreOutcome.Restored : RestoreOutcome.Skipped;
    }

    public Task ShowFirstRunAsync()
    {
        var vm = _services.GetRequiredService<FirstRunWizardViewModel>();
        return _gates.ShowAsync(g => FirstRunGate.Create(g, vm));
    }

    public async Task<SessionRecoveryChoice> ShowSessionRecoveryAsync(StreamSession session)
    {
        // Üç sonuç ikili Close()'a sığmıyor; karar gate'in üzerinde duruyor.
        SessionRecoveryGate? view = null;
        await _gates.ShowAsync(g => view = SessionRecoveryGate.Create(g, session));
        return view?.Choice ?? SessionRecoveryChoice.Exit;
    }
}
```

- [ ] **Step 2: `WpfStartupEnvironment`'ı yaz**

Bu sınıf `App.xaml.cs:235-343`'teki arka plan servis başlatmayı ve
`490-500`'deki durdurmayı OLDUĞU GİBİ devralıyor — port çakışması
`MessageBox`'ları dahil (spec §5: bunlar kalıyor). Dört servis alanı da
buraya taşınıyor; başlatma ve durdurma aynı sınıfta durmalı.

`OrderDeck.App/Startup/WpfStartupEnvironment.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services;
using OrderDeck.App.Shortcuts;
using OrderDeck.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Backup;
using OrderDeck.Licensing.Services;

namespace OrderDeck.App.Startup;

/// <summary>
/// <see cref="IStartupEnvironment"/>'ın gerçek uygulaması. Arka plan
/// servislerinin ömrü (başlat + durdur) buraya taşındı: eskiden App'in
/// dört alanı tutuyordu, başlatma OnStartup'ta durdurma OnExit'teydi.
/// İkisi aynı sınıfta olunca "başlattığını durdur" gözle görülür oluyor.
/// </summary>
public sealed class WpfStartupEnvironment : IStartupEnvironment
{
    private readonly LicenseService _license;
    private readonly RestoreService _restore;
    private readonly SettingsStore _settings;
    private readonly StreamSessionService _sessions;
    private readonly BackupService _backups;
    private readonly Views.AppRootView _root;
    private readonly IServiceProvider _services;
    private readonly ILogger<WpfStartupEnvironment> _log;

    private OverlayHost? _overlay;
    private ChatBridgeIngestor? _ingestor;
    private HeartbeatHostedService? _heartbeat;
    private Services.IntakeForm.IntakeFormSyncHostedService? _intakeSync;

    public WpfStartupEnvironment(
        LicenseService license,
        RestoreService restore,
        SettingsStore settings,
        StreamSessionService sessions,
        BackupService backups,
        Views.AppRootView root,
        IServiceProvider services,
        ILogger<WpfStartupEnvironment> log)
    {
        _license = license;
        _restore = restore;
        _settings = settings;
        _sessions = sessions;
        _backups = backups;
        _root = root;
        _services = services;
        _log = log;
    }

    // Task.Run sarmalayıcısı DÜŞTÜ: eskiden GetAwaiter().GetResult() UI
    // thread'ini bloklamasın diye gerekiyordu, artık gerçekten await
    // ediliyor.
    public Task InitializeLicenseAsync() => _license.InitializeAsync();

    public bool HasLicense => _license.CurrentStatus != LicenseStatus.NoLicense;

    public bool IsDatabaseMissingOrTiny()
    {
        var dbFile = AppPaths.DatabaseFile;
        return !File.Exists(dbFile) || new FileInfo(dbFile).Length < 10240;
    }

    public async Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync() =>
        await _restore.ListAvailableAsync();

    public bool HasCompletedFirstRun => _settings.Load().HasCompletedFirstRun;

    public StreamSession? GetActiveSession() => _sessions.GetActive();

    public void EndSession(string sessionId) => _sessions.End(sessionId);

    public async Task<bool> StartBackgroundServicesAsync()
    {
        // Yayın bitti → bulut yedeği (fire-and-forget). Shell kurulmadan
        // önce bağlanıyor; SessionEnded yalnız shell'den yükselebildiği
        // için burası en geç nokta.
        _sessions.SessionEnded += (_, _) => _backups.QueueBackup("stream-end");

        _overlay = _services.GetRequiredService<OverlayHost>();
        _ingestor = _services.GetRequiredService<ChatBridgeIngestor>();

        try
        {
            await _overlay.StartAsync();
        }
        catch (Exception ex) when (IsPortInUse(ex))
        {
            _log.LogError(ex, "All overlay port candidates already in use");
            MessageBox.Show(
                "Overlay portlarının tümü kullanımda (4747, 4757-4760).\n\n" +
                "Büyük ihtimalle başka bir OrderDeck çalışıyor. Görev Yöneticisi'nden " +
                "OrderDeck.App'i kapatıp tekrar dene.\n\n" +
                $"Detay: {ex.Message}",
                "OrderDeck — Port Çakışması", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestShutdown();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Overlay startup failed");
            MessageBox.Show(
                $"Overlay başlatılamadı:\n\n{ex.Message}\n\nUygulama kapatılıyor.",
                "OrderDeck — Başlatma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestShutdown();
            return false;
        }

        if (_overlay.FellBackFromPreferredPort)
        {
            _log.LogWarning("Overlay running on fallback port {Port} (4747 was busy)", _overlay.Port);
            MessageBox.Show(
                $"Overlay portu 4747 başka uygulama kullanıyor; otomatik olarak {_overlay.Port}'e geçildi.\n\n" +
                "OBS Browser Source URL'lerini güncelle:\n" +
                $"  http://localhost:{_overlay.Port}/overlay/chat\n" +
                $"  http://localhost:{_overlay.Port}/overlay/giveaway\n\n" +
                "Bu durum genelde başka bir OrderDeck instance veya farklı bir uygulama " +
                "tarafından 4747'nin tutulduğunda olur.",
                "OrderDeck — Yedek Port Kullanılıyor",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        try
        {
            await _ingestor.StartAsync(CancellationToken.None);
        }
        catch (Exception ex) when (IsPortInUse(ex))
        {
            _log.LogError(ex, "Bridge port 4748 already in use");
            MessageBox.Show(
                "Chrome eklenti köprüsü portu (4748) zaten kullanımda.\n\n" +
                "Büyük ihtimalle başka bir OrderDeck çalışıyor. Görev Yöneticisi'nden " +
                "kapatıp tekrar dene.\n\n" +
                $"Detay: {ex.Message}",
                "OrderDeck — Port Çakışması", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestShutdown();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bridge startup failed");
            MessageBox.Show(
                $"Chrome eklenti köprüsü başlatılamadı:\n\n{ex.Message}\n\n" +
                "Uygulama açık kalıyor — Instagram/TikTok chat çalışmayacak.",
                "OrderDeck — Köprü Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Köprüsüz devam — YouTube ve elle akışlar çalışıyor.
        }

        _heartbeat = _services.GetServices<IHostedService>()
            .OfType<HeartbeatHostedService>().FirstOrDefault();
        _ = _heartbeat?.StartAsync(CancellationToken.None);

        _intakeSync = _services.GetServices<IHostedService>()
            .OfType<Services.IntakeForm.IntakeFormSyncHostedService>().FirstOrDefault();
        _ = _intakeSync?.StartAsync(CancellationToken.None);

        // WPF'te IHost builder yok; kalan hosted service'ler elle
        // başlatılmazsa ölü örnek kalıyor (CLAUDE.md kuralı, PR #89).
        var alreadyStarted = new HashSet<IHostedService>(ReferenceEqualityComparer.Instance);
        if (_heartbeat is not null) alreadyStarted.Add(_heartbeat);
        if (_intakeSync is not null) alreadyStarted.Add(_intakeSync);

        foreach (var svc in _services.GetServices<IHostedService>())
        {
            if (alreadyStarted.Contains(svc)) continue;
            try
            {
                _ = svc.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Hosted service {Service} failed to start; continuing",
                    svc.GetType().Name);
            }
        }

        return true;
    }

    /// <summary>App.OnExit'ten çağrılır. Task.Run sarmalayıcıları korunuyor:
    /// kapanışta hâlâ senkron beklenen bir yol var.</summary>
    public void StopBackgroundServices()
    {
        try { Task.Run(() => _intakeSync?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { Task.Run(() => _heartbeat?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { Task.Run(() => _ingestor?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { Task.Run(() => _overlay?.StopAsync() ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
    }

    public void MountShell()
    {
        _root.MountShell(new Views.MainShellView());

        // Kısayollar eskiden MainWindow.Loaded'da bağlanıyordu; pencere artık
        // shell'den ÖNCE açıldığı için o an kısayolların hedefi yok.
        var window = Window.GetWindow(_root);
        if (window is not null)
            _services.GetRequiredService<ShortcutBinder>().Apply(window);
    }

    public void RequestShutdown() => Application.Current.Shutdown();

    public void RequestRestart()
    {
        // Geri yükleme yeni bir DB dosyası yazıyor; süreç boyunca açık
        // tutulan SQLite bağlantılarıyla tutarlı olmasının tek yolu yeni
        // süreç. Eskiden operatör uygulamayı ELLE açmak zorundaydı.
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Restart could not be launched; closing only");
            }
        }
        Application.Current.Shutdown();
    }

    private static bool IsPortInUse(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return true;
            if (current is HttpListenerException hle &&
                (hle.ErrorCode == 32 || hle.ErrorCode == 183 || hle.ErrorCode == unchecked((int)0x80004005)))
                return true;
            if (current is IOException io &&
                (io.Message.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                 io.Message.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                 io.Message.Contains("conflicts", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }
}
```

Ad alanı/tip adları derlerken tutmazsa (`OverlayHost`, `ChatBridgeIngestor`,
`HeartbeatHostedService`, `AppPaths`) `App.xaml.cs`'in using bloğundan
kopyala — hepsi bugün orada çözülüyor.

- [ ] **Step 3: `MainWindow`'u `AppRootView`'a çevir**

`OrderDeck.App/MainWindow.xaml` — 12. satırı değiştir:

```xml
        Foreground="{StaticResource OD.Brush.Text}">
    <!-- İçerik artık doğrudan shell DEĞİL: AppRootView boş bir shell
         yuvası + üstünde gate katmanı taşıyor. Pencere gate'lerden ÖNCE
         açıldığı için shell'i buraya sabitleyemeyiz — geri yükleme
         durumunda daha veritabanı bile yok. -->
    <ContentControl x:Name="RootHost"/>
</Window>
```

`OrderDeck.App/MainWindow.xaml.cs` — ctor'u ve `OnLoaded`'ı değiştir,
`OnClosing`'e koruma ekle:

```csharp
    public MainWindow(Views.AppRootView root)
    {
        InitializeComponent();
        RootHost.Content = root;
    }
```

`OnLoaded` metodunu ve `Loaded += OnLoaded;` satırını SİL (kısayol bağlama
`WpfStartupEnvironment.MountShell`'e taşındı; burada çalışırsa hedefi boş
olur). `using OrderDeck.App.Shortcuts;` da düşer.

`OnClosing`'in başına koruma:

```csharp
    protected override void OnClosing(CancelEventArgs e)
    {
        // Shell kurulmadan kapatılıyorsa (gate ekranındayız) MainShellViewModel'i
        // ÇÖZME: geri yükleme durumunda veritabanı henüz yok, çözmek çökme
        // demek. Pencere gate'lerden önce açıldığı için bu yol gerçek.
        var root = RootHost.Content as Views.AppRootView;
        if (root is null || !root.IsShellMounted)
        {
            base.OnClosing(e);
            return;
        }

        // Buradan aşağısı DEĞİŞMİYOR — bugünkü çekiliş koruması aynen kalıyor.
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
```

- [ ] **Step 4: DI kayıtları**

`OrderDeck.App/AppHost.cs` — 521. satırdaki `services.AddTransient<Views.LoginDialog>();`
ve 528. satırdaki `services.AddTransient<Views.FirstRunWizard>();` satırlarını
SİL (pencereler kalkıyor, ViewModel kayıtları kalıyor). Aynı bölgeye ekle:

```csharp
        // Faz 4a: tam-ekran açılış durumları. Yığın TEK örnek — hem
        // IAppGateService olarak enjekte ediliyor hem de AppRootView'daki
        // GateHost'un DataContext'i.
        services.AddSingleton<Services.Gates.AppGateStack>();
        services.AddSingleton<Services.Gates.IAppGateService>(
            sp => sp.GetRequiredService<Services.Gates.AppGateStack>());
        services.AddSingleton<Views.AppRootView>();

        services.AddSingleton<Startup.IStartupGates, Startup.WpfStartupGates>();
        services.AddSingleton<Startup.WpfStartupEnvironment>();
        services.AddSingleton<Startup.IStartupEnvironment>(
            sp => sp.GetRequiredService<Startup.WpfStartupEnvironment>());
        services.AddSingleton<Startup.StartupFlow>();
```

`WpfStartupEnvironment` iki kayıtla giriyor çünkü `App.OnExit`
`StopBackgroundServices()` için somut tipe ihtiyaç duyuyor.

- [ ] **Step 5: `App.xaml.cs`'i sadeleştir**

`72-75`. satırlardaki dört servis alanını SİL (`_ingestor`, `_overlay`,
`_heartbeat`, `_intakeSync` → `WpfStartupEnvironment`'a taşındı).

`112-343` arasını (lisans → giriş → geri yükleme → sihirbaz → oturum
bağlama → kurtarma → overlay → köprü → hosted service döngüsü) tamamen
SİL. Velopack güncelleme bloğu (`345-376`) YERİNDE KALIYOR.

`378-388` yerine:

```csharp
        base.OnStartup(e);

        // AÇILIŞ SIRASI TERSİNE DÖNDÜ (Faz 4a). Pencere artık İLK açılıyor;
        // lisans/geri yükleme/sihirbaz kontrolleri onun içinde tam ekran
        // gate olarak koşuyor. Eskiden üç ShowDialog() pencereden önce
        // gelirdi ve operatör görev çubuğunda ikonsuz, alt+tab'de bulunamayan
        // pencerelerle uğraşırdı.
        var root = Host.Services.GetRequiredService<Views.AppRootView>();
        var main = new MainWindow(root);
        MainWindow = main;
        main.Show();

        // Bilerek await EDİLMİYOR: OnStartup'ın dönmesi gerekiyor ki
        // dispatcher mesaj döngüsü koşsun ve gate'ler çizilsin.
        _ = RunStartupAsync(logger);
    }

    private static async Task RunStartupAsync(ILogger<App> logger)
    {
        try
        {
            await Host.Services.GetRequiredService<Startup.StartupFlow>().RunAsync();
        }
        catch (Exception ex)
        {
            // Buraya düşmek akışın kendi try/catch'lerinin kaçırdığı bir
            // hata demek; sessizce boş gate ekranında kalmaktansa söyle.
            logger.LogError(ex, "Startup flow failed");
            MessageBox.Show(
                $"OrderDeck açılamadı:\n\n{ex.Message}",
                "OrderDeck — Başlatma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }
```

`OnExit` (490-500) gövdesindeki dört `Task.Run(...)` satırını şununla
değiştir:

```csharp
        Host.Services.GetRequiredService<Startup.WpfStartupEnvironment>()
            .StopBackgroundServices();
        Host.Dispose();
        base.OnExit(e);
```

`IsPortInUse` (472-488) SİL — `WpfStartupEnvironment`'a taşındı.

- [ ] **Step 6: `AccountDialogViewModel`'i gate'e bağla**

`OrderDeck.App/ViewModels/AccountDialogViewModel.cs:35`:

```csharp
        OpenLoginCommand = new AsyncRelayCommand(OpenLoginAsync);
```

`96-105` yerine:

```csharp
    private async Task OpenLoginAsync()
    {
        // Faz 4a: hesap sayfasından giriş de aynı tam-ekran LoginGate.
        // Owner ayarı gerekmiyor (pencere yok) ve KRİTİK olarak shell
        // sökülmüyor: yayın sürerken hesap değiştirilirse sohbet paneli,
        // sayaçlar ve açık çekmeceler yerinde kalıyor.
        var gates = global::OrderDeck.App.App.Host.Services
            .GetRequiredService<global::OrderDeck.App.Startup.IStartupGates>();
        await gates.ShowLoginAsync(isStartupGate: false);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
```

- [ ] **Step 7: Shell'in Window kısayollarını gate açıkken sustur**

Task 3'ün gözden geçirmesinde çıktı: `AppRootView` gate açıkken
`ShellHost.IsEnabled`'ı `false` yapıyor, ama
[MainShellView.xaml.cs:26-35](OrderDeck.App/Views/MainShellView.xaml.cs#L26)
`PreviewKeyDown`'ı **Window'a** bağlıyor. Window devre dışı olmadığı için
handler gate açıkken de ateşliyor: ESC arkadaki çekmeceyi kapatır, Ctrl+K
görünmeyen ürün kodu kutusuna odaklanmaya çalışır. Bu yalnız çalışırken
(hesap değiştirme) mümkün — açılışta shell zaten kurulu değil.

`OnWindowPreviewKeyDown`'ın en başına, mevcut `App.Host` servis-bulucu
kalıbıyla aynı hizada bir erken dönüş ekle:

```csharp
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Gate açıkken shell'in kısayolları susar. ShellHost.IsEnabled=false
        // yalnız odak/tıklamayı keser; bu handler Window'a bağlı olduğu için
        // ondan etkilenmiyor — ESC arkadaki çekmeceyi kapatırdı.
        if (App.Host?.Services.GetRequiredService<Services.Gates.AppGateStack>().IsOpen == true)
            return;

        // Ctrl+K → ürün kodu kutusuna odaklan. ESC kontrolünden ÖNCE olmalı:
```

- [ ] **Step 8: Eski pencereleri sil**

```bash
git rm OrderDeck.App/Views/LoginDialog.xaml OrderDeck.App/Views/LoginDialog.xaml.cs \
       OrderDeck.App/Views/RestoreDialog.xaml OrderDeck.App/Views/RestoreDialog.xaml.cs \
       OrderDeck.App/Views/FirstRunWizard.xaml OrderDeck.App/Views/FirstRunWizard.xaml.cs
```

- [ ] **Step 9: Kalan atıf var mı, doğrula**

```bash
grep -rn "LoginDialog\b\|RestoreDialog\b\|FirstRunWizard\b" --include=*.cs --include=*.xaml OrderDeck.App OrderDeck.Tests
```

Beklenen: yalnızca `LoginDialogViewModel`, `RestoreDialogViewModel`,
`FirstRunWizardViewModel` eşleşmeleri. (ViewModel adlarındaki "Dialog"
tarihsel; yeniden adlandırma bu fazın işi değil.)

- [ ] **Step 10: Derle**

Çalıştır: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Beklenen: 0 hata.

- [ ] **Step 11: Commit**

```bash
git add OrderDeck.App/Startup OrderDeck.App/MainWindow.xaml OrderDeck.App/MainWindow.xaml.cs OrderDeck.App/AppHost.cs OrderDeck.App/App.xaml.cs OrderDeck.App/ViewModels/AccountDialogViewModel.cs
git commit -m "$(cat <<'EOF'
feat(shell): açılış sırasını tersine çevir, üç pencereyi kaldır

MainWindow artık İLK açılıyor; lisans / geri yükleme / sihirbaz kontrolleri
onun içinde tam ekran gate olarak koşuyor. Eskiden üç ShowDialog() pencereden
önce gelir, operatör görev çubuğunda ikonsuz ve alt+tab'de bulunamayan
pencerelerle uğraşırdı.

Arka plan servislerinin ömrü WpfStartupEnvironment'a taşındı: başlatma
OnStartup'ta durdurma OnExit'teydi, artık ikisi aynı sınıfta.

Hesap sayfasındaki giriş de aynı gate'i kullanıyor ve shell'i SÖKMÜYOR —
yayın sürerken hesap değiştirilebiliyor, sohbet paneli kaybolmuyor.

LoginDialog / RestoreDialog / FirstRunWizard pencereleri silindi. Kalan tek
Window: MainWindow (spec §10).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Doğrulama ve kapanış

- [ ] **Step 1: Tüm test paketi**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Beklenen: taban 905 test + bu fazın yenileri, HEPSİ geçiyor, 0 başarısız.

Bir test **kilitlenirse** ilk şüpheli PR #247'nin sorunudur: bir test
`App`'i örnekleyip dispatcher'ı pompalıyor ve `OnStartup` koşuyor olabilir.
`_startedFromEntryPoint` korumasının yerinde durduğunu ve `StartupFlow`'un
`App` yapıcısından DEĞİL yalnız `OnStartup`'tan tetiklendiğini doğrula.

- [ ] **Step 2: WPF derlemesi**

Çalıştır: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Beklenen: 0 hata, 0 yeni uyarı.

- [ ] **Step 3: Tek `Window` kaldığını doğrula**

```bash
grep -rln "^<Window\|<Window " --include=*.xaml OrderDeck.App
```

Beklenen: tek dosya — `OrderDeck.App/MainWindow.xaml` (spec §10).

- [ ] **Step 4: Sabit hex ve emoji taraması**

```bash
grep -rn "#FF[0-9A-Fa-f]\{6\}" --include=*.xaml OrderDeck.App/Views/Gates
grep -rnP "[\x{1F300}-\x{1FAFF}]" --include=*.xaml OrderDeck.App/Views/Gates
```

Beklenen: ikisi de boş (spec §10: sabit hex 0, emoji ikon 0).

- [ ] **Step 5: Elle açılış turu (spec §7.4)**

Dört senaryo, hepsi `dotnet run --project OrderDeck.App` ile:

1. **Temiz kurulum** — `%APPDATA%`'daki OrderDeck klasörünü yedekleyip sil.
   Beklenen: pencere HEMEN açılıyor → marka + "Hazırlanıyor…" → giriş →
   sihirbaz → shell. Hiçbir aşamada ayrı pencere yok, görev çubuğunda tek
   ikon.
2. **Lisanslı, DB silinmiş** — `OrderDeck.db`'yi sil, bulutta yedek olsun.
   Beklenen: geri yükleme ekranı → yedek seç → "Tamamlandı" durumu →
   "Yeniden Başlat" → uygulama kendi kapanıp AÇILIYOR (eskiden elle açmak
   gerekiyordu).
3. **Yayın ortasında öldür** — yayın başlat, Görev Yöneticisi'nden süreci
   sonlandır, tekrar aç. Beklenen: kurtarma ekranı başlangıç saati ve
   başlıkla; "Devam et" kuyruğu geri yüklüyor, "Yayını bitir" temiz
   başlatıyor, "Çıkış" kapatıyor.
4. **Çalışırken hesap değiştirme** — yayın sürerken Ayarlar → Hesap →
   Giriş yap. Beklenen: gate tam ekran biniyor, kapanınca **sohbet paneli
   ve sayaçlar kaldığı yerde** — shell hiç sökülmedi. (Bu fazın asıl
   sınavı; sayfa/pencere yaklaşımı bunu veremiyordu.)

Bu turda ayrıca aşağıdaki **açık kararlar** ekranda karara bağlanacak
(Task 4, Task 5 ve Task 8 gözden geçirmelerinden kaldı; ilk üçü kod
yorumlarında da yazıyor):

- `GateBrand` rozeti 20→48px büyürken köşe yarıçapı `Md`→`Lg` (8→10)
  kalıyor; ölçek `Lg`'de bittiği için işaret raydakinden orantısal olarak
  daha kare duruyor. Tam ekranda rahatsız ediyorsa çözüm yeni bir
  `OD.Radius.Xl` basamağı.
- `BootGate`'in "Hazırlanıyor…" satırı `OD.Text.Hint` (F0 = 11px). Tam
  ekranda 48px'lik markanın altında küçük kalıyorsa `OD.Text.Section`'a
  çıkar — ama beş gate'in tamamında birlikte değiştir, tek ekranda değil.
- `LoginGate`'in şifre kutuları örtük `DarkControls.xaml` stilinde: hemen
  üstteki e-posta kutusuyla yan yana zemin (`OD.Bg.Input` ↔ Surface2) ve
  yuvarlatma farkı var, odak kenarlığı da kırmızı yerine **mavi**
  (`OD.Border.Focus`). Gerçek ekranda ne kadar göze battığını ölç. Çözüm
  yeri Faz 4b (`DarkControls.xaml` silinirken tokenize `PasswordBox`),
  burada değil — bu tur sadece ne kadar acil olduğunu belirlemek için.
- `LoginGate`'te hata mesajı belirdiğinde form kayıyor: gate'in bütün
  satırları `Auto` ve dış grid `VerticalAlignment="Center"`, dolayısıyla
  hata satırı açılınca üstteki alanlar yukarı zıplıyor. Gerçek ekranda
  rahatsız ediciyse hata satırına sabit yükseklik verilecek.
  `RestoreGate`'in `StatusMessage` satırı da artık aynı şekilde davranıyor
  (mesaj yokken `NullToCollapsedConverter` ile kalkıyor, gelince form
  kayıyor) — aynı sınıf bir ekran kararı, ikisine birlikte bak.
- Kayıt modunda Enter tuşu ölü: "Kayıt ol" düğmesinde `IsDefault` yok.
  **Bu bir gerileme değil** — eski `LoginDialog.xaml`'de de yoktu (tek
  `IsDefault` lisans aktivasyon düğmesindeydi, `LoginDialog.xaml:131`);
  gate girişe ayrıca `IsDefault` ekleyerek zaten iyileştirdi. Kayıt moduna
  da eklenecek mi, bilinçli bir UX kararı olarak burada verilecek.
- `RestoreGate` "tamamlandı" durumuna geçtiğinde odak, collapse olan
  düğmede kalıyor: `AppRootView` odağı yalnız gate AÇILIRKEN taşıyor,
  durum geçişinde taşımıyor. Enter yolu `IsDefault` ile çözüldü; gerçek
  ekranda Tab'ın nereye düştüğüne bak, rahatsız ediciyse durum geçişinde
  odağı yeniden taşı.

Bunların yanında **ekranda değil, kodu okuyarak** kapatılacak maddeler:

- Spec §4.5 yayın özetinde "başlangıç zamanı, **etiket sayısı**" istiyor;
  `SessionRecoveryGate` yalnız başlangıç zamanı + başlık gösteriyor. Sebep
  veri modeli: `StreamSession` (`OrderDeck.Core/Sessions/StreamSession.cs:5`)
  etiket sayısı taşımıyor ve `StreamSessionService`'te sayacak bir uç yok
  (`Start` / `End` / `GetActive` — hepsi bu). Eklemek açılış gate'ine yeni bir
  DB sorgusu ve yeni bir servis bağımlılığı sokardı; Faz 4a'nın "akış birebir
  aynı kalır" kuralına (spec §5) aykırı. Bilinçli düşürüldü — özetin yetip
  yetmediği gerçek ekranda görülecek, gerekiyorsa ayrı bir iş olarak açılacak.

- `LoginGate.xaml:15`, `LoginGate.xaml.cs` ve `RestoreGate.xaml.cs`'deki
  ileriye dönük yorumlar (`StartupFlow`, `IStartupEnvironment.RequestRestart`)
  Task 9 ve Task 10 bittikten sonra yeniden okunacak — plandan sapıldıysa
  bu cümleler yalana döner.
- "Atla, yeni başlat" geri yükleme sürerken tıklanabilir: gate kapanır,
  `RestoreInternalAsync` arka planda DB'yi yazmaya devam eder, `StartupFlow`
  `Skipped` alıp shell'i o dosyanın üstüne kurar. Eski pencerede de böyleydi
  (gerileme değil), ama artık tek yol bu ekran — Task 9/10 akışı otururken
  kapatılacak mı, karar verilecek.

- [ ] **Step 6: Faz 4b'ye devir notu**

`DarkControls.xaml` bu fazda YERİNDE KALIYOR: `PasswordBox`, `ListBox`,
`ListBoxItem` ve `Window` için hâlâ tek kaynak. Faz 4b onu silecek ve
`Themes/Base.xaml`'i `OD.*` token'ları üzerine yeniden kuracak;
`OrderDeck.Tests/App/ThemeMergeTests.cs:28`'deki
`["DarkControls.xaml", "PlatformIcons.xaml"]` listesi orada güncellenecek.

- [ ] **Step 7: PR**

```bash
git push -u origin feat/arayuz-faz4a-acilis-durumlari
```

PR başlığı: `feat(shell): açılış durumları tam ekrana (Faz 4a)`

---
