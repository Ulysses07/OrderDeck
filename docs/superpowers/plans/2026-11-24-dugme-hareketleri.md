# Düğme hareketleri (İş 1) — uygulama planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Beş düğme stiline durağan görünümü değiştirmeden 150 ms'lik geçiş, basılı durum ve meşgul durumu eklemek.

**Architecture:** Üzerine gelme ve basma, şablondaki görsel durumlarla (`VisualStateManager`, `CommonStates`) anlatılır; canlanan yalnız örtü katmanlarının saydamlığı ve ölçektir. Geçişleri, hareket azaltmayı tek yerden uygulayan özel bir durum yöneticisi başlatır. Meşgul durumu şablon tetikleyicisidir; dönen halka kendi kendini yöneten küçük bir öğedir.

**Tech Stack:** .NET 10, WPF (ControlTemplate, VisualStateManager), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-24-dugme-hareketleri-ve-sohbeti-yenile-design.md` (§3, §4, §9 "İş 1")

## Global Constraints

- Durağan görünüm hiçbir stilde değişmez; hiçbir ekranın XAML'ine dokunulmaz (kenar çubuğunun kendi stili hariç).
- Süre yalnız `OD.Dur.Fast`, eğri yalnız `OD.Ease.Out`. `OD.Ease.Spring` düğmelerde kullanılmaz. Yeni süre ya da eğri belirteci yok.
- Canlanan yalnız `Opacity` ve `ScaleTransform.ScaleX/ScaleY`. Genişlik, yükseklik, boşluk, fırça rengi canlanmaz.
- `Colors.xaml`'e fırça eklenmez. Gradyan fırça, `Effect`, gölge yok.
- Örtü değerleri (başlangıç): üzerinde beyaz 0,14 (yalnız birincil), basılı koyu 0,12 (yalnız birincil), basılı açık 0,06 (öbür dördü). Ölçek 0,98 (yalnız birincil ve ikincil).
- Hareket azaltma kararı tek yerde: `MotionPolicy.IsEnabled`. Kapalıyken geçişler anında, halka dönmez.
- Şablon parça adları sözleşmedir: kök `Root`, katmanlar `Hover` ve `Press`, içerik `Content`, halka `Busy`.
- Testlerde animasyon saati yalnız gerçek ileti döngüsüyle ilerler: `ThemeTestHost.Pump()` yetmez, `WpfWait` kullanılır. `MotionPolicy.Override` yalnız `ThemeTestHost.RunOnSta` gövdesinde, `try/finally` ile eski değerine döndürülerek değiştirilir.
- Özel durum yöneticisi, şablonda tanımlı olmayan durumlar için de çağrılır (`Unfocused`, `Valid`, `Unchecked`); `state` null gelir ve taban sınıfa verilirse `ArgumentNullException` atar. Koruma zorunlu.
- Commit iletileri Türkçe emir kipi; son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.

## Review Focus

Testlerin kapsamadığı, en çok ısıracak beş durum:

1. Gerçek fare yolunda (düğmenin kendi durum geçişleri) hareketin akışı ve basılıyken imleç düğmeden çıkınca geri dönüş: testler durumu doğrudan tetikler. Elde denenir (Görev 6).
2. Yayın düğmesinin yeşil ve kırmızı zemininde basılı ton ayırt edilir mi: durum panosu (Görev 6).
3. Türeyen stiller (`OD.ShortcutCapture`, `RadioButton` sekmeleri) şablonu miras alır ve çalışır: testi Görev 3 ve 4'te.
4. Meşgul bayrağı onu tanımayan stile (çip, kenar çubuğu öğesi) verilirse hata çıkmaz, görünüm değişmez: testi Görev 5'te.
5. Üzerindeyken devre dışı kalan düğmede örtü kalmaz: testi Görev 3'te.

---

### Task 1: Hareket ilkesi ve durum yöneticisi

**Files:**
- Create: `OrderDeck.App/Controls/MotionPolicy.cs`, `OrderDeck.App/Controls/MotionAwareVisualStateManager.cs`
- Modify: `OrderDeck.App/Views/Shell/DrawerHost.xaml.cs` (`SystemParameters.ClientAreaAnimation` → `MotionPolicy.IsEnabled`), `OrderDeck.App/Themes/Motion.xaml` (yorum: karar noktası `MotionPolicy`)
- Test: `OrderDeck.Tests/App/MotionPolicyTests.cs`

**Interfaces:**
- Produces:
  - `public static class MotionPolicy { public static bool IsEnabled { get; } internal static bool? Override { get; set; } }` — `Override` null ise `SystemParameters.ClientAreaAnimation`.
  - `public sealed class MotionAwareVisualStateManager : VisualStateManager` — `internal static bool Effective(bool requested)` (`requested && MotionPolicy.IsEnabled`); `GoToStateCore` `group` ya da `state` null ise `false` döner, değilse tabanı `Effective(useTransitions)` ile çağırır.

- [ ] **Step 1: Başarısız testi yaz**

```csharp
public class MotionPolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Transitions_run_only_when_requested_and_motion_is_enabled(bool motion, bool requested, bool expected)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var before = MotionPolicy.Override;
            try
            {
                MotionPolicy.Override = motion;
                Assert.Equal(motion, MotionPolicy.IsEnabled);
                Assert.Equal(expected, MotionAwareVisualStateManager.Effective(requested));
            }
            finally { MotionPolicy.Override = before; }
        });
        Assert.Null(error);
    }

    [Fact]
    public void Without_override_the_system_setting_decides()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var before = MotionPolicy.Override;
            try
            {
                MotionPolicy.Override = null;
                Assert.Equal(SystemParameters.ClientAreaAnimation, MotionPolicy.IsEnabled);
            }
            finally { MotionPolicy.Override = before; }
        });
        Assert.Null(error);
    }
}
```

- [ ] **Step 2: Çalıştır, derlenmediğini gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~MotionPolicyTests"`
Expected: derleme hatası (`MotionPolicy` yok)

- [ ] **Step 3: İki sınıfı yaz; `DrawerHost`'u ilkeye bağla**
- [ ] **Step 4: Testi çalıştır** — Expected: 5 test geçer; `DrawerHostLayoutTests` de geçer
- [ ] **Step 5: Commit** — `feat(theme): hareket ilkesi ve durum yöneticisi`

---

### Task 2: Meşgul halkası ve meşgul bayrağı

**Files:**
- Create: `OrderDeck.App/Controls/BusyRing.cs`, `OrderDeck.App/Controls/ButtonState.cs`, `OrderDeck.Tests/App/WpfWait.cs`
- Test: `OrderDeck.Tests/App/BusyRingTests.cs`

**Interfaces:**
- Consumes: `MotionPolicy.IsEnabled`
- Produces:
  - `public sealed class BusyRing : FrameworkElement` — bağımlılık özellikleri `Stroke` (`Brush`, çizimi etkiler) ve `Period` (`Duration`, bir turun süresi; varsayılan 850 ms). 270 derecelik, 2 px çizgili, yuvarlak uçlu yay çizer; kendi merkezinde döner. `internal bool IsSpinning { get; }`: görünür (`IsVisible`) ve hareket açıkken true. Görünürlük değişince döngüyü başlatır ya da durdurur; görünmezken animasyon çalışmaz.
  - `public static class ButtonState` — iliştirilmiş `IsBusy` (`bool`, varsayılan false): `IsBusyProperty`, `GetIsBusy(DependencyObject)`, `SetIsBusy(DependencyObject, bool)`.
  - `internal static class WpfWait` (test) — `void For(int milliseconds)` (zamanlayıcıyla biten `DispatcherFrame`), `bool Until(Func<bool> condition, int timeoutMilliseconds = 3000)` (20 ms adımlarla yoklar).

- [ ] **Step 1: Başarısız testi yaz**

```csharp
public class BusyRingTests
{
    [Theory]
    [InlineData(true, true, true)]     // görünür, hareket açık
    [InlineData(true, false, false)]   // görünür, hareket azaltılmış
    [InlineData(false, true, false)]   // gizli
    public void Ring_spins_only_while_visible_and_motion_is_enabled(bool visible, bool motion, bool expected)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var before = MotionPolicy.Override;
            Window? window = null;
            try
            {
                MotionPolicy.Override = motion;
                var ring = new BusyRing { Width = 16, Height = 16, Visibility = visible ? Visibility.Visible : Visibility.Collapsed };
                window = OffScreen(ring);
                Assert.True(WpfWait.Until(() => ring.IsSpinning == expected));
                WpfWait.For(60);
                Assert.Equal(expected, ring.IsSpinning);
            }
            finally { window?.Close(); MotionPolicy.Override = before; }
        });
        Assert.Null(error);
    }

    [Fact]
    public void Ring_stops_when_hidden()
    {
        // görünürken IsSpinning true; Visibility = Collapsed sonrası WpfWait.Until(() => !ring.IsSpinning)
    }
}
```

`OffScreen(content)`: `PrintedLabelsPanelLayoutTests`'teki pencere kalıbı (`Left = Top = -10000`, `ShowInTaskbar = false`, `ShowActivated = false`, `WindowStyle.None`), `Show()` edilmiş.

- [ ] **Step 2: Çalıştır** — Expected: derleme hatası (`BusyRing` yok)
- [ ] **Step 3: `BusyRing`, `ButtonState` ve `WpfWait`'i yaz**
- [ ] **Step 4: Testi çalıştır** — Expected: 4 test geçer
- [ ] **Step 5: Commit** — `feat(theme): meşgul halkası ve meşgul bayrağı`

---

### Task 3: Birincil ve ikincil düğme (örtü + ölçek)

**Files:**
- Modify: `OrderDeck.App/Themes/Metrics.xaml` (üç saydamlık belirteci), `OrderDeck.App/Themes/Controls.xaml` (`OD.Button.Primary`, `OD.Button.Secondary`)
- Test: `OrderDeck.Tests/App/ButtonMotionTests.cs`

**Interfaces:**
- Consumes: `MotionAwareVisualStateManager`, `WpfWait`
- Produces:
  - `Metrics.xaml`: `OD.Opacity.HoverVeil` = 0.14, `OD.Opacity.PressDark` = 0.12, `OD.Opacity.PressLight` = 0.06 (`sys:Double`).
  - Şablon sözleşmesi (Görev 4 ve 5 aynısını izler): kök öğenin adı `Root`; `VisualStateManager.CustomVisualStateManager` = `MotionAwareVisualStateManager`; `CommonStates` grubu `Normal`, `MouseOver`, `Pressed`, `Disabled` durumlarıyla; tek geçiş: `GeneratedDuration` = `OD.Dur.Fast`, `GeneratedEasingFunction` = `OD.Ease.Out`; durum animasyonları `DoubleAnimation`, `Duration="0"`, yalnız `To`.
  - Birincil: `Root` = zemin `Border`'ı (`ScaleTransform` taşır, merkez 0.5,0.5). İçinde sırayla `Hover` (`OnAccent`), `Press` (`OD.Brush.Bg`), `Content`. `MouseOver`: `Hover.Opacity` → HoverVeil. `Pressed`: `Press.Opacity` → PressDark, ölçek X ve Y → 0.98 (`Hover` basılıyken 0'a döner). 1 px yukarı kalkma tetikleyicisi silinir.
  - İkincil: `Root` = `Grid` (`ScaleTransform`). Sırayla `Hover` (`Surface2`, köşe `Md`), bugünkü çerçeveli `Border` (içinde `Content`), `Press` (`OnAccent`, köşe `Md`, fare almaz). `MouseOver`: `Hover.Opacity` → 1. `Pressed`: `Hover.Opacity` → 1, `Press.Opacity` → PressLight, ölçek → 0.98.
  - Devre dışı tetikleyicisi `Root.Opacity` = 0.45 ve ok imleci (bugünkü değerler).
  - Test yardımcıları (`ButtonMotionTests` içinde, Görev 4 ve 5 de kullanır): `static ButtonBase Realized(string styleKey)` (stili uygular, içerik "Metin", penceresiz gerçekleştirir), `static FrameworkElement Root(ButtonBase)`, `static IReadOnlyDictionary<string, double> StateTargets(ButtonBase, string stateName)` (anahtar `"<Parça>.Opacity"`, `"Root.ScaleX"`, `"Root.ScaleY"`; değer `To`).

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
public class ButtonMotionTests
{
    public static TheoryData<string, string[], string[]> Expected => new()
    {
        // stil, MouseOver hedefleri, Pressed hedefleri ("anahtar=değer")
        { "OD.Button.Primary",   ["Hover.Opacity=0.14"], ["Press.Opacity=0.12", "Root.ScaleX=0.98", "Root.ScaleY=0.98"] },
        { "OD.Button.Secondary", ["Hover.Opacity=1"],    ["Hover.Opacity=1", "Press.Opacity=0.06", "Root.ScaleX=0.98", "Root.ScaleY=0.98"] },
    };

    [Theory, MemberData(nameof(Expected))]
    public void States_animate_exactly_the_declared_targets(string style, string[] over, string[] pressed)
    // StateTargets(button, "MouseOver") ve "Pressed" sözlükleri beklenenle birebir eşit; "Normal" ve "Disabled" boş.

    [Theory, MemberData(nameof(Expected))]
    public void Root_uses_the_motion_aware_manager_and_the_fast_ease_out_transition(string style, string[] _, string[] __)
    // GetCustomVisualStateManager(Root) MotionAwareVisualStateManager; tek VisualTransition:
    // GeneratedDuration == 150 ms, GeneratedEasingFunction CubicEase + EaseOut.

    [Theory, MemberData(nameof(Expected))]
    public void At_rest_overlays_are_invisible_and_scale_is_one(string style, string[] _, string[] __)
    // Hover ve Press Opacity 0; Root.RenderTransform ScaleTransform 1/1.

    [Theory, MemberData(nameof(Expected))]
    public void Disabled_button_dims_to_45_percent(string style, string[] _, string[] __)
    // IsEnabled = false → Root.Opacity 0.45.

    [Theory, MemberData(nameof(Expected))]
    public void Pressed_values_arrive_in_a_real_message_loop(string style, string[] _, string[] pressed)
    // Ekran dışı pencere; GoToState(button, "Pressed", false); WpfWait.Until ile her Pressed hedefi
    // gerçek değerde (|fark| < 0.001) görülür.

    [Fact]
    public void Disabling_a_hovered_button_clears_the_hover_overlay()
    // Birincil, pencere içinde: GoToState "MouseOver" → Hover 0.14 görülür; IsEnabled = false →
    // WpfWait.Until(() => Hover.Opacity == 0).

    [Fact]
    public void Derived_shortcut_capture_style_inherits_the_template()
    // "OD.ShortcutCapture" (ikincilden türer) Realized; Root'ta MotionAwareVisualStateManager var.
}
```

- [ ] **Step 2: Çalıştır** — Expected: `Root` bulunamadığı için FAIL
- [ ] **Step 3: Belirteçleri ekle, iki şablonu yeniden yaz**
- [ ] **Step 4: Testi çalıştır** — Expected: `ButtonMotionTests` geçer; `ControlsThemeTests`, `ThemeMergeTests`, `ThemeMetricsTests`, `UnstyledControlGuardTests` geçer
- [ ] **Step 5: Commit** — `feat(theme): birincil ve ikincil düğmeye geçiş ve basılı durum`

---

### Task 4: Hayalet, çip ve kenar çubuğu öğesi (yalnız örtü)

**Files:**
- Modify: `OrderDeck.App/Themes/Controls.xaml` (`OD.Button.Ghost`, `OD.Chip`), `OrderDeck.App/Views/Shell/ShellSidebar.xaml` (`NavButton`; `xmlns:controls` eklenir)
- Test: `OrderDeck.Tests/App/ButtonMotionTests.cs`

**Interfaces:**
- Consumes: Görev 3'ün şablon sözleşmesi ve test yardımcıları
- Produces:
  - Üçünde de `Root` = `Grid`, ölçek yok. Sıra: (varsa) `Hover` (`Surface2`), bugünkü `Border` (içeriğiyle, hiç değişmeden), `Press` (`OnAccent`, fare almaz). Köşe yarıçapı o stilin bugünkü yarıçapı.
  - Hayalet ve kenar çubuğu öğesi: `MouseOver`: `Hover.Opacity` → 1. `Pressed`: `Hover.Opacity` → 1, `Press.Opacity` → PressLight. Üzerine gelince zemini değiştiren eski tetikleyici ayarı silinir; yazı rengini değiştiren ayar kalır.
  - Çip: `Hover` katmanı yok. `MouseOver` boş. `Pressed`: `Press.Opacity` → PressLight. Seçili ve yazı rengi tetikleyicileri değişmez.
  - `Realized` kenar çubuğu stili için `"NavButton"` anahtarını `new ShellSidebar().Resources`'tan alır.

- [ ] **Step 1: `Expected`'e üç satır ekle; iki test daha yaz**

```csharp
{ "OD.Button.Ghost", ["Hover.Opacity=1"], ["Hover.Opacity=1", "Press.Opacity=0.06"] },
{ "OD.Chip",         [],                  ["Press.Opacity=0.06"] },
{ "NavButton",       ["Hover.Opacity=1"], ["Hover.Opacity=1", "Press.Opacity=0.06"] },
```

`At_rest_overlays_are_invisible_and_scale_is_one` ölçeksiz stillerde `Root.RenderTransform`'un birim dönüşüm olduğunu doğrular. `Disabled_button_dims_to_45_percent` çip ve kenar çubuğu öğesinde `Root.Opacity`'nin 1 kaldığını doğrular (bugün devre dışı görünümleri yok).

```csharp
[Fact]
public void Radio_button_tab_based_on_chip_inherits_the_template()
// RadioButton, Style = BasedOn OD.Chip (PrintQueuePanel'deki PanelTab gibi): Root'ta yönetici var,
// GoToState(tab, "Pressed", false) true döner.

[Fact]
public void Selected_nav_button_keeps_its_accent_border_under_the_overlays()
// NavButton, BorderBrush = Accent, Background = Surface2 verilmiş: çerçeveli Border bu değerleri taşır.
```

- [ ] **Step 2: Çalıştır** — Expected: yeni satırlar FAIL
- [ ] **Step 3: Üç şablonu yeniden yaz**
- [ ] **Step 4: Testi çalıştır** — Expected: `ButtonMotionTests` geçer; `ShellSidebar` ve `ChatPanel` yerleşim testleri geçer
- [ ] **Step 5: Commit** — `feat(theme): hayalet, çip ve kenar çubuğu öğesine geçiş ve basılı durum`

---

### Task 5: Meşgul durumu (üç düğme stili)

**Files:**
- Modify: `OrderDeck.App/Themes/Controls.xaml` (birincil, ikincil, hayalet)
- Test: `OrderDeck.Tests/App/ButtonBusyTests.cs`

**Interfaces:**
- Consumes: `BusyRing`, `ButtonState.IsBusy`, Görev 3'ün test yardımcıları
- Produces:
  - Üç şablonda `Content`'in yanına aynı hücrede `BusyRing x:Name="Busy"`: `Visibility="Collapsed"`, genişlik ve yükseklik `OD.Icon.Md`, `Stroke` = düğmenin `Foreground`'u, `Period` = `OD.Dur.Slow`, hizası içerikle aynı (birincil ve ikincilde orta; hayalette `HorizontalContentAlignment`).
  - Devre dışı tetikleyicisinden SONRA gelen tetikleyici (`controls:ButtonState.IsBusy` = True): `Content.Opacity` = 0, `Busy.Visibility` = Visible, `Root.IsHitTestVisible` = False, `Root.Opacity` = 1, imleç ok.

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
public class ButtonBusyTests
{
    public static TheoryData<string> Styles => ["OD.Button.Primary", "OD.Button.Secondary", "OD.Button.Ghost"];

    [Theory, MemberData(nameof(Styles))]
    public void Busy_swaps_content_for_the_ring_without_changing_size_or_content(string style)
    // Önce ölçü ve Content alınır; SetIsBusy(true) + UpdateLayout: Content.Opacity 0, Busy Visible,
    // Root.IsHitTestVisible false, button.Content aynı nesne, ActualWidth/ActualHeight aynı.
    // SetIsBusy(false): Content.Opacity 1, Busy Collapsed, Root.IsHitTestVisible true.

    [Theory, MemberData(nameof(Styles))]
    public void Busy_set_before_the_template_is_applied_gives_the_same_result(string style)

    [Theory, MemberData(nameof(Styles))]
    public void A_busy_button_that_is_also_disabled_does_not_dim(string style)
    // IsEnabled = false ve IsBusy = true → Root.Opacity 1; IsBusy = false → 0.45.

    [Theory]
    [InlineData("OD.Chip")]
    [InlineData("NavButton")]
    public void Styles_without_a_busy_state_ignore_the_flag(string style)
    // Template.FindName("Busy") null; SetIsBusy(true) hata vermez, Root.Opacity ve Root.IsHitTestVisible değişmez.
}
```

- [ ] **Step 2: Çalıştır** — Expected: `Busy` bulunamadığı için FAIL
- [ ] **Step 3: Halkayı ve tetikleyiciyi üç şablona ekle**
- [ ] **Step 4: Testi çalıştır** — Expected: `ButtonBusyTests` ve `ButtonMotionTests` geçer. Düğmeyi `Content` metniyle bulan testler (`ActiveProductBarLayoutTests`, `PrintedLabelsPanelLayoutTests`, `SettingsViewModelInstagramAccountsTests`) geçer.
- [ ] **Step 5: Commit** — `feat(theme): düğmelere meşgul durumu`

---

### Task 6: Koruma testleri, tüm paket, görsel doğrulama

**Files:**
- Create: `OrderDeck.Tests/App/ButtonStyleGuardTests.cs`
- Modify: `OrderDeck.Tests/App/ControlsThemeTests.cs` (`StyleKeys`'e `OD.Button.Secondary`), `OrderDeck.Tests/App/ThemeMotionTests.cs` (bayat "SIFIR animasyon" yorumu)

**Interfaces:**
- Consumes: Görev 3–5'in şablonları

- [ ] **Step 1: Koruma testini yaz**

```csharp
public class ButtonStyleGuardTests
{
    // Controls.xaml'deki OD.Button.Primary / .Secondary / .Ghost / OD.Chip ve ShellSidebar.xaml'deki NavButton
    // Style öğelerinin XML metni (depo kökü UnstyledControlGuardTests.RepoRoot kalıbıyla bulunur).
    [Theory]
    [InlineData("Effect")]
    [InlineData("GradientBrush")]
    [InlineData("OD.Ease.Spring")]
    [InlineData("BackEase")]
    [InlineData("ElasticEase")]
    [InlineData("BounceEase")]
    [InlineData("ColorAnimation")]
    public void Button_styles_stay_plain(string forbidden)

    [Fact]
    public void Button_states_animate_only_opacity_and_scale()
    // Beş stilin bütün durum animasyonlarının hedef özelliği Opacity ya da ScaleTransform.ScaleX/ScaleY.
}
```

- [ ] **Step 2: Çalıştır** — Expected: geçer (koruma; şablonlar zaten uygun). Geçmezse şablon düzeltilir.
- [ ] **Step 3: `ControlsThemeTests` anahtarını ve bayat yorumu güncelle**
- [ ] **Step 4: Tüm paketi çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Expected: tümü geçer

- [ ] **Step 5: Commit** — `test(theme): düğme stilleri için koruma testleri`
- [ ] **Step 6: Görsel doğrulama (depoya girmez; çıktılar Burak'a sunulur)**
  - Beş stilin durağan görüntüsü, değişiklikten önceki derlemeyle (saklanan `OrderDeck.App.dll`) ve sonrakiyle çizilip piksel piksel karşılaştırılır. Beklenen: fark 0.
  - Durum panosu: beş stilin durağan, üzerinde ve basılı hâlleri; üç düğme stilinin devre dışı ve meşgul hâlleri; `Bg`, `Surface`, `Surface2` zeminlerinde; yayın düğmesinin yeşil ve kırmızı hâlleri. Ölçüt: durağan, üzerinde ve basılı `Bg` ve `Surface` zeminlerinde ayırt edilir. Edilmiyorsa belirteç değeri ayarlanır, Görev 3–4 testleri yeni değerle güncellenir.
  - Uygulama derlenip elde denenir: gerçek fareyle üzerine gelme, basma, basılıyken dışarı çıkma.
