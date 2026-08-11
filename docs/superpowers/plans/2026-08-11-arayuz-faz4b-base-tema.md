# Faz 4b — `DarkControls.xaml` emekliliği / `Base.xaml` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `OrderDeck.App/Themes/DarkControls.xaml` (49 KB, 26 örtük stil, kendi 17 renk token'ı) silinsin; yerine `OD.*` token'ları üzerine kurulu ince bir `Themes/Base.xaml` gelsin ve arayüzün tamamı tek palete (yakın-siyah + kırmızı) insin.

**Architecture:** İş iki PR'a bölünür çünkü iki farklı türde risk var. **PR 1** hiçbir stili taşımaz, yalnız `DarkControls.xaml`'in 17 renk token'ının hex değerini `Colors.xaml` karşılıklarına çeker — risk göz kararı ("mavi kalan var mı?"). **PR 2** yapıyı böler: XAML'den erişilemeyen 6 stil `Base.xaml`'e taşınır, geri kalan örtük stiller silinir, çıplak kalan 20 kullanım keyed `OD.*` stillerine açıkça bağlanır — risk ölçülebilir, kalıcı bir bekçi testiyle kapatılır.

**Tech Stack:** WPF (`net10.0-windows`), XAML `ResourceDictionary`, xUnit (`OrderDeck.Tests`), `ThemeTestHost` STA koşum düzeneği.

**Spec:** `docs/superpowers/specs/2026-08-11-arayuz-faz4b-base-tema-design.md`

---

## Dosya yapısı

**PR 1 — dal `chore/tema-renk-takasi`**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.App/Themes/DarkControls.xaml` (değişir, satır 24-43 + 56-102) | 17 renk token'ının hex değeri + `OD.Icon.Gift`'in kırmızıları yeni palete çekilir. Stil gövdeleri **hiç dokunulmaz**. |
| `OrderDeck.Tests/App/DarkControlsPaletteBridgeTests.cs` (yeni, **geçici**) | 17 elle yapılan hex düzenlemesinin doğruluğunu makineyle doğrular. PR 2 Task 8'de silinir. |

**PR 2 — dal `chore/base-tema-darkcontrols-silme`**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.Tests/App/UnstyledControlGuardTests.cs` (yeni, **kalıcı**) | İzlenen kontrol tiplerinden stilsiz kullanım kalmadığını sürekli doğrular. |
| `OrderDeck.App/Themes/Controls.xaml` (değişir) | 4 yeni keyed stil: `OD.ListBoxItem`, `OD.ListBox`, `OD.PasswordBox`, `OD.CheckBox`, `OD.ShortcutCapture`. Ayrıca 5 `ContentPresenter.Resources` kaçamağı silinir. |
| `OrderDeck.App/Themes/Icons.xaml` (değişir) | `OD.Icon.Gift` buraya taşınır. |
| `OrderDeck.App/Themes/Base.xaml` (yeni) | Yalnız XAML'den erişilemeyen örtük stiller: `Window`, `ContextMenu`, `MenuItem`, `Separator`, `ScrollBar` (+ `OD.ScrollBar.Thumb`), `ToolTip`. |
| `OrderDeck.App/Themes/DarkControls.xaml` | **silinir** |
| `OrderDeck.App/Themes/PlatformIcons.xaml` (değişir, satır 118, 122) | Silinen 2 token atfı yeni token'lara çevrilir. |
| `OrderDeck.App/App.xaml` (değişir, satır 20-27) | Merge sırası `… Icons → Base → Controls → PlatformIcons`. |
| `OrderDeck.Tests/App/ThemeMergeTests.cs` (değişir, satır 18-19, 27-28, `OD.Bg.Window` iddiası) | Sözlük listeleri + bayat token iddiası güncellenir. |
| `OrderDeck.Tests/App/ControlsThemeTests.cs` (değişir, satır ~14) | Yeni 5 anahtar `StyleKeys`'e eklenir. |
| 12 görünüm dosyası | 20 stilsiz kullanım keyed stile bağlanır (Task 6'da tam liste). |

---

## PR 1 — renk takası

### Task 1: Palet köprüsü testi + 17 token'ın takası

**Files:**
- Create: `OrderDeck.Tests/App/DarkControlsPaletteBridgeTests.cs`
- Modify: `OrderDeck.App/Themes/DarkControls.xaml:24-43`

**Bağlam:** `DarkControls.xaml` kendi kendine yeten bir sözlük — stilleri kendi
`OD.Bg.*` / `OD.Fg.*` anahtarlarına `StaticResource` ile bağlanıyor. Bu yüzden
anahtarlar silinmez, yalnız **`Color` değerleri** `Colors.xaml`'deki
karşılıklarına çekilir. 17 satırlık elle hex düzenlemesi hata yapmaya çok
müsait; test bunu makineyle doğruluyor.

- [ ] **Step 1: Başarısız olacak testi yaz**

Create `OrderDeck.Tests/App/DarkControlsPaletteBridgeTests.cs`:

```csharp
using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// GEÇİCİ TEST — Faz 4b PR 1 ile geldi, PR 2'de <c>DarkControls.xaml</c> ile
/// birlikte silinecek.
///
/// NEDEN VAR: PR 1 tek iş yapıyor — <c>DarkControls.xaml</c>'in 17 eski renk
/// token'ının hex değerini <c>Colors.xaml</c>'deki karşılığına çekmek. Bu 17
/// elle düzenleme; bir hanesi yanlış yazılırsa hiçbir derleyici uyarmaz ve
/// arayüzde ancak gözle fark edilir. Eşleme tablosu spec'te
/// (2026-08-11-arayuz-faz4b-base-tema-design.md, "PR 1 — renk takası").
/// </summary>
public class DarkControlsPaletteBridgeTests
{
    /// <summary>Eski anahtar → yeni anahtar. Spec'teki tablonun birebir kopyası.</summary>
    private static readonly (string Old, string New)[] Mapping =
    [
        ("OD.Bg.Window",        "OD.Brush.Bg"),
        ("OD.Bg.Surface",       "OD.Brush.Surface"),
        ("OD.Bg.Elevated",      "OD.Brush.Surface2"),
        ("OD.Bg.Input",         "OD.Brush.Surface2"),
        ("OD.Bg.InputHover",    "OD.Brush.Surface2"),
        ("OD.Bg.InputPressed",  "OD.Brush.Surface2"),
        ("OD.Bg.InputDisabled", "OD.Brush.Surface2"),
        ("OD.Border.Subtle",    "OD.Brush.Border"),
        ("OD.Border.Hover",     "OD.Brush.BorderStrong"),
        ("OD.Border.Focus",     "OD.Brush.Accent"),
        ("OD.Fg.Primary",       "OD.Brush.Text"),
        ("OD.Fg.Secondary",     "OD.Brush.TextDim"),
        ("OD.Fg.Disabled",      "OD.Brush.TextMute"),
        ("OD.Accent",           "OD.Brush.Accent"),
        ("OD.Accent.Hover",     "OD.Brush.AccentHot"),
        ("OD.Accent.Pressed",   "OD.Brush.AccentDeep"),
        ("OD.Selection",        "OD.Brush.Surface2"),
    ];

    [Fact]
    public void Every_legacy_token_carries_its_new_palette_colour()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var dark = Load("DarkControls.xaml");
            var colors = Load("Colors.xaml");

            foreach (var (oldKey, newKey) in Mapping)
            {
                var actual = ((SolidColorBrush)dark[oldKey]).Color;
                var expected = ((SolidColorBrush)colors[newKey]).Color;

                Assert.True(actual == expected,
                    $"{oldKey} = {actual}, beklenen {newKey} = {expected}");
            }
        });

        Assert.Null(error);
    }

    private static ResourceDictionary Load(string fileName)
        => new()
        {
            Source = new Uri(
                "pack://application:,,,/OrderDeck.App;component/Themes/" + fileName)
        };
}
```

- [ ] **Step 2: Testi koştur, kırmızı olduğunu gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~DarkControlsPaletteBridgeTests`
Expected: FAIL — ilk satırda `OD.Bg.Window = #FF0F1118, beklenen OD.Brush.Bg = #FF090A0E`.

- [ ] **Step 3: 17 token'ı takas et**

`OrderDeck.App/Themes/DarkControls.xaml` satır 19-43'teki blok **tamamen**
aşağıdakiyle değiştirilir (yorumlar dahil — eski "handoff tablosu" gerekçesi
artık yanlış):

```xml
    <!-- ── Palet — Faz 4b PR 1: değerler Themes/Colors.xaml'den ────────────
         Bu anahtarlar SİLİNMEDİ çünkü aşağıdaki stiller onlara StaticResource
         ile bağlı ve bu sözlük kendi kendine yetiyor (testler onu tek başına
         yüklüyor). Yapılan tek iş renkleri yeni palete çekmek; stil gövdeleri
         PR 2'de taşınacak.

         Üç uyumsuzluk renk eşlemesiyle çözülmüyor, PR 2'ye devrediyor:
         (1) yeni dilde devre-dışı ayrı renk değil Opacity="0.45",
         (2) hover artık zemin yükseltmiyor kenarlık güçlendiriyor → PR 1'den
             sonra hover geçici olarak görünmez, beklenen ara durum,
         (3) seçim idiyomu: liste türü kontroller Surface2 + AccentHot
             (bkz. OD.ComboBoxItem), dolu Accent yalnız DataGrid'e özgü. -->
    <SolidColorBrush x:Key="OD.Bg.Window"          Color="#090A0E"/>    <!-- OD.Brush.Bg -->
    <SolidColorBrush x:Key="OD.Bg.Surface"         Color="#0F111A"/>    <!-- OD.Brush.Surface -->
    <SolidColorBrush x:Key="OD.Bg.Elevated"        Color="#161A26"/>    <!-- OD.Brush.Surface2 -->
    <SolidColorBrush x:Key="OD.Bg.Input"           Color="#161A26"/>    <!-- OD.Brush.Surface2 -->
    <SolidColorBrush x:Key="OD.Bg.InputHover"      Color="#161A26"/>    <!-- OD.Brush.Surface2 (hover PR 2'de kenarlığa taşınır) -->
    <SolidColorBrush x:Key="OD.Bg.InputPressed"    Color="#161A26"/>    <!-- OD.Brush.Surface2 -->
    <SolidColorBrush x:Key="OD.Bg.InputDisabled"   Color="#161A26"/>    <!-- OD.Brush.Surface2 (geçici — PR 2'de Opacity 0.45) -->

    <SolidColorBrush x:Key="OD.Border.Subtle"      Color="#12FFFFFF"/>  <!-- OD.Brush.Border -->
    <SolidColorBrush x:Key="OD.Border.Hover"       Color="#21FFFFFF"/>  <!-- OD.Brush.BorderStrong -->
    <SolidColorBrush x:Key="OD.Border.Focus"       Color="#FF4A38"/>    <!-- OD.Brush.Accent -->

    <SolidColorBrush x:Key="OD.Fg.Primary"         Color="#F4F2EC"/>    <!-- OD.Brush.Text -->
    <SolidColorBrush x:Key="OD.Fg.Secondary"       Color="#A6ACBA"/>    <!-- OD.Brush.TextDim -->
    <SolidColorBrush x:Key="OD.Fg.Disabled"        Color="#868C9C"/>    <!-- OD.Brush.TextMute (geçici) -->

    <SolidColorBrush x:Key="OD.Accent"             Color="#FF4A38"/>    <!-- OD.Brush.Accent -->
    <SolidColorBrush x:Key="OD.Accent.Hover"       Color="#FF6A5A"/>    <!-- OD.Brush.AccentHot -->
    <SolidColorBrush x:Key="OD.Accent.Pressed"     Color="#E23A2A"/>    <!-- OD.Brush.AccentDeep -->
    <SolidColorBrush x:Key="OD.Selection"          Color="#161A26"/>    <!-- OD.Brush.Surface2 -->
```

- [ ] **Step 4: Testi koştur, yeşil olduğunu gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~DarkControlsPaletteBridgeTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.App/Themes/DarkControls.xaml OrderDeck.Tests/App/DarkControlsPaletteBridgeTests.cs
git commit -m "$(cat <<'EOF'
refactor(theme): DarkControls'un 17 renk token'ını yeni palete çek

Faz 4b PR 1. Stil gövdeleri dokunulmadı; yalnız eski mavi-gri hex
değerleri Colors.xaml karşılıklarına çekildi. 17 elle düzenlemeyi
doğrulayan geçici köprü testi eklendi (PR 2'de silinecek).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `OD.Icon.Gift` kırmızılarını palete hizala

**Files:**
- Modify: `OrderDeck.App/Themes/DarkControls.xaml:60, 66`

**Bağlam:** Hediye ikonu kutusu `#EF4444` / `#DC2626` — Tailwind kırmızısı,
paletin kırmızısı değil. Sarı kurdele (`#FBBF24` / `#FCD34D`) kalır: paletteki
`OD.Brush.Amber` `#FFB23E` ona çok yakın ama ikon içinde iki kademe sarı var,
tek token'a indirmek 3D hissini bozar. Kullanıcılar: `ShellBanners.xaml:69`,
`ShellTopBar.xaml:209`.

- [ ] **Step 1: Kutu gövdesinin rengini değiştir**

`OrderDeck.App/Themes/DarkControls.xaml:60`:

```xml
                <!-- Box body -->
                <GeometryDrawing Brush="#FF4A38">
```

(eski değer `#FFEF4444` idi; yeni `#FF4A38` = `OD.Brush.Accent`)

- [ ] **Step 2: Kapağın rengini değiştir**

`OrderDeck.App/Themes/DarkControls.xaml:66`:

```xml
                <!-- Box lid (slightly darker red, subtle 3D feel) -->
                <GeometryDrawing Brush="#E23A2A">
```

(eski değer `#FFDC2626` idi; yeni `#E23A2A` = `OD.Brush.AccentDeep`)

- [ ] **Step 3: Derle**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: `Build succeeded` — 0 hata, 0 uyarı.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.App/Themes/DarkControls.xaml
git commit -m "$(cat <<'EOF'
refactor(theme): hediye ikonunun kırmızılarını palete hizala

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: PR 1 doğrulaması + açılış

**Files:** yok (yalnız koşum)

- [ ] **Step 1: Tüm test paketini koştur**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Expected: PASS — 943 test (942 + yeni köprü testi). Hiçbir mevcut test
kırmızıya dönmemeli; PR 1'de kod taşınmadı.

- [ ] **Step 2: Uygulamayı aç, tek soruyu sor**

Run: `dotnet run --project OrderDeck.App/OrderDeck.App.csproj`

Bakılacak: **"her yer kırmızı/siyah palette mi, mavi kalan var mı?"**
Tur: giriş ekranı → ana kabuk → sağ üst üç-nokta menüsü (bağlam menüsü) →
Ayarlar sayfası → bir çekmece (Müşteri Ara).
Beklenen ara durumlar (**hata değil**): düğme/girdi hover'ı görünmez oldu
(zemin üç kademeden tek kademeye indi, kenarlık geçişi PR 2'de gelecek);
devre-dışı düğmeler zeminden ayrışmıyor.

- [ ] **Step 3: PR aç**

```bash
git push -u origin chore/tema-renk-takasi
gh pr create --title "refactor(theme): Faz 4b PR 1 — DarkControls renk takası" --body "$(cat <<'EOF'
## Özet
- `DarkControls.xaml`'in 17 renk token'ı `Colors.xaml` karşılıklarına çekildi;
  uygulamanın tamamı tek palete (yakın-siyah + kırmızı) indi.
- `OD.Icon.Gift`'in kutu kırmızıları palet kırmızısına hizalandı.
- 17 elle hex düzenlemesini doğrulayan geçici `DarkControlsPaletteBridgeTests`
  eklendi — PR 2'de `DarkControls.xaml` ile birlikte silinecek.

Stil gövdeleri **hiç taşınmadı**; yapı değişikliği PR 2'de.

## Beklenen ara durumlar (hata değil)
- Hover görünmez: eski dil zemini üç kademe yükseltiyordu, yeni dilde zemin
  sabit kalıp kenarlık güçleniyor. Kenarlık geçişi PR 2'de geliyor.
- Devre-dışı kontroller zeminden ayrışmıyor: yeni dilde devre-dışı ayrı renk
  değil `Opacity="0.45"`. PR 2'de idiyoma çevrilecek.

## Test planı
- [ ] `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` → 943 yeşil
- [ ] `dotnet build OrderDeck.App/OrderDeck.App.csproj` → 0 hata, 0 uyarı
- [ ] Elle tur: giriş → kabuk → bağlam menüsü → Ayarlar → çekmece; mavi kalan yok

Spec: `docs/superpowers/specs/2026-08-11-arayuz-faz4b-base-tema-design.md`
EOF
)"
```

---

## PR 2 — `Base.xaml` + silme

> Dal: `git checkout master && git pull && git checkout -b chore/base-tema-darkcontrols-silme`
> (PR 1 merge edildikten sonra.)

### Task 4: Kalıcı "stilsiz kontrol" bekçi testi

**Files:**
- Create: `OrderDeck.Tests/App/UnstyledControlGuardTests.cs`

**Bağlam:** Bu testin amacı Faz 4b'yi bitirmek değil, **bitmiş kalmasını**
sağlamak. Örtük stiller gidince stilsiz bir kontrol derleme hatası vermez,
çalışma anında da patlamaz — sadece Windows'un açık gri varsayılan görünümüyle
koyu zeminin üstünde çirkin durur. Tek kanıt tarama.

**"Stilli" sayılmanın üç yolu** (spec "Risk A" maddesi):
1. Öğenin kendi `Style="…"` özniteliği ya da `<X.Style>` eleman sözdizimi.
2. Aynı dosyada tanımlı **yerel örtük stil** (`<Style TargetType="X">`, `x:Key` yok).
3. Ebeveynin **`ItemContainerStyle`** setter'ı.

Test 1 ve 2'yi doğrudan uyguluyor. 3 için **kapsayıcı öğe tipleri
(`ListBoxItem`, `ComboBoxItem`, `TabItem`, `MenuItem`) izleme listesine hiç
alınmıyor** — ebeveyn-çocuk ilişkisini metin taramasıyla çözmek kırılgan
olurdu ve stilleri meşru olarak dışarıdan geliyor. `TextBlock` de dışarıda:
`Foreground` miras alınan bir özellik ve `MainWindow.xaml:10` onu veriyor.

- [ ] **Step 1: Başarısız olacak testi yaz**

Create `OrderDeck.Tests/App/UnstyledControlGuardTests.cs`:

```csharp
using System.Text.RegularExpressions;

namespace OrderDeck.Tests.App;

/// <summary>
/// KALICI BEKÇİ — Faz 4b ile geldi.
///
/// NEDEN VAR: Faz 4b <c>DarkControls.xaml</c>'in örtük stillerini sildi. Örtük
/// stil yokken <c>&lt;Button/&gt;</c> yazmak derlemeyi kırmaz, çalışma anında
/// da patlamaz — Windows'un açık gri varsayılan görünümüyle koyu zeminin
/// üstünde durur. Gözle gezmeden fark edilmez; tek kanıt tarama.
///
/// KAPSAM DIŞI TİPLER ve gerekçeleri:
/// - <c>ListBoxItem</c> / <c>ComboBoxItem</c> / <c>TabItem</c> /
///   <c>MenuItem</c>: stilleri ebeveynin <c>ItemContainerStyle</c>'ından
///   geliyor. Bu ilişkiyi metin taramasıyla çözmek kırılgan olurdu ve
///   kullanım yerinde stil olmaması burada DOĞRU.
/// - <c>TextBlock</c>: <c>Foreground</c> miras alınan bir bağımlılık
///   özelliği, MainWindow.xaml onu veriyor (Faz 4b'nin ince çekirdek kararı
///   buna dayanıyor).
/// - <c>Themes/</c> ve <c>App.xaml</c>: stil tanımlarının kendisi.
/// </summary>
public class UnstyledControlGuardTests
{
    private static readonly string[] Guarded =
    [
        "Button", "TextBox", "PasswordBox", "CheckBox", "RadioButton",
        "ComboBox", "ListBox", "TabControl", "DataGrid", "Label", "GroupBox",
        "controls:ShortcutCaptureButton",
    ];

    [Fact]
    public void No_view_uses_a_guarded_control_without_a_style()
    {
        var offenders = new List<string>();
        var root = RepoRoot();

        foreach (var file in ViewFiles(root))
        {
            var xaml = File.ReadAllText(file);
            var localImplicit = LocalImplicitTargets(xaml);
            var relative = Path.GetRelativePath(root, file);

            foreach (var name in Guarded)
            {
                if (localImplicit.Contains(name) ||
                    localImplicit.Contains(name.Split(':')[^1]))
                    continue;

                foreach (Match m in ElementTag(name).Matches(xaml))
                {
                    if (Regex.IsMatch(m.Groups[1].Value, "\\bStyle\\s*=\\s*\""))
                        continue;

                    var selfClosing = m.Groups[2].Value == "/";
                    if (!selfClosing &&
                        xaml[(m.Index + m.Length)..].TrimStart()
                            .StartsWith("<" + name + ".Style", StringComparison.Ordinal))
                        continue;

                    var line = xaml[..m.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{relative}:{line} — <{name}> stilsiz");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Stilsiz kontrol(ler):" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Nitelik değerinin içindeki &gt; karakterini yutmasın diye tırnak farkındalıklı.</summary>
    private static Regex ElementTag(string name)
        => new("<" + Regex.Escape(name) + "(?![\\w.:])((?:\"[^\"]*\"|[^<>])*?)(/?)>",
               RegexOptions.Singleline);

    private static readonly Regex StyleTag =
        new("<Style(?![\\w.:])((?:\"[^\"]*\"|[^<>])*?)/?>", RegexOptions.Singleline);

    private static readonly Regex TargetTypeAttr =
        new("TargetType\\s*=\\s*\"(?:\\{x:Type\\s+)?([\\w:]+)\\}?\"");

    /// <summary>Dosyanın kendi Resources'ında tanımlı, x:Key'siz (= örtük) stillerin hedef tipleri.</summary>
    private static HashSet<string> LocalImplicitTargets(string xaml)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in StyleTag.Matches(xaml))
        {
            var attrs = m.Groups[1].Value;
            if (attrs.Contains("x:Key", StringComparison.Ordinal)) continue;

            var target = TargetTypeAttr.Match(attrs);
            if (target.Success) set.Add(target.Groups[1].Value);
        }

        return set;
    }

    private static IEnumerable<string> ViewFiles(string root)
        => Directory
            .EnumerateFiles(Path.Combine(root, "OrderDeck.App"), "*.xaml",
                            SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)!) != "Themes")
            .Where(f => Path.GetFileName(f) != "App.xaml")
            .OrderBy(f => f, StringComparer.Ordinal);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderDeck.sln")))
            dir = dir.Parent;

        Assert.True(dir is not null, "OrderDeck.sln bulunamadı — depo kökü tespit edilemedi.");
        return dir!.FullName;
    }
}
```

- [ ] **Step 2: Testi koştur, tam 20 ihlalle kırmızı olduğunu gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~UnstyledControlGuardTests`
Expected: FAIL — listede tam olarak şu 20 kayıt:

```
OrderDeck.App\Controls\AnimationPickerControl.xaml:72  <Button>
OrderDeck.App\Controls\AnimationPickerControl.xaml:75  <Button>
OrderDeck.App\Views\Drawers\CustomerSearchDrawer.xaml:136  <ListBox>
OrderDeck.App\Views\Drawers\DekontEkleDrawer.xaml:32  <Button>
OrderDeck.App\Views\Drawers\DekontEkleDrawer.xaml:119  <ComboBox>
OrderDeck.App\Views\Drawers\FacebookPagePickerDrawer.xaml:49  <ListBox>
OrderDeck.App\Views\Drawers\GiveawayDrawer.xaml:50  <ComboBox>
OrderDeck.App\Views\Drawers\GiveawayDrawer.xaml:74  <ComboBox>
OrderDeck.App\Views\Drawers\GiveawayDrawer.xaml:87  <ComboBox>
OrderDeck.App\Views\Drawers\GiveawayDrawer.xaml:119  <CheckBox>
OrderDeck.App\Views\Gates\LoginGate.xaml:66  <PasswordBox>
OrderDeck.App\Views\Gates\LoginGate.xaml:105  <PasswordBox>
OrderDeck.App\Views\Gates\LoginGate.xaml:110  <PasswordBox>
OrderDeck.App\Views\Gates\LoginGate.xaml:162  <ListBox>
OrderDeck.App\Views\Gates\RestoreGate.xaml:38  <ListBox>
OrderDeck.App\Views\Pages\PeriodReportPage.xaml:45  <CheckBox>
OrderDeck.App\Views\Pages\SettingsPage.xaml:657  <controls:ShortcutCaptureButton>
OrderDeck.App\Views\Shell\ChatPanel.xaml:50  <ListBox>
OrderDeck.App\Views\Shell\PrintQueuePanel.xaml:44  <ListBox>
OrderDeck.App\Views\Shell\PrintQueuePanel.xaml:101  <Button>
```

Sayı 20'den **fazlaysa** test yanlış yazılmış demektir (muhtemelen yerel örtük
stili ya da tırnak içindeki `>` karakterini kaçırıyor); **azsa** eleman
eşleşmesi kaçıyordur. İkisinde de önce testi düzelt, görünümlere dokunma.

- [ ] **Step 3: Commit (test bilerek kırmızı)**

```bash
git add OrderDeck.Tests/App/UnstyledControlGuardTests.cs
git commit -m "test(theme): stilsiz kontrol bekçisi ekle (şu an kırmızı)

Faz 4b PR 2'nin ölçütü. 20 stilsiz kullanımı listeliyor; Task 5-6
onları keyed OD.* stillerine bağlayınca yeşile dönecek.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: Beş yeni keyed stil

**Files:**
- Modify: `OrderDeck.App/Themes/Controls.xaml` (sona eklenir; `OD.ListBoxItem`
  mutlaka `OD.ListBox`'tan **önce** — `StaticResource` ileriye referans veremez)
- Modify: `OrderDeck.Tests/App/ControlsThemeTests.cs:8-14`

- [ ] **Step 1: `ControlsThemeTests.StyleKeys`'e beş anahtarı ekle (test önce)**

`OrderDeck.Tests/App/ControlsThemeTests.cs` içindeki `StyleKeys` dizisini
aşağıdakiyle değiştir:

```csharp
    private static readonly string[] StyleKeys =
    [
        "OD.Panel", "OD.Button.Primary", "OD.Button.Ghost", "OD.Chip",
        "OD.TextBox", "OD.Text.Micro", "OD.Text.Mono", "OD.CountPill",
        // Faz 4b: DarkControls'un örtük stillerinin keyed karşılıkları.
        "OD.ListBoxItem", "OD.ListBox", "OD.PasswordBox", "OD.CheckBox",
        "OD.ShortcutCapture"
    ];
```

- [ ] **Step 2: Testi koştur, kırmızı olduğunu gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ControlsThemeTests`
Expected: FAIL — `Controls_dictionary_defines_every_faz1_style`,
`OD.ListBoxItem` anahtarı bulunamıyor.

- [ ] **Step 3: `Controls.xaml` kök etiketine `controls:` ad alanını ekle**

`OD.ShortcutCapture` `TargetType="controls:ShortcutCaptureButton"` diyecek;
önek kökte tanımlı olmalı. `OrderDeck.App/Themes/Controls.xaml` kök
`<ResourceDictionary …>` etiketine ekle:

```xml
                    xmlns:controls="clr-namespace:OrderDeck.App.Controls"
```

- [ ] **Step 4: Beş stili `Controls.xaml`'in sonuna, kapanış `</ResourceDictionary>` etiketinden hemen önce yaz**

```xml
    <!-- ══ Faz 4b — DarkControls'un örtük stillerinin keyed karşılıkları ═══
         Örtük DEĞİL keyed: kullanım yerinde açıkça bağlanıyorlar ki yeni bir
         liste/girdi eklendiğinde tek görünüme çakılmasın. Bekçi:
         OrderDeck.Tests/App/UnstyledControlGuardTests.cs -->

    <!-- ListBoxItem — seçim idiyomu OD.ComboBoxItem ile aynı: zemin Surface2,
         yazı AccentHot. Dolu Accent zemin bilerek KULLANILMIYOR; sohbet akışı
         ve baskı kuyruğu uzun listeler, dolu kırmızı satır o yoğunlukta
         gürültü yapıyor (dolu Accent DataGrid'e özgü kaldı).
         OD.ListBox'tan ÖNCE tanımlı: StaticResource ileriye bakmaz. -->
    <Style x:Key="OD.ListBoxItem" TargetType="ListBoxItem">
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="Padding"    Value="{StaticResource OD.Pad.3}"/>
        <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="Bd"
                            Background="Transparent"
                            Padding="{TemplateBinding Padding}"
                            CornerRadius="{StaticResource OD.Radius.Xs}"
                            Margin="{StaticResource OD.Pad.1}"
                            SnapsToDevicePixels="True">
                        <ContentPresenter TextElement.Foreground="{TemplateBinding Foreground}"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource OD.Brush.Surface2}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource OD.Brush.Surface2}"/>
                            <Setter Property="Foreground"
                                    Value="{StaticResource OD.Brush.AccentHot}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ListBox — kapsayıcı görünmez olsun: zemin şeffaf, kenarlık yok.
         ItemContainerStyle setter'ı, kullanım yerinde kendi satır stilini
         yazan görünümleri EZMEZ: WPF önceliğinde yerel değer stil setter'ını
         yener (PrintQueuePanel, ChatPanel, CustomerSearchDrawer,
         FacebookPagePickerDrawer kendi stillerini korur). -->
    <Style x:Key="OD.ListBox" TargetType="ListBox">
        <Setter Property="Background"      Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding"         Value="0"/>
        <Setter Property="Foreground"      Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="FontFamily"      Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"        Value="{StaticResource OD.Font.F2}"/>
        <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
        <Setter Property="ItemContainerStyle" Value="{StaticResource OD.ListBoxItem}"/>
    </Style>

    <!-- PasswordBox — OD.TextBox'ın birebir eşi. LoginGate'te parola kutusunun
         komşusundan farklı zeminde durup MAVİ odak kenarlığı almasının sebebi
         DarkControls'un örtük PasswordBox stiliydi; asıl çözüm bu.
         İpucu (Tag) yok: parola alanında placeholder istenmiyor. -->
    <Style x:Key="OD.PasswordBox" TargetType="PasswordBox">
        <Setter Property="Background"      Value="{StaticResource OD.Brush.Surface2}"/>
        <Setter Property="BorderBrush"     Value="{StaticResource OD.Brush.Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding"         Value="{StaticResource OD.Pad.4}"/>
        <Setter Property="Foreground"      Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="CaretBrush"      Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="FontFamily"      Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"        Value="{StaticResource OD.Font.F2}"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="PasswordBox">
                    <Border x:Name="Root"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{StaticResource OD.Radius.Sm}"
                            SnapsToDevicePixels="True">
                        <ScrollViewer x:Name="PART_ContentHost"
                                      Margin="{TemplateBinding Padding}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Setter TargetName="Root" Property="BorderBrush"
                                    Value="{StaticResource OD.Brush.Accent}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- CheckBox — işlem seçenekleri için. Ayarlardaki açık/kapalı anahtarları
         OD.Toggle kullanmaya devam ediyor; anahtar görünümü burada anlamı
         yanlış verir (bkz. OD.Toggle'ın kendi yorumu). -->
    <Style x:Key="OD.CheckBox" TargetType="CheckBox">
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"   Value="{StaticResource OD.Font.F2}"/>
        <Setter Property="Cursor"     Value="Hand"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                    <StackPanel Orientation="Horizontal" Background="Transparent">
                        <Border x:Name="Box"
                                Width="{StaticResource OD.Icon.Md}"
                                Height="{StaticResource OD.Icon.Md}"
                                Background="{StaticResource OD.Brush.Surface2}"
                                BorderBrush="{StaticResource OD.Brush.BorderStrong}"
                                BorderThickness="1"
                                CornerRadius="{StaticResource OD.Radius.Xs}"
                                VerticalAlignment="Center"
                                SnapsToDevicePixels="True">
                            <Path x:Name="Check"
                                  Visibility="Collapsed"
                                  Data="M 3,8 L 6.5,11.5 L 13,4"
                                  Stroke="{StaticResource OD.Brush.OnAccent}"
                                  StrokeThickness="2"
                                  StrokeStartLineCap="Round"
                                  StrokeEndLineCap="Round"
                                  StrokeLineJoin="Round"/>
                        </Border>
                        <ContentPresenter Margin="{StaticResource OD.Pad.Left4}"
                                          VerticalAlignment="Center"
                                          TextElement.Foreground="{TemplateBinding Foreground}"/>
                    </StackPanel>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="Box" Property="Background"
                                    Value="{StaticResource OD.Brush.Accent}"/>
                            <Setter TargetName="Box" Property="BorderBrush"
                                    Value="{StaticResource OD.Brush.Accent}"/>
                            <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Box" Property="BorderBrush"
                                    Value="{StaticResource OD.Brush.Accent}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45"/>
                            <Setter Property="Cursor"  Value="Arrow"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ShortcutCaptureButton — Button'dan türeyen özel kontrol; örtük stil
         eşleşmesi TAM tipe baktığı için ayrı bir girdi gerekiyordu.
         DarkControls'ta BasedOn="{StaticResource {x:Type Button}}" ile silinen
         örtük Button stiline yaslanıyordu; artık keyed OD.Button.Secondary'ye
         yaslanıyor. Tek fark tuş bileşiminin mono yazı tipiyle okunması. -->
    <Style x:Key="OD.ShortcutCapture"
           TargetType="controls:ShortcutCaptureButton"
           BasedOn="{StaticResource OD.Button.Secondary}">
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Mono}"/>
        <Setter Property="HorizontalContentAlignment" Value="Center"/>
    </Style>
```

- [ ] **Step 5: Testi koştur, yeşil olduğunu gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ControlsThemeTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/Themes/Controls.xaml OrderDeck.Tests/App/ControlsThemeTests.cs
git commit -m "feat(theme): OD.ListBox/ListBoxItem/PasswordBox/CheckBox/ShortcutCapture

Faz 4b PR 2. DarkControls'un silinecek örtük stillerinin keyed
karşılıkları; seçim idiyomu OD.ComboBoxItem'la aynı (Surface2 +
AccentHot), devre-dışı Opacity 0.45.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: 20 stilsiz kullanımı bağla

**Files:**
- Modify: `OrderDeck.App/Controls/AnimationPickerControl.xaml:72, 75`
- Modify: `OrderDeck.App/Views/Drawers/CustomerSearchDrawer.xaml:136`
- Modify: `OrderDeck.App/Views/Drawers/DekontEkleDrawer.xaml:32, 119`
- Modify: `OrderDeck.App/Views/Drawers/FacebookPagePickerDrawer.xaml:49`
- Modify: `OrderDeck.App/Views/Drawers/GiveawayDrawer.xaml:50, 74, 87, 119`
- Modify: `OrderDeck.App/Views/Gates/LoginGate.xaml:66, 105, 110, 162`
- Modify: `OrderDeck.App/Views/Gates/RestoreGate.xaml:38`
- Modify: `OrderDeck.App/Views/Pages/PeriodReportPage.xaml:45`
- Modify: `OrderDeck.App/Views/Pages/SettingsPage.xaml:657`
- Modify: `OrderDeck.App/Views/Shell/ChatPanel.xaml:50`
- Modify: `OrderDeck.App/Views/Shell/PrintQueuePanel.xaml:44, 101`

**Bağlam:** Her düzenleme tek bir `Style="{StaticResource …}"` özniteliği
eklemek. Var olan öznitelikler (`Background="Transparent"`,
`BorderThickness="0"`, `Padding`, `Margin`) **silinmez**: WPF önceliğinde
yerel değer stil setter'ını yener, yani o görünümlerin bilinçli ayarları
korunur. Satır numaraları önceki düzenlemelerle kayabilir — anahtar öznitelikle
(ör. `x:Name`) doğrula.

- [ ] **Step 1: `ListBox`'lar (6 adet) → `OD.ListBox`**

`CustomerSearchDrawer.xaml:136`:
```xml
            <ListBox x:Name="ResultsList"
                     Style="{StaticResource OD.ListBox}"
                     ItemsSource="{Binding Results}"
```

`FacebookPagePickerDrawer.xaml:49`:
```xml
        <ListBox x:Name="PagesList"
                 Style="{StaticResource OD.ListBox}"
                 Background="Transparent"
```

`LoginGate.xaml:162`:
```xml
            <ListBox Grid.Row="1"
                     Style="{StaticResource OD.ListBox}"
                     MaxHeight="{StaticResource OD.Layout.GateListMaxHeight}"
```

`RestoreGate.xaml:38`:
```xml
            <ListBox x:Name="BackupList" Grid.Row="1"
                     Style="{StaticResource OD.ListBox}"
                     MaxHeight="{StaticResource OD.Layout.GateListMaxHeight}"
```

`ChatPanel.xaml:50`:
```xml
            <ListBox Grid.Row="1"
                     x:Name="ChatList"
                     Style="{StaticResource OD.ListBox}"
                     ItemsSource="{Binding ChatView}"
```

`PrintQueuePanel.xaml:44`:
```xml
            <ListBox Grid.Row="1"
                     x:Name="QueueList"
                     Style="{StaticResource OD.ListBox}"
                     ItemsSource="{Binding PrintQueue}"
```

- [ ] **Step 2: `ComboBox`'lar (4 adet) → `OD.ComboBox`**

`DekontEkleDrawer.xaml:119`:
```xml
                <ComboBox Style="{StaticResource OD.ComboBox}"
                          SelectedValue="{Binding CustomerPlatform}"
```

`GiveawayDrawer.xaml:50`:
```xml
                <ComboBox Style="{StaticResource OD.ComboBox}"
                          ItemsSource="{Binding DurationOptions}"
```

`GiveawayDrawer.xaml:74`:
```xml
                <ComboBox Style="{StaticResource OD.ComboBox}"
                          ItemsSource="{Binding PlatformOptions}"
```

`GiveawayDrawer.xaml:87`:
```xml
                <ComboBox x:Name="AnimCombo"
                          Style="{StaticResource OD.ComboBox}"
                          ItemsSource="{Binding AvailableAnimations}"
```

Not: `DekontEkleDrawer`'daki 6 `<ComboBoxItem>` ayrıca bağlanmıyor —
`OD.ComboBox` `ItemContainerStyle="{StaticResource OD.ComboBoxItem}"` taşıyor
(Controls.xaml:940) ve WPF bunu XAML'de doğrudan yazılmış kapsayıcılara da
uygular.

- [ ] **Step 3: `PasswordBox`'lar (3 adet) → `OD.PasswordBox`**

`LoginGate.xaml:66`:
```xml
                <PasswordBox x:Name="LoginPassword"
                             Style="{StaticResource OD.PasswordBox}"
                             PasswordChanged="OnLoginPasswordChanged"
                             Padding="{StaticResource OD.Pad.InputWithIcon}"/>
```

`LoginGate.xaml:105`:
```xml
            <PasswordBox x:Name="RegisterPassword"
                         Style="{StaticResource OD.PasswordBox}"
                         Margin="{StaticResource OD.Pad.Bottom4}"
                         PasswordChanged="OnRegisterPasswordChanged"/>
```

`LoginGate.xaml:110`:
```xml
            <PasswordBox x:Name="RegisterPasswordConfirm"
                         Style="{StaticResource OD.PasswordBox}"
                         Margin="{StaticResource OD.Pad.Bottom4}"
                         PasswordChanged="OnRegisterPasswordConfirmChanged"/>
```

Ayrıca `LoginGate.xaml:60-62` ve `101-102`'deki iki yorum artık yanlış
("örtük DarkControls.xaml stilinde" diyor). İkisini de sil.

- [ ] **Step 4: `CheckBox`'lar (2 adet) → `OD.CheckBox`**

`GiveawayDrawer.xaml:119`:
```xml
                <CheckBox Content="Önceki kazananları dahil etme"
                          Style="{StaticResource OD.CheckBox}"
                          IsChecked="{Binding PreventRewinning}"
                          Margin="{StaticResource OD.Pad.Top5}"/>
```

`PeriodReportPage.xaml:45`:
```xml
            <CheckBox Content="Yalnızca adı bilinenler"
                      Style="{StaticResource OD.CheckBox}"
                      IsChecked="{Binding OnlyInvoiceReady}"
                      VerticalAlignment="Center"/>
```

- [ ] **Step 5: `Button`'lar (4 adet)**

`AnimationPickerControl.xaml:72` — kartın birincil eylemi:
```xml
                  <Button Content="Seç" Width="64" Margin="0,0,6,0"
                          Style="{StaticResource OD.Button.Primary}"
                          CommandParameter="{Binding Id}"
                          Click="SelectButton_Click"/>
```

`AnimationPickerControl.xaml:75` — ikincil eylem:
```xml
                  <Button Content="Önizle" Width="72"
                          Style="{StaticResource OD.Button.Secondary}"
                          CommandParameter="{Binding Id}"
                          Click="PreviewButton_Click"/>
```

`DekontEkleDrawer.xaml:32` — tıklanabilir kart; kendi
`Background="Transparent"` + `BorderThickness="0"` değerleri yerel değer
olduğu için `OD.Button.Ghost`'un setter'larını yenmeye devam eder:
```xml
                <Button Click="OnPickPdf"
                        Style="{StaticResource OD.Button.Ghost}"
                        HorizontalAlignment="Stretch"
```

`PrintQueuePanel.xaml:101` — küçük sayaç düğmesi:
```xml
                        <Button Grid.Column="3" Margin="8,0,0,0" Padding="8,2"
                                Style="{StaticResource OD.Button.Secondary}"
                                FontSize="{StaticResource OD.Font.F0}"
```

- [ ] **Step 6: `ShortcutCaptureButton` (1 adet) → `OD.ShortcutCapture`**

`SettingsPage.xaml:657`:
```xml
                                            <controls:ShortcutCaptureButton
                                                Style="{StaticResource OD.ShortcutCapture}"
                                                DockPanel.Dock="Right" Width="220"
```

- [ ] **Step 7: Bekçi testini koştur, yeşil olduğunu gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~UnstyledControlGuardTests`
Expected: PASS.

- [ ] **Step 8: Derle**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: `Build succeeded` — 0 hata, 0 uyarı.

- [ ] **Step 9: Commit**

```bash
git add OrderDeck.App/Controls/AnimationPickerControl.xaml OrderDeck.App/Views
git commit -m "refactor(theme): 20 stilsiz kontrolü keyed OD.* stillerine bağla

Bekçi testi yeşile döndü. Var olan yerel öznitelikler korundu: WPF
önceliğinde yerel değer stil setter'ını yener.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: `OD.Icon.Gift`'i `Icons.xaml`'e taşı + `PlatformIcons` atıflarını çevir

**Files:**
- Modify: `OrderDeck.App/Themes/Icons.xaml` (sona ekleme + satır 10-13'teki yorum)
- Modify: `OrderDeck.App/Themes/DarkControls.xaml:50-102` (silinir)
- Modify: `OrderDeck.App/Themes/PlatformIcons.xaml:115-122`

**Bağlam:** `DarkControls.xaml`'in dışarıdan kullanılan tek varlıkları bunlar.
Kullanıcılar: `ShellBanners.xaml:69`, `ShellTopBar.xaml:209` (ikon) ve
`PlatformIcons.xaml:118,122` (2 token). İkisi de taşınmadan `DarkControls.xaml`
silinemez.

- [ ] **Step 1: `Icons.xaml`'in baş yorumunu düzelt**

`OrderDeck.App/Themes/Icons.xaml:10-13`'teki paragraf `DarkControls.xaml`'a
atıf yapıyor; şununla değiştir:

```
    Neden DrawingImage değil: OD.Icon.Gift gibi ÇOK RENKLİ ikonlar
    DrawingImage olmalı (dosyanın sonunda). Aşağıdakiler tek renkli — Path
    olarak çizilip Fill'i ana kontrolün Foreground'ından alırlarsa
    hover/disabled durumlarında kendiliğinden doğru renge geçerler.
```

- [ ] **Step 2: `OD.Icon.Gift`'i `Icons.xaml`'in sonuna (kapanış etiketinden önce) taşı**

```xml
  <!-- ── Çok renkli ikon ────────────────────────────────────────────────
       WPF renkli emoji çizemiyor (glyph hattında COLR/CPAL desteği yok), bu
       yüzden çok renkli ikonlar DrawingImage olarak besteleniyor: vektör,
       ölçeklenebilir ve sistemin emoji fontuna bağlı değil.
       Kutu kırmızıları palete bağlı (Accent / AccentDeep); kurdelenin iki
       kademe sarısı 3D hissi için elde kalıyor. -->
  <DrawingImage x:Key="OD.Icon.Gift">
    <DrawingImage.Drawing>
      <DrawingGroup>
        <!-- Kutu gövdesi -->
        <GeometryDrawing Brush="#FF4A38">
          <GeometryDrawing.Geometry>
            <RectangleGeometry Rect="2,9,16,11" RadiusX="1" RadiusY="1"/>
          </GeometryDrawing.Geometry>
        </GeometryDrawing>
        <!-- Kapak -->
        <GeometryDrawing Brush="#E23A2A">
          <GeometryDrawing.Geometry>
            <RectangleGeometry Rect="1,7,18,3" RadiusX="1" RadiusY="1"/>
          </GeometryDrawing.Geometry>
        </GeometryDrawing>
        <!-- Dikey kurdele -->
        <GeometryDrawing Brush="#FFFBBF24">
          <GeometryDrawing.Geometry>
            <RectangleGeometry Rect="8.5,7,3,13"/>
          </GeometryDrawing.Geometry>
        </GeometryDrawing>
        <!-- Yatay kurdele -->
        <GeometryDrawing Brush="#FFFBBF24">
          <GeometryDrawing.Geometry>
            <RectangleGeometry Rect="1,8,18,2"/>
          </GeometryDrawing.Geometry>
        </GeometryDrawing>
        <!-- Fiyonk halkaları -->
        <GeometryDrawing Brush="#FFFBBF24">
          <GeometryDrawing.Geometry>
            <EllipseGeometry Center="6.5,5" RadiusX="3" RadiusY="2.4"/>
          </GeometryDrawing.Geometry>
        </GeometryDrawing>
        <GeometryDrawing Brush="#FFFBBF24">
          <GeometryDrawing.Geometry>
            <EllipseGeometry Center="13.5,5" RadiusX="3" RadiusY="2.4"/>
          </GeometryDrawing.Geometry>
        </GeometryDrawing>
        <!-- Fiyonk düğümü -->
        <GeometryDrawing Brush="#FFFCD34D">
          <GeometryDrawing.Geometry>
            <EllipseGeometry Center="10,6" RadiusX="1.6" RadiusY="1.4"/>
          </GeometryDrawing.Geometry>
        </GeometryDrawing>
      </DrawingGroup>
    </DrawingImage.Drawing>
  </DrawingImage>
```

Sonra `DarkControls.xaml`'den satır 50-102 (`<!-- ── Color icons -->` yorumu
ile `</DrawingImage>` arası) **silinir**. `OD.Shadow.Soft` (satır 45-48) de
silinir: dış kullanımı sıfır, menü/tooltip stillerinin içine gömülü kopyası
zaten var.

- [ ] **Step 3: `PlatformIcons.xaml`'in 2 token atfını çevir**

`OrderDeck.App/Themes/PlatformIcons.xaml:115-122`:

```xml
    <!-- DynamicResource: bu sözlük Base.xaml'den ayrı merge edildiği için
         kardeş sözlükteki fırçalar parse anında StaticResource ile
         çözülemez. -->
    <Border CornerRadius="6" Background="{DynamicResource OD.Brush.Surface2}"
            Padding="6,1" HorizontalAlignment="Center" VerticalAlignment="Center">
      <TextBlock Text="{Binding Platform}" FontSize="10" FontWeight="Bold"
                 FontFamily="Consolas" TextTrimming="CharacterEllipsis"
                 Foreground="{DynamicResource OD.Brush.TextDim}"/>
```

- [ ] **Step 4: Testleri koştur**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~Theme`
Expected: PASS — özellikle `ThemeMergeTests` ve `PlatformIconResourcesTests`.
`DarkControlsPaletteBridgeTests` hâlâ yeşil (17 token'a dokunulmadı).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.App/Themes/Icons.xaml OrderDeck.App/Themes/DarkControls.xaml OrderDeck.App/Themes/PlatformIcons.xaml
git commit -m "refactor(theme): OD.Icon.Gift'i Icons.xaml'e taşı, platform rozetini yeni tokenlara çevir

DarkControls.xaml'e kalan son dış bağımlılıklar koparıldı; artık
silinebilir durumda.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 8: `Base.xaml` + `DarkControls.xaml`'in silinmesi (**tek commit**)

**Files:**
- Create: `OrderDeck.App/Themes/Base.xaml`
- Delete: `OrderDeck.App/Themes/DarkControls.xaml`
- Delete: `OrderDeck.Tests/App/DarkControlsPaletteBridgeTests.cs`
- Modify: `OrderDeck.App/App.xaml:18-31`
- Modify: `OrderDeck.Tests/App/ThemeMergeTests.cs`

**NEDEN TEK COMMIT — atlanamaz:** Örtük stiller `ResourceDictionary`'de
**`Type` nesnesiyle** anahtarlanır. `ThemeMergeTests`'in çakışma taraması
anahtar kümelerini karşılaştırdığı için `Base.xaml` ile `DarkControls.xaml`
aynı merge'de bir arada duramaz (`Window`, `ContextMenu`, `MenuItem`,
`Separator`, `ScrollBar`, `ToolTip` çakışır). "Önce ekle, sonra sil" ara
adımı derlenir ama test kırmızı verir; ikisi aynı commit'te olmalı.

- [ ] **Step 1: `Base.xaml`'i yaz**

Create `OrderDeck.App/Themes/Base.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!--
      İNCE ÇEKİRDEK — burada YALNIZCA XAML'den erişilemeyen kontrollerin
      örtük stilleri durur. Her yeni örtük stil, o kontrolün tüm uygulamada
      tek görünüme çakılması demektir; eklemeden önce "kullanım yerinde
      Style= yazılabilir mi?" sorusunu yanıtla — yanıt evetse Controls.xaml'e
      keyed stil yaz.

      Neden bu altısı:
      - Window       : Background/Foreground/FontFamily emniyet ağı.
      - ContextMenu  : çalışma anında üretiliyor, elde tutulamıyor.
      - MenuItem     : ContextMenu'nün içi; tek görünüm isteniyor.
      - Separator    : menü ayracı.
      - ScrollBar    : ScrollViewer'ın şablonunun içinde üretiliyor.
      - ToolTip      : ToolTip="metin" yazınca WPF sarmalıyor.

      Örtük TextBlock stili BİLEREK YOK: Foreground miras alınan bir bağımlılık
      özelliği ve MainWindow.xaml onu veriyor. Örtük stil mirası yendiği için
      eskiden beş şablona ContentPresenter.Resources kaçamağı yazılmıştı;
      o tuzak bu dosyayla birlikte kapandı.

      Değerler Colors.xaml/Metrics.xaml'den; App.xaml bu dosyayı onlardan
      SONRA merge ediyor.
    -->

    <!-- ── Window — Background/Foreground/FontFamily ayarlamayı unutan bir
         pencere Windows 11'in açık sistem zemininde açılmasın diye. Emoji
         yedek zinciri: Latin karakterler Segoe UI'dan, emoji kod noktaları
         Segoe UI Emoji'den geliyor (renkli glif). -->
    <Style TargetType="{x:Type Window}">
        <Setter Property="Background" Value="{StaticResource OD.Brush.Bg}"/>
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="FontFamily" Value="Segoe UI, Segoe UI Emoji"/>
    </Style>

    <!-- ── ContextMenu — tam şablon, çünkü varsayılan Aero "ikon sütunu"
         (her MenuItem için onay/ikon yer tutan 28 px'lik açık şerit) koyu
         temada beyaz bir çizgi olarak görünüyordu. Yerine düz StackPanel
         ItemsHost. -->
    <Style TargetType="{x:Type ContextMenu}">
        <Setter Property="Background" Value="{StaticResource OD.Brush.Surface2}"/>
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="BorderBrush" Value="{StaticResource OD.Brush.Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="{StaticResource OD.Pad.2}"/>
        <Setter Property="HasDropShadow" Value="True"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type ContextMenu}">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            Padding="{TemplateBinding Padding}"
                            CornerRadius="{StaticResource OD.Radius.Md}"
                            SnapsToDevicePixels="True">
                        <StackPanel IsItemsHost="True"
                                    KeyboardNavigation.DirectionalNavigation="Cycle"/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ── MenuItem — ikon/onay oluğu yok (yukarıdaki beyaz şerit) ve dış
         Margin yok: popup'ın Padding'iyle birleşince öğelerin ilk 1-2
         karakteri soldan kırpılıyordu. Sol boşluk Border'ın Padding'inden
         geliyor ki popup ölçüm geçişinde tam genişliği görsün. -->
    <Style TargetType="{x:Type MenuItem}">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize" Value="{StaticResource OD.Font.F2}"/>
        <Setter Property="Padding" Value="14,7"/>
        <Setter Property="HorizontalContentAlignment" Value="Left"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type MenuItem}">
                    <Border x:Name="Bd"
                            Background="{TemplateBinding Background}"
                            BorderThickness="0"
                            CornerRadius="{StaticResource OD.Radius.Xs}"
                            Padding="{TemplateBinding Padding}"
                            SnapsToDevicePixels="True">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <ContentPresenter Grid.Column="0"
                                              ContentSource="Header"
                                              VerticalAlignment="Center"
                                              RecognizesAccessKey="True"
                                              TextElement.Foreground="{TemplateBinding Foreground}"/>
                            <TextBlock Grid.Column="1"
                                       Text="{TemplateBinding InputGestureText}"
                                       Foreground="{StaticResource OD.Brush.TextDim}"
                                       VerticalAlignment="Center"
                                       Margin="14,0,0,0"/>
                            <Path x:Name="SubmenuArrow"
                                  Grid.Column="2"
                                  Visibility="Collapsed"
                                  Data="M 0,0 L 4,4 L 0,8 Z"
                                  Fill="{StaticResource OD.Brush.TextDim}"
                                  VerticalAlignment="Center"
                                  Margin="14,0,0,0"/>
                            <Popup x:Name="PART_Popup"
                                   Placement="Right"
                                   IsOpen="{TemplateBinding IsSubmenuOpen}"
                                   AllowsTransparency="True"
                                   Focusable="False"
                                   PopupAnimation="Fade">
                                <Border Background="{StaticResource OD.Brush.Surface2}"
                                        BorderBrush="{StaticResource OD.Brush.Border}"
                                        BorderThickness="1"
                                        CornerRadius="{StaticResource OD.Radius.Xs}"
                                        Padding="{StaticResource OD.Pad.2}">
                                    <Border.Effect>
                                        <DropShadowEffect BlurRadius="14" ShadowDepth="3" Opacity="0.45"
                                                          Direction="270" Color="Black"/>
                                    </Border.Effect>
                                    <StackPanel IsItemsHost="True"
                                                KeyboardNavigation.DirectionalNavigation="Cycle"/>
                                </Border>
                            </Popup>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="Role" Value="SubmenuHeader">
                            <Setter TargetName="SubmenuArrow" Property="Visibility" Value="Visible"/>
                        </Trigger>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource OD.Brush.Surface}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="{x:Type Separator}">
        <Setter Property="Background" Value="{StaticResource OD.Brush.Border}"/>
        <Setter Property="Margin" Value="6,4"/>
        <Setter Property="Height" Value="1"/>
    </Style>

    <!-- ── ScrollBar — varsayılan Aero 17 px'lik oklu gri çubuk koyu dili
         bozuyor. 10 px şeffaf ray + yuvarlak tutamak; tutamak hover/sürükleme
         ile açılıyor. -->
    <Style x:Key="OD.ScrollBar.Thumb" TargetType="{x:Type Thumb}">
        <Setter Property="OverridesDefaultStyle" Value="True"/>
        <Setter Property="IsTabStop" Value="False"/>
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type Thumb}">
                    <Border x:Name="Bd"
                            Background="{StaticResource OD.Brush.BorderStrong}"
                            CornerRadius="3"
                            Margin="2"
                            SnapsToDevicePixels="True"/>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource OD.Brush.TextMute}"/>
                        </Trigger>
                        <Trigger Property="IsDragging" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource OD.Brush.TextDim}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="{x:Type ScrollBar}">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Width" Value="10"/>
        <Setter Property="MinWidth" Value="10"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type ScrollBar}">
                    <Grid Background="{TemplateBinding Background}">
                        <Track Name="PART_Track" IsDirectionReversed="True">
                            <Track.Thumb>
                                <Thumb Style="{StaticResource OD.ScrollBar.Thumb}"/>
                            </Track.Thumb>
                            <!-- Boş sayfa-yukarı/aşağı düğmeleri: raya tıklama alanı görünmesin. -->
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageDownCommand"
                                              Background="Transparent" BorderThickness="0"
                                              IsTabStop="False" Focusable="False"
                                              Template="{x:Null}"/>
                            </Track.IncreaseRepeatButton>
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageUpCommand"
                                              Background="Transparent" BorderThickness="0"
                                              IsTabStop="False" Focusable="False"
                                              Template="{x:Null}"/>
                            </Track.DecreaseRepeatButton>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto"/>
                <Setter Property="MinWidth" Value="0"/>
                <Setter Property="Height" Value="10"/>
                <Setter Property="MinHeight" Value="10"/>
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style TargetType="{x:Type ToolTip}">
        <Setter Property="Background" Value="{StaticResource OD.Brush.Surface2}"/>
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="BorderBrush" Value="{StaticResource OD.Brush.Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize" Value="{StaticResource OD.Font.F1}"/>
        <Setter Property="Padding" Value="8,5"/>
        <Setter Property="HasDropShadow" Value="True"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 2: `App.xaml`'in merge listesini güncelle**

`OrderDeck.App/App.xaml:18-31` aralığını şununla değiştir:

```xml
                <!-- XAML'den erişilemeyen kontrollerin örtük stilleri
                     (ScrollBar, ToolTip, ContextMenu, MenuItem, Separator,
                     Window). Token'lara bağımlı → onlardan SONRA. -->
                <ResourceDictionary Source="Themes/Base.xaml"/>
                <!-- Bileşen stilleri: token'lara BAĞIMLI, bu yüzden token
                     sözlüklerinden SONRA merge edilmeli. Base'ten de sonra:
                     keyed stiller örtük olanı yenebilsin. -->
                <ResourceDictionary Source="Themes/Controls.xaml"/>
                <!-- Sohbet listesindeki platform rozetleri: her platformun
                     resmi marka ikonu. Marka rehberleri kısaltma + marka
                     rengi taklidine izin vermiyor (bkz. dosya başı notu). -->
                <ResourceDictionary Source="Themes/PlatformIcons.xaml"/>
```

- [ ] **Step 3: `DarkControls.xaml`'i sil**

```bash
git rm OrderDeck.App/Themes/DarkControls.xaml
git rm OrderDeck.Tests/App/DarkControlsPaletteBridgeTests.cs
```

`DarkControls.xaml` `Page`/`Resource` olarak `.csproj`'de açıkça listelenmiyor
(WPF SDK varsayılan glob'u topluyor) — proje dosyasında düzenleme gerekmez.
Yine de doğrula: `grep -n DarkControls OrderDeck.App/OrderDeck.App.csproj`
çıktı vermemeli.

- [ ] **Step 4: `ThemeMergeTests`'i güncelle**

`OrderDeck.Tests/App/ThemeMergeTests.cs`:

```csharp
    private static readonly string[] NewDictionaries =
        ["Colors.xaml", "Metrics.xaml", "Motion.xaml", "Icons.xaml",
         "Base.xaml", "Controls.xaml"];
```

```csharp
    private static readonly string[] ExistingDictionaries =
        ["PlatformIcons.xaml"];
```

Ayrıca `App_resources_expose_the_new_tokens` testindeki
`OD.Bg.Window` iddiası **silinir** (o anahtar artık yok);
`OD.PlatformIcon.YouTube` iddiası kalır. Yerine `Base.xaml`'in yüklendiğini
kanıtlayan bir iddia eklenir:

```csharp
        Assert.IsType<Style>(
            Application.Current.Resources[typeof(System.Windows.Controls.ToolTip)]);
```

- [ ] **Step 5: Tüm test paketini koştur**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Expected: PASS — 942 test (942 taban + 1 bekçi − 1 silinen köprü testi).
Gerçekten XAML yükleyen 153 WPF testi asıl güvence: `Base.xaml`'de eksik bir
token kalırsa `XamlParseException` ile patlarlar.

- [ ] **Step 6: Derle**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: `Build succeeded` — 0 hata, 0 uyarı.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.App/Themes/Base.xaml OrderDeck.App/App.xaml OrderDeck.Tests/App/ThemeMergeTests.cs
git commit -m "refactor(theme): DarkControls.xaml'i sil, yerine ince Base.xaml koy

Örtük stil yalnız XAML'den erişilemeyen altı kontrolde kaldı: Window,
ContextMenu, MenuItem, Separator, ScrollBar, ToolTip. Örtük TextBlock
stili gitti — Foreground miras alınıyor, MainWindow onu veriyor.

Tek commit zorunlu: örtük stiller Type nesnesiyle anahtarlanıyor, iki
sözlük aynı merge'de bir arada durursa ThemeMergeTests çakışma görüyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 9: Beş `ContentPresenter.Resources` kaçamağını kaldır

**Files:**
- Modify: `OrderDeck.App/Themes/Controls.xaml:56, 131, 341, 406, 479` (satır
  numaraları Task 5'ten sonra kaymaz — eklemeler dosyanın sonuna yapıldı)

**Bağlam:** Örtük `TextBlock` stili mirası yeniyordu, bu yüzden beş şablona
`AncestorType` binding'li kaçamak yazılmıştı. Örtük stil gittiği için beşi de
gereksiz. Kaldırılmazsa zarar vermezler ama tuzağı belgeleyen yorumlarıyla
birlikte yanıltıcı hale gelirler.

- [ ] **Step 1: Beş bloğu sil**

Her birinde şu kalıp (ve varsa üstündeki açıklama yorumu) silinir; ilgili
`<ContentPresenter …>` etiketi tek satırlık kendine kapanan biçime döner:

```xml
                        <ContentPresenter.Resources>
                            <Style TargetType="TextBlock">
                                <Setter Property="Foreground"
                                        Value="{Binding Foreground,
                                                RelativeSource={RelativeSource AncestorType=Button}}"/>
                            </Style>
                        </ContentPresenter.Resources>
```

Yerler ve `AncestorType` değerleri:
- `OD.Button.Primary` (56) — `Button`
- `OD.Button.Secondary` (131) — `Button`
- `OD.CalendarDayButton` (341) — `CalendarDayButton`
- `OD.CalendarButton` (406) — `CalendarButton`
- `OD.CalendarItem` içindeki başlık düğmesi (479) — `Button`

- [ ] **Step 2: Testleri koştur**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Expected: PASS — 942 test.

- [ ] **Step 3: Takvim ve düğme metinlerini gözle doğrula**

Run: `dotnet run --project OrderDeck.App/OrderDeck.App.csproj`

Bakılacak: Dönem Raporu sayfasındaki tarih seçici (takvim gün/ay/yıl
düğmelerinin yazısı okunuyor mu, seçili gün beyaz yazı alıyor mu) ve
birincil/ikincil düğmelerin yazı rengi.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.App/Themes/Controls.xaml
git commit -m "refactor(theme): örtük TextBlock kaçamaklarını kaldır

Örtük TextBlock stili silindiği için beş şablondaki AncestorType
binding'li kaçamak stiller gereksizleşti.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 10: PR 2 doğrulaması + açılış

**Files:** yok (yalnız koşum)

- [ ] **Step 1: Kalıntı taraması**

Run: `grep -rn "DarkControls\|OD.Bg\.\|OD.Fg\.\|OD.Border\.\|OD.Accent\.\|OD.Selection\|OD.Shadow.Soft" --include=*.xaml --include=*.cs OrderDeck.App OrderDeck.Tests`
Expected: hiç eşleşme yok. (Eşleşme varsa o dosya `DarkControls`'un silinmiş
token'larına bakıyor demektir; çalışma anında sessizce boş fırça verir.)

- [ ] **Step 2: Tüm test paketi + build**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj && dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: 942 test yeşil; `Build succeeded`, 0 hata, 0 uyarı.

- [ ] **Step 3: Elle tam tur (Risk B)**

Run: `dotnet run --project OrderDeck.App/OrderDeck.App.csproj`

Öncelikli bakılacaklar:
1. **`LoginGate`** — e-posta kutusu ile şifre kutusu **aynı zeminde** mi,
   ikisi de **kırmızı** odak kenarlığı mı alıyor? (Faz 4b'nin çıkış noktası
   olan hata buydu.)
2. **`RestoreGate`** — yedek listesinde seçili satır `Surface2` zemin +
   `AccentHot` yazı mı?
3. **`ChatPanel` / `PrintQueuePanel`** — satır stilleri korunmuş mu (kara
   listedeki müşterinin zemini, kuyruk satırı), kaydırma çubuğu ince mi?
4. **Bağlam menüsü** (sohbette sağ tık) — sol kenarda beyaz şerit yok,
   ilk karakter kırpılmıyor, üzerine gelince zemin `Surface`'a kalkıyor.
5. **`GiveawayDrawer`** — üç `ComboBox` ve onay kutusu; onay kutusu işaretli
   iken kırmızı kutu + beyaz tik.
6. **Ayarlar → Kısayollar** — yakalama düğmeleri mono yazı tipiyle, ikincil
   düğme görünümünde; devre dışıyken solgun (`Opacity 0.45`).
7. **`AnimationPickerControl`** (Çekiliş → animasyon seç) — "Seç" birincil,
   "Önizle" ikincil.
8. **Herhangi bir `ToolTip`** — koyu zemin, ince kenarlık.

- [ ] **Step 4: PR aç**

```bash
git push -u origin chore/base-tema-darkcontrols-silme
gh pr create --title "refactor(theme): Faz 4b PR 2 — Base.xaml + DarkControls silme" --body "## Özet
- \`DarkControls.xaml\` (49 KB, 26 örtük stil) silindi; yerine ince
  \`Themes/Base.xaml\` geldi: yalnız XAML'den erişilemeyen altı kontrol
  (\`Window\`, \`ContextMenu\`, \`MenuItem\`, \`Separator\`, \`ScrollBar\`,
  \`ToolTip\`) örtük kaldı.
- Örtük \`TextBlock\` stili gitti — \`Foreground\` miras alınıyor ve
  \`MainWindow.xaml:10\` onu veriyor. Bu sayede beş şablondaki
  \`ContentPresenter.Resources\` kaçamağı da kaldırıldı.
- 5 yeni keyed stil: \`OD.ListBox\`, \`OD.ListBoxItem\`, \`OD.PasswordBox\`,
  \`OD.CheckBox\`, \`OD.ShortcutCapture\`.
- Çıplak kalan 20 kullanım açıkça bağlandı.
- \`OD.Icon.Gift\` → \`Icons.xaml\`; \`PlatformIcons\`'un 2 token atfı çevrildi.
- **Kalıcı bekçi:** \`UnstyledControlGuardTests\` — izlenen kontrol
  tiplerinden stilsiz kullanım eklenirse test kırmızı verir.

Bununla arayüz yenilemesinin **bütün fazları** bitti.

## Test planı
- [ ] \`dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj\` → 942 yeşil
- [ ] \`dotnet build OrderDeck.App/OrderDeck.App.csproj\` → 0 hata, 0 uyarı
- [ ] LoginGate: e-posta ve şifre kutuları aynı zemin + kırmızı odak
- [ ] RestoreGate / ChatPanel / PrintQueuePanel: seçim ve satır stilleri
- [ ] Bağlam menüsü: beyaz şerit yok, kırpma yok
- [ ] GiveawayDrawer: üç ComboBox + onay kutusu
- [ ] Ayarlar → Kısayollar: mono yakalama düğmeleri
- [ ] Takvim (Dönem Raporu): gün/ay/yıl yazıları okunuyor

Spec: \`docs/superpowers/specs/2026-08-11-arayuz-faz4b-base-tema-design.md\`
Plan: \`docs/superpowers/plans/2026-08-11-arayuz-faz4b-base-tema.md\`"
```

---

## Kapsam dışı

- `AnimationPickerControl.xaml`'daki sabit `Foreground="White"` /
  `#FFB0B0B0` gibi hex değerler — `DarkControls`'un token'ları değiller,
  ayrı bir temizlik işi.
- `OD.Toggle`'ın `OD.CheckBox` ile birleştirilmesi — ikisi farklı anlam
  taşıyor (ayar anahtarı ↔ işlem seçeneği).
- PostgreSQL geçişi ve stok sistemi — bu faz bitince sıraya girecekler.
