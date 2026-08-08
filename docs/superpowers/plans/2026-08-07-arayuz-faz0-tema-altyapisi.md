# Arayüz Yenilemesi — Faz 0: Tema Altyapısı

> **Ajan işçiler için:** GEREKLİ ALT BECERİ: Bu planı görev görev uygulamak için
> superpowers:subagent-driven-development (önerilen) veya
> superpowers:executing-plans kullanın. Adımlar takip için checkbox (`- [ ]`)
> sözdizimi kullanır.

**Amaç:** Tasarım sisteminin token katmanını (renk, ölçü, hareket, font)
`OrderDeck.App/Themes/` altına kurmak — hiçbir view'a dokunmadan, hiçbir görsel
değişiklik üretmeden.

**Mimari:** Üç yeni `ResourceDictionary` (`Colors.xaml`, `Metrics.xaml`,
`Motion.xaml`) `App.xaml`'de merge edilir. Mevcut sözlükler
(`DarkControls.xaml`, `SettingsTheme.xaml`, `GiveawayTheme.xaml`,
`PlatformIcons.xaml`) olduğu gibi kalır; anahtar isimleri çakışmaz
(`OD.Brush.*` / `OD.Font.*` / `OD.Space.*` / `OD.Pad.*` / `OD.Radius.*` /
`OD.Icon.*` / `OD.Layout.*` / `OD.Dur.*` / `OD.Ease.*` önekleri bugün kullanılmıyor).
Eski ve yeni sistem Faz 1-4 boyunca yan yana yaşar.

**Tech Stack:** WPF (`net10.0-windows`), XAML `ResourceDictionary`, xunit +
FluentAssertions. Her sözlük için `pack://` URI ile yüklenip anahtar/değer
sözleşmesini doğrulayan STA test'i yazılır — mevcut
`OrderDeck.Tests/PlatformIconResourcesTests.cs` bu kalıbın örneğidir.

**Spec:** [docs/superpowers/specs/2026-08-07-arayuz-yenileme-design.md](../specs/2026-08-07-arayuz-yenileme-design.md)

**Kapsam dışı (Faz 1'e kalır):** `Icons.xaml` (Lucide ikonları — tüketildikleri
view ile birlikte yazılır), `Controls.xaml` (13 bileşen `Style`'ı), her türlü
view değişikliği.

---

## Dosya yapısı

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.App/Fonts/IBMPlexSans-{Regular,Medium,SemiBold}.ttf` | gövde fontu (yeni) |
| `OrderDeck.App/Fonts/JetBrainsMono-Medium.ttf` | mono 500 ağırlığı (yeni) |
| `OrderDeck.App/Fonts/OFL-{IBMPlexSans,JetBrainsMono,BricolageGrotesque}.txt` | OFL lisans metinleri (yeni) |
| `OrderDeck.App/Themes/Colors.xaml` | 16 `SolidColorBrush` |
| `OrderDeck.App/Themes/Metrics.xaml` | font ailesi + boyut, boşluk, dolgu, yarıçap, ikon ölçüsü, düzen sabitleri |
| `OrderDeck.App/Themes/Motion.xaml` | 3 `Duration` + 2 easing |
| `OrderDeck.App/App.xaml` | üç sözlüğü merge et |
| `OrderDeck.Tests/App/ThemeColorsTests.cs` | renk sözleşmesi |
| `OrderDeck.Tests/App/ThemeMetricsTests.cs` | ölçü sözleşmesi |
| `OrderDeck.Tests/App/ThemeMotionTests.cs` | hareket sözleşmesi |
| `OrderDeck.Tests/App/ThemeMergeTests.cs` | App.xaml merge + çakışma yok |

Testler tek dosyada toplanmıyor: her sözlük kendi test dosyasını alır, böylece
bir sözleşme değiştiğinde hangi katmanın bozulduğu doğrudan görünür.

---

## Görev 1: Fontları ekle

**Dosyalar:**
- Oluştur: `OrderDeck.App/Fonts/IBMPlexSans-Regular.ttf`
- Oluştur: `OrderDeck.App/Fonts/IBMPlexSans-Medium.ttf`
- Oluştur: `OrderDeck.App/Fonts/IBMPlexSans-SemiBold.ttf`
- Oluştur: `OrderDeck.App/Fonts/JetBrainsMono-Medium.ttf`

`OrderDeck.App.csproj` **değişmez** — satır 63'teki `<Resource Include="Fonts\*.ttf" />`
joker kuralı yeni dosyaları kendiliğinden kapsar.

- [ ] **Adım 1: Mevcut durumu doğrula**

```bash
ls -1 OrderDeck.App/Fonts/
```

Beklenen: `BricolageGrotesque-Bold.ttf`, `BricolageGrotesque-Regular.ttf`,
`JetBrainsMono-Bold.ttf`, `JetBrainsMono-Regular.ttf` — yani gövde fontu yok.

- [ ] **Adım 2: Dosyaları edin — KULLANICI ONAYI GEREKTİRİR**

Bu adım dosya indirme içerir; devam etmeden önce kullanıcıdan açık onay alın.
Onay isterken kaynağı ve dosya adlarını söyleyin.

Kaynaklar (ikisi de birinci-el, ikisi de OFL — gömme serbest):

| Font | Kaynak |
|---|---|
| IBM Plex Sans | `github.com/IBM/plex` sürüm paketi `@ibm/plex-sans@1.1.0` → `ibm-plex-sans/fonts/complete/ttf/` |
| JetBrains Mono | `github.com/JetBrains/JetBrainsMono` sürüm paketi `v2.304` → `fonts/ttf/` |

Gereken tam dosyalar: `IBMPlexSans-Regular.ttf` (400), `IBMPlexSans-Medium.ttf`
(500), `IBMPlexSans-SemiBold.ttf` (600), `JetBrainsMono-Medium.ttf` (500).
Bricolage Grotesque yalnız 700 ağırlığında kullanılıyor, `-Bold.ttf` zaten var.

Dosyaları `OrderDeck.App/Fonts/` altına yukarıdaki adlarla koyun. Ad değişirse
Görev 3'teki `FontFamily` pack URI'lerini de değiştirmek gerekir.

**Sürüm karışımı — bilinçli:** repodaki `JetBrainsMono-Regular/Bold.ttf`
v2.211, eklenen Medium v2.304. Mevcut ikisini değiştirmek Faz 0'ın "hiçbir
şey görsel olarak değişmemeli" ölçütünü bozardı; Medium ise bu fazda hiçbir
yerde kullanılmıyor. WPF üçünü tek `JetBrains Mono` ailesinde topluyor
(ölçüldü: 400/500/700). Faz 1'de 500 ağırlığı gerçekten kullanılınca 400/700
ile yan yana bakılıp gerekirse sürüm birleştirilir.

**Lisans dosyaları da eklenir.** OFL, font yazılımının her kopyasının lisans
metniyle birlikte dağıtılmasını şart koşuyor; repoda bugün hiç lisans dosyası
yok (Bricolage Grotesque için de eksik). `Fonts/` altına üç dosya konur:
`OFL-IBMPlexSans.txt`, `OFL-JetBrainsMono.txt`, `OFL-BricolageGrotesque.txt`.
Bunlar `.txt` olduğu için `csproj`'un `Fonts\*.ttf` kuralına takılmaz, derleme
çıktısına girmez — kaynak ağacında dururlar.

- [ ] **Adım 3: Derlemenin fontları kaynak olarak aldığını doğrula**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
```

Beklenen: 0 hata. `TreatWarningsAsErrors` açık olduğu için 0 uyarı da gerekir.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.App/Fonts/
git commit -m "$(cat <<'EOF'
feat(theme): IBM Plex Sans ve JetBrains Mono Medium fontlarını göm

Tasarım sisteminin gövde fontu IBM Plex Sans; bugüne kadar yalnız Bricolage
Grotesque ve JetBrains Mono gömülüydü, gövde metni Segoe UI'a düşüyordu.

OFL lisans metinleri de ekleniyor — lisans, font yazılımının her kopyasının
lisansla birlikte dağıtılmasını şart koşuyor; repoda hiç yoktu (mevcut
Bricolage Grotesque için de eksikti).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 2: Colors.xaml

**Dosyalar:**
- Oluştur: `OrderDeck.App/Themes/Colors.xaml`
- Test: `OrderDeck.Tests/App/ThemeColorsTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/ThemeColorsTests.cs`:

```csharp
using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// Themes/Colors.xaml renk sözleşmesi.
///
/// NEDEN: Palet tek doğruluk kaynağı web/app/globals.css'ten hizalandı.
/// Bir fırçanın değeri sessizce kayarsa masaüstü ile canlı site arasında
/// yeniden ayrışma başlar — bugünkü 120 rastgele rengin oluşma biçimi buydu.
/// Test değerleri çivileyerek o kaymayı derleme zamanına çeker.
/// </summary>
public class ThemeColorsTests
{
    private static readonly (string Key, string Hex)[] Expected =
    [
        ("OD.Brush.Bg",           "#FF090A0E"),
        ("OD.Brush.Surface",      "#FF0F111A"),
        ("OD.Brush.Surface2",     "#FF161A26"),
        ("OD.Brush.Border",       "#12FFFFFF"),
        ("OD.Brush.BorderStrong", "#21FFFFFF"),
        ("OD.Brush.Text",         "#FFF4F2EC"),
        ("OD.Brush.TextDim",      "#FFA6ACBA"),
        ("OD.Brush.TextMute",     "#FF868C9C"),
        ("OD.Brush.Accent",       "#FFFF4A38"),
        ("OD.Brush.AccentHot",    "#FFFF6A5A"),
        ("OD.Brush.AccentDeep",   "#FFE23A2A"),
        ("OD.Brush.AccentInk",    "#FF180603"),
        ("OD.Brush.Amber",        "#FFFFB23E"),
        ("OD.Brush.Success",      "#FF2DD06F"),
        ("OD.Brush.Info",         "#FF4D8DF6"),
        ("OD.Brush.OnAccent",     "#FFFFFFFF"),
    ];

    [Fact]
    public void All_brushes_resolve_with_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, hex) in Expected)
            {
                var brush = Assert.IsType<SolidColorBrush>(dict[key]);
                Assert.Equal(hex, brush.Color.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(brush.IsFrozen, key + " dondurulmalı");
            }
        }, "Colors.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Dictionary_has_no_extra_keys()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            // Palet kapalı bir küme. Yeni renk eklemek spec'i güncellemeyi
            // gerektirir; bu test onu unutturmaz.
            Assert.Equal(Expected.Length, dict.Count);
        }, "Colors.xaml");

        Assert.Null(error);
    }
}
```

Ortak STA yardımcısı — `OrderDeck.Tests/App/ThemeTestHost.cs`:

```csharp
using System.Windows;

namespace OrderDeck.Tests.App;

/// <summary>
/// WPF kaynak sözlüğü STA thread + kayıtlı "pack:" şeması ister. Üç tema
/// testinin ortak koşum düzeneği.
/// </summary>
internal static class ThemeTestHost
{
    /// <returns>Hata varsa metni, yoksa null.</returns>
    internal static string? Run(Action<ResourceDictionary> assert, string fileName)
    {
        string? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = typeof(OrderDeck.App.App);                        // App assembly'sini yükle
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;  // "pack:" şemasını kaydet
                if (Application.Current is null) new Application();

                var dict = new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/OrderDeck.App;component/Themes/" + fileName)
                };

                assert(dict);
            }
            catch (Exception ex) { error = ex.ToString(); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error;
    }
}
```

- [ ] **Adım 2: Testin başarısız olduğunu doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeColorsTests"
```

Beklenen: FAIL — `Colors.xaml` bulunamadığı için `IOException` / `XamlParseException`.

- [ ] **Adım 3: Sözlüğü yaz**

`OrderDeck.App/Themes/Colors.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"
                    mc:Ignorable="po">
  <!--
    OrderDeck paleti — 15 renk + beyaz.

    TEK DOĞRULUK KAYNAĞI: web/app/globals.css (orderdeckapp.com'da canlı).
    Buradaki değerler oradan hizalandı; birini değiştiriyorsanız ikisini
    birden değiştirin, yoksa masaüstü ile site yeniden ayrışır.

    PLATFORM RENGİ YOK. Rozetler Themes/PlatformIcons.xaml içindeki resmi
    marka ikonlarını kullanır — kısaltma + marka rengi kalıbı daha önce
    kullanıldı ve Google itiraz etti. Ayrıntı o dosyanın baş notunda.
  -->

  <!-- Yüzey -->
  <SolidColorBrush x:Key="OD.Brush.Bg"           Color="#090A0E"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.Surface"      Color="#0F111A"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.Surface2"     Color="#161A26"   po:Freeze="True"/>

  <!-- Kenarlık (beyaz üzerinden alfa: .07 ve .13) -->
  <SolidColorBrush x:Key="OD.Brush.Border"       Color="#12FFFFFF" po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.BorderStrong" Color="#21FFFFFF" po:Freeze="True"/>

  <!-- Metin -->
  <SolidColorBrush x:Key="OD.Brush.Text"         Color="#F4F2EC"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.TextDim"      Color="#A6ACBA"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.TextMute"     Color="#868C9C"   po:Freeze="True"/>

  <!-- Anlamsal. Danger ayrı renk değil, Accent'in kendisi: yıkıcı eylem
       birincilden renkle değil KONUMLA ayrılır (dolu buton / hayalet bağlantı). -->
  <SolidColorBrush x:Key="OD.Brush.Accent"       Color="#FF4A38"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.AccentHot"    Color="#FF6A5A"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.AccentDeep"   Color="#E23A2A"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.AccentInk"    Color="#180603"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.Amber"        Color="#FFB23E"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.Success"      Color="#2DD06F"   po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.Info"         Color="#4D8DF6"   po:Freeze="True"/>

  <!-- Accent zemin üstündeki metin -->
  <SolidColorBrush x:Key="OD.Brush.OnAccent"     Color="#FFFFFF"   po:Freeze="True"/>
</ResourceDictionary>
```

- [ ] **Adım 4: Testin geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeColorsTests"
```

Beklenen: PASS, 2 test.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.App/Themes/Colors.xaml \
        OrderDeck.Tests/App/ThemeColorsTests.cs \
        OrderDeck.Tests/App/ThemeTestHost.cs
git commit -m "$(cat <<'EOF'
feat(theme): renk paletini tek sözlüğe topla

15 renk + beyaz, web/app/globals.css'ten hizalandı. Sözleşme testi değerleri
çiviliyor; palet kapalı küme, yeni renk eklemek testi de güncellemeyi
gerektiriyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 3: Metrics.xaml

**Dosyalar:**
- Oluştur: `OrderDeck.App/Themes/Metrics.xaml`
- Test: `OrderDeck.Tests/App/ThemeMetricsTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/ThemeMetricsTests.cs`:

```csharp
using System.Windows;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// Themes/Metrics.xaml ölçü sözleşmesi — tip, boşluk, dolgu, yarıçap, ikon,
/// düzen sabitleri ve font aileleri.
///
/// NEDEN: Uygulamada bugün 18 farklı FontSize var. Ölçeği 6 basamağa
/// indirmenin tek koruması, ölçeğin kendisinin test edilmesi.
/// </summary>
public class ThemeMetricsTests
{
    private static readonly (string Key, double Value)[] Doubles =
    [
        ("OD.Font.F0", 11),  ("OD.Font.F1", 12.5), ("OD.Font.F2", 14),
        ("OD.Font.F3", 20),  ("OD.Font.F4", 32),   ("OD.Font.F5", 64),

        ("OD.Space.1", 2),   ("OD.Space.2", 4),    ("OD.Space.3", 8),
        ("OD.Space.4", 12),  ("OD.Space.5", 16),   ("OD.Space.6", 20),
        ("OD.Space.7", 24),

        ("OD.Icon.Sm", 14),  ("OD.Icon.Md", 16),   ("OD.Icon.Lg", 20),
        ("OD.Icon.Xl", 26),

        ("OD.Layout.SideWidth",       224),
        ("OD.Layout.SideWidthMin",     64),
        ("OD.Layout.RightWidth",      344),
        ("OD.Layout.DrawerWidth",     344),
        ("OD.Layout.TopbarHeight",     56),
        ("OD.Layout.ButtonHeight",     46),
        ("OD.Layout.ContentMaxWidth",1760),
        ("OD.Layout.AppMinWidth",    1280),
        ("OD.Layout.AppMinHeight",    720),
    ];

    private static readonly (string Key, double Uniform)[] Pads =
    [
        ("OD.Pad.1", 2), ("OD.Pad.2", 4),  ("OD.Pad.3", 8),  ("OD.Pad.4", 12),
        ("OD.Pad.5", 16), ("OD.Pad.6", 20), ("OD.Pad.7", 24),
    ];

    private static readonly (string Key, double Radius)[] Radii =
    [
        ("OD.Radius.Xs", 4), ("OD.Radius.Sm", 6), ("OD.Radius.Md", 8),
        ("OD.Radius.Lg", 10), ("OD.Radius.Full", 999),
    ];

    // NOT: aile adları ölçüldü (PowerShell Fonts.GetFontFamilies probe'u,
    // repo'daki gerçek .ttf'ler üzerinde). Bricolage'ın gerçek aile adı
    // "Bricolage Grotesque 14pt"; kısaltılmış "Bricolage Grotesque" kısmi
    // eşleşme olup sentezlenmiş yüzleri de (900) çekiyor. Mevcut
    // SettingsTheme.xaml:11 zaten doğru adı kullanıyor.
    private static readonly (string Key, string Face)[] Fonts =
    [
        ("OD.Font.Sans",    "IBM Plex Sans"),
        ("OD.Font.Mono",    "JetBrains Mono"),
        ("OD.Font.Display", "Bricolage Grotesque 14pt"),
    ];

    [Fact]
    public void Scalar_tokens_have_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, value) in Doubles)
                Assert.Equal(value, Assert.IsType<double>(dict[key]));
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Padding_tokens_mirror_the_spacing_scale()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, uniform) in Pads)
            {
                var t = Assert.IsType<Thickness>(dict[key]);
                Assert.Equal(new Thickness(uniform), t);
            }
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Radius_tokens_have_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, r) in Radii)
                Assert.Equal(new CornerRadius(r), Assert.IsType<CornerRadius>(dict[key]));
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    /// <summary>
    /// Her ailenin BEKLENEN AĞIRLIKLARI sunduğunu doğrular.
    ///
    /// NEDEN sadece "çözülüyor mu" yetmiyor: IBM Plex Sans'ın Medium ve
    /// SemiBold dosyalarında eski aile adı (name ID 1) "IBM Plex Sans Medm" /
    /// "IBM Plex Sans SmBld"; tek aileye ancak tipografik aile adı (ID 16)
    /// üzerinden katılıyorlar. WPF bunu doğru yapıyor (ölçüldü), ama font
    /// dosyalarından biri eksik kalırsa aile yine ÇÖZÜLÜR — yalnız o ağırlık
    /// sessizce en yakınına düşer. Ağırlık listesi bunu yakalar.
    /// </summary>
    [Fact]
    public void Font_families_expose_expected_weights()
    {
        var expectedWeights = new Dictionary<string, int[]>
        {
            ["OD.Font.Sans"]    = [400, 500, 600],
            ["OD.Font.Mono"]    = [400, 500, 700],
            ["OD.Font.Display"] = [400, 700],
        };

        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var (key, face) in Fonts)
            {
                var family = Assert.IsType<FontFamily>(dict[key]);
                // Gömülü font pack URI ile gelir; Source "…/Fonts/#Yüz Adı".
                Assert.Contains("#" + face, family.Source);

                var weights = family.GetTypefaces()
                    .Where(t => t.Style == FontStyles.Normal)
                    .Select(t => t.Weight.ToOpenTypeWeight())
                    .Distinct().Order().ToArray();

                Assert.Equal(expectedWeights[key], weights);
            }
        }, "Metrics.xaml");

        Assert.Null(error);
    }
}
```

- [ ] **Adım 2: Testin başarısız olduğunu doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeMetricsTests"
```

Beklenen: FAIL — `Metrics.xaml` yok.

- [ ] **Adım 3: Sözlüğü yaz**

`OrderDeck.App/Themes/Metrics.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">
  <!--
    OrderDeck ölçü tokenları.

    Bu dosyanın dışında sabit sayı KULLANILMAZ. Uygulamada bugün 18 farklı
    FontSize var; ölçek altı basamağa indi ve orada kalması bu sözlüğün tek
    kaynak olmasına bağlı. Yeni bir değer gerekiyorsa önce buraya eklenir.
  -->

  <!-- Font aileleri. Gömülü .ttf'ler pack URI ile: klasör + "#" + yüz adı.
       Sans 400/500/600, Mono 400/500/700, Display yalnız 700 kullanılıyor. -->
  <FontFamily x:Key="OD.Font.Sans">pack://application:,,,/OrderDeck.App;component/Fonts/#IBM Plex Sans</FontFamily>
  <FontFamily x:Key="OD.Font.Mono">pack://application:,,,/OrderDeck.App;component/Fonts/#JetBrains Mono</FontFamily>
  <!-- Aile adı "Bricolage Grotesque 14pt" — kısaltılmış hâli kısmi eşleşme
       yapıp sentezlenmiş ağırlık ekliyor. SettingsTheme.xaml:11 ile aynı. -->
  <FontFamily x:Key="OD.Font.Display">pack://application:,,,/OrderDeck.App;component/Fonts/#Bricolage Grotesque 14pt</FontFamily>

  <!-- Tip ölçeği: 6 basamak -->
  <sys:Double x:Key="OD.Font.F0">11</sys:Double>    <!-- mikro etiket, rozet, saat -->
  <sys:Double x:Key="OD.Font.F1">12.5</sys:Double>  <!-- ikincil metin, kullanıcı adı -->
  <sys:Double x:Key="OD.Font.F2">14</sys:Double>    <!-- GÖVDE (varsayılan) -->
  <sys:Double x:Key="OD.Font.F3">20</sys:Double>    <!-- panel başlığı, ürün adı -->
  <sys:Double x:Key="OD.Font.F4">32</sys:Double>    <!-- istatistik değeri, fiyat -->
  <sys:Double x:Key="OD.Font.F5">64</sys:Double>    <!-- aktif ürün kodu (tek yer) -->

  <!-- Boşluk ölçeği: 7 basamak -->
  <sys:Double x:Key="OD.Space.1">2</sys:Double>   <!-- kıl payı: liste satırları arası -->
  <sys:Double x:Key="OD.Space.2">4</sys:Double>   <!-- çok dar: rozet içi dikey dolgu -->
  <sys:Double x:Key="OD.Space.3">8</sys:Double>   <!-- dar: satır içi, ikon-metin arası -->
  <sys:Double x:Key="OD.Space.4">12</sys:Double>  <!-- panel içi standart -->
  <sys:Double x:Key="OD.Space.5">16</sys:Double>  <!-- blok arası, başlık dolgusu -->
  <sys:Double x:Key="OD.Space.6">20</sys:Double>  <!-- geniş: kenar çubuğu yan dolgu -->
  <sys:Double x:Key="OD.Space.7">24</sys:Double>  <!-- en geniş: hero yan dolgu -->

  <!-- Aynı ölçeğin Thickness hali. WPF'te CSS 'gap' karşılığı yok; boşluk
       Margin/Padding ile verilir, o yüzden her basamağın iki biçimi var. -->
  <Thickness x:Key="OD.Pad.1">2</Thickness>
  <Thickness x:Key="OD.Pad.2">4</Thickness>
  <Thickness x:Key="OD.Pad.3">8</Thickness>
  <Thickness x:Key="OD.Pad.4">12</Thickness>
  <Thickness x:Key="OD.Pad.5">16</Thickness>
  <Thickness x:Key="OD.Pad.6">20</Thickness>
  <Thickness x:Key="OD.Pad.7">24</Thickness>

  <!-- Köşe yarıçapı: 5 basamak -->
  <CornerRadius x:Key="OD.Radius.Xs">4</CornerRadius>     <!-- çip, kod parçacığı -->
  <CornerRadius x:Key="OD.Radius.Sm">6</CornerRadius>     <!-- input, küçük buton -->
  <CornerRadius x:Key="OD.Radius.Md">8</CornerRadius>     <!-- satır, buton, banner -->
  <CornerRadius x:Key="OD.Radius.Lg">10</CornerRadius>    <!-- panel, kart -->
  <CornerRadius x:Key="OD.Radius.Full">999</CornerRadius> <!-- hap -->

  <!-- İkon ölçüsü: 4 basamak (Path/Viewbox kenar uzunluğu) -->
  <sys:Double x:Key="OD.Icon.Sm">14</sys:Double>
  <sys:Double x:Key="OD.Icon.Md">16</sys:Double>
  <sys:Double x:Key="OD.Icon.Lg">20</sys:Double>
  <sys:Double x:Key="OD.Icon.Xl">26</sys:Double>

  <!-- Düzen sabitleri -->
  <sys:Double x:Key="OD.Layout.SideWidth">224</sys:Double>      <!-- sol kenar çubuğu -->
  <sys:Double x:Key="OD.Layout.SideWidthMin">64</sys:Double>    <!-- ikon modu (<1360px) -->
  <sys:Double x:Key="OD.Layout.RightWidth">344</sys:Double>     <!-- sağ panel -->
  <sys:Double x:Key="OD.Layout.DrawerWidth">344</sys:Double>    <!-- çekmece (sağ paneli örter) -->
  <sys:Double x:Key="OD.Layout.TopbarHeight">56</sys:Double>
  <sys:Double x:Key="OD.Layout.ButtonHeight">46</sys:Double>    <!-- birincil buton -->
  <sys:Double x:Key="OD.Layout.ContentMaxWidth">1760</sys:Double> <!-- fazlası ortalanır -->
  <sys:Double x:Key="OD.Layout.AppMinWidth">1280</sys:Double>
  <sys:Double x:Key="OD.Layout.AppMinHeight">720</sys:Double>
</ResourceDictionary>
```

- [ ] **Adım 4: Testin geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeMetricsTests"
```

Beklenen: PASS, 4 test. `Font_families_resolve_to_embedded_faces` başarısız
olursa Görev 1'deki dosya adları ile buradaki pack URI'ler uyuşmuyordur.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.App/Themes/Metrics.xaml OrderDeck.Tests/App/ThemeMetricsTests.cs
git commit -m "$(cat <<'EOF'
feat(theme): tip, boşluk, yarıçap ve düzen ölçeklerini tokenla

18 farklı FontSize yerine 6 basamaklı tip ölçeği; boşluk 7, yarıçap 5, ikon 4
basamak. Boşluklar hem double hem Thickness olarak veriliyor (WPF'te CSS gap
karşılığı yok).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 4: Motion.xaml

**Dosyalar:**
- Oluştur: `OrderDeck.App/Themes/Motion.xaml`
- Test: `OrderDeck.Tests/App/ThemeMotionTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/ThemeMotionTests.cs`:

```csharp
using System.Windows;
using System.Windows.Media.Animation;

namespace OrderDeck.Tests.App;

/// <summary>
/// Themes/Motion.xaml hareket sözleşmesi.
///
/// NEDEN: Uygulamada bugün SIFIR animasyon var. Hareket eklenirken her
/// ekranın kendi süresini uydurması, 120 rastgele rengin animasyon
/// karşılığını üretir. Üç süre + iki easing; başka değer yok.
/// </summary>
public class ThemeMotionTests
{
    [Fact]
    public void Durations_have_expected_values()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(150),
                Assert.IsType<Duration>(dict["OD.Dur.Fast"]).TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(350),
                Assert.IsType<Duration>(dict["OD.Dur.Base"]).TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(850),
                Assert.IsType<Duration>(dict["OD.Dur.Slow"]).TimeSpan);
        }, "Motion.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Easings_are_ease_out_and_spring()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            var outEase = Assert.IsType<CubicEase>(dict["OD.Ease.Out"]);
            Assert.Equal(EasingMode.EaseOut, outEase.EasingMode);

            var spring = Assert.IsType<BackEase>(dict["OD.Ease.Spring"]);
            Assert.Equal(EasingMode.EaseOut, spring.EasingMode);
            Assert.Equal(0.3, spring.Amplitude);
        }, "Motion.xaml");

        Assert.Null(error);
    }
}
```

- [ ] **Adım 2: Testin başarısız olduğunu doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeMotionTests"
```

Beklenen: FAIL — `Motion.xaml` yok.

- [ ] **Adım 3: Sözlüğü yaz**

`OrderDeck.App/Themes/Motion.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!--
    OrderDeck hareket tokenları. Üç süre, iki easing — başkası yok.

    CSS karşılıkları (referans mockup: docs/design/yayin-ekrani-referans.html):
      cubic-bezier(.2,.8,.3,1)     -> OD.Ease.Out    (CubicEase/EaseOut)
      cubic-bezier(.34,1.56,.64,1) -> OD.Ease.Spring (BackEase/EaseOut, A=0.3)

    prefers-reduced-motion karşılığı: Storyboard'ları çalıştırmadan önce
    SystemParameters.ClientAreaAnimation kontrol edilir; false ise atlanır.
  -->
  <!-- Duration bir struct; XAML metin içeriğini TypeConverter ile çözer.
       Derleme "Duration nesne öğesi oluşturulamıyor" derse alternatif:
       xmlns:sys ile <sys:TimeSpan> tanımlayıp Storyboard'da
       Duration="{StaticResource ...}" yerine BeginTime/Duration'ı koddan
       vermek. Önce bu biçimi deneyin. -->
  <Duration x:Key="OD.Dur.Fast">0:0:0.15</Duration>  <!-- hover, odak geçişi -->
  <Duration x:Key="OD.Dur.Base">0:0:0.35</Duration>  <!-- giriş-çıkış: mesaj, kuyruk, banner, çekmece -->
  <Duration x:Key="OD.Dur.Slow">0:0:0.85</Duration>  <!-- vurgu: yeşil flaş, onay nabzı -->

  <CubicEase x:Key="OD.Ease.Out"    EasingMode="EaseOut"/>
  <BackEase  x:Key="OD.Ease.Spring" EasingMode="EaseOut" Amplitude="0.3"/>
</ResourceDictionary>
```

- [ ] **Adım 4: Testin geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeMotionTests"
```

Beklenen: PASS, 2 test.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.App/Themes/Motion.xaml OrderDeck.Tests/App/ThemeMotionTests.cs
git commit -m "$(cat <<'EOF'
feat(theme): hareket tokenlarını ekle (3 süre + 2 easing)

Uygulamada bugüne kadar hiç animasyon yoktu. Faz 1'de eklenecek hareketin
ölçeğini önden çiviliyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 5: App.xaml merge + regresyon

**Dosyalar:**
- Değiştir: `OrderDeck.App/App.xaml:6-17`
- Test: `OrderDeck.Tests/App/ThemeMergeTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/ThemeMergeTests.cs`:

```csharp
using System.Windows;

namespace OrderDeck.Tests.App;

/// <summary>
/// App.xaml'in yeni tema sözlüklerini merge ettiğini ve eski sözlüklerle
/// anahtar çakışması olmadığını doğrular.
///
/// NEDEN: WPF merge'de aynı anahtar iki kez tanımlanırsa SESSİZCE sonuncusu
/// kazanır — hata vermez. Yeni sistem eskisiyle bir süre yan yana yaşayacağı
/// için, çakışmanın fark edilmeden davranış değiştirmesi gerçek bir risk.
/// </summary>
public class ThemeMergeTests
{
    private static readonly string[] NewDictionaries =
        ["Colors.xaml", "Metrics.xaml", "Motion.xaml"];

    private static readonly string[] ExistingDictionaries =
        ["DarkControls.xaml", "PlatformIcons.xaml", "SettingsTheme.xaml", "GiveawayTheme.xaml"];

    [Fact]
    public void New_dictionaries_do_not_collide_with_existing_ones()
    {
        var error = RunOnSta(() =>
        {
            var newKeys = NewDictionaries.SelectMany(Keys).ToList();
            var oldKeys = ExistingDictionaries.SelectMany(Keys).ToHashSet();

            var collisions = newKeys.Where(oldKeys.Contains).ToList();
            Assert.Empty(collisions);

            // Yeni sözlükler kendi aralarında da çakışmamalı.
            Assert.Equal(newKeys.Count, newKeys.Distinct().Count());
        });

        Assert.Null(error);
    }

    [Fact]
    public void App_resources_expose_the_new_tokens()
    {
        var error = RunOnSta(() =>
        {
            // App.xaml kökü <Application>; iki argümanlı LoadComponent onu
            // var olan örneğe yükler (WPF'in ürettiği InitializeComponent de
            // aynısını yapar). RunOnSta örneği zaten oluşturdu — ikincisini
            // yaratmak "Cannot create more than one Application" atar.
            var app = Application.Current!;
            Application.LoadComponent(
                app, new Uri("/OrderDeck.App;component/App.xaml", UriKind.Relative));

            // Her yeni sözlükten bir temsilci anahtar.
            Assert.NotNull(app.Resources["OD.Brush.Accent"]);
            Assert.NotNull(app.Resources["OD.Font.F2"]);
            Assert.NotNull(app.Resources["OD.Dur.Base"]);

            // Eski sözlükler hâlâ çözülüyor (regresyon).
            Assert.NotNull(app.Resources["OD.Bg.Window"]);
            Assert.NotNull(app.Resources["OD.PlatformIcon.YouTube"]);
        });

        Assert.Null(error);
    }

    private static IEnumerable<string> Keys(string fileName)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/OrderDeck.App;component/Themes/" + fileName)
        };
        return dict.Keys.Cast<object>().Select(k => k.ToString()!).ToList();
    }

    private static string? RunOnSta(Action body)
    {
        string? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = typeof(OrderDeck.App.App);
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                if (Application.Current is null) new Application();
                body();
            }
            catch (Exception ex) { error = ex.ToString(); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error;
    }
}
```

- [ ] **Adım 2: Testin başarısız olduğunu doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeMergeTests"
```

Beklenen: `New_dictionaries_do_not_collide_with_existing_ones` PASS (anahtar
önekleri zaten ayrık), `App_resources_expose_the_new_tokens` FAIL —
`OD.Brush.Accent` çözülemiyor, çünkü App.xaml henüz merge etmiyor.

- [ ] **Adım 3: App.xaml'i güncelle**

`OrderDeck.App/App.xaml` içinde `<ResourceDictionary.MergedDictionaries>`
bloğunu (satır 8-17) şununla değiştirin:

```xml
            <ResourceDictionary.MergedDictionaries>
                <!-- Tasarım sistemi token katmanı. Sabit renk/ölçü/süre YALNIZ
                     bu üç dosyada tanımlanır; view'lar StaticResource ile bağlar.
                     Spec: docs/superpowers/specs/2026-08-07-arayuz-yenileme-design.md -->
                <ResourceDictionary Source="Themes/Colors.xaml"/>
                <ResourceDictionary Source="Themes/Metrics.xaml"/>
                <ResourceDictionary Source="Themes/Motion.xaml"/>

                <!-- Implicit dark-theme styles for unstyled TextBox / ComboBox /
                     Label / GroupBox / DataGrid headers / etc. Closes the
                     "white text on white background" UI bug across dialogs.
                     Token katmanına devrediliyor: view'lar Faz 1-3'te
                     dönüştükçe küçülecek, Faz 4'te silinecek. -->
                <ResourceDictionary Source="Themes/DarkControls.xaml"/>
                <!-- Sohbet listesindeki platform rozetleri: her platformun
                     resmi marka ikonu. Marka rehberleri kısaltma + marka
                     rengi taklidine izin vermiyor (bkz. dosya başı notu). -->
                <ResourceDictionary Source="Themes/PlatformIcons.xaml"/>
            </ResourceDictionary.MergedDictionaries>
```

- [ ] **Adım 4: Testin geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ThemeMergeTests"
```

Beklenen: PASS, 2 test.

- [ ] **Adım 5: Tüm süiti ve derlemeyi doğrula**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
```

Beklenen: 0 hata, 0 uyarı (`TreatWarningsAsErrors` açık); mevcut testlerin
hepsi + 10 yeni test geçer. Hiçbir mevcut test kırılmamalı — bu fazda hiçbir
view'a dokunulmadı.

- [ ] **Adım 6: Görsel regresyon olmadığını gözle doğrula**

Uygulamayı çalıştırın, ana pencereyi ve `SettingsDialog`'u açın.

Beklenen: **hiçbir şey değişmemiş** görünmeli. Yeni sözlükler yalnız kaynak
tanımlıyor, hiçbir `Style` uygulamıyor. Bir şey değiştiyse anahtar çakışması
vardır — Adım 4'teki testi gözden geçirin.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/App.xaml OrderDeck.Tests/App/ThemeMergeTests.cs
git commit -m "$(cat <<'EOF'
feat(theme): token sözlüklerini App.xaml'e bağla

Üç yeni sözlük mevcut olanların önüne merge ediliyor. Çakışma testi, WPF'in
aynı anahtarı sessizce ezmesi riskini derleme zamanına çekiyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Faz 0 tamamlanma ölçütü

- `OrderDeck.App/Themes/` altında `Colors.xaml`, `Metrics.xaml`, `Motion.xaml`.
- `Fonts/` altında IBM Plex Sans üç ağırlık + JetBrains Mono Medium.
- 10 yeni test geçiyor, mevcut süit kırılmamış.
- Uygulama görsel olarak değişmemiş.
- Hiçbir view dosyası değişmemiş: `git diff --name-only master -- OrderDeck.App/Views MainWindow.xaml` boş.

Sonraki adım Faz 1 (`MainShellView`) — kendi planını alır.
