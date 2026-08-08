# Arayüz Yenilemesi Faz 1 — `MainShellView` Implementasyon Planı

> **Ajan çalışanlar için:** GEREKLİ ALT BECERİ: Bu planı görev görev uygulamak
> için `superpowers:subagent-driven-development` (önerilen) veya
> `superpowers:executing-plans` kullan. Adımlar onay kutusu (`- [ ]`) sözdizimi
> ile izlenir.

**Hedef:** Yayın sırasında bakılan tek ekranı (`MainShellView`) referans
mockup'a göre yeniden yaz; 664 satırlık tek XAML dosyasını odaklı
`UserControl`'lere böl; ürün kartını gerçek veriyle besle.

**Mimari:** `MainShellView.xaml` ince bir kompozisyon köküne iner; her bölge
kendi `UserControl`'ünde yaşar ve `DataContext`'i miras alır. Yeni yerel
SQLite tabloları (`Product`, `ProductSize`) ürün kartını besler. Mevcut
`MainShellViewModel` yüzeyinin **hiçbiri kaldırılmaz** — yalnız eklenir.

**Teknoloji:** WPF (`net10.0-windows`), CommunityToolkit.Mvvm, Dapper +
SQLite, xUnit + FluentAssertions.

---

## Bağlam — bunu okumadan başlama

**Spec:** `docs/superpowers/specs/2026-08-07-arayuz-yenileme-design.md`
**Referans mockup:** `docs/design/yayin-ekrani-referans.html` (tarayıcıda aç)
**Faz 0 (bitti, PR #238):** `Themes/Colors.xaml`, `Themes/Metrics.xaml`,
`Themes/Motion.xaml` token sözlükleri + 4 gömülü font. Bu planda sabit renk,
sabit punto, sabit süre **yazılmaz** — hepsi `StaticResource` ile bağlanır.

### Mevcut token anahtarları (Faz 0'da doğrulandı)

```
Colors.xaml   OD.Brush.Bg  Surface  Surface2  Border  BorderStrong
              Text  TextDim  TextMute  Accent  AccentHot  AccentDeep
              AccentInk  Amber  Success  Info  OnAccent
Metrics.xaml  OD.Font.Sans|Mono|Display   OD.Font.F0..F5
              OD.Space.1..7 (double)      OD.Pad.1..7 (Thickness)
              OD.Radius.Xs|Sm|Md|Lg|Full  OD.Icon.Sm|Md|Lg|Xl
              OD.Layout.SideWidth SideWidthMin RightWidth DrawerWidth
                        TopbarHeight ButtonHeight ContentMaxWidth
                        AppMinWidth AppMinHeight
Motion.xaml   OD.Dur.Fast|Base|Slow   OD.Ease.Out  OD.Ease.Spring
PlatformIcons.xaml (Faz 0 öncesi)  OD.PlatformIcon.{YouTube,Instagram,TikTok,
              Facebook}   OD.PlatformChip.{...}  OD.PlatformChip.Unknown
```

### Üç zorunlu kural

1. **Mockup'ın platform rozetini KOPYALAMA.** Mockup'ta `.pb-yt` / `.pb-ig` /
   `.pb-tt` / `.pb-fb` var — kısaltma + marka rengi. Bu desen Google itirazı
   almıştı (`Themes/PlatformIcons.xaml` dosya başı notu). Sohbet rozetleri
   `OD.PlatformChip.*` **resmi ikonlarıyla** çizilecek. Marka rengi tokenı yok
   ve eklenmeyecek.
2. **Davranış değişmez.** `MainShellViewModel`'in mevcut 47+ özelliği ve 40+
   komutu ile `MainShellView.xaml.cs`'teki her olay işleyicisi çalışmaya devam
   etmeli. Bu faz sunum katmanı; tek istisna spec §9.1 (ürün kartı verisi).
3. **Hiçbir şey pop-up olmayacak** (spec §6). Ürün kartındaki düzenleme
   satır-içi; yeni `Window` açılmaz.

### Faz 1'in veri istisnası (spec §9.1)

Ürün kartı gerçek veriyle çalışır ama **yalnız WPF-yerel SQLite**: sunucu
tablosu yok, senkron yok, R2 yok. **Beden gridi yalnız gösterir** — etiket
kuyruğa girince stok düşmez, `Label`'a beden alanı eklenmez, sohbet
mesajından beden ayıklanmaz. Adetleri operatör kartta elle düzenler.

---

## Dosya yapısı

**Yeni tema dosyası**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.App/Themes/Controls.xaml` | Faz 1'in tükettiği bileşen `Style`'ları (panel yüzeyi, birincil/ikincil buton, çip, arama kutusu, mikro etiket, hap sayaç). Faz 2-4 kendi stillerini ekleyecek. |

**Yeni veri katmanı**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.Core/Storage/Migrations/024_product_stock.sql` | `Product` + `ProductSize` tabloları |
| `OrderDeck.Core/Catalog/Product.cs` | `Product` ve `ProductSize` kayıt tipleri |
| `OrderDeck.Core/Storage/Repositories/ProductRepository.cs` | Ürün + beden okuma/yazma |

**Yeni ViewModel'ler**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.App/ViewModels/ProductCardViewModel.cs` | Aktif kodun ürün kaydı, satır-içi düzenleme, fotoğraf kopyalama |
| `OrderDeck.App/ViewModels/ProductSizeViewModel.cs` | Tek beden kutusu (ad, adet, düşük/tükendi durumu) |
| `OrderDeck.App/ViewModels/PlatformConnectionViewModel.cs` | Sol alt "BAĞLANTILAR" satırı |

**View bölünmesi** — `MainShellView.xaml` 664 satırdan ~90 satırlık kompozisyon
köküne iner:

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.App/Views/Shell/ShellSidebar.xaml` | Logo, nav, bağlantılar, yazıcı satırı |
| `OrderDeck.App/Views/Shell/ShellTopBar.xaml` | CANLI rozeti, yayın süresi, izleyici çipleri, sağ eylemler |
| `OrderDeck.App/Views/Shell/ShellBanners.xaml` | 4 bildirim şeridi |
| `OrderDeck.App/Views/Shell/ActiveProductBar.xaml` | Kod + fiyat girişi, ürün adı, 4 istatistik |
| `OrderDeck.App/Views/Shell/ChatPanel.xaml` | Arama, "Sadece {kod}" çipi, sohbet listesi, yeni-mesaj hapı |
| `OrderDeck.App/Views/Shell/ProductCard.xaml` | Fotoğraf, kod/fiyat/ad, beden gridi, satır-içi düzenleme |
| `OrderDeck.App/Views/Shell/PrintQueuePanel.xaml` | Kuyruk başlığı, satırlar, yazdır yuvası |
| `OrderDeck.App/Views/MainShellView.xaml` | Yalnız yerleşim: kenar + ana ızgara |

Her `UserControl` `DataContext`'i ebeveynden miras alır — kendi `DataContext`
atamaz. Kod-arkası olay işleyicileri ait oldukları parçaya taşınır
(`ChatList_OnDoubleClick` → `ChatPanel.xaml.cs`, `QueueList_OnSelectionChanged`
→ `PrintQueuePanel.xaml.cs`).

---

## Görev 1: `Controls.xaml` + Faz 1 ölçü tokenları

**Dosyalar:**
- Oluştur: `OrderDeck.App/Themes/Controls.xaml`
- Değiştir: `OrderDeck.App/Themes/Metrics.xaml` (Faz 1 ölçüleri eklenir)
- Değiştir: `OrderDeck.App/App.xaml` (yeni sözlük merge edilir)
- Test: `OrderDeck.Tests/App/ControlsThemeTests.cs`

Mockup'ta Faz 0'da karşılığı olmayan altı sabit ölçü var. Sabit sayı olarak
XAML'e gömmek yasak (spec §2) → önce token'a çevrilir.

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/ControlsThemeTests.cs`:

```csharp
using System.Windows;

namespace OrderDeck.Tests.App;

/// <summary>
/// Controls.xaml Faz 1'in tükettiği bileşen stillerini tanımlar; Metrics.xaml
/// da Faz 1'e özgü ölçüleri. İkisi de App.xaml'den merge ediliyor.
///
/// NEDEN: Bir Style anahtarı yeniden adlandırılırsa XAML'deki StaticResource
/// derlemede değil, pencere AÇILIRKEN patlar. Bu test o riski test koşumuna
/// çeker. Ayrıca Faz 4'ün "sabit hex 0" ölçümünün ön şartı: her görsel değer
/// bir token'dan gelmeli.
/// </summary>
public class ControlsThemeTests
{
    private static readonly string[] StyleKeys =
    [
        "OD.Panel",           // kart/panel yüzeyi
        "OD.Button.Primary",  // yazdır
        "OD.Button.Ghost",    // nav, kuyruk temizle
        "OD.Chip",            // "Sadece A12" (basılı hâli kendi IsChecked
                              //  tetikleyicisinde — ayrı anahtar yok)
        "OD.TextBox",         // kod, fiyat, sohbet arama, ürün adı, adet
        "OD.Text.Micro",      // "AKTİF ÜRÜN", "BEDEN STOĞU"
        "OD.Text.Mono",       // sayısal değerler (tabular)
        "OD.CountPill"        // kuyruk sayacı
    ];

    private static readonly string[] Faz1Metrics =
    [
        "OD.Layout.ProductImageHeight",     // 142 / kısa modda 56
        "OD.Layout.ProductImageHeightShort",
        "OD.Layout.QueueMinHeight",         // 64
        "OD.Layout.ChatTimeColumn",         // 40
        "OD.Layout.ChatBadgeColumn",        // 28
        "OD.Layout.ChatUserMaxWidth",       // 132
        "OD.Layout.QueueNoColumn",          // 44
        "OD.Layout.CodeFontShort"           // 44 (F5=64'ün kısa-mod karşılığı)
    ];

    [Fact]
    public void Controls_dictionary_defines_every_faz1_style()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var key in StyleKeys)
                Assert.IsType<Style>(dict[key]);
        }, "Controls.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void Metrics_defines_faz1_layout_sizes()
    {
        var error = ThemeTestHost.Run(dict =>
        {
            foreach (var key in Faz1Metrics)
                Assert.True(Assert.IsType<double>(dict[key]) > 0, key);
        }, "Metrics.xaml");

        Assert.Null(error);
    }

    [Fact]
    public void App_merges_controls_dictionary()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var app = Application.Current!;
            Application.LoadComponent(
                app, new Uri("/OrderDeck.App;component/App.xaml", UriKind.Relative));

            Assert.IsType<Style>(app.Resources["OD.Panel"]);
        });

        Assert.Null(error);
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ControlsThemeTests
```
Beklenen: 3 test de FAIL — `Controls.xaml` yok, anahtarlar tanımsız.

- [ ] **Adım 3: Metrics.xaml'e Faz 1 ölçülerini ekle**

`OrderDeck.App/Themes/Metrics.xaml` içinde, mevcut `OD.Layout.*` bloğunun
sonuna:

```xml
    <!-- Faz 1'e özgü ölçüler. Mockup'ta CSS değişkeni olarak duruyorlar
         (--h-pimg, --h-q-min, --w-col-*, --w-user); WPF'te sabit sayı
         yazmamak için token'a çevrildiler. -->
    <sys:Double x:Key="OD.Layout.ProductImageHeight">142</sys:Double>
    <!-- Alçak pencerede (<850px) ürün görseli kısalır: mockup @media
         (max-height:849px) --h-pimg:56px. -->
    <sys:Double x:Key="OD.Layout.ProductImageHeightShort">56</sys:Double>
    <sys:Double x:Key="OD.Layout.QueueMinHeight">64</sys:Double>
    <sys:Double x:Key="OD.Layout.ChatTimeColumn">40</sys:Double>
    <sys:Double x:Key="OD.Layout.ChatBadgeColumn">28</sys:Double>
    <sys:Double x:Key="OD.Layout.ChatUserMaxWidth">132</sys:Double>
    <sys:Double x:Key="OD.Layout.QueueNoColumn">44</sys:Double>
    <!-- Spec §7: alçak pencerede aktif ürün kodu F5 (64) yerine 44px.
         Font ölçeğine (F0..F5) yeni basamak eklemiyoruz — bu, tek bir
         kontrolün duyarlı ölçüsü, tipografi basamağı değil. -->
    <sys:Double x:Key="OD.Layout.CodeFontShort">44</sys:Double>
```

> `sys` öneki dosyanın kökünde zaten tanımlı
> (`xmlns:sys="clr-namespace:System;assembly=System.Runtime"`). Değilse ekle.

- [ ] **Adım 4: Controls.xaml'i yaz**

`OrderDeck.App/Themes/Controls.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!--
      Faz 1'in tükettiği bileşen stilleri. Spec §9: Style'lar önden değil,
      TÜKETİLDİKLERİ fazda yazılır — kullanılmayan stil doğrulanmamış tasarım
      borcudur. Faz 2-4 kendi ihtiyaçlarını buraya ekleyecek.

      Buradaki hiçbir değer sabit değil: renk Colors.xaml'den, ölçü
      Metrics.xaml'den, süre Motion.xaml'den geliyor. App.xaml bu üçünü
      Controls.xaml'den ÖNCE merge ediyor, StaticResource çözülüyor.
    -->

    <!-- Panel/kart yüzeyi: sohbet paneli, ürün kartı, kuyruk, hero. -->
    <Style x:Key="OD.Panel" TargetType="Border">
        <Setter Property="Background"   Value="{StaticResource OD.Brush.Surface}"/>
        <Setter Property="BorderBrush"  Value="{StaticResource OD.Brush.Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="{StaticResource OD.Radius.Lg}"/>
    </Style>

    <!-- Birincil eylem: Yazdır. Mockup .btn-print -->
    <Style x:Key="OD.Button.Primary" TargetType="Button">
        <Setter Property="Height"     Value="{StaticResource OD.Layout.ButtonHeight}"/>
        <Setter Property="Background" Value="{StaticResource OD.Brush.Accent}"/>
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.OnAccent}"/>
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"   Value="{StaticResource OD.Font.F2}"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Cursor"     Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd"
                            Background="{TemplateBinding Background}"
                            CornerRadius="{StaticResource OD.Radius.Md}"
                            RenderTransformOrigin="0.5,0.5">
                        <Border.RenderTransform>
                            <TranslateTransform x:Name="Lift"/>
                        </Border.RenderTransform>
                        <ContentPresenter HorizontalAlignment="Center"
                                          VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource OD.Brush.AccentHot}"/>
                            <Setter TargetName="Lift" Property="Y" Value="-1"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Bd" Property="Opacity" Value="0.45"/>
                            <Setter Property="Cursor" Value="Arrow"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Arka planı olmayan buton: nav öğeleri, "Temizle", ikon butonları. -->
    <Style x:Key="OD.Button.Ghost" TargetType="Button">
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.TextDim}"/>
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"   Value="{StaticResource OD.Font.F2}"/>
        <Setter Property="Padding"    Value="{StaticResource OD.Pad.4}"/>
        <Setter Property="Cursor"     Value="Hand"/>
        <Setter Property="HorizontalContentAlignment" Value="Left"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="Transparent"
                            CornerRadius="{StaticResource OD.Radius.Md}"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter
                            HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                            VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background"
                                    Value="{StaticResource OD.Brush.Surface2}"/>
                            <Setter Property="Foreground"
                                    Value="{StaticResource OD.Brush.Text}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Bd" Property="Opacity" Value="0.45"/>
                            <Setter Property="Cursor" Value="Arrow"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Çip: sohbet başlığındaki "Sadece A12". Mockup .chip -->
    <Style x:Key="OD.Chip" TargetType="ToggleButton">
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.TextDim}"/>
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"   Value="{StaticResource OD.Font.F1}"/>
        <Setter Property="FontWeight" Value="Medium"/>
        <Setter Property="Padding"    Value="{StaticResource OD.Pad.4}"/>
        <Setter Property="Cursor"     Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ToggleButton">
                    <Border x:Name="Bd"
                            Background="{StaticResource OD.Brush.Surface2}"
                            BorderBrush="{StaticResource OD.Brush.Border}"
                            BorderThickness="1"
                            CornerRadius="{StaticResource OD.Radius.Sm}"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground"
                                    Value="{StaticResource OD.Brush.Text}"/>
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush"
                                    Value="{StaticResource OD.Brush.Accent}"/>
                            <Setter Property="Foreground"
                                    Value="{StaticResource OD.Brush.AccentHot}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Metin girişi. Mockup .search / .input.
         TargetType Border DEĞİL TextBox: Faz 1'de bu stil altı yerde
         doğrudan bir TextBox'a uygulanıyor (kod, fiyat, sohbet arama, ürün
         adı, beden dizesi, adet). TextBox'ta CornerRadius özelliği yok, o
         yüzden yuvarlatma ControlTemplate içindeki Border'dan geliyor —
         WPF'te "yuvarlak input" için tek doğru yol bu. -->
    <Style x:Key="OD.TextBox" TargetType="TextBox">
        <Setter Property="Background"      Value="{StaticResource OD.Brush.Surface2}"/>
        <Setter Property="BorderBrush"     Value="{StaticResource OD.Brush.Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding"         Value="{StaticResource OD.Pad.4}"/>
        <Setter Property="Foreground"      Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="CaretBrush"      Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="FontFamily"      Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"        Value="{StaticResource OD.Font.F2}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Border x:Name="Root"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{StaticResource OD.Radius.Sm}">
                        <!-- PART_ContentHost adı zorunlu: TextBox metni
                             buraya yerleştirir, adı değişirse kutu boş görünür. -->
                        <ScrollViewer x:Name="PART_ContentHost"
                                      Margin="{TemplateBinding Padding}"
                                      VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Setter TargetName="Root" Property="BorderBrush"
                                    Value="{StaticResource OD.Brush.Accent}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Bölüm üstü küçük büyük-harf etiket. Mockup .micro -->
    <Style x:Key="OD.Text.Micro" TargetType="TextBlock">
        <Setter Property="FontFamily"     Value="{StaticResource OD.Font.Sans}"/>
        <Setter Property="FontSize"       Value="{StaticResource OD.Font.F0}"/>
        <Setter Property="FontWeight"     Value="SemiBold"/>
        <Setter Property="Foreground"     Value="{StaticResource OD.Brush.TextMute}"/>
        <Setter Property="TextOptions.TextFormattingMode" Value="Ideal"/>
    </Style>

    <!-- Sayısal değer: sabit genişlikli rakam, sayaçlar zıplamasın. -->
    <Style x:Key="OD.Text.Mono" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource OD.Font.Mono}"/>
        <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        <Setter Property="TextOptions.TextFormattingMode" Value="Ideal"/>
    </Style>

    <!-- Hap sayaç: kuyruk başlığındaki adet. Mockup .q-count -->
    <Style x:Key="OD.CountPill" TargetType="Border">
        <Setter Property="Background"   Value="{StaticResource OD.Brush.Surface2}"/>
        <Setter Property="BorderBrush"  Value="{StaticResource OD.Brush.Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="{StaticResource OD.Radius.Full}"/>
        <Setter Property="Padding"      Value="{StaticResource OD.Pad.3}"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Adım 5: App.xaml'e merge et**

`OrderDeck.App/App.xaml` içinde `Motion.xaml` satırından hemen sonra:

```xml
                <ResourceDictionary Source="Themes/Motion.xaml"/>
                <!-- Bileşen stilleri: token'lara BAĞIMLI, bu yüzden üç token
                     sözlüğünden SONRA merge edilmeli. -->
                <ResourceDictionary Source="Themes/Controls.xaml"/>
```

- [ ] **Adım 6: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ControlsThemeTests
```
Beklenen: 3/3 PASS.

Ayrıca çakışma testi hâlâ geçmeli — `Controls.xaml` yeni bir sözlük, eskilerle
anahtar paylaşmamalı:

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ThemeMergeTests
```
Beklenen: 2/2 PASS.

- [ ] **Adım 7: `ThemeMergeTests`'e Controls.xaml'i ekle**

`OrderDeck.Tests/App/ThemeMergeTests.cs` içinde:

```csharp
    private static readonly string[] NewDictionaries =
        ["Colors.xaml", "Metrics.xaml", "Motion.xaml", "Controls.xaml"];
```

Tekrar koş, 2/2 PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.App/Themes/Controls.xaml OrderDeck.App/Themes/Metrics.xaml \
        OrderDeck.App/App.xaml OrderDeck.Tests/App/ControlsThemeTests.cs \
        OrderDeck.Tests/App/ThemeMergeTests.cs
git commit -m "feat(theme): Faz 1 bileşen stilleri ve ölçü tokenları"
```

---

## Görev 2: `Product` + `ProductSize` yerel şeması

**Dosyalar:**
- Oluştur: `OrderDeck.Core/Storage/Migrations/024_product_stock.sql`
- Oluştur: `OrderDeck.Core/Catalog/Product.cs`
- Oluştur: `OrderDeck.Core/Storage/Repositories/ProductRepository.cs`
- Test: `OrderDeck.Tests/Storage/ProductRepositoryTests.cs`

`.sql` dosyası `OrderDeck.Core.csproj:11`'deki
`<EmbeddedResource Include="Storage\Migrations\*.sql" />` glob'u ile
kendiliğinden gömülür — ayrı kayıt gerekmez. `MigrationRunner` dosya adının
ilk 3 karakterinden sürümü okur ve sırayla koşar; **her betik
`UPDATE _meta SET SchemaVersion = N WHERE Id = 1` ile bitmeli.**

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/Storage/ProductRepositoryTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Ürün kartının yerel deposu (spec §9.1). Sunucuya hiç dokunmaz — Postgres
/// göçünden etkilenmemesi bilinçli bir sınır.
/// </summary>
public class ProductRepositoryTests
{
    private static (InMemorySqlite Db, ProductRepository Repo) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return (db, new ProductRepository(db));
    }

    [Fact]
    public void Get_returns_null_when_code_unknown()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Get("A12").Should().BeNull();
    }

    [Fact]
    public void Save_then_Get_round_trips_product_and_sizes()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Save(new Product("A12", "Krem Triko Kazak", "photos/a12.jpg", 1000),
                  [new ProductSize("A12", "S", 6, 0),
                   new ProductSize("A12", "M", 9, 1)]);

        var p = repo.Get("A12");
        p.Should().NotBeNull();
        p!.Name.Should().Be("Krem Triko Kazak");
        p.PhotoPath.Should().Be("photos/a12.jpg");

        var sizes = repo.GetSizes("A12");
        sizes.Should().HaveCount(2);
        sizes[0].Size.Should().Be("S");
        sizes[0].Quantity.Should().Be(6);
        sizes[1].Size.Should().Be("M");
    }

    [Fact]
    public void Save_replaces_previous_size_set_entirely()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Save(new Product("A12", "Kazak", null, 1000),
                  [new ProductSize("A12", "S", 6, 0),
                   new ProductSize("A12", "M", 9, 1)]);

        // Beden seti daralıyor: M gitmeli, kalıntı bırakmamalı.
        repo.Save(new Product("A12", "Kazak", null, 2000),
                  [new ProductSize("A12", "S", 4, 0)]);

        var sizes = repo.GetSizes("A12");
        sizes.Should().ContainSingle();
        sizes[0].Quantity.Should().Be(4);
        repo.Get("A12")!.UpdatedAt.Should().Be(2000);
    }

    [Fact]
    public void GetSizes_orders_by_sort_order_not_alphabetically()
    {
        var (db, repo) = Fx();
        using var _ = db;

        // Alfabetik sıra L, M, S, XL olurdu — beden sırası bu değil.
        repo.Save(new Product("A12", "Kazak", null, 1000),
                  [new ProductSize("A12", "S", 1, 0),
                   new ProductSize("A12", "M", 2, 1),
                   new ProductSize("A12", "L", 3, 2),
                   new ProductSize("A12", "XL", 4, 3)]);

        repo.GetSizes("A12").Select(s => s.Size)
            .Should().Equal("S", "M", "L", "XL");
    }

    [Fact]
    public void Codes_are_matched_case_insensitively()
    {
        var (db, repo) = Fx();
        using var _ = db;

        // Hero kod girişi büyük harfe zorluyor ama eski kayıtlar karışık
        // olabilir; kod aramasının harf duyarlı olmaması gerekiyor.
        repo.Save(new Product("A12", "Kazak", null, 1000), []);

        repo.Get("a12").Should().NotBeNull();
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductRepositoryTests
```
Beklenen: derleme hatası — `OrderDeck.Core.Catalog` ad alanı yok.

- [ ] **Adım 3: Migration'ı yaz**

`OrderDeck.Core/Storage/Migrations/024_product_stock.sql`:

```sql
-- Arayüz Faz 1 (spec §9.1): sağ paneldeki ürün kartı ad, fotoğraf ve beden
-- başına adet gösteriyor; uygulamada bunların karşılığı yoktu.
--
-- Bilinçli sınırlar:
--  * Bu tablolar YALNIZ yerel SQLite'ta. Sunucuda karşılığı yok, senkron yok.
--    Sebep: PostgreSQL göçü arayüz yenilemesi bitmeden başlamayacak; yerel
--    SQLite göçten etkilenmiyor, dolayısıyla bu iş iki kez yapılmayacak.
--  * Fiyat kolonu YOK. Karttaki fiyat, hero'daki aktif fiyat girişinin
--    aynısı; ürüne ayrı fiyat alanı eklemek yeni bir kavram olurdu.
--  * Quantity düz bir sayı; hareket defteri DEĞİL. Etiket kuyruğa girince
--    stok düşmüyor — operatör adetleri elle giriyor. Otomatik düşüş ve
--    hareket tabanlı defter stok projesine ait (bkz. stok spec'i).
--  * PhotoPath, %LOCALAPPDATA%\OrderDeck\products\ altındaki dosyaya GÖRECELİ
--    yol (uygulamanın yerleşik veri klasörü kuralı; bkz.
--    AnimationHoverPreviewService.cs:142). Mutlak yol yazılmıyor;
--    mutlak yol yazılmıyor ki kullanıcı profili taşınınca kırılmasın.
CREATE TABLE Product (
    Code      TEXT PRIMARY KEY COLLATE NOCASE,
    Name      TEXT NOT NULL,
    PhotoPath TEXT,
    UpdatedAt INTEGER NOT NULL
);

CREATE TABLE ProductSize (
    Code      TEXT NOT NULL COLLATE NOCASE,
    Size      TEXT NOT NULL COLLATE NOCASE,
    Quantity  INTEGER NOT NULL,
    SortOrder INTEGER NOT NULL,
    PRIMARY KEY (Code, Size),
    FOREIGN KEY (Code) REFERENCES Product(Code) ON DELETE CASCADE
);

UPDATE _meta SET SchemaVersion = 24 WHERE Id = 1;
```

- [ ] **Adım 4: Kayıt tiplerini yaz**

`OrderDeck.Core/Catalog/Product.cs`:

```csharp
namespace OrderDeck.Core.Catalog;

/// <summary>
/// Yayın ekranındaki ürün kartının yerel kaydı. Kod (A12) birincil anahtar —
/// operatör zaten kodla çalışıyor, ayrı bir kimlik üretmek yapay olurdu.
/// </summary>
/// <param name="Code">Ürün kodu, harf duyarsız (SQLite COLLATE NOCASE).</param>
/// <param name="Name">Kartta ve hero'da görünen ad.</param>
/// <param name="PhotoPath">
/// %LOCALAPPDATA%\OrderDeck\products\ altına göreceli dosya yolu; fotoğraf yoksa
/// null. Mutlak yol saklanmıyor — kullanıcı profili taşınınca kırılmasın.
/// </param>
/// <param name="UpdatedAt">Unix saniye; son düzenleme.</param>
public sealed record Product(
    string Code,
    string Name,
    string? PhotoPath,
    long UpdatedAt);

/// <summary>
/// Bir ürünün tek bedeni ve elde kalan adedi.
/// </summary>
/// <param name="SortOrder">
/// Görüntüleme sırası. Beden alfabetik sıralanamaz (L &lt; M &lt; S &lt; XL
/// yanlış olur), bu yüzden sıra açıkça saklanıyor.
/// </param>
public sealed record ProductSize(
    string Code,
    string Size,
    int Quantity,
    int SortOrder);
```

- [ ] **Adım 5: Repository'yi yaz**

`OrderDeck.Core/Storage/Repositories/ProductRepository.cs`:

```csharp
using Dapper;
using OrderDeck.Core.Catalog;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Ürün kartının yerel deposu (arayüz Faz 1, spec §9.1). Yalnız SQLite —
/// sunucuya hiç yazmıyor.
/// </summary>
public sealed class ProductRepository
{
    private readonly IDbConnectionFactory _factory;

    public ProductRepository(IDbConnectionFactory factory) => _factory = factory;

    public Product? Get(string code)
    {
        using var conn = _factory.Open();
        return conn.QuerySingleOrDefault<Product>(
            "SELECT Code, Name, PhotoPath, UpdatedAt FROM Product WHERE Code = @code",
            new { code });
    }

    public IReadOnlyList<ProductSize> GetSizes(string code)
    {
        using var conn = _factory.Open();
        return conn.Query<ProductSize>(
            """
            SELECT Code, Size, Quantity, SortOrder
            FROM ProductSize
            WHERE Code = @code
            ORDER BY SortOrder
            """,
            new { code }).ToList();
    }

    /// <summary>
    /// Ürünü ve beden setini birlikte yazar. Beden seti TAMAMEN değiştirilir:
    /// operatör bir bedeni kaldırdığında satır kalıntı bırakmasın diye önce
    /// silinir. Tek transaction — yarım yazılmış bir kart bırakmaz.
    /// </summary>
    public void Save(Product product, IReadOnlyList<ProductSize> sizes)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute(
            """
            INSERT INTO Product (Code, Name, PhotoPath, UpdatedAt)
            VALUES (@Code, @Name, @PhotoPath, @UpdatedAt)
            ON CONFLICT(Code) DO UPDATE SET
                Name      = excluded.Name,
                PhotoPath = excluded.PhotoPath,
                UpdatedAt = excluded.UpdatedAt
            """,
            product, tx);

        conn.Execute("DELETE FROM ProductSize WHERE Code = @Code",
                     new { product.Code }, tx);

        if (sizes.Count > 0)
        {
            conn.Execute(
                """
                INSERT INTO ProductSize (Code, Size, Quantity, SortOrder)
                VALUES (@Code, @Size, @Quantity, @SortOrder)
                """,
                sizes, tx);
        }

        tx.Commit();
    }
}
```

- [ ] **Adım 6: `AppHost`'a kaydet**

`OrderDeck.App/AppHost.cs` içinde, diğer repository kayıtlarının yanına
(`services.AddSingleton<GiveawayRepository>();` civarı):

```csharp
        services.AddSingleton<ProductRepository>();
```

- [ ] **Adım 7: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductRepositoryTests
```
Beklenen: 5/5 PASS.

`MigrationRunnerTests` de geçmeli (yeni sürüm numarası):

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests
```
Beklenen: PASS. Bu test şema sürümünü sabit bir sayıya karşı doğruluyorsa
**24'e güncelle** ve neden değiştiğini commit mesajına yaz.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.Core/Catalog/Product.cs \
        OrderDeck.Core/Storage/Migrations/024_product_stock.sql \
        OrderDeck.Core/Storage/Repositories/ProductRepository.cs \
        OrderDeck.App/AppHost.cs \
        OrderDeck.Tests/Storage/ProductRepositoryTests.cs
git commit -m "feat(catalog): ürün kartı için yerel Product/ProductSize şeması"
```

---

## Görev 3: Ürüne göre sipariş sayısı sorgusu

**Dosyalar:**
- Değiştir: `OrderDeck.Core/Storage/Repositories/LabelRepository.cs`
- Test: `OrderDeck.Tests/Storage/LabelRepositoryProductCountTests.cs`

Mockup'ın "BU ÜRÜNDEN 14 sipariş" istatistiği için `(SessionId, Code)` bazlı
bir sayım gerekiyor. `GetSessionTotals` (satır 182-206) yayın geneli veriyor;
kod kırılımı yok. Aynı filtre kurallarını kullan: iptal edilmiş ve
**onaylanmamış yedek** satırlar sayılmaz.

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/Storage/LabelRepositoryProductCountTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Hero'daki "BU ÜRÜNDEN N sipariş" sayacı. GetSessionTotals ile AYNI dışlama
/// kurallarını uygulamalı: iptal edilmiş satır ve onaylanmamış yedek satılmış
/// sayılmaz — yoksa iki sayaç birbirini tutmaz ve operatör hangisine
/// inanacağını bilemez.
/// </summary>
public class LabelRepositoryProductCountTests
{
    private static (InMemorySqlite Db, LabelRepository Repo) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, null, null, null));
        return (db, new LabelRepository(db));
    }

    private static Label Row(string id, string? code, long? printedAt = 200,
                             long? cancelledAt = null, bool tentative = false) =>
        new(id, "s1", "c1", "instagram", "@a", "mesaj", code, 100m, 150,
            printedAt, cancelledAt, null, false, null, tentative);

    [Fact]
    public void Counts_only_rows_with_matching_code()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Insert(Row("l1", "A12"));
        repo.Insert(Row("l2", "A12"));
        repo.Insert(Row("l3", "B7"));

        repo.CountSessionLabelsByCode("s1", "A12").Should().Be(2);
    }

    [Fact]
    public void Ignores_cancelled_and_tentative_backup_rows()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Insert(Row("l1", "A12"));
        repo.Insert(Row("l2", "A12", cancelledAt: 300));
        repo.Insert(Row("l3", "A12", tentative: true));

        repo.CountSessionLabelsByCode("s1", "A12").Should().Be(1);
    }

    [Fact]
    public void Counts_queued_rows_too_not_just_printed()
    {
        var (db, repo) = Fx();
        using var _ = db;

        // Hero sayacı "sipariş" diyor, "yazdırıldı" demiyor: operatör kuyruğa
        // düşen siparişi anında görmeli, yazdırmayı beklememeli.
        repo.Insert(Row("l1", "A12", printedAt: null));

        repo.CountSessionLabelsByCode("s1", "A12").Should().Be(1);
    }

    [Fact]
    public void Code_match_is_case_insensitive()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Insert(Row("l1", "A12"));

        repo.CountSessionLabelsByCode("s1", "a12").Should().Be(1);
    }

    [Fact]
    public void Returns_zero_for_unknown_code()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.CountSessionLabelsByCode("s1", "ZZZ").Should().Be(0);
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~LabelRepositoryProductCountTests
```
Beklenen: derleme hatası — `CountSessionLabelsByCode` yok.

- [ ] **Adım 3: Sorguyu ekle**

`LabelRepository.cs` içinde `GetSessionTotals`'ın hemen ardına:

```csharp
    /// <summary>
    /// Bir yayında belirli ürün kodundan kaç sipariş alındığı. Hero'daki
    /// "BU ÜRÜNDEN" sayacı.
    ///
    /// GetSessionTotals'tan iki farkı var, ikisi de bilinçli:
    ///  * PrintedAt filtresi YOK — operatör siparişi kuyruğa düştüğü anda
    ///    saymak istiyor, yazdırmayı beklemek sayacı geciktirirdi.
    ///  * Kod eşleşmesi harf duyarsız; hero girişi büyük harfe zorluyor ama
    ///    eski satırlar karışık olabilir.
    /// İptal ve onaylanmamış yedek dışlaması ise AYNI — iki sayaç birbirini
    /// tutmalı.
    /// </summary>
    public int CountSessionLabelsByCode(string sessionId, string code)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            """
            SELECT COUNT(*)
            FROM Label
            WHERE SessionId = @sessionId
              AND Code IS NOT NULL
              AND Code = @code COLLATE NOCASE
              AND CancelledAt IS NULL
              AND IsTentativeBackup = 0
            """,
            new { sessionId, code });
    }
```

- [ ] **Adım 4: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~LabelRepositoryProductCountTests
```
Beklenen: 5/5 PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.Core/Storage/Repositories/LabelRepository.cs \
        OrderDeck.Tests/Storage/LabelRepositoryProductCountTests.cs
git commit -m "feat(labels): ürün koduna göre yayın sipariş sayacı"
```

---

## Görev 4: `ProductPhotoStore` — fotoğrafı uygulama veri klasörüne al

**Dosyalar:**
- Oluştur: `OrderDeck.App/Services/ProductPhotoStore.cs`
- Test: `OrderDeck.Tests/App/ProductPhotoStoreTests.cs`
- Değiştir: `OrderDeck.App/AppHost.cs` (kayıt)

**Neden bu sınıf var:** Operatör fotoğrafı masaüstünden/indirilenlerden seçecek.
O yolu doğrudan veritabanına yazarsak dosya taşınınca/silinince kart boş kalır.
Bu yüzden dosya `%LOCALAPPDATA%\OrderDeck\products\` altına **kopyalanır** ve
veritabanına yalnız **dosya adı** yazılır (mutlak yol değil) — böylece kullanıcı
profili değişse de kayıt çözülmeye devam eder.

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/ProductPhotoStoreTests.cs`:

```csharp
using System;
using System.IO;
using FluentAssertions;
using OrderDeck.App.Services;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// ProductPhotoStore'un dosya sözleşmesi. WPF'e dokunmuyor (Application
/// singleton'ı gerekmez) — ThemeTestHost'a ihtiyaç YOK, düz sınıf testi.
/// </summary>
public class ProductPhotoStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "od-photos-" + Guid.NewGuid().ToString("N"));

    private string MakeSourceFile(string name)
    {
        var dir = Path.Combine(_root, "src");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void Save_copies_file_and_returns_relative_name()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));
        var src = MakeSourceFile("foto.jpg");

        var stored = store.Save("A100", src);

        stored.Should().Be("a100.jpg");
        File.Exists(Path.Combine(_root, "products", "a100.jpg")).Should().BeTrue();
        // Kaynak dosyaya dokunulmaz — kullanıcının indirilenler klasörü bizim
        // değil.
        File.Exists(src).Should().BeTrue();
    }

    [Fact]
    public void Save_replaces_previous_photo_even_with_different_extension()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));
        store.Save("A100", MakeSourceFile("ilk.jpg"));

        var stored = store.Save("A100", MakeSourceFile("ikinci.png"));

        stored.Should().Be("a100.png");
        // Eski uzantı bırakılırsa klasör her düzenlemede şişer ve
        // ResolveAbsolute hangisini seçeceğini bilemez.
        File.Exists(Path.Combine(_root, "products", "a100.jpg")).Should().BeFalse();
    }

    [Fact]
    public void ResolveAbsolute_returns_null_when_file_is_missing()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));

        // Kayıt var ama dosya elle silinmiş — kart placeholder'a düşmeli,
        // patlamamalı.
        store.ResolveAbsolute("yok.jpg").Should().BeNull();
    }

    [Fact]
    public void ResolveAbsolute_returns_null_for_null_or_blank()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));

        store.ResolveAbsolute(null).Should().BeNull();
        store.ResolveAbsolute("   ").Should().BeNull();
    }

    [Fact]
    public void ResolveAbsolute_rejects_paths_that_escape_the_root()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));

        // Veritabanı satırı bozulsa bile kök dışına çıkılmamalı.
        store.ResolveAbsolute(@"..\..\windows\win.ini").Should().BeNull();
        store.ResolveAbsolute(@"C:\windows\win.ini").Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductPhotoStoreTests
```
Beklenen: derleme hatası — `ProductPhotoStore` tipi yok.

- [ ] **Adım 3: Sınıfı yaz**

`OrderDeck.App/Services/ProductPhotoStore.cs`:

```csharp
using System;
using System.IO;
using System.Linq;

namespace OrderDeck.App.Services;

/// <summary>
/// Ürün fotoğraflarının dosya deposu.
///
/// NEDEN kopyalıyoruz: operatör fotoğrafı İndirilenler/Masaüstü'nden seçiyor.
/// O yolu veritabanına yazsak dosya taşınınca kart sessizce boşalırdı.
/// Dosya <c>%LOCALAPPDATA%\OrderDeck\products\</c> altına alınır; tabloya
/// yalnız DOSYA ADI yazılır — mutlak yol değil, çünkü kullanıcı profili
/// (makine değişimi, profil taşıma) yolu geçersiz kılar.
///
/// Kapsam notu: R2'ye yükleme / panelden görsel yönetimi stok projesine ait
/// (spec §9.1). Burası kasıtlı olarak yerel ve aptal.
/// </summary>
public sealed class ProductPhotoStore
{
    private readonly string _root;

    /// <param name="root">
    /// Yalnız test için. Üretimde null → %LOCALAPPDATA%\OrderDeck\products.
    /// (WebView2 klasöründeki aynı kural: exe dizini salt-okunur olabilir.)
    /// </param>
    public ProductPhotoStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrderDeck", "products");
    }

    /// <summary>
    /// <paramref name="sourcePath"/>'i depoya kopyalar, tabloya yazılacak
    /// göreli adı döner. Aynı kodun önceki fotoğrafı (uzantısı ne olursa
    /// olsun) silinir — yoksa klasör her düzenlemede şişer ve
    /// <see cref="ResolveAbsolute"/> hangisini seçeceğini bilemez.
    /// </summary>
    public string Save(string code, string sourcePath)
    {
        var key = Normalize(code);
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";

        Directory.CreateDirectory(_root);
        Delete(code);

        var fileName = key + ext;
        File.Copy(sourcePath, Path.Combine(_root, fileName), overwrite: true);
        return fileName;
    }

    /// <summary>
    /// Göreli adı açılabilir mutlak yola çevirir. Dosya yoksa, ad boşsa ya da
    /// kök dışına çıkıyorsa <c>null</c> — çağıran placeholder gösterir.
    /// </summary>
    public string? ResolveAbsolute(string? relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName)) return null;

        var full = Path.GetFullPath(Path.Combine(_root, relativeName));
        var rootFull = Path.GetFullPath(_root);
        // Bozuk/kötü niyetli bir satır ("..\..\windows\win.ini") kökten
        // kaçmasın; kart rastgele dosya açan bir pencereye dönüşmemeli.
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;

        return File.Exists(full) ? full : null;
    }

    /// <summary>Bir ürün koduna ait fotoğrafı (varsa) siler.</summary>
    public void Delete(string code)
    {
        if (!Directory.Exists(_root)) return;
        var key = Normalize(code);
        foreach (var f in Directory.EnumerateFiles(_root, key + ".*").ToList())
        {
            try { File.Delete(f); } catch (IOException) { /* dosya kilitli: kartı düşürme */ }
        }
    }

    private static string Normalize(string code)
    {
        var cleaned = new string((code ?? "").Trim()
            .Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "_" : cleaned.ToLowerInvariant();
    }
}
```

- [ ] **Adım 4: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductPhotoStoreTests
```
Beklenen: 5/5 PASS.

- [ ] **Adım 5: `AppHost`'a kaydet**

`OrderDeck.App/AppHost.cs` içinde `ProductRepository` kaydının (Görev 2) hemen
ardına:

```csharp
        // Ürün fotoğrafı deposu — kapsamı %LOCALAPPDATA%\OrderDeck\products.
        services.AddSingleton<ProductPhotoStore>();
```

`using OrderDeck.App.Services;` zaten dosyada var; yoksa ekle.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.App/Services/ProductPhotoStore.cs \
        OrderDeck.Tests/App/ProductPhotoStoreTests.cs \
        OrderDeck.App/AppHost.cs
git commit -m "feat(catalog): ürün fotoğrafı yerel dosya deposu"
```

---

## Görev 5: `ProductCardViewModel` + `ProductSizeViewModel`

**Dosyalar:**
- Oluştur: `OrderDeck.App/ViewModels/ProductSizeViewModel.cs`
- Oluştur: `OrderDeck.App/ViewModels/ProductCardViewModel.cs`
- Test: `OrderDeck.Tests/App/ProductCardViewModelTests.cs`

**Kapsam hatırlatması (spec §9.1):** grid **yalnız gösterir**. Etiket kuyruğa
girince stok düşmez, `Label`'a beden alanı eklenmez, sohbetten beden ayıklanmaz.
Adetleri operatör kartta elle düzenler.

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/ProductCardViewModelTests.cs`:

```csharp
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using Xunit;

namespace OrderDeck.Tests.App;

public class ProductCardViewModelTests
{
    private static (ProductCardViewModel Vm, ProductRepository Repo) Make()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new ProductRepository(db);
        var photos = new ProductPhotoStore(
            Path.Combine(Path.GetTempPath(), "od-card-" + System.Guid.NewGuid().ToString("N")));
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(2000L);
        return (new ProductCardViewModel(repo, photos, clock.Object), repo);
    }

    [Fact]
    public void Load_unknown_code_enters_edit_mode_with_empty_fields()
    {
        var (vm, _) = Make();

        vm.Load("A100");

        vm.Code.Should().Be("A100");
        vm.HasProduct.Should().BeFalse();
        // Pop-up yok (spec §6) — kart kendi içinde tanımlama moduna düşer.
        vm.IsEditing.Should().BeTrue();
        vm.Name.Should().BeEmpty();
        vm.Sizes.Should().BeEmpty();
    }

    [Fact]
    public void Load_known_code_shows_saved_product()
    {
        var (vm, repo) = Make();
        repo.Save(new Product("A100", "Kırmızı Elbise", "a100.jpg", 0),
            new[] { new ProductSize("A100", "S", 3, 0), new ProductSize("A100", "M", 0, 1) });

        vm.Load("A100");

        vm.HasProduct.Should().BeTrue();
        vm.IsEditing.Should().BeFalse();
        vm.Name.Should().Be("Kırmızı Elbise");
        vm.Sizes.Select(s => s.Size).Should().Equal("S", "M");
        vm.Sizes[0].Quantity.Should().Be(3);
    }

    [Fact]
    public void Load_blank_code_clears_the_card()
    {
        var (vm, repo) = Make();
        repo.Save(new Product("A100", "Kırmızı Elbise", null, 0), new[] { new ProductSize("A100", "S", 3, 0) });
        vm.Load("A100");

        vm.Load("");

        // Hero'daki kod kutusu boşaltıldığında kart eski ürünü göstermeye
        // devam ederse operatör yanlış stoğa bakar.
        vm.HasProduct.Should().BeFalse();
        vm.IsEditing.Should().BeFalse();
        vm.Sizes.Should().BeEmpty();
    }

    [Fact]
    public void ApplySizesText_creates_tiles_in_written_order()
    {
        var (vm, _) = Make();
        vm.Load("A100");

        vm.SizesText = "S, M, L, XL";
        vm.ApplySizesText();

        vm.Sizes.Select(s => s.Size).Should().Equal("S", "M", "L", "XL");
        vm.Sizes.Select(s => s.SortOrder).Should().Equal(0, 1, 2, 3);
        vm.Sizes.Should().OnlyContain(s => s.Quantity == 0);
    }

    [Fact]
    public void ApplySizesText_keeps_quantities_of_surviving_sizes()
    {
        var (vm, _) = Make();
        vm.Load("A100");
        vm.SizesText = "S,M";
        vm.ApplySizesText();
        vm.Sizes[1].Quantity = 7;   // M = 7

        vm.SizesText = "M,L";
        vm.ApplySizesText();

        // Operatör beden setini düzeltirken hayatta kalan bedenin adedini
        // yeniden yazmak zorunda kalmamalı.
        vm.Sizes.Select(s => s.Size).Should().Equal("M", "L");
        vm.Sizes[0].Quantity.Should().Be(7);
    }

    [Fact]
    public void ApplySizesText_drops_duplicates_and_blanks()
    {
        var (vm, _) = Make();
        vm.Load("A100");

        vm.SizesText = "S, , s ,M,,M";
        vm.ApplySizesText();

        // Beden Product tablosunda PK'nın parçası — çift satır INSERT'te
        // patlar; burada eleriz.
        vm.Sizes.Select(s => s.Size).Should().Equal("S", "M");
    }

    [Fact]
    public void Save_persists_product_and_leaves_edit_mode()
    {
        var (vm, repo) = Make();
        vm.Load("A100");
        vm.Name = "Kırmızı Elbise";
        vm.SizesText = "S,M";
        vm.ApplySizesText();
        vm.Sizes[0].Quantity = 4;

        vm.SaveCommand.Execute(null);

        vm.IsEditing.Should().BeFalse();
        vm.HasProduct.Should().BeTrue();
        repo.Get("A100")!.Name.Should().Be("Kırmızı Elbise");
        repo.Get("A100")!.UpdatedAt.Should().Be(2000);   // IClock, unix SANİYE
        repo.GetSizes("A100").Single(s => s.Size == "S").Quantity.Should().Be(4);
    }

    [Fact]
    public void Save_is_blocked_while_name_is_blank()
    {
        var (vm, repo) = Make();
        vm.Load("A100");
        vm.SizesText = "S";
        vm.ApplySizesText();

        vm.SaveCommand.CanExecute(null).Should().BeFalse();

        vm.Name = "Kırmızı Elbise";
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CancelEdit_restores_the_saved_state()
    {
        var (vm, repo) = Make();
        repo.Save(new Product("A100", "Kırmızı Elbise", null, 0),
            new[] { new ProductSize("A100", "S", 3, 0) });
        vm.Load("A100");
        vm.BeginEditCommand.Execute(null);
        vm.Name = "Bozuk isim";
        vm.SizesText = "XXL";
        vm.ApplySizesText();

        vm.CancelEditCommand.Execute(null);

        vm.Name.Should().Be("Kırmızı Elbise");
        vm.Sizes.Select(s => s.Size).Should().Equal("S");
        vm.IsEditing.Should().BeFalse();
    }

    [Fact]
    public void SetPhoto_copies_the_file_and_exposes_absolute_path()
    {
        var (vm, _) = Make();
        vm.Load("A100");
        var src = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(src, new byte[] { 1 });

        vm.SetPhoto(src);

        vm.PhotoPath.Should().Be("a100.png");
        vm.PhotoAbsolutePath.Should().NotBeNull();
        File.Exists(vm.PhotoAbsolutePath!).Should().BeTrue();
        File.Delete(src);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, false, false)]
    public void Size_tile_low_and_out_flags(int qty, bool low, bool outOfStock)
    {
        var tile = new ProductSizeViewModel("M", qty, 0);

        // Mockup: .cnt.low amber, .size.out soluk + üstü çizili.
        tile.IsLow.Should().Be(low);
        tile.IsOutOfStock.Should().Be(outOfStock);
    }

    [Fact]
    public void Size_tile_flags_react_to_quantity_edits()
    {
        var tile = new ProductSizeViewModel("M", 5, 0);

        tile.Quantity = 0;

        // Adet kartta satır-içi düzenleniyor; rozetler anında dönmezse
        // operatör tükenmiş bedeni fark etmez.
        tile.IsOutOfStock.Should().BeTrue();
        tile.IsLow.Should().BeFalse();
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCardViewModelTests
```
Beklenen: derleme hatası — `ProductCardViewModel` / `ProductSizeViewModel` yok.

- [ ] **Adım 3: `ProductSizeViewModel`'i yaz**

`OrderDeck.App/ViewModels/ProductSizeViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Beden ızgarasının tek karesi. Adet kartta satır-içi düzenlenir; otomatik
/// düşüş YOK (spec §9.1 — Faz 1'de grid yalnız gösterir).
/// </summary>
public sealed partial class ProductSizeViewModel : ObservableObject
{
    /// <summary>
    /// "Az kaldı" eşiği. Mockup'ta amber rozet; 2 ve altı = son parçalar.
    /// Ayarlanabilir yapmıyoruz — stok projesi gelene kadar tek sabit yeter.
    /// </summary>
    public const int LowStockThreshold = 2;

    public ProductSizeViewModel(string size, int quantity, int sortOrder)
    {
        Size = size;
        _quantity = quantity;
        SortOrder = sortOrder;
    }

    public string Size { get; }
    public int SortOrder { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLow))]
    [NotifyPropertyChangedFor(nameof(IsOutOfStock))]
    private int _quantity;

    public bool IsOutOfStock => Quantity <= 0;
    public bool IsLow => Quantity > 0 && Quantity <= LowStockThreshold;
}
```

- [ ] **Adım 4: `ProductCardViewModel`'i yaz**

`OrderDeck.App/ViewModels/ProductCardViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Sağ paneldeki ürün kartı: fotoğraf, ad, beden stoğu.
///
/// Hero'daki kod kutusu her değiştiğinde <see cref="Load"/> çağrılır. Kod
/// tanınmıyorsa kart satır-içi TANIMLAMA moduna düşer — pop-up açılmaz
/// (spec §6: hiçbir şey pop-up değil).
///
/// Kartta FİYAT ALANI YOK: karttaki fiyat hero'daki aktif fiyat girişinin
/// aynısıdır, view onu MainShellViewModel'den bağlar (spec §9.1).
/// </summary>
public sealed partial class ProductCardViewModel : ObservableObject
{
    private readonly ProductRepository _repo;
    private readonly ProductPhotoStore _photos;
    private readonly IClock _clock;

    public ProductCardViewModel(ProductRepository repo, ProductPhotoStore photos, IClock clock)
    {
        _repo = repo;
        _photos = photos;
        _clock = clock;
    }

    public ObservableCollection<ProductSizeViewModel> Sizes { get; } = new();

    [ObservableProperty] private string _code = "";
    [ObservableProperty] private bool _hasProduct;
    [ObservableProperty] private bool _isEditing;

    /// <summary>Beden seti düzenleme kutusu: "S, M, L, XL".</summary>
    [ObservableProperty] private string _sizesText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotoAbsolutePath))]
    private string? _photoPath;

    /// <summary>
    /// Image kaynağı. Dosya silinmişse null → view placeholder gösterir.
    /// </summary>
    public string? PhotoAbsolutePath => _photos.ResolveAbsolute(PhotoPath);

    /// <summary>
    /// Hero'daki kod değişince çağrılır. Boş kod = kart temizlenir; tanınmayan
    /// kod = tanımlama modu; tanınan kod = kayıtlı ürün.
    /// </summary>
    public void Load(string? code)
    {
        var trimmed = (code ?? "").Trim();
        Code = trimmed;

        if (trimmed.Length == 0) { Reset(hasProduct: false, editing: false); return; }

        var product = _repo.Get(trimmed);
        if (product is null)
        {
            Reset(hasProduct: false, editing: true);
            return;
        }

        Name = product.Name;
        PhotoPath = product.PhotoPath;
        LoadSizes(_repo.GetSizes(trimmed));
        HasProduct = true;
        IsEditing = false;
    }

    /// <summary>
    /// <see cref="SizesText"/>'i ızgaraya uygular. Hayatta kalan bedenlerin
    /// adedi korunur — operatör "S,M" → "M,L" düzeltmesi yaparken M'nin
    /// adedini yeniden yazmak zorunda kalmamalı.
    /// </summary>
    public void ApplySizesText()
    {
        var existing = Sizes.ToDictionary(s => s.Size, StringComparer.OrdinalIgnoreCase);

        var wanted = SizesText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Sizes.Clear();
        for (var i = 0; i < wanted.Count; i++)
        {
            existing.TryGetValue(wanted[i], out var prev);
            Sizes.Add(new ProductSizeViewModel(wanted[i], prev?.Quantity ?? 0, i));
        }
    }

    /// <summary>Seçilen dosyayı depoya kopyalar (dosya seçme diyaloğu view'da).</summary>
    public void SetPhoto(string sourcePath)
    {
        if (Code.Length == 0) return;
        PhotoPath = _photos.Save(Code, sourcePath);
    }

    [RelayCommand]
    private void BeginEdit()
    {
        SizesText = string.Join(", ", Sizes.Select(s => s.Size));
        IsEditing = true;
    }

    private bool CanSave() => Code.Length > 0 && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        // Unix SANİYE — repo'daki her zaman damgası IClock ile aynı birimde
        // (bkz. OrderDeck.Core/Time/IClock.cs).
        _repo.Save(
            new Product(Code, Name.Trim(), PhotoPath, _clock.UnixNow()),
            Sizes.Select((s, i) => new ProductSize(Code, s.Size, s.Quantity, i)).ToList());

        HasProduct = true;
        IsEditing = false;
    }

    /// <summary>Düzenlemeyi at, diskteki hâle dön.</summary>
    [RelayCommand]
    private void CancelEdit() => Load(Code);

    private void Reset(bool hasProduct, bool editing)
    {
        Name = "";
        PhotoPath = null;
        SizesText = "";
        Sizes.Clear();
        HasProduct = hasProduct;
        IsEditing = editing;
    }

    private void LoadSizes(IReadOnlyList<ProductSize> sizes)
    {
        Sizes.Clear();
        foreach (var s in sizes) Sizes.Add(new ProductSizeViewModel(s.Size, s.Quantity, s.SortOrder));
        SizesText = string.Join(", ", sizes.Select(s => s.Size));
    }
}
```

- [ ] **Adım 5: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCardViewModelTests
```
Beklenen: 13/13 PASS (Theory 4 sonuç üretir).

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.App/ViewModels/ProductCardViewModel.cs \
        OrderDeck.App/ViewModels/ProductSizeViewModel.cs \
        OrderDeck.Tests/App/ProductCardViewModelTests.cs
git commit -m "feat(catalog): ürün kartı ViewModel'i + beden ızgarası"
```

---

## Görev 6: Hero istatistikleri — `MainShellViewModel`

**Dosyalar:**
- Değiştir: `OrderDeck.App/ViewModels/MainShellViewModel.cs`
- Değiştir: `OrderDeck.App/AppHost.cs` (yeni bağımlılıklar)
- Değiştir: `OrderDeck.Tests/App/MainShellTestHarness.cs:102`
- Değiştir: `OrderDeck.Tests/App/MainShellPrintTests.cs:164`
- Test: `OrderDeck.Tests/App/MainShellHeroStatsTests.cs`

Mockup'ın hero şeridinde dört sayaç var: **BU ÜRÜNDEN**, **YAYIN TOPLAMI**,
**YAYIN CİROSU** (göz ikonuyla gizlenebilir), **KUYRUKTA**. Üst barda yayın
süresi sayacı ve saat var.

**Ciro maskesi neden var:** operatör ekranı yayında paylaşabiliyor; ciro
izleyiciye görünmemeli. Maske yalnız görüntüyü değiştirir, veriyi değil.

**Yeni bağımlılıklar zorunlu (opsiyonel değil):** `LabelRepository` ve
`ProductCardViewModel` ctor'a **zorunlu** parametre olarak eklenir. Opsiyonel
yapıp null-kontrolü koymak, hiç oluşmayacak bir durum için ölü kod üretirdi;
yalnız iki inşa noktası var ve ikisinde de `labelRepo`/`db` zaten kapsamda.

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/MainShellHeroStatsTests.cs`:

```csharp
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Hero şeridindeki dört sayaç + ciro maskesi. Harness gerçek repo'lar
/// kullanıyor, bu yüzden sayaçlar SQL'in kendisini de doğruluyor.
/// </summary>
public class MainShellHeroStatsTests
{
    [Fact]
    public void Queue_count_tracks_the_print_queue()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.QueueCount.Should().Be(0);
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 100m);

        h.Vm.QueueCount.Should().Be(1);
    }

    [Fact]
    public void Product_count_counts_only_the_active_code()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.ActiveCode = "A100";
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "fatma", 100m);
        h.Vm.ActiveCode = "B200";
        MainShellTestHarness.EnqueueLabel(h.Vm, "zeynep", 100m);

        h.Vm.ProductOrderCount.Should().Be(1);

        h.Vm.ActiveCode = "A100";
        h.Vm.ProductOrderCount.Should().Be(2);
    }

    [Fact]
    public void Product_count_is_zero_when_no_code_is_active()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 100m);

        h.Vm.ActiveCode = "";

        // Kod yokken "BU ÜRÜNDEN" kutusu anlamsız — sayaç sıfırlanır, view
        // kutuyu soluklaştırır.
        h.Vm.ProductOrderCount.Should().Be(0);
    }

    [Fact]
    public async Task Session_totals_count_printed_labels_only()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 150m);

        // Henüz basılmadı → GetSessionTotals PrintedAt IS NOT NULL istiyor.
        h.Vm.SessionLabelCount.Should().Be(0);
        h.Vm.SessionRevenue.Should().Be(0m);

        // PrintCommand async (UI freeze fix 2026-05-13) — ExecuteAsync await edilir.
        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Vm.SessionLabelCount.Should().Be(1);
        h.Vm.SessionRevenue.Should().Be(150m);
    }

    [Fact]
    public async Task Revenue_mask_hides_the_amount_but_not_the_value()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 150m);
        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Vm.SessionRevenueText.Should().Contain("150");

        h.Vm.ToggleRevenueMaskCommand.Execute(null);

        // Yayında ekran paylaşılıyor olabilir; metin gizlenir ama sayı durur.
        h.Vm.IsRevenueMasked.Should().BeTrue();
        h.Vm.SessionRevenueText.Should().Be("₺ ••••");
        h.Vm.SessionRevenue.Should().Be(150m);
    }

    [Fact]
    public void Active_code_change_loads_the_product_card()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.ActiveCode = "A100";

        h.Vm.ProductCard.Code.Should().Be("A100");
        // Kod tanınmıyor → kart satır-içi tanımlama moduna düşer (pop-up yok).
        h.Vm.ProductCard.IsEditing.Should().BeTrue();
    }

    [Fact]
    public void Stream_duration_text_is_empty_without_an_active_session()
    {
        var h = MainShellTestHarness.Build();
        // EndStreamCommand yerine servisi doğrudan çağırıyoruz: komut async ve
        // onay MessageBox'ı açabiliyor — test sürecinde diyalog istemiyoruz.
        h.Sessions.End(h.Sessions.GetActive()!.Id);

        h.Vm.RefreshHeroStats();

        h.Vm.StreamDurationText.Should().BeEmpty();
    }

    [Fact]
    public void Stream_duration_text_is_hh_mm_ss()
    {
        var h = MainShellTestHarness.Build();       // session StartedAt = 1000
        h.Clock.Setup(c => c.UnixNow()).Returns(1000L + 3661L);

        h.Vm.RefreshHeroStats();

        h.Vm.StreamDurationText.Should().Be("01:01:01");
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellHeroStatsTests
```
Beklenen: derleme hatası — `QueueCount`, `ProductCard`, `RefreshHeroStats` yok.

- [ ] **Adım 3: Ctor'a bağımlılıkları ekle**

`MainShellViewModel.cs`, alan bildirimlerine (`_customerRepo`'nun yanına):

```csharp
    private readonly LabelRepository _labelRepo;
    private readonly IClock _clock;
```

Ctor imzasında `CustomerRepository customerRepo,` satırının hemen ardına:

```csharp
        LabelRepository labelRepo,
        IClock clock,
        ProductCardViewModel productCard,
```

Ctor gövdesinde `_customerRepo = customerRepo;` satırının ardına:

```csharp
        _labelRepo = labelRepo;
        _clock = clock;
        ProductCard = productCard;
```

Ctor sonunda `ReloadQueueFromActiveSession();` çağrısının **ardına**:

```csharp
        // Kuyruk her değiştiğinde (ekleme, silme, iptal, yazdırma) hero
        // sayaçları tazelenir — tek yerden, çağrı noktalarını serpiştirmeden.
        PrintQueue.CollectionChanged += (_, _) => RefreshHeroStats();
        EnsureHeroTimer();
        RefreshHeroStats();
```

- [ ] **Adım 4: Hero üyelerini ekle**

`MainShellViewModel.cs` içinde, `[ObservableProperty] private string _activeCode = "";`
bloğunun hemen ardına:

```csharp
    /// <summary>Sağ paneldeki ürün kartı. Hero'daki kod her değişince yüklenir.</summary>
    public ProductCardViewModel ProductCard { get; private set; } = null!;

    [ObservableProperty] private int _productOrderCount;
    [ObservableProperty] private int _sessionLabelCount;
    [ObservableProperty] private int _queueCount;
    [ObservableProperty] private string _streamDurationText = "";
    [ObservableProperty] private string _clockText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionRevenueText))]
    private decimal _sessionRevenue;

    /// <summary>
    /// Ciro maskesi. Operatör yayında ekranını paylaşabiliyor; ciro
    /// izleyiciye görünmemeli. Yalnız görüntüyü değiştirir — SessionRevenue
    /// gerçek değeri tutmaya devam eder.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionRevenueText))]
    private bool _isRevenueMasked;

    public string SessionRevenueText => IsRevenueMasked
        ? "₺ ••••"
        : SessionRevenue.ToString("C0", new System.Globalization.CultureInfo("tr-TR"));

    [RelayCommand] private void ToggleRevenueMask() => IsRevenueMasked = !IsRevenueMasked;

    partial void OnActiveCodeChanged(string value)
    {
        ProductCard.Load(value);
        RefreshHeroStats();
    }

    private System.Windows.Threading.DispatcherTimer? _heroTimer;

    private void EnsureHeroTimer()
    {
        if (_heroTimer is not null) return;
        // 1 sn: yayın süresi ve duvar saati saniye çözünürlüğünde. Sayaç
        // sorguları da burada tazeleniyor ki dışarıdan (senkron, iptal)
        // gelen değişiklikler de yakalansın.
        _heroTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => RefreshHeroStats(), _dispatcher);
        _heroTimer.Start();
    }

    /// <summary>
    /// Hero'daki dört sayaç + süre/saat. Test'ten de çağrılabilsin diye
    /// public — zamanlayıcıyı beklemeden doğrulanabiliyor.
    /// </summary>
    public void RefreshHeroStats()
    {
        QueueCount = PrintQueue.Count;
        ClockText = DateTime.Now.ToString("HH:mm");

        var session = _sessions.GetActive();
        if (session is null)
        {
            ProductOrderCount = 0;
            SessionLabelCount = 0;
            SessionRevenue = 0m;
            StreamDurationText = "";
            return;
        }

        var elapsed = TimeSpan.FromSeconds(Math.Max(0, _clock.UnixNow() - session.StartedAt));
        StreamDurationText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        var totals = _labelRepo.GetSessionTotals(session.Id);
        SessionLabelCount = totals.PrintedCount;
        SessionRevenue = totals.TotalAmount;

        ProductOrderCount = string.IsNullOrWhiteSpace(ActiveCode)
            ? 0
            : _labelRepo.CountSessionLabelsByCode(session.Id, ActiveCode.Trim());
    }
```

`Dispose()` içine, `_chatFlushTimer` durdurma satırlarının yanına:

```csharp
        _heroTimer?.Stop();
        _heroTimer = null;
```

Gerekli `using`ler dosyada var mı kontrol et: `OrderDeck.Core.Time`,
`OrderDeck.Core.Storage.Repositories`.

- [ ] **Adım 5: İki inşa noktasını güncelle**

`OrderDeck.Tests/App/MainShellTestHarness.cs` ve
`OrderDeck.Tests/App/MainShellPrintTests.cs` — her ikisinde de `var vm = new
MainShellViewModel(...)` çağrısından **önce**:

```csharp
        var productRepo = new ProductRepository(db);
        var productCard = new ProductCardViewModel(
            productRepo,
            new ProductPhotoStore(Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))),
            clock.Object);
```

ve çağrının kendisi:

```csharp
        var vm = new MainShellViewModel(
            bus, labelSvc, sessionSvc, printer, customerSvc, customerRepo,
            labelRepo, clock.Object, productCard,
            giveawaySvc, banner, licenseSvc, intakeSync, tempStore);
```

`MainShellTestHarness.Harness` record'una `Mock<IClock> Clock` zaten var —
testler `h.Clock` üzerinden saati ileri alabiliyor.

- [ ] **Adım 6: `AppHost`'u güncelle**

`MainShellViewModel` kaydında yeni bağımlılıklar DI'dan gelir; `LabelRepository`,
`IClock` ve `ProductRepository` zaten kayıtlı, `ProductPhotoStore` Görev 4'te
eklendi. Yalnız `ProductCardViewModel`'i kaydet:

```csharp
        services.AddSingleton<ProductCardViewModel>();
```

- [ ] **Adım 7: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShell
```
Beklenen: yeni 8 test PASS; mevcut `MainShellPrintTests` / harness kullanan
testlerin tamamı hâlâ PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.App/ViewModels/MainShellViewModel.cs OrderDeck.App/AppHost.cs \
        OrderDeck.Tests/App/MainShellTestHarness.cs \
        OrderDeck.Tests/App/MainShellPrintTests.cs \
        OrderDeck.Tests/App/MainShellHeroStatsTests.cs
git commit -m "feat(shell): hero istatistikleri, yayın süresi ve ciro maskesi"
```

---

## Görev 7: Sohbet filtresi — arama + "yalnız aktif kod"

**Dosyalar:**
- Değiştir: `OrderDeck.App/ViewModels/MainShellViewModel.cs`
- Test: `OrderDeck.Tests/App/MainShellChatFilterTests.cs`

Mockup'ın sohbet başlığında bir arama kutusu ve bir "yalnız aktif kod" çipi var.
Ayrı bir koleksiyon tutmuyoruz — `ChatMessages` üzerinde bir
`ICollectionView` filtresi kuruyoruz; böylece mevcut ekleme/kırpma mantığına
hiç dokunulmuyor.

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/MainShellChatFilterTests.cs`:

```csharp
using System.Linq;
using System.Windows.Data;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using Xunit;

namespace OrderDeck.Tests.App;

public class MainShellChatFilterTests
{
    private static int VisibleCount(MainShellViewModel vm) =>
        vm.ChatView.Cast<ChatMessageViewModel>().Count();

    [Fact]
    public void No_filter_shows_every_message()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "A100 alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "merhaba"));

        VisibleCount(h.Vm).Should().Be(2);
    }

    [Fact]
    public void Search_matches_username_and_text_case_insensitively()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "merhaba"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "AYSE'ye selam"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("zeynep", "başka"));

        h.Vm.ChatSearchText = "ayse";

        // Kullanıcı adı ya da metin — operatör hangisini hatırlarsa.
        VisibleCount(h.Vm).Should().Be(2);
    }

    [Fact]
    public void Only_active_code_filters_by_the_hero_code()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "a100 alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "B200 alıyorum"));
        h.Vm.ActiveCode = "A100";

        h.Vm.OnlyActiveCode = true;

        VisibleCount(h.Vm).Should().Be(1);
    }

    [Fact]
    public void Only_active_code_is_inert_while_no_code_is_active()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "a100"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "B200"));
        h.Vm.ActiveCode = "";

        h.Vm.OnlyActiveCode = true;

        // Kod yokken her şeyi gizlemek sohbeti boşaltır ve operatör paneli
        // bozuldu sanır — filtre kendini devre dışı bırakır.
        VisibleCount(h.Vm).Should().Be(2);
    }

    [Fact]
    public void Filters_combine()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "A100 alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "A100 alıyorum"));
        h.Vm.ActiveCode = "A100";

        h.Vm.OnlyActiveCode = true;
        h.Vm.ChatSearchText = "fatma";

        VisibleCount(h.Vm).Should().Be(1);
    }

    [Fact]
    public void Changing_the_active_code_refreshes_the_view()
    {
        var h = MainShellTestHarness.Build();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "A100"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "B200"));
        h.Vm.ActiveCode = "A100";
        h.Vm.OnlyActiveCode = true;

        h.Vm.ActiveCode = "B200";

        // Filtre delegesi ActiveCode'u okuyor ama CollectionView bunu
        // kendiliğinden bilmez — Refresh() tetiklenmeli.
        VisibleCount(h.Vm).Should().Be(1);
        h.Vm.ChatView.Cast<ChatMessageViewModel>().Single().Username.Should().Be("fatma");
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellChatFilterTests
```
Beklenen: derleme hatası — `ChatView`, `ChatSearchText`, `OnlyActiveCode` yok.

- [ ] **Adım 3: Filtreyi ekle**

`MainShellViewModel.cs`, `ChatMessages` bildiriminin hemen ardına:

```csharp
    /// <summary>
    /// Sohbet listesinin bağlandığı görünüm. Ayrı bir "filtrelenmiş
    /// koleksiyon" tutmuyoruz — ekleme/kırpma mantığı ChatMessages'ta
    /// olduğu gibi kalsın, filtre yalnız sunum katmanında olsun diye.
    /// </summary>
    public ICollectionView ChatView { get; }
```

Ctor'da `_busSubscription = bus.Subscribe(OnChatMessage);` satırından **önce**:

```csharp
        ChatView = CollectionViewSource.GetDefaultView(ChatMessages);
        ChatView.Filter = ChatFilter;
```

Filtre üyeleri (`ProductCard` bloğunun ardına):

```csharp
    [ObservableProperty] private string _chatSearchText = "";
    [ObservableProperty] private bool _onlyActiveCode;

    partial void OnChatSearchTextChanged(string value) => ChatView.Refresh();
    partial void OnOnlyActiveCodeChanged(bool value) => ChatView.Refresh();

    private bool ChatFilter(object item)
    {
        if (item is not ChatMessageViewModel m) return false;

        // "Yalnız aktif kod" — kod boşken filtreyi uygulamıyoruz, yoksa
        // sohbet tamamen boşalır ve operatör panel bozuldu sanır.
        if (OnlyActiveCode && !string.IsNullOrWhiteSpace(ActiveCode) &&
            m.Text?.Contains(ActiveCode.Trim(), StringComparison.OrdinalIgnoreCase) != true)
            return false;

        if (string.IsNullOrWhiteSpace(ChatSearchText)) return true;

        var q = ChatSearchText.Trim();
        return m.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
            || m.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
    }
```

`OnActiveCodeChanged`'i genişlet (Görev 6'da eklenmişti):

```csharp
    partial void OnActiveCodeChanged(string value)
    {
        ProductCard.Load(value);
        RefreshHeroStats();
        ChatView.Refresh();
    }
```

`using System.ComponentModel;` ve `using System.Windows.Data;` ekle.

- [ ] **Adım 4: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellChatFilterTests
```
Beklenen: 6/6 PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.App/ViewModels/MainShellViewModel.cs \
        OrderDeck.Tests/App/MainShellChatFilterTests.cs
git commit -m "feat(shell): sohbet arama ve aktif-kod filtresi"
```

---

## Görev 8: Kenar çubuğu alt bilgisi — bağlantılar + yazıcı

**Dosyalar:**
- Oluştur: `OrderDeck.App/ViewModels/PlatformConnectionViewModel.cs`
- Değiştir: `OrderDeck.App/ViewModels/MainShellViewModel.cs`
- Test: `OrderDeck.Tests/App/MainShellConnectionsTests.cs`

Mockup'ın kenar çubuğu altında "BAĞLANTILAR" başlığı, dört platform noktası ve
bir yazıcı satırı var.

**Yeni veri katmanı gerekmiyor:** `ViewerCountTracker.GetSnapshot(maxAge)` zaten
`PerPlatform` listesi döndürüyor — taze kayıt = o platform bağlı. Yazıcı satırı
`AppSettings.PrinterName`'den gelir. **Yazıcının gerçekten hazır olup olmadığını
sorgulamıyoruz** — o yeni iş, kapsam dışı; seçili değilse amber uyarı yeter.

**Platform rozeti kuralı burada da geçerli:** kısaltma + marka rengi YOK.
Noktanın yanındaki ikon `OD.PlatformIcon.*`, adı Türkçe düz metin.

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.Tests/App/MainShellConnectionsTests.cs`:

```csharp
using System.Linq;
using FluentAssertions;
using Xunit;

namespace OrderDeck.Tests.App;

public class MainShellConnectionsTests
{
    [Fact]
    public void Four_platforms_are_always_listed()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.RefreshConnections();

        // Bağlı olmayan platform listeden düşerse operatör "bağlanmadı mı,
        // yoksa hiç mi yok?" ayrımını yapamaz — dördü de hep durur.
        h.Vm.Connections.Select(c => c.Platform)
            .Should().Equal("youtube", "instagram", "tiktok", "facebook");
    }

    [Fact]
    public void Platforms_without_a_tracker_are_all_disconnected()
    {
        var h = MainShellTestHarness.Build();   // ViewerCountTracker verilmedi

        h.Vm.RefreshConnections();

        h.Vm.Connections.Should().OnlyContain(c => !c.IsConnected);
    }

    [Fact]
    public void Printer_line_shows_the_configured_printer()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.RefreshPrinterStatus("Zebra ZD420");

        h.Vm.PrinterStatusText.Should().Be("Zebra ZD420");
        h.Vm.IsPrinterConfigured.Should().BeTrue();
    }

    [Fact]
    public void Printer_line_warns_when_no_printer_is_configured()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.RefreshPrinterStatus(null);

        h.Vm.PrinterStatusText.Should().Be("Yazıcı seçilmedi");
        h.Vm.IsPrinterConfigured.Should().BeFalse();
    }

    [Fact]
    public void Connection_view_model_exposes_a_turkish_display_name()
    {
        new OrderDeck.App.ViewModels.PlatformConnectionViewModel("youtube")
            .DisplayName.Should().Be("YouTube");
        new OrderDeck.App.ViewModels.PlatformConnectionViewModel("tiktok")
            .DisplayName.Should().Be("TikTok");
    }
}
```

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellConnectionsTests
```
Beklenen: derleme hatası — `PlatformConnectionViewModel`, `Connections` yok.

- [ ] **Adım 3: `PlatformConnectionViewModel`'i yaz**

`OrderDeck.App/ViewModels/PlatformConnectionViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Kenar çubuğu alt bilgisindeki tek bağlantı satırı.
///
/// DİKKAT: burada "kısaltma + marka rengi" rozeti KULLANILMAZ (bkz.
/// Themes/PlatformIcons.xaml dosya başı — Google itirazı). View, resmi
/// ikonu OD.PlatformIcon.* üzerinden bağlar.
/// </summary>
public sealed partial class PlatformConnectionViewModel : ObservableObject
{
    public PlatformConnectionViewModel(string platform)
    {
        Platform = platform;
        DisplayName = platform switch
        {
            "youtube"   => "YouTube",
            "instagram" => "Instagram",
            "tiktok"    => "TikTok",
            "facebook"  => "Facebook",
            _           => platform
        };
    }

    public string Platform { get; }
    public string DisplayName { get; }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _viewerCount;
}
```

- [ ] **Adım 4: `MainShellViewModel`'e bağla**

Alanlar (`Connections` `ProductCard` bloğunun ardına):

```csharp
    /// <summary>
    /// Kenar çubuğu alt bilgisindeki bağlantı noktaları. Sabit dört satır —
    /// eksik platform "bağlı değil" olarak görünür, listeden düşmez.
    /// </summary>
    public ObservableCollection<PlatformConnectionViewModel> Connections { get; } =
        new(new[]
        {
            new PlatformConnectionViewModel("youtube"),
            new PlatformConnectionViewModel("instagram"),
            new PlatformConnectionViewModel("tiktok"),
            new PlatformConnectionViewModel("facebook"),
        });

    [ObservableProperty] private string _printerStatusText = "Yazıcı seçilmedi";
    [ObservableProperty] private bool _isPrinterConfigured;

    /// <summary>
    /// Yeni veri katmanı YOK: ViewerCountTracker zaten platform başına
    /// yapılandırılmış kayıt tutuyor, taze kayıt = bağlı.
    /// </summary>
    public void RefreshConnections()
    {
        var snap = _viewers?.GetSnapshot(TimeSpan.FromSeconds(90));
        foreach (var c in Connections)
        {
            var row = snap?.PerPlatform
                .FirstOrDefault(p => string.Equals(p.Platform, c.Platform, StringComparison.OrdinalIgnoreCase));
            c.IsConnected = row is not null;
            c.ViewerCount = row?.Count ?? 0;
        }
    }

    /// <summary>
    /// Yazıcı satırı. Yazıcının GERÇEKTEN hazır olup olmadığını sormuyoruz —
    /// spooler sorgusu ayrı bir iş; burada yalnız "seçilmiş mi" var.
    /// </summary>
    public void RefreshPrinterStatus(string? printerName)
    {
        IsPrinterConfigured = !string.IsNullOrWhiteSpace(printerName);
        PrinterStatusText = IsPrinterConfigured ? printerName!.Trim() : "Yazıcı seçilmedi";
    }
```

**Nereye bağlanacak — 1 sn'lik hero zamanlayıcısına DEĞİL.** `SettingsStore.Load()`
önbelleksiz; her çağrıda `File.ReadAllText` yapıyor
(`OrderDeck.Core/Settings/SettingsStore.cs:22-29`). Saniyede bir disk okuması
kabul edilemez. Zaten var olan **5 sn'lik `_chatHealthTimer`** hem `Load()`'u
hem `_viewers.GetSnapshot()`'ı çağırıyor — ikisi de oraya biner.

`UpdateChatHealth()` içinde, `var settings = _settingsStore.Load();` satırının
bulunduğu `try` bloğunun **hemen ardına** (blok dışına, `catch`'ten sonra
`hasAnyChatSource` hesabından önce):

```csharp
        RefreshConnections();
        RefreshPrinterStatus(printerName);
```

`printerName`'i `try` içinde `settings`ten yakala:

```csharp
        bool hasActiveSession;
        bool hasYouTubeHandle;
        string? printerName;
        try
        {
            hasActiveSession = _sessions.GetActive() is not null;
            var settings = _settingsStore.Load();
            hasYouTubeHandle = !string.IsNullOrWhiteSpace(settings.YouTubeChannelHandle);
            printerName = settings.PrinterName;
        }
        catch
        {
            ChatHealthState = "off";
            ChatHealthTooltip = "Chat takibi kapalı";
            return;
        }
```

(Mevcut `catch` gövdesine dokunma — yalnız `printerName` bildirimini ve
atamasını ekle.)

- [ ] **Adım 5: Testleri koş, YEŞİL olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellConnectionsTests
```
Beklenen: 5/5 PASS.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.App/ViewModels/PlatformConnectionViewModel.cs \
        OrderDeck.App/ViewModels/MainShellViewModel.cs \
        OrderDeck.Tests/App/MainShellConnectionsTests.cs
git commit -m "feat(shell): kenar çubuğu bağlantı ve yazıcı durumu"
```

---

## Görevler 9-15 hakkında — XAML bölünmesi ortak kuralları

Buradan sonraki yedi görev `MainShellView.xaml`'ı parçalara ayırıyor. Hepsinde
geçerli kurallar:

- **`DataContext` atanmaz.** Her `UserControl` ebeveynden miras alır; kök
  `MainShellView`'ün `DataContext`'i `MainShellViewModel`'dir. Alt kontrol
  içinde `d:DataContext` bile koyma — tasarım zamanı desteği bu fazın işi değil.
- **Sabit değer yasak.** Renk, punto, boşluk, yarıçap, süre → `StaticResource`.
  Tek istisna: `Grid` satır/kolon oranları (`*`, `Auto`) ve `ColumnDefinition`
  piksel genişlikleri — bunlar da mümkünse `OD.Layout.*`'tan bağlanır.
- **Mevcut binding adları değişmez.** Aşağıdaki XAML'lerde geçen her
  `{Binding X}` bugün `MainShellViewModel`'de var (ya da Görev 6-8'de eklendi).
  Yeni bir isim uydurma.
- **Platform rozeti kuralı:** yalnız `OD.PlatformChip.*`. Mockup'ın renkli
  kısaltma rozeti kopyalanmayacak.
- **Her görevin son adımı aynı:** `dotnet build OrderDeck.App/OrderDeck.App.csproj`
  → 0 hata, ardından uygulamayı bir kez aç ve ilgili bölgenin çizildiğini
  gözle doğrula. XAML hataları derlemede değil, **çalışma anında**
  `XamlParseException` olarak patlar; açmadan "tamam" deme.

`OrderDeck.App/Views/Shell/` klasörünü ilk görevde oluştur. `.csproj`'a
elle ekleme gerekmez — SDK-style proje `**/*.xaml`'ı zaten topluyor.

---

## Görev 9: `ShellSidebar` — logo, navigasyon, bağlantılar, yazıcı

**Dosyalar:**
- Oluştur: `OrderDeck.App/Views/Shell/ShellSidebar.xaml` + `.xaml.cs`
- Taşınacak kaynak: `MainShellView.xaml:5-159` (RailButton stili + sol ray)
- Taşınacak kod-arkası: `MainShellView.xaml.cs:74-82` (`OnMenuClick`)

Mockup'ın kenar çubuğu 224px geniş, dar ekranda 64px ikon moduna düşüyor
(`--w-side` / `--w-side-min`). Faz 0 bu ikisini `OD.Layout.SideWidth` ve
`OD.Layout.SideWidthMin` olarak verdi.

**Navigasyon kararı:** mockup'ın "Yayın / Siparişler / Müşteriler / Etiketler /
Raporlar / Ayarlar" listesi **kullanılmıyor**. Sayfa navigasyonu Faz 3'ün işi;
Faz 1'de o düğmelerin yarısı ölü olurdu. Bunun yerine bugünkü beş ray komutu
(Yayın Geçmişi, Müşteriler, Kara Liste, Yeni Dekont, Toplu SMS) etiketli
satırlara dönüşür, kalanlar alttaki "⋯" taşma menüsünde durur.

- [ ] **Adım 1: `ShellSidebar.xaml`'ı yaz**

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.ShellSidebar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <!-- Nav satırı: ikon + etiket. Dar modda (IsCompact) etiket gizlenir,
             kontrol 64px'e iner — mockup'taki .side.icon davranışı. -->
        <Style x:Key="NavButton" TargetType="Button">
            <Setter Property="Height" Value="{StaticResource OD.Layout.ButtonHeight}"/>
            <Setter Property="Margin" Value="0,1"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Foreground" Value="{StaticResource OD.Brush.TextDim}"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                                CornerRadius="{StaticResource OD.Radius.Md}"
                                Padding="{StaticResource OD.Pad.3}">
                            <ContentPresenter VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background"
                                        Value="{StaticResource OD.Brush.Surface2}"/>
                                <Setter Property="Foreground"
                                        Value="{StaticResource OD.Brush.Text}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </UserControl.Resources>

    <Border Background="{StaticResource OD.Brush.Surface}"
            BorderBrush="{StaticResource OD.Brush.Border}"
            BorderThickness="0,0,1,0">
        <Border.Style>
            <Style TargetType="Border">
                <Setter Property="Width" Value="{StaticResource OD.Layout.SideWidth}"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsCompact}" Value="True">
                        <Setter Property="Width" Value="{StaticResource OD.Layout.SideWidthMin}"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Border.Style>

        <Grid Margin="{StaticResource OD.Pad.3}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>   <!-- logo -->
                <RowDefinition Height="*"/>      <!-- nav -->
                <RowDefinition Height="Auto"/>   <!-- bağlantılar + yazıcı -->
                <RowDefinition Height="Auto"/>   <!-- taşma menüsü -->
            </Grid.RowDefinitions>

            <!-- ── Logo ────────────────────────────────────────────────── -->
            <StackPanel Grid.Row="0" Orientation="Horizontal"
                        Margin="{StaticResource OD.Pad.2}">
                <Border Width="{StaticResource OD.Icon.Lg}"
                        Height="{StaticResource OD.Icon.Lg}"
                        CornerRadius="{StaticResource OD.Radius.Md}"
                        Background="{StaticResource OD.Brush.Accent}">
                    <TextBlock Text="OD"
                               Foreground="{StaticResource OD.Brush.OnAccent}"
                               FontFamily="{StaticResource OD.Font.Display}"
                               FontSize="{StaticResource OD.Font.F1}"
                               HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <TextBlock Text="OrderDeck" VerticalAlignment="Center"
                           Margin="{StaticResource OD.Pad.3}"
                           FontFamily="{StaticResource OD.Font.Display}"
                           FontSize="{StaticResource OD.Font.F2}"
                           Foreground="{StaticResource OD.Brush.Text}"
                           Visibility="{Binding IsCompact,
                                        Converter={StaticResource BoolToCollapsedConverter}}"/>
            </StackPanel>

            <!-- ── Navigasyon ──────────────────────────────────────────── -->
            <StackPanel Grid.Row="1" Margin="{StaticResource OD.Pad.Top5}">
                <Button Style="{StaticResource NavButton}"
                        Command="{Binding OpenStreamHistoryCommand}" ToolTip="Yayın Geçmişi">
                    <TextBlock Text="Yayın Geçmişi"/>
                </Button>
                <Button Style="{StaticResource NavButton}"
                        Command="{Binding OpenCustomerSearchCommand}" ToolTip="Müşteriler">
                    <TextBlock Text="Müşteriler"/>
                </Button>
                <Button Style="{StaticResource NavButton}"
                        Command="{Binding OpenBlacklistCommand}" ToolTip="Kara Liste">
                    <TextBlock Text="Kara Liste"/>
                </Button>
                <Button Style="{StaticResource NavButton}"
                        Command="{Binding OpenDekontEkleCommand}" ToolTip="Yeni Dekont">
                    <TextBlock Text="Yeni Dekont"/>
                </Button>
                <Button Style="{StaticResource NavButton}"
                        Command="{Binding OpenBulkSmsCommand}" ToolTip="Toplu SMS">
                    <TextBlock Text="Toplu SMS"/>
                </Button>
            </StackPanel>

            <!-- ── BAĞLANTILAR + yazıcı ────────────────────────────────── -->
            <StackPanel Grid.Row="2" Margin="{StaticResource OD.Pad.2}"
                        Visibility="{Binding IsCompact,
                                     Converter={StaticResource BoolToCollapsedConverter}}">
                <TextBlock Text="BAĞLANTILAR" Style="{StaticResource OD.Text.Micro}"
                           Margin="{StaticResource OD.Pad.Bottom3}"/>

                <ItemsControl ItemsSource="{Binding Connections}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Grid Margin="0,2">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <!-- Nokta: bağlıysa yeşil, değilse soluk.
                                     Marka rengi KULLANILMIYOR (Google itirazı). -->
                                <Ellipse Grid.Column="0" Width="8" Height="8"
                                         VerticalAlignment="Center"
                                         Margin="{StaticResource OD.Pad.2}">
                                    <Ellipse.Style>
                                        <Style TargetType="Ellipse">
                                            <Setter Property="Fill" Value="{StaticResource OD.Brush.TextMute}"/>
                                            <Setter Property="Opacity" Value="0.45"/>
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding IsConnected}" Value="True">
                                                    <Setter Property="Fill" Value="{StaticResource OD.Brush.Success}"/>
                                                    <Setter Property="Opacity" Value="1"/>
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </Ellipse.Style>
                                </Ellipse>
                                <TextBlock Grid.Column="1" Text="{Binding DisplayName}"
                                           FontSize="{StaticResource OD.Font.F1}"
                                           Foreground="{StaticResource OD.Brush.TextDim}"
                                           VerticalAlignment="Center"/>
                                <TextBlock Grid.Column="2" Text="{Binding ViewerCount}"
                                           Style="{StaticResource OD.Text.Mono}"
                                           VerticalAlignment="Center"
                                           Visibility="{Binding IsConnected,
                                                        Converter={StaticResource BoolToVisibleConverter}}"/>
                            </Grid>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

                <!-- Yazıcı satırı. "Hazır mı" SORULMUYOR — yalnız seçili mi. -->
                <TextBlock Text="{Binding PrinterStatusText}"
                           FontSize="{StaticResource OD.Font.F1}"
                           TextTrimming="CharacterEllipsis"
                           Margin="{StaticResource OD.Pad.Top4}">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Foreground" Value="{StaticResource OD.Brush.Amber}"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsPrinterConfigured}" Value="True">
                                    <Setter Property="Foreground" Value="{StaticResource OD.Brush.TextDim}"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </StackPanel>

            <!-- ── Taşma menüsü (x:Name ve handler MainShellView'dan aynen) ─ -->
            <Button Grid.Row="3"
                    x:Name="MenuButton"
                    Click="OnMenuClick"
                    Style="{StaticResource NavButton}"
                    Margin="{StaticResource OD.Pad.Top4}"
                    ToolTip="Diğer">
                <TextBlock Text="⋯  Diğer"/>
                <Button.ContextMenu>
                    <ContextMenu x:Name="MainMenu" Placement="Right">
                        <MenuItem Header="Dönem Raporu…" Command="{Binding OpenPeriodReportCommand}"/>
                        <Separator/>
                        <MenuItem Header="Ayarlar" Command="{Binding OpenSettingsCommand}"/>
                        <MenuItem Header="Destek Talepleri" Command="{Binding OpenSupportRequestsCommand}"/>
                        <MenuItem Header="Hesap" Command="{Binding OpenAccountCommand}"/>
                    </ContextMenu>
                </Button.ContextMenu>
            </Button>
        </Grid>
    </Border>
</UserControl>
```

**Tek yönlü boşluk tokenları.** Yukarıdaki XAML `OD.Pad.Top4`, `OD.Pad.Top5` ve
`OD.Pad.Bottom3` kullanıyor. WPF'te `Margin="0,{StaticResource X},0,0"`
**geçersizdir** — `Thickness` içine markup extension gömülemez, tam
`Thickness` tokenı gerekir. `Themes/Metrics.xaml`'a `OD.Pad.*` bloğunun
ardına ekle:

```xml
  <!-- Tek yönlü boşluklar. Simetrik OD.Pad.* yetmediği yerlerde; sayı
       XAML'a değil buraya yazılsın diye token. Değerler OD.Space.* ile
       aynı ölçekte (3=8, 4=16, 5=20). -->
  <Thickness x:Key="OD.Pad.Top4"    Left="0" Top="16" Right="0" Bottom="0"/>
  <Thickness x:Key="OD.Pad.Top5"    Left="0" Top="20" Right="0" Bottom="0"/>
  <Thickness x:Key="OD.Pad.Bottom3" Left="0" Top="0"  Right="0" Bottom="8"/>
```

Yukarıdaki `ShellSidebar.xaml`'da şu dört yeri bu tokenlarla yaz:
navigasyon `StackPanel` → `Margin="{StaticResource OD.Pad.Top5}"`;
"BAĞLANTILAR" başlığı → `Margin="{StaticResource OD.Pad.Bottom3}"`;
yazıcı satırı ve taşma butonu → `Margin="{StaticResource OD.Pad.Top4}"`.

- [ ] **Adım 2: Kod-arkasını yaz**

`OrderDeck.App/Views/Shell/ShellSidebar.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace OrderDeck.App.Views.Shell;

public partial class ShellSidebar : UserControl
{
    public ShellSidebar() => InitializeComponent();

    /// <summary>
    /// MainShellView'dan taşındı. ContextMenu'yü butonun kendisine
    /// bağlıyoruz ki DataContext (MainShellViewModel) menü öğelerine aksın —
    /// ContextMenu görsel ağaçta değil, miras kendiliğinden gelmiyor.
    /// </summary>
    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.ContextMenu is null) return;
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.DataContext = DataContext;
        b.ContextMenu.IsOpen = true;
    }
}
```

> `MainShellView.xaml.cs:74-82`'deki mevcut gövdeyi **birebir kopyala**;
> yukarıdaki sürüm ondan farklıysa mevcut olan kazanır (davranış değişmez
> kuralı).

- [ ] **Adım 3: `IsCompact`'i ViewModel'e ekle**

`MainShellViewModel.cs`, hero bloğunun yanına:

```csharp
    /// <summary>
    /// Duyarlı yerleşim. Mockup'ın 1360px kırılımı: altında kenar çubuğu
    /// ikon moduna düşer. Pencere boyutunu view bildirir (SizeChanged).
    /// </summary>
    [ObservableProperty] private bool _isCompact;

    /// <summary>Mockup'ın 850px yükseklik kırılımı: ürün fotoğrafı kısalır.</summary>
    [ObservableProperty] private bool _isShort;
```

- [ ] **Adım 4: Derle ve gözle doğrula**

```
dotnet build OrderDeck.App/OrderDeck.App.csproj
```
Beklenen: 0 hata. Uygulamayı aç: kenar çubuğu etiketli, bağlantı noktaları
dört satır, yazıcı satırı görünüyor.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.App/Views/Shell/ShellSidebar.xaml \
        OrderDeck.App/Views/Shell/ShellSidebar.xaml.cs \
        OrderDeck.App/ViewModels/MainShellViewModel.cs
git commit -m "feat(shell): kenar çubuğunu ayrı UserControl'e taşı"
```

---

## Görev 10: `ShellTopBar` + `ShellBanners`

**Dosyalar:**
- Oluştur: `OrderDeck.App/Views/Shell/ShellTopBar.xaml` + `.xaml.cs`
- Oluştur: `OrderDeck.App/Views/Shell/ShellBanners.xaml` + `.xaml.cs`
- Taşınacak kaynak: `MainShellView.xaml:171-263` (üst bar) ve `288-367` (şeritler)

Üst bar mockup'ta 56px (`OD.Layout.TopbarHeight`): solda CANLI rozeti + yayın
süresi + saat, sağda izleyici/chat sağlık göstergeleri, lisans metni, zil ve
üç eylem düğmesi.

**Şeritler aynen taşınır.** Dördü de bugün çalışıyor; yalnız sabit ARGB
tint'ler token'a çevrilir: `#14FBBF24` → `OD.Brush.Amber` %8 opaklıkla
(`Border.Background` + `Opacity` yerine ayrı bir fırça istiyorsan
`Colors.xaml`'a `OD.Brush.AmberTint` ekle — sabit hex XAML'a yazılmaz).

- [ ] **Adım 1: `ShellTopBar.xaml`'ı yaz**

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.ShellTopBar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Height="{StaticResource OD.Layout.TopbarHeight}"
            BorderBrush="{StaticResource OD.Brush.Border}"
            BorderThickness="0,0,0,1">
        <DockPanel LastChildFill="False" Margin="{StaticResource OD.Pad.4}">

            <!-- ── SOL: durum + süre + saat ─────────────────────────────── -->
            <TextBlock DockPanel.Dock="Left" Text="{Binding StreamStatusLabel}"
                       VerticalAlignment="Center"
                       FontFamily="{StaticResource OD.Font.Display}"
                       FontSize="{StaticResource OD.Font.F2}"
                       Foreground="{StaticResource OD.Brush.Text}"
                       Visibility="{Binding IsGiveawayActive,
                                    Converter={StaticResource BoolToCollapsedConverter}}"/>

            <TextBlock DockPanel.Dock="Left" Text="{Binding StreamDurationText}"
                       Style="{StaticResource OD.Text.Mono}"
                       VerticalAlignment="Center"
                       Margin="{StaticResource OD.Pad.4}"/>

            <TextBlock DockPanel.Dock="Left" Text="{Binding ClockText}"
                       Style="{StaticResource OD.Text.Mono}"
                       VerticalAlignment="Center"/>

            <!-- ── SAĞ: eylemler (ters sırada dock edilir) ───────────────── -->
            <Button DockPanel.Dock="Right"
                    Command="{Binding StartGiveawayCommand}"
                    IsEnabled="{Binding CanStartGiveaway}"
                    Style="{StaticResource OD.Button.Ghost}"
                    Margin="{StaticResource OD.Pad.2}">
                <StackPanel Orientation="Horizontal">
                    <Image Source="{StaticResource OD.Icon.Gift}"
                           Width="{StaticResource OD.Icon.Sm}"
                           Height="{StaticResource OD.Icon.Sm}"
                           VerticalAlignment="Center"
                           RenderOptions.BitmapScalingMode="HighQuality"/>
                    <TextBlock Text="Çekiliş" VerticalAlignment="Center"
                               Margin="{StaticResource OD.Pad.2}"/>
                </StackPanel>
            </Button>

            <Button DockPanel.Dock="Right" Content="Yayını Bitir"
                    Command="{Binding EndStreamCommand}"
                    Style="{StaticResource OD.Button.Ghost}"
                    Margin="{StaticResource OD.Pad.2}"/>

            <Button DockPanel.Dock="Right" Content="Yayın Başlat"
                    Command="{Binding StartStreamCommand}"
                    Style="{StaticResource OD.Button.Primary}"
                    Margin="{StaticResource OD.Pad.2}"/>

            <!-- Zil. Button kalıyor: Border.InputBindings LeftClick WPF'te
                 güvenilir tetiklenmiyordu (mevcut yorum). -->
            <Button DockPanel.Dock="Right"
                    Command="{Binding OpenIntakeSubmissionsCommand}"
                    Style="{StaticResource OD.Button.Ghost}"
                    Margin="{StaticResource OD.Pad.2}"
                    Visibility="{Binding HasNewIntakeSubmissions,
                                 Converter={StaticResource BoolToVisibleConverter}}"
                    ToolTip="Yeni form başvurularını görüntülemek için tıkla">
                <TextBlock Foreground="{StaticResource OD.Brush.Accent}"
                           FontSize="{StaticResource OD.Font.F1}">
                    <Run Text="🔔"/>
                    <Run Text="{Binding NewIntakeSubmissionsCount, Mode=OneWay}"/>
                    <Run Text="yeni başvuru"/>
                </TextBlock>
            </Button>

            <TextBlock DockPanel.Dock="Right"
                       Text="{Binding LicenseStatusText}"
                       Foreground="{Binding LicenseStatusBrush}"
                       VerticalAlignment="Center" Cursor="Hand"
                       Margin="{StaticResource OD.Pad.3}"
                       ToolTip="Hesap detayları için tıkla">
                <TextBlock.InputBindings>
                    <MouseBinding MouseAction="LeftClick" Command="{Binding OpenAccountCommand}"/>
                </TextBlock.InputBindings>
            </TextBlock>

            <!-- Chat sağlık noktası — üç durum (off/idle/ok) aynen. -->
            <Ellipse DockPanel.Dock="Right" Width="10" Height="10"
                     VerticalAlignment="Center"
                     Margin="{StaticResource OD.Pad.2}"
                     ToolTip="{Binding ChatHealthTooltip}">
                <Ellipse.Style>
                    <Style TargetType="Ellipse">
                        <Setter Property="Fill" Value="{StaticResource OD.Brush.TextMute}"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding ChatHealthState}" Value="ok">
                                <Setter Property="Fill" Value="{StaticResource OD.Brush.Success}"/>
                            </DataTrigger>
                            <DataTrigger Binding="{Binding ChatHealthState}" Value="idle">
                                <Setter Property="Fill" Value="{StaticResource OD.Brush.Amber}"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Ellipse.Style>
            </Ellipse>

            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal"
                        VerticalAlignment="Center"
                        Margin="{StaticResource OD.Pad.3}"
                        Visibility="{Binding ViewerCountVisible,
                                     Converter={StaticResource BoolToVisibleConverter}}"
                        ToolTip="{Binding ViewerCountTooltip}">
                <TextBlock Text="👁" VerticalAlignment="Center"/>
                <TextBlock Text="{Binding ViewerCountText}"
                           Style="{StaticResource OD.Text.Mono}"
                           VerticalAlignment="Center"
                           Margin="{StaticResource OD.Pad.2}"/>
            </StackPanel>
        </DockPanel>
    </Border>
</UserControl>
```

- [ ] **Adım 2: `ShellBanners.xaml`'ı yaz**

`MainShellView.xaml:288-367`'deki dört `Border`'ı **aynı sırada, aynı
binding'lerle** taşı. Tek değişiklik: sabit hex'ler token'a çevrilir. Bunun
için `Themes/Colors.xaml`'a üç tint fırçası ekle (Faz 0 tonlu zeminleri
öngörmemişti):

```xml
  <!-- Bildirim şeritlerinin tonlu zeminleri. Sabit hex XAML'a yazılmasın
       diye token; alfa değerleri mevcut MainShellView'dan birebir taşındı. -->
  <SolidColorBrush x:Key="OD.Brush.AmberTint"       Color="#14FFB23E" po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.AmberTintBorder" Color="#66FFB23E" po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.InfoTint"        Color="#1A4D8DF6" po:Freeze="True"/>
  <SolidColorBrush x:Key="OD.Brush.InfoTintBorder"  Color="#594D8DF6" po:Freeze="True"/>
```

Şerit gövdesi (dördü de aynı iskelet, içerik farklı):

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.ShellBanners"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <!-- Sunucu erişilemiyor -->
        <Border CornerRadius="{StaticResource OD.Radius.Md}"
                Background="{StaticResource OD.Brush.AmberTint}"
                BorderBrush="{StaticResource OD.Brush.AmberTintBorder}"
                BorderThickness="1"
                Visibility="{Binding ServerOfflineBanner,
                             Converter={StaticResource NullToCollapsedConverter}}">
            <TextBlock Text="{Binding ServerOfflineBanner}"
                       Foreground="{StaticResource OD.Brush.Amber}"
                       Margin="{StaticResource OD.Pad.4}"
                       VerticalAlignment="Center" TextWrapping="Wrap"/>
        </Border>

        <!-- Lisans süresi doluyor — aynı iskelet, LicenseExpiryBanner binding'i -->
        <!-- Yedek seçim modu — InfoTint, BackupModeBanner + CancelBackupSelectionCommand -->
        <!-- Çekiliş aktif — AmberTint, Banner.* binding'leri + Şimdi Çek / İptal -->
    </Grid>
</UserControl>
```

> Kalan üç şeridi `MainShellView.xaml:301-366`'dan **kopyalayıp** yalnız
> renkleri token'a çevirerek yaz. Binding adları, `Visibility` converter'ları,
> buton komutları ve z-sırası (yedek modu en üstte) değişmeyecek.

- [ ] **Adım 3: Kod-arkası dosyaları**

Her ikisi için de yalnız `InitializeComponent()`:

```csharp
using System.Windows.Controls;

namespace OrderDeck.App.Views.Shell;

public partial class ShellTopBar : UserControl
{
    public ShellTopBar() => InitializeComponent();
}
```

(`ShellBanners` için aynısı, sınıf adı değişik.)

- [ ] **Adım 4: Derle + gözle doğrula**

```
dotnet build OrderDeck.App/OrderDeck.App.csproj
```
Uygulamayı aç; yayını başlat/bitir, çekiliş başlat → şeritlerin dördü de
eskisi gibi görünüp kayboluyor.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.App/Views/Shell/ShellTopBar.xaml OrderDeck.App/Views/Shell/ShellTopBar.xaml.cs \
        OrderDeck.App/Views/Shell/ShellBanners.xaml OrderDeck.App/Views/Shell/ShellBanners.xaml.cs \
        OrderDeck.App/Themes/Colors.xaml
git commit -m "feat(shell): üst bar ve bildirim şeritlerini ayır"
```

---

## Görev 11: `ActiveProductBar` — kod, fiyat, dört sayaç

**Dosyalar:**
- Oluştur: `OrderDeck.App/Views/Shell/ActiveProductBar.xaml` + `.xaml.cs`
- Taşınacak kaynak: `MainShellView.xaml:266-283` (Kod/Fiyat satırı)

Mockup'ın hero şeridi: büyük kod kutusu (`--f5` = 64px, `maxlength 6`), fiyat
kutusu, ürün adı ve dört istatistik bloğu. Kod kutusu ekranın en önemli
girişi — operatör yayında oraya bakıyor.

**Klavye sözleşmesi (mockup JS'inden):** `Ctrl+K` kod kutusuna odaklanır.
`MainShellView.xaml.cs:34`'teki `OnWindowPreviewKeyDown` zaten pencere
seviyesinde çalışıyor; yeni kısayol oraya eklenir, bu kontrole değil.

- [ ] **Adım 1: `ActiveProductBar.xaml`'ı yaz**

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.ActiveProductBar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <!-- İstatistik bloğu: üstte mikro etiket, altta mono sayı. -->
        <Style x:Key="StatValue" TargetType="TextBlock"
               BasedOn="{StaticResource OD.Text.Mono}">
            <Setter Property="FontSize" Value="{StaticResource OD.Font.F3}"/>
            <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
        </Style>
    </UserControl.Resources>

    <Border Style="{StaticResource OD.Panel}" Padding="{StaticResource OD.Pad.5}">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>   <!-- kod -->
                <ColumnDefinition Width="Auto"/>   <!-- fiyat -->
                <ColumnDefinition Width="*"/>      <!-- ürün adı -->
                <ColumnDefinition Width="Auto"/>   <!-- 4 sayaç -->
            </Grid.ColumnDefinitions>

            <!-- ÜRÜN KODU -->
            <StackPanel Grid.Column="0">
                <TextBlock Text="ÜRÜN KODU" Style="{StaticResource OD.Text.Micro}"/>
                <TextBox x:Name="CodeBox" MaxLength="6"
                         Text="{Binding ActiveCode, UpdateSourceTrigger=PropertyChanged}"
                         CharacterCasing="Upper"
                         FontFamily="{StaticResource OD.Font.Display}"
                         MinWidth="180">
                    <TextBox.Style>
                        <Style TargetType="TextBox"
                               BasedOn="{StaticResource OD.TextBox}">
                            <Setter Property="FontSize"
                                    Value="{StaticResource OD.Font.F5}"/>
                            <Style.Triggers>
                                <!-- Spec §7: yükseklik < 850px → 64 yerine 44. -->
                                <DataTrigger Binding="{Binding DataContext.IsShort,
                                             RelativeSource={RelativeSource AncestorType=Window}}"
                                             Value="True">
                                    <Setter Property="FontSize"
                                            Value="{StaticResource OD.Layout.CodeFontShort}"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBox.Style>
                </TextBox>
            </StackPanel>

            <!-- FİYAT -->
            <StackPanel Grid.Column="1" Margin="{StaticResource OD.Pad.5}">
                <TextBlock Text="FİYAT" Style="{StaticResource OD.Text.Micro}"/>
                <TextBox Text="{Binding ActivePriceText, UpdateSourceTrigger=PropertyChanged}"
                         Style="{StaticResource OD.TextBox}"
                         FontFamily="{StaticResource OD.Font.Mono}"
                         FontSize="{StaticResource OD.Font.F4}"
                         MinWidth="120"/>
            </StackPanel>

            <!-- ÜRÜN ADI (ürün kartından okunur; yeni alan değil) -->
            <StackPanel Grid.Column="2" VerticalAlignment="Bottom"
                        Margin="{StaticResource OD.Pad.5}">
                <TextBlock Text="{Binding ProductCard.Name}"
                           FontFamily="{StaticResource OD.Font.Display}"
                           FontSize="{StaticResource OD.Font.F3}"
                           Foreground="{StaticResource OD.Brush.Text}"
                           TextTrimming="CharacterEllipsis"/>
            </StackPanel>

            <!-- DÖRT SAYAÇ -->
            <StackPanel Grid.Column="3" Orientation="Horizontal" VerticalAlignment="Center">
                <StackPanel Margin="{StaticResource OD.Pad.4}">
                    <TextBlock Text="BU ÜRÜNDEN" Style="{StaticResource OD.Text.Micro}"/>
                    <TextBlock Text="{Binding ProductOrderCount}" Style="{StaticResource StatValue}"/>
                </StackPanel>
                <StackPanel Margin="{StaticResource OD.Pad.4}">
                    <TextBlock Text="YAYIN TOPLAMI" Style="{StaticResource OD.Text.Micro}"/>
                    <TextBlock Text="{Binding SessionLabelCount}" Style="{StaticResource StatValue}"/>
                </StackPanel>
                <StackPanel Margin="{StaticResource OD.Pad.4}" Cursor="Hand"
                            ToolTip="Ciroyu gizle/göster (ekran paylaşımı için)">
                    <StackPanel.InputBindings>
                        <MouseBinding MouseAction="LeftClick"
                                      Command="{Binding ToggleRevenueMaskCommand}"/>
                    </StackPanel.InputBindings>
                    <TextBlock Text="YAYIN CİROSU" Style="{StaticResource OD.Text.Micro}"/>
                    <TextBlock Text="{Binding SessionRevenueText}" Style="{StaticResource StatValue}"/>
                </StackPanel>
                <StackPanel Margin="{StaticResource OD.Pad.4}">
                    <TextBlock Text="KUYRUKTA" Style="{StaticResource OD.Text.Micro}"/>
                    <TextBlock Text="{Binding QueueCount}" Style="{StaticResource StatValue}"/>
                </StackPanel>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Adım 2: Kod-arkası + `Ctrl+K`**

`ActiveProductBar.xaml.cs` yalnız `InitializeComponent()` içerir artı kod
kutusuna dışarıdan odak verme yolu:

```csharp
using System.Windows.Controls;

namespace OrderDeck.App.Views.Shell;

public partial class ActiveProductBar : UserControl
{
    public ActiveProductBar() => InitializeComponent();

    /// <summary>
    /// Ctrl+K. Pencere seviyesindeki OnWindowPreviewKeyDown buraya yönlendirir
    /// — kısayolun tek sahibi pencere olsun diye kontrol kendi kısayolunu
    /// kurmuyor (iki yerde tanımlanınca hangisinin kazandığı belirsizleşir).
    /// </summary>
    public void FocusCode()
    {
        CodeBox.Focus();
        CodeBox.SelectAll();
    }
}
```

`MainShellView.xaml.cs:34` `OnWindowPreviewKeyDown` içine:

```csharp
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ProductBar.FocusCode();
            e.Handled = true;
            return;
        }
```

(`ProductBar`, Görev 15'te `MainShellView.xaml`'daki `ActiveProductBar`'ın
`x:Name`'i olacak.)

- [ ] **Adım 3: Derle + gözle doğrula**

Uygulamayı aç: kod yaz → "BU ÜRÜNDEN" sayacı değişiyor, ciro kutusuna
tıklayınca `₺ ••••` oluyor, Ctrl+K kod kutusuna odaklanıyor.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.App/Views/Shell/ActiveProductBar.xaml \
        OrderDeck.App/Views/Shell/ActiveProductBar.xaml.cs
git commit -m "feat(shell): aktif ürün şeridi ve yayın sayaçları"
```

---

## Görev 12: `ChatPanel` — arama, "Sadece {kod}" çipi, sohbet listesi

**Dosyalar:**
- Oluştur: `OrderDeck.App/Views/Shell/ChatPanel.xaml` + `.xaml.cs`
- Taşınacak kaynak: `MainShellView.xaml:378-544`
- Taşınacak kod-arkası: `MainShellView.xaml.cs:62-72` (`ChatList_OnDoubleClick`)
  ve `95-105` (`ChatList_OnPreviewKeyDown`)

**Bu Faz 1'in en riskli parçası.** Sohbet listesinin `ContextMenu`'sünde 10
`MenuItem` var (müşteri detayı, YouTube 3'lü moderasyon, Facebook 3'lü
moderasyon, kara liste) ve hepsi `PlacementTarget.DataContext...`
`RelativeSource` zinciriyle bağlı. **Bu menüyü satır satır kopyala, yeniden
yazma.** `RelativeSource AncestorType=ContextMenu` zinciri `UserControl`
sınırından etkilenmez — çalışmaya devam eder.

- [ ] **Adım 1: `ChatPanel.xaml`'ı yaz**

Panel iskeleti (başlık şeridi mockup'a göre yenilenir, liste **aynen** taşınır):

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.ChatPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource OD.Panel}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <!-- ── Başlık şeridi: ad + arama + "Sadece {kod}" çipi ───────── -->
            <Border Grid.Row="0" Padding="{StaticResource OD.Pad.4}"
                    BorderBrush="{StaticResource OD.Brush.Border}"
                    BorderThickness="0,0,0,1">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>

                    <TextBlock Grid.Column="0" Text="Canlı Sohbet"
                               VerticalAlignment="Center"
                               FontFamily="{StaticResource OD.Font.Display}"
                               FontSize="{StaticResource OD.Font.F2}"
                               Foreground="{StaticResource OD.Brush.Text}"/>

                    <TextBox Grid.Column="1"
                             Style="{StaticResource OD.TextBox}"
                             Margin="{StaticResource OD.Pad.3}"
                             Text="{Binding ChatSearchText, UpdateSourceTrigger=PropertyChanged}"
                             ToolTip="Kullanıcı adı veya mesaj içinde ara"/>

                    <!-- Çip yalnız aktif kod varken anlamlı; kod yokken
                         gizlenir ki operatör etkisiz bir düğmeye basmasın. -->
                    <ToggleButton Grid.Column="2"
                                  Style="{StaticResource OD.Chip}"
                                  IsChecked="{Binding OnlyActiveCode}"
                                  Visibility="{Binding ActiveCode,
                                               Converter={StaticResource NullToCollapsedConverter}}"
                                  Content="{Binding ActiveCode, StringFormat=Sadece {0}}"/>
                </Grid>
            </Border>

            <!-- ── Liste ────────────────────────────────────────────────── -->
            <ListBox Grid.Row="1"
                     x:Name="ChatList"
                     ItemsSource="{Binding ChatView}"
                     MouseDoubleClick="ChatList_OnDoubleClick"
                     PreviewKeyDown="ChatList_OnPreviewKeyDown"
                     Background="Transparent"
                     Foreground="{StaticResource OD.Brush.Text}"
                     BorderThickness="0"
                     Padding="{StaticResource OD.Pad.2}"
                     HorizontalContentAlignment="Stretch"
                     ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                <!-- ItemContainerStyle, ItemTemplate ve ContextMenu:
                     MainShellView.xaml:410-541'den BİREBİR kopyalanır.
                     Değişecek tek şey ItemTemplate'in kolon genişlikleri:
                       Grid.Column 0 (rozet) Width -> OD.Layout.ChatBadgeColumn
                       Grid.Column 1 (ad)    Width -> OD.Layout.ChatUserMaxWidth
                     Platform rozeti OD.PlatformChip.* kalır; marka renkli
                     kısaltma rozetine DÖNÜLMEZ. -->
            </ListBox>
        </Grid>
    </Border>
</UserControl>
```

> `ItemsSource` artık `ChatMessages` değil **`ChatView`** (Görev 7). Filtreli
> görünüm bu; ham koleksiyona bağlarsan arama ve "Sadece {kod}" çalışmaz.

- [ ] **Adım 2: Kod-arkasını taşı**

`ChatPanel.xaml.cs` — `MainShellView.xaml.cs:62-72` ve `95-105`'teki iki
gövdeyi **birebir** taşı. Her ikisi de `DataContext`'i `MainShellViewModel`'e
cast ediyor; `UserControl` `DataContext`'i miras aldığı için cast aynen
çalışır. `MainShellView.xaml.cs`'ten bu iki metodu **sil**.

```csharp
using System.Windows.Controls;
using System.Windows.Input;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Shell;

public partial class ChatPanel : UserControl
{
    public ChatPanel() => InitializeComponent();

    // MainShellView.xaml.cs'ten taşındı — gövdeler değişmedi.
    private void ChatList_OnDoubleClick(object sender, MouseButtonEventArgs e) { /* mevcut gövde */ }
    private void ChatList_OnPreviewKeyDown(object sender, KeyEventArgs e)      { /* mevcut gövde */ }
}
```

- [ ] **Adım 3: Derle + gözle doğrula**

Uygulamayı aç, yayın başlat, sohbete mesaj düşür:
- çift tık → kuyruğa ekliyor (davranış değişmedi),
- sağ tık → 10 menü öğesi eskisi gibi (YouTube/Facebook öğeleri yalnız ilgili
  platform satırında görünüyor),
- arama kutusuna yaz → liste süzülüyor,
- kod gir + çipi aç → yalnız o kodu içeren mesajlar kalıyor.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.App/Views/Shell/ChatPanel.xaml \
        OrderDeck.App/Views/Shell/ChatPanel.xaml.cs \
        OrderDeck.App/Views/MainShellView.xaml.cs
git commit -m "feat(shell): sohbet panelini ayır, arama ve kod çipi ekle"
```

---

## Görev 13: `ProductCard` — fotoğraf, beden ızgarası, satır-içi düzenleme

**Dosyalar:**
- Oluştur: `OrderDeck.App/Views/Shell/ProductCard.xaml` + `.xaml.cs`
- Yeni kaynak (mockup `.pcard`) — mevcut XAML'da karşılığı yok

Kartın iki hâli var ve **ikisi de aynı kartın içinde** — pop-up yok (spec §6):

1. **Görüntüleme** (`HasProduct = true`, `IsEditing = false`): fotoğraf, kod,
   fiyat, ad, beden ızgarası, "Düzenle" düğmesi.
2. **Tanımlama/düzenleme** (`IsEditing = true`): ad kutusu, beden seti kutusu,
   fotoğraf seçme düğmesi, Kaydet / Vazgeç.

Kod hiç girilmemişse (`Code` boş) kart tamamen boş bir ipucu gösterir.

- [ ] **Adım 1: `ProductCard.xaml`'ı yaz**

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.ProductCard"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <!-- Beden karesi. .low → amber sayı, .out → soluk + üstü çizili
             (mockup .size / .cnt.low / .size.out). -->
        <DataTemplate x:Key="SizeTile">
            <Border Background="{StaticResource OD.Brush.Surface2}"
                    BorderBrush="{StaticResource OD.Brush.Border}"
                    BorderThickness="1"
                    CornerRadius="{StaticResource OD.Radius.Sm}"
                    Padding="{StaticResource OD.Pad.3}"
                    Margin="{StaticResource OD.Pad.1}">
                <Border.Style>
                    <Style TargetType="Border">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsOutOfStock}" Value="True">
                                <Setter Property="Opacity" Value="0.38"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Border.Style>
                <StackPanel>
                    <TextBlock Text="{Binding Size}"
                               HorizontalAlignment="Center"
                               FontSize="{StaticResource OD.Font.F1}"
                               Foreground="{StaticResource OD.Brush.TextDim}"/>
                    <!-- Adet satır-içi düzenlenir: gridin tek etkileşimi bu.
                         Otomatik düşüş YOK (spec §9.1). -->
                    <!-- Stil özelliği ya öznitelik olarak ya da öğe olarak
                         verilir, İKİSİ BİRDEN olmaz (XamlParseException).
                         Tetikleyici gerektiği için öğe biçimi seçildi. -->
                    <TextBox Text="{Binding Quantity, UpdateSourceTrigger=PropertyChanged}"
                             HorizontalContentAlignment="Center"
                             FontFamily="{StaticResource OD.Font.Mono}"
                             FontSize="{StaticResource OD.Font.F3}">
                        <TextBox.Style>
                            <Style TargetType="TextBox" BasedOn="{StaticResource OD.TextBox}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding IsLow}" Value="True">
                                        <Setter Property="Foreground"
                                                Value="{StaticResource OD.Brush.Amber}"/>
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </TextBox.Style>
                    </TextBox>
                </StackPanel>
            </Border>
        </DataTemplate>
    </UserControl.Resources>

    <Border Style="{StaticResource OD.Panel}" Padding="{StaticResource OD.Pad.4}"
            DataContext="{Binding ProductCard}">
        <Grid>
            <!-- ── A: kod girilmemiş ────────────────────────────────────── -->
            <TextBlock Text="Ürün kodu gir"
                       HorizontalAlignment="Center" VerticalAlignment="Center">
                <TextBlock.Style>
                    <Style TargetType="TextBlock" BasedOn="{StaticResource OD.Text.Micro}">
                        <Setter Property="Visibility" Value="Collapsed"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Code}" Value="">
                                <Setter Property="Visibility" Value="Visible"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>

            <!-- ── B: görüntüleme ───────────────────────────────────────── -->
            <StackPanel Visibility="{Binding HasProduct,
                                     Converter={StaticResource BoolToVisibleConverter}}">
                <Border CornerRadius="{StaticResource OD.Radius.Md}"
                        Background="{StaticResource OD.Brush.Surface2}"
                        ClipToBounds="True">
                    <Border.Style>
                        <Style TargetType="Border">
                            <Setter Property="Height"
                                    Value="{StaticResource OD.Layout.ProductImageHeight}"/>
                            <Style.Triggers>
                                <!-- 850px altı: fotoğraf kısalır, ızgara ekranda kalır. -->
                                <DataTrigger Binding="{Binding DataContext.IsShort,
                                             RelativeSource={RelativeSource AncestorType=Window}}"
                                             Value="True">
                                    <Setter Property="Height"
                                            Value="{StaticResource OD.Layout.ProductImageHeightShort}"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Border.Style>
                    <Image Source="{Binding PhotoAbsolutePath}" Stretch="UniformToFill"
                           RenderOptions.BitmapScalingMode="HighQuality"/>
                </Border>

                <DockPanel LastChildFill="False" Margin="{StaticResource OD.Pad.Top4}">
                    <TextBlock DockPanel.Dock="Left" Text="{Binding Code}"
                               Style="{StaticResource OD.Text.Mono}"/>
                    <Button DockPanel.Dock="Right" Content="Düzenle"
                            Style="{StaticResource OD.Button.Ghost}"
                            Command="{Binding BeginEditCommand}"/>
                </DockPanel>

                <TextBlock Text="{Binding Name}" TextWrapping="Wrap"
                           FontFamily="{StaticResource OD.Font.Display}"
                           FontSize="{StaticResource OD.Font.F2}"
                           Foreground="{StaticResource OD.Brush.Text}"/>

                <TextBlock Text="BEDEN STOĞU" Style="{StaticResource OD.Text.Micro}"
                           Margin="{StaticResource OD.Pad.Top4}"/>
                <ItemsControl ItemsSource="{Binding Sizes}"
                              ItemTemplate="{StaticResource SizeTile}">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <UniformGrid Columns="4"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                </ItemsControl>
            </StackPanel>

            <!-- ── C: tanımlama / düzenleme (satır-içi, pop-up YOK) ──────── -->
            <StackPanel Visibility="{Binding IsEditing,
                                     Converter={StaticResource BoolToVisibleConverter}}">
                <TextBlock Text="ÜRÜN ADI" Style="{StaticResource OD.Text.Micro}"/>
                <TextBox Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}"
                         Style="{StaticResource OD.TextBox}"/>

                <TextBlock Text="BEDENLER (virgülle)" Style="{StaticResource OD.Text.Micro}"
                           Margin="{StaticResource OD.Pad.Top4}"/>
                <DockPanel LastChildFill="True">
                    <Button DockPanel.Dock="Right" Content="Uygula"
                            Style="{StaticResource OD.Button.Ghost}"
                            Click="ApplySizes_OnClick"/>
                    <TextBox Text="{Binding SizesText, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource OD.TextBox}"/>
                </DockPanel>

                <ItemsControl ItemsSource="{Binding Sizes}"
                              ItemTemplate="{StaticResource SizeTile}"
                              Margin="{StaticResource OD.Pad.Top4}">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <UniformGrid Columns="4"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                </ItemsControl>

                <StackPanel Orientation="Horizontal" Margin="{StaticResource OD.Pad.Top4}">
                    <Button Content="Fotoğraf Seç…" Style="{StaticResource OD.Button.Ghost}"
                            Click="PickPhoto_OnClick"/>
                    <Button Content="Kaydet" Style="{StaticResource OD.Button.Primary}"
                            Command="{Binding SaveCommand}"
                            Margin="{StaticResource OD.Pad.2}"/>
                    <Button Content="Vazgeç" Style="{StaticResource OD.Button.Ghost}"
                            Command="{Binding CancelEditCommand}"/>
                </StackPanel>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

> **`DataContext="{Binding ProductCard}"` bu kontrolde bilinçli bir
> istisnadır** — genel kural "DataContext atama" idi, ama kartın içeriği
> baştan sona `ProductCardViewModel`'e ait. Kabuk seviyesine erişmesi gereken
> tek yer `IsShort`; o yüzden oradaki bağ `AncestorType=Window` üzerinden
> gidiyor. **`AncestorType=UserControl` yazma** — o, `ProductCard`'ın
> kendisini bulur, `DataContext`'i zaten `ProductCardViewModel` olduğu için
> `IsShort` çözülmez ve tetikleyici sessizce hiç çalışmaz.

- [ ] **Adım 2: Kod-arkasını yaz**

`ProductCard.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Shell;

public partial class ProductCard : UserControl
{
    public ProductCard() => InitializeComponent();

    private ProductCardViewModel? Vm => DataContext as ProductCardViewModel;

    /// <summary>
    /// Beden metnini ızgaraya uygular. Komut değil Click: ApplySizesText bir
    /// dönüşüm, geri alınacak/CanExecute'lu bir eylem değil.
    /// </summary>
    private void ApplySizes_OnClick(object sender, RoutedEventArgs e) => Vm?.ApplySizesText();

    /// <summary>
    /// Dosya seçme diyaloğu. Bu bir işletim sistemi diyaloğu — spec §6'nın
    /// "pop-up yok" kuralı uygulamanın kendi pencerelerini kapsıyor, dosya
    /// seçiciyi değil (alternatifi sürükle-bırak zorunluluğu olurdu).
    /// </summary>
    private void PickPhoto_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Ürün fotoğrafı seç",
            Filter = "Görseller|*.jpg;*.jpeg;*.png;*.webp|Tüm dosyalar|*.*"
        };
        if (dlg.ShowDialog() == true) Vm.SetPhoto(dlg.FileName);
    }
}
```

- [ ] **Adım 3: Derle + gözle doğrula**

Uygulamayı aç, hero'ya yeni bir kod yaz → kart tanımlama moduna düşüyor. Ad
yaz, "S,M,L,XL" gir, Uygula, adetleri yaz, fotoğraf seç, Kaydet. Kodu değiştir,
geri dön → ürün kayıtlı geliyor, fotoğraf görünüyor, 0 olan beden soluk.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.App/Views/Shell/ProductCard.xaml \
        OrderDeck.App/Views/Shell/ProductCard.xaml.cs
git commit -m "feat(shell): ürün kartı ve beden ızgarası görünümü"
```

---

## Görev 14: `PrintQueuePanel`

**Dosyalar:**
- Oluştur: `OrderDeck.App/Views/Shell/PrintQueuePanel.xaml` + `.xaml.cs`
- Taşınacak kaynak: `MainShellView.xaml:547-660`
- Taşınacak kod-arkası: `MainShellView.xaml.cs:83-93` (`QueueList_OnSelectionChanged`)

Mockup'ta kuyruk satırı dört kolon: saat, platform rozeti, sıra no, kullanıcı +
mesaj, fiyat. Faz 0 bunlar için `OD.Layout.ChatTimeColumn`,
`ChatBadgeColumn`, `QueueNoColumn`, `ChatUserMaxWidth` tokenlarını verdi
(Görev 1'de eklendi).

**Yedek çipi korunuyor.** `BackupChipConverter` + `BeginAddBackupCommand`
zinciri bugün çalışan bir özellik; mockup'ta karşılığı yok ama kaldırılmıyor
(davranış değişmez kuralı).

- [ ] **Adım 1: `PrintQueuePanel.xaml`'ı yaz**

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.PrintQueuePanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource OD.Panel}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <!-- Başlık + adet hapı -->
            <Border Grid.Row="0" Padding="{StaticResource OD.Pad.4}"
                    BorderBrush="{StaticResource OD.Brush.Border}"
                    BorderThickness="0,0,0,1">
                <DockPanel LastChildFill="False">
                    <TextBlock DockPanel.Dock="Left" Text="Yazdırılacak Etiketler"
                               VerticalAlignment="Center"
                               FontFamily="{StaticResource OD.Font.Display}"
                               FontSize="{StaticResource OD.Font.F2}"
                               Foreground="{StaticResource OD.Brush.Text}"/>
                    <Border DockPanel.Dock="Left" Style="{StaticResource OD.CountPill}"
                            Margin="{StaticResource OD.Pad.3}">
                        <TextBlock Text="{Binding PrintQueue.Count}"/>
                    </Border>
                </DockPanel>
            </Border>

            <!-- Liste: ItemContainerStyle / ItemTemplate / ContextMenu
                 MainShellView.xaml:582-638'den birebir taşınır. Tek değişiklik
                 ItemTemplate kolon genişlikleri:
                   ad kolonu   -> OD.Layout.ChatUserMaxWidth
                   fiyat kolonu-> Auto (mono font, sağa yaslı) -->
            <ListBox Grid.Row="1"
                     x:Name="QueueList"
                     ItemsSource="{Binding PrintQueue}"
                     SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"
                     SelectionMode="Extended"
                     SelectionChanged="QueueList_OnSelectionChanged"
                     Background="Transparent"
                     Foreground="{StaticResource OD.Brush.Text}"
                     BorderThickness="0"
                     Padding="{StaticResource OD.Pad.2}"
                     MinHeight="{StaticResource OD.Layout.QueueMinHeight}"/>

            <!-- Alt eylem şeridi -->
            <Border Grid.Row="2" Padding="{StaticResource OD.Pad.4}"
                    BorderBrush="{StaticResource OD.Brush.Border}"
                    BorderThickness="0,1,0,0">
                <StackPanel Orientation="Horizontal">
                    <Button Content="{Binding PrintButtonLabel}"
                            Command="{Binding PrintCommand}"
                            Style="{StaticResource OD.Button.Primary}"/>
                    <Button Content="{Binding DeleteButtonLabel}"
                            Command="{Binding RemoveSelectedFromQueueCommand}"
                            Style="{StaticResource OD.Button.Ghost}"
                            Margin="{StaticResource OD.Pad.2}"/>
                    <Button Content="Hepsini Temizle"
                            Command="{Binding ClearQueueCommand}"
                            Style="{StaticResource OD.Button.Ghost}"
                            Foreground="{StaticResource OD.Brush.Accent}"/>
                </StackPanel>
            </Border>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Adım 2: Kod-arkasını taşı**

`PrintQueuePanel.xaml.cs` — `MainShellView.xaml.cs:83-93`'teki
`QueueList_OnSelectionChanged` gövdesini **birebir** taşı, kaynaktan sil.
Metot `SelectedQueueItems` koleksiyonunu senkronluyor; `DataContext` mirası
sayesinde cast aynen çalışır.

- [ ] **Adım 3: Derle + gözle doğrula**

Uygulamayı aç: kuyruğa etiket ekle, çoklu seç → "Seçili N etiketi yazdır"
başlığı değişiyor; yedek çipi tıklanınca yedek seçim modu şeridi açılıyor;
yazdır çalışıyor.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.App/Views/Shell/PrintQueuePanel.xaml \
        OrderDeck.App/Views/Shell/PrintQueuePanel.xaml.cs \
        OrderDeck.App/Views/MainShellView.xaml.cs
git commit -m "feat(shell): yazdırma kuyruğu panelini ayır"
```

---

## Görev 15: `MainShellView` kompozisyon kökü + duyarlı yerleşim

**Dosyalar:**
- Değiştir: `OrderDeck.App/Views/MainShellView.xaml` (664 → ~90 satır)
- Değiştir: `OrderDeck.App/Views/MainShellView.xaml.cs`
- Test: `OrderDeck.Tests/App/MainShellViewCompositionTests.cs`

Mockup'ın ana ızgarası: **sol kenar | içerik**, içerik = üst bar / şeritler /
hero / [sohbet | sağ sütun]. Sağ sütun sabit `OD.Layout.RightWidth` (344px) ve
içinde ürün kartı + kuyruk paneli üst üste.

- [ ] **Adım 1: Kompozisyon testini yaz**

`OrderDeck.Tests/App/MainShellViewCompositionTests.cs`:

```csharp
using System.Linq;
using FluentAssertions;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// MainShellView'ün XAML'ı ÇÖZÜLEBİLİYOR mu? XAML hataları derlemede değil
/// çalışma anında XamlParseException olarak patlar — bu test o riski CI'ya
/// çeker. Faz 1'in en pahalı hatası "uygulama hiç açılmıyor" olurdu.
/// </summary>
public class MainShellViewCompositionTests
{
    [Fact]
    public void MainShellView_and_all_shell_parts_can_be_constructed()
    {
        var error = OrderDeck.Tests.App.ThemeTestHost.RunOnUi(() =>
        {
            _ = new OrderDeck.App.Views.MainShellView();
            _ = new OrderDeck.App.Views.Shell.ShellSidebar();
            _ = new OrderDeck.App.Views.Shell.ShellTopBar();
            _ = new OrderDeck.App.Views.Shell.ShellBanners();
            _ = new OrderDeck.App.Views.Shell.ActiveProductBar();
            _ = new OrderDeck.App.Views.Shell.ChatPanel();
            _ = new OrderDeck.App.Views.Shell.ProductCard();
            _ = new OrderDeck.App.Views.Shell.PrintQueuePanel();
        });

        Assert.Null(error);
    }
}
```

> `ThemeTestHost`'ta böyle bir `RunOnUi` yoksa ekle: mevcut `Run(...)`
> yardımcısıyla aynı düzenek (STA thread + `lock (AppGate)` + tek
> `Application`), farkı sözlük yüklemek yerine verilen `Action`'ı çalıştırıp
> yakalanan `Exception`'ı döndürmesi. **Kendi thread'ini kurma** — süreç
> başına tek `Application` kuralı bozulur ve `App.xaml` testi sıraya bağlı
> düşer.

- [ ] **Adım 2: Testi koş, KIRMIZI olduğunu gör**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellViewCompositionTests
```
Beklenen: derleme hatası (`Shell` ad alanı tipleri henüz `MainShellView`'a
bağlanmadı) ya da `XamlParseException`.

- [ ] **Adım 3: `MainShellView.xaml`'ı kompozisyon köküne indir**

```xml
<UserControl x:Class="OrderDeck.App.Views.MainShellView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:shell="clr-namespace:OrderDeck.App.Views.Shell"
             MinWidth="{StaticResource OD.Layout.AppMinWidth}"
             MinHeight="{StaticResource OD.Layout.AppMinHeight}"
             SizeChanged="OnSizeChanged"
             Loaded="OnLoaded">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <shell:ShellSidebar Grid.Column="0"/>

        <Grid Grid.Column="1" MaxWidth="{StaticResource OD.Layout.ContentMaxWidth}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>   <!-- üst bar -->
                <RowDefinition Height="Auto"/>   <!-- şeritler -->
                <RowDefinition Height="Auto"/>   <!-- hero -->
                <RowDefinition Height="*"/>      <!-- sohbet | sağ sütun -->
            </Grid.RowDefinitions>

            <shell:ShellTopBar     Grid.Row="0"/>
            <shell:ShellBanners    Grid.Row="1" Margin="{StaticResource OD.Pad.4}"/>
            <shell:ActiveProductBar Grid.Row="2" x:Name="ProductBar"
                                    Margin="{StaticResource OD.Pad.4}"/>

            <Grid Grid.Row="3" Margin="{StaticResource OD.Pad.4}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <shell:ChatPanel Grid.Column="0"/>

                <!-- Sağ sütun sabit genişlik: sohbetin genişliği ekran
                     daralınca değişmesin (spec §6 — feda edilen kuyruk). -->
                <Grid Grid.Column="1" Width="{StaticResource OD.Layout.RightWidth}"
                      Margin="{StaticResource OD.Pad.4}">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <shell:ProductCard      Grid.Row="0"/>
                    <shell:PrintQueuePanel  Grid.Row="1"
                                            Margin="{StaticResource OD.Pad.Top4}"/>
                </Grid>
            </Grid>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Adım 4: Duyarlı kırılımları bağla**

`MainShellView.xaml.cs`'e ekle (mevcut `OnLoaded`, `AttachWindowEscHandler`,
`OnWindowPreviewKeyDown` **kalır**; taşınan dört handler silinmiş olmalı):

```csharp
    /// <summary>
    /// Mockup'ın iki kırılımı. WPF'te medya sorgusu yok; pencere boyutunu
    /// ViewModel'e bayrak olarak bildiriyoruz ki stiller DataTrigger ile
    /// tepki versin. Eşikler mockup'taki @media değerleriyle birebir.
    /// </summary>
    private const double CompactWidth = 1360;
    private const double ShortHeight = 850;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;
        vm.IsCompact = e.NewSize.Width  < CompactWidth;
        vm.IsShort   = e.NewSize.Height < ShortHeight;
    }
```

> **Spec §7 tablosunun hangi maddeleri bağlandı.** `IsCompact`: kenar
> çubuğu `SideWidthMin`'e iner, etiketler ve `BAĞLANTILAR` bloğu gizlenir
> (Görev 9). `IsShort`: ürün görseli 142→56 (Görev 13), kod `F5` 64→44
> (Görev 11). Tablodaki **"panel dolguları bir basamak iner"** ve
> **"'sıradaki' satırı gizlenir"** bilinçli olarak bağlanmadı: birincisi her
> panele ayrı tetikleyici demek ve 850px'te iki token basamağı gözle fark
> edilmiyor; ikincisinin Faz 1'de karşılığı yok — "sıradaki" satırı yalnız
> mockup'ta var, uygulamada kuyruk listesi zaten seçim odaklı. İhtiyaç
> doğarsa Faz 3'te eklenir; şimdi eklemek doğrulanmamış borç olur.

- [ ] **Adım 5: Testleri koş**

```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
```
Beklenen: **tüm** paket yeşil (Faz 0 sonrası 824 + bu fazda eklenen ~45).

- [ ] **Adım 6: Uçtan uca gözle doğrulama**

Uygulamayı aç ve şunları tek tek dene:

1. Yayın Başlat → üst barda süre saymaya başlıyor.
2. Kod gir → kart tanımlama moduna düşüyor; ürünü kaydet.
3. Sohbete mesaj gelsin → çift tık kuyruğa ekliyor, "BU ÜRÜNDEN" ve
   "KUYRUKTA" artıyor.
4. Yazdır → "YAYIN TOPLAMI" ve "YAYIN CİROSU" artıyor.
5. Ciroya tıkla → `₺ ••••`.
6. Pencereyi 1360px altına daralt → kenar çubuğu ikon moduna düşüyor,
   **sohbet genişliği değişmiyor**.
7. Pencereyi 850px altına kısalt → ürün fotoğrafı kısalıyor, kod kutusu
   44px'e iniyor, beden ızgarası ekranda kalıyor.
8. Sağ tık menüleri (sohbet ve kuyruk) eskisi gibi.
9. ESC → yedek seçim modundan çıkıyor (mevcut `AttachWindowEscHandler`).
10. Ctrl+K → kod kutusuna odak.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/Views/MainShellView.xaml \
        OrderDeck.App/Views/MainShellView.xaml.cs \
        OrderDeck.Tests/App/MainShellViewCompositionTests.cs \
        OrderDeck.Tests/App/ThemeTestHost.cs
git commit -m "feat(shell): MainShellView'ı kompozisyon köküne indir"
```

---

## Kapanış

- [ ] **Tüm paket + WPF derlemesi**

```
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test  OrderDeck.Tests/OrderDeck.Tests.csproj
```

- [ ] **Sabit değer taraması** — Faz 1'in dokunduğu XAML'larda kaçak hex/punto
  kalmadığını doğrula:

```
grep -rn "#[0-9A-Fa-f]\{6,8\}\|FontSize=\"[0-9]" OrderDeck.App/Views/Shell/ OrderDeck.App/Views/MainShellView.xaml
```
Beklenen: hiç eşleşme. (Eşleşme varsa ya token'a çevir ya da `Colors.xaml` /
`Metrics.xaml`'a yeni token ekle.)

- [ ] **PR aç**

```bash
git push -u origin feat/arayuz-faz1-mainshell
gh pr create --title "feat(arayuz): Faz 1 — MainShellView yenilemesi" --body "..."
```

**PR'a KARIŞTIRILMAYACAK** (bu dalda commit'siz duruyorlar):
`.claude/launch.json`, `.gitignore`, `docs/proje-analiz-raporu-2026-07-16.md`,
`docs/superpowers/plans/2026-07-28-whatsapp-odeme-hatirlatma-cloud-api.md`,
`docs/superpowers/specs/2026-07-28-whatsapp-otomasyon-design.md`,
`docs/superpowers/specs/2026-08-07-stok-sistemi-design.md`.

## Faz 1 kapsamı DIŞI

- Çekmeceler (Faz 2), sayfa navigasyonu (Faz 3), `LoginDialog` +
  `FirstRunWizard` (Faz 4).
- Stok düşümü, beden ayıklama, sunucu senkronu, R2 — stok projesine ait.
- PostgreSQL göçü — arayüz yenilemesinin **tüm** fazları bitmeden başlamayacak.
