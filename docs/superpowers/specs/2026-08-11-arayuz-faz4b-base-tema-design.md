# Faz 4b — `DarkControls.xaml`'in emekliye ayrılması

Tarih: 2026-08-11
Durum: tasarım onaylandı, plan yazılacak
Önceki faz: [Faz 4a — açılış durumları](2026-08-10-arayuz-faz4-acilis-durumlari-design.md) (PR #250, merged)
Üst spec: [Arayüz yenileme](2026-08-07-arayuz-yenileme-design.md)

## Amaç

Arayüz yenilemesinin **son fazı**. `Themes/DarkControls.xaml` (49 KB, 30 örtük
stil, kendi 17 renk token'ı) silinir; yerine yalnız gerçekten gerekli örtük
stilleri taşıyan ince bir `Themes/Base.xaml` gelir ve geri kalan her kontrol
`Controls.xaml`'deki keyed `OD.*` stillerine açıkça bağlanır.

**Bu bir görünüm koruma işi değil.** Kullanıcı kararı: yeni tasarım dili her
yere yayılsın. Bugün hâlâ eski mavi-gri paletiyle çizilen kontroller (girdi
kutuları, listeler, menüler, sekmeler) yeni siyah/kırmızı palete geçer.
Değişiklik bilinçli, geniş ve görünür olacak.

**Neden şimdi:** uygulamada iki palet aynı anda yaşıyor. `DarkControls.xaml`
mavi-gri yüzey + mavi vurgu (`OD.Bg.Window #FF0F1118`, `OD.Border.Focus
#FF5B8DEF`), `Colors.xaml` ise siyaha yakın yüzey + kırmızı vurgu
(`OD.Brush.Bg #090A0E`, `OD.Brush.Accent #FF4A38`). Çakışmanın canlı kanıtı
`LoginGate`: şifre kutusu komşusu olan e-posta kutusundan **farklı zeminde**
duruyor ve odaklanınca **mavi** kenarlık alıyor, komşusu kırmızı alıyor.

## Ölçümler

Tasarımın dayandığı sayılar tahmin değil, ölçüm. `OrderDeck.App/Views/**/*.xaml`
taranarak (özellik-eleman etiketleri hariç tutularak) bulundu:

| Kontrol | Toplam | Stilli | **Stilsiz** | Dosya |
|---|---:|---:|---:|---:|
| `TextBlock` | 384 | 312 | 72 | 25 |
| `MenuItem` | 16 | 0 | 16 | 4 |
| `ComboBoxItem` | 10 | 0 | 10 | 2 |
| `ListBox` | 9 | 4 | 5 | 5 |
| `TabItem` | 15 | 10 | 5 | 1 |
| `ContextMenu` | 4 | 0 | 4 | 4 |
| `ComboBox` | 9 | 5 | 4 | 2 |
| `Button` | 108 | 104 | 4 | 4 |
| `PasswordBox` | 3 | 0 | 3 | 1 |
| `CheckBox` | 15 | 13 | 2 | 2 |
| `TextBox` | 56 | 54 | 2 | 2 |
| `Separator` | 1 | 0 | 1 | 1 |
| `DataGrid` | 4 | 4 | 0 | 0 |
| **Toplam stilsiz** | | | **128** | |

Bu 128'in yalnız **16'sı** iş çıkarıyor (dökümü aşağıda): 72 `TextBlock`
mirasla çözülüyor, 21'i (`MenuItem`/`ContextMenu`/`Separator`) örtük kalıyor,
15'i (`ComboBoxItem` + `TabItem`) ebeveyninin `ItemContainerStyle`
setter'ından geliyor, 4'ü görünümün kendi yerel örtük stiliyle kapsanıyor.

`DarkControls.xaml`'in 17 renk token'ına **dışarıdan yalnız 2 atıf** var
(`PlatformIcons.xaml:118,122`); ayrıca `OD.Icon.Gift` 2 yerde
(`ShellBanners.xaml:69`, `ShellTopBar.xaml:209`), `OD.Shadow.Soft` ve
`OD.ScrollBar.Thumb` ise **hiçbir yerde** kullanılmıyor.

> Not: üst spec §6'daki kullanım tablosu bayat (Faz 3d öncesine ait —
> `RadioButton` 7 ve `TabControl` 2 diyor, oysa ikisi de bugün tamamen
> token'lanmış durumda). Geçerli olan yukarıdaki tablodur.

## Karar: ince çekirdek

`Base.xaml` **yalnızca** şunları örtük tutar:

| Örtük kalan | Gerekçe |
|---|---|
| `ScrollBar` (+ `OD.ScrollBar.Thumb`) | `ScrollViewer` şablonunun içinde üretiliyor, XAML'den erişilemez |
| `ToolTip` | `ToolTip="metin"` yazınca WPF sarmalıyor, elde tutulamaz |
| `ContextMenu` | çalışma anında üretiliyor |
| `MenuItem` | `ContextMenu`'nün içi; tek görünüm isteniyor (16 kullanım) |
| `Separator` | menü ayracı |
| `Window` | `Background`/`Foreground`/`FontFamily` fallback'i |

**`TextBlock` örtük stili silinir.** Tek yaptığı `Foreground` vermekti;
`MainWindow.xaml:10` zaten `Foreground="{StaticResource OD.Brush.Text}"`
diyor ve `Foreground` miras alınan bir bağımlılık özelliği → 72 stilsiz
`TextBlock` doğru rengi pencereden alır. "Silersek her yer koyu üstüne koyu
olur" korkusu ölçümle çürütüldü.

Bu silmenin ikinci kazancı: örtük stil mirası **yener**, bu yüzden beş ayrı
şablona `ContentPresenter.Resources` + `AncestorType` binding'li kaçamak
`TextBlock` stili yazılmıştı — `OD.Button.Primary` (Controls.xaml:56),
`OD.Button.Secondary` (131), `OD.CalendarDayButton` (341), `OD.CalendarButton`
(406) ve `OD.CalendarItem`'ın içindeki başlık düğmesi (479). Örtük stil gidince
beşi de gereksizleşir ve **kaldırılır** — tuzak kökünden temizlenir.
(`OD.Button.Ghost`'ta böyle bir kaçamak yok; içeriği `Foreground`'u miras
alıyor.)

**Doğrudan silinecekler** (`Base.xaml`'e girmez): `Label` (0 kullanım),
`GroupBox` (0), `Menu` (0), `DataGridRowHeader` (`HeadersVisibility="Column"`
olduğu için hiç çizilmiyor), `OD.Shadow.Soft` (dış kullanım 0 → menü ve
tooltip stillerinin içine gömülür).

### Neden sıfır-örtük değil

`ScrollBar` ve `ToolTip` XAML'den erişilemiyor; onlar için örtük stil
zorunluluk. `ContextMenu`/`MenuItem`/`Separator` ise çalışma anında üretiliyor
ve tek görünüm istiyor.

### Neden kalın çekirdek değil

`ListBoxItem`/`ComboBoxItem`/`DataGridCell` örtük bırakılırsa her liste tek bir
görünüme çakılır; oysa üçü de `ItemContainerStyle` ile açıkça verilebiliyor.
Kalın çekirdek, "yeni dili her yere yay" kararının kendisiyle çelişir.

## Uygulama sırası: iki PR

Bu fazın iki farklı riski var ve ikisi farklı türden: *renk yanlış görünüyor*
(göz kararı) ve *stil bağlanmamış, kontrol çıplak kaldı* (ölçülebilir). İkisi
ayrı PR'lara konur ki hangi adımın neyi bozduğu belli olsun.

### PR 1 — renk takası

`DarkControls.xaml` yerinde kalır, yalnız 17 token satırı `Colors.xaml`
karşılıklarına bağlanır. Tek commit'te tüm uygulama yeni palete geçer, hiçbir
stil taşınmaz.

| Eski (mavi-gri) | Yeni |
|---|---|
| `OD.Bg.Window` `#0F1118` | `OD.Brush.Bg` `#090A0E` |
| `OD.Bg.Surface` `#1A1D27` | `OD.Brush.Surface` `#0F111A` |
| `OD.Bg.Elevated` `#242833` | `OD.Brush.Surface2` `#161A26` |
| `OD.Bg.Input` `#1A1D27` | `OD.Brush.Surface2` |
| `OD.Bg.InputHover` `#242833` | `OD.Brush.Surface2` |
| `OD.Bg.InputPressed` `#2D3340` | `OD.Brush.Surface2` |
| `OD.Bg.InputDisabled` `#15171F` | `OD.Brush.Surface2` (geçici) |
| `OD.Border.Subtle` `#252935` | `OD.Brush.Border` `#12FFFFFF` |
| `OD.Border.Hover` `#323845` | `OD.Brush.BorderStrong` `#21FFFFFF` |
| `OD.Border.Focus` `#5B8DEF` | `OD.Brush.Accent` `#FF4A38` |
| `OD.Fg.Primary` `#E8EAF0` | `OD.Brush.Text` `#F4F2EC` |
| `OD.Fg.Secondary` `#8B919E` | `OD.Brush.TextDim` `#A6ACBA` |
| `OD.Fg.Disabled` `#5A5F6B` | `OD.Brush.TextMute` (geçici) |
| `OD.Accent` `#5B8DEF` | `OD.Brush.Accent` |
| `OD.Accent.Hover` `#7BA0F5` | `OD.Brush.AccentHot` |
| `OD.Accent.Pressed` `#4A77D4` | `OD.Brush.AccentDeep` |
| `OD.Selection` `#2A3F5C` | `OD.Brush.Surface2` (bkz. seçim idiyomu) |

Ayrıca `OD.Icon.Gift`'in kutu kırmızısı (`#EF4444`/`#DC2626`) palet kırmızısına
(`OD.Brush.Accent`/`AccentDeep`) hizalanır.

**Üç gerçek uyumsuzluk — renk eşlemesiyle çözülmez, PR 2'ye devreder:**

1. **Devre-dışı rengi yok.** Yeni dil devre-dışıyı ayrı renkle değil
   `Opacity="0.45"` ile veriyor (Controls.xaml:152, 187). `OD.Bg.InputDisabled`
   ve `OD.Fg.Disabled` PR 1'de en yakın renge bağlanır, PR 2'de stiller
   taşınırken opacity idiyomuna çevrilir.
2. **Hover/pressed üç kademe değil iki.** Eski dilde girdi zemini üç kez
   yükseliyordu (`1A1D27 → 242833 → 2D3340`); yeni dilde zemin sabit kalıp
   **kenarlık** güçleniyor. PR 1'den sonra hover geçici olarak görünmez olur —
   beklenen ara durum, hata değil. PR 2'de kenarlık geçişiyle geri gelir.
3. **Seçim idiyomu ikiye ayrılmış durumda.** `OD.DataGrid.Cell` seçiliyi dolu
   `Accent` zemin + `OnAccent` yazı ile veriyor (Controls.xaml:766);
   `OD.ComboBoxItem` ise `Surface2` zemin + `AccentHot` yazı ile
   (Controls.xaml:922).
   **Karar: liste türü kontroller `ComboBoxItem` idiyomunu kullanır.** Sohbet
   akışı ve baskı kuyruğu uzun listeler; dolu kırmızı satır o yoğunlukta
   gürültü yapar. Dolu `Accent`, tekil/ızgara seçimine (`DataGrid`) özgü kalır.

PR 1'de kod taşınmaz, test listesi değişmez; mevcut 942 test yeşil kalmalı.

### PR 2 — `Base.xaml` + silme

**Yeni dosya `Themes/Base.xaml`:** yukarıdaki "örtük kalan" tablosu.

**Yazılacak üç yeni keyed stil (`Controls.xaml`):**

- `OD.PasswordBox` — `OD.TextBox`'ın birebir eşi (aynı zemin, kenarlık, odak
  rengi, yarıçap). `LoginGate`'teki zemin/odak çakışmasının asıl çözümü bu.
- `OD.ListBox` — şeffaf zemin, kenarlıksız, dolgusuz; kapsayıcı görünümü
  ebeveyn `Border`'a bırakır.
- `OD.ListBoxItem` — seçim idiyomu: `Surface2` zemin + `AccentHot` yazı;
  hover `Surface2`.

**`OD.CheckBox` de yazılır.** Stilsiz iki `CheckBox` var: `GiveawayDrawer.xaml:119`
("Önceki kazananları dahil etme") ve `PeriodReportPage.xaml:45` ("Yalnızca adı
bilinenler"). İkisi de kalıcı ayar değil, o an yapılacak işlemin seçeneği.
`OD.Toggle` kendi notunda (Controls.xaml:991) "ayarlardaki 12 açık/kapalı alan"
için tanımlı — anahtar burada anlamı yanlış verir. `OD.Toggle` ayarlarda kalır.

**Açık stil bağlanacak 16 kullanım.** Ölçümün ham "128 stilsiz" sayısı iki
mekanizmayı görmüyor: (a) görünümün kendi `Resources`'ında tanımlı **yerel
örtük stil**, (b) ebeveynin **`ItemContainerStyle`** setter'ı. İkisi düşülünce
gerçek iş şu:

| Kontrol | Adet | Bağlanacağı stil | Yer |
|---|---:|---|---|
| `ListBox` | 5 | `OD.ListBox` (yeni) | `CustomerSearchDrawer` 136, `LoginGate` 162, `RestoreGate` 38, `ChatPanel` 50, `PrintQueuePanel` 44 |
| `ComboBox` | 4 | `OD.ComboBox` (var) | `DekontEkleDrawer` 119, `GiveawayDrawer` 50, 74, 87 |
| `PasswordBox` | 3 | `OD.PasswordBox` (yeni) | `LoginGate` 66, 105, 110 |
| `CheckBox` | 2 | `OD.CheckBox` (yeni) | `GiveawayDrawer` 119, `PeriodReportPage` 45 |
| `Button` | 2 | `OD.Button.*` (var) | `DekontEkleDrawer` 32, `PrintQueuePanel` 101 |

**İş istemeyenler ve nedenleri:**

- `TabItem` (5, `CustomerDetailDrawer` 235/275/372/403/481) — ebeveyn
  `NarrowTabs` stili `ItemContainerStyle="{StaticResource NarrowTab}"` diyor
  (satır 65) ve `NarrowTab` zaten yeni palete bağlı.
- `ComboBoxItem` (10) — `OD.ComboBox` zaten
  `ItemContainerStyle="{StaticResource OD.ComboBoxItem}"` taşıyor
  (Controls.xaml:940).
- `TextBox` (2) — bulundukları görünümlerde yerel örtük `TextBox` stili var
  (`ActiveProductBar` 19, `ProductCard` 38; ikincisi
  `BasedOn="{StaticResource OD.TextBox}"`).
- `ListBoxItem` — 5 `ListBox`'ın 3'ünde yerel örtük stil var
  (`CustomerSearchDrawer` 145, `ChatPanel` 62, `PrintQueuePanel` 56). Yeni
  `OD.ListBoxItem` kalan ikisi (`LoginGate`, `RestoreGate`) ve ileride
  eklenecekler için.

Not: `RestoreGate` 131'de yerel örtük `Button` stili var — Faz 4a'da eklenen
`IsBusy` tetikleyicisi. Kalması doğru, tabloya girmiyor.

**Dış bağımlılıkların yeni evi:**

- `OD.Icon.Gift` → `Icons.xaml`'e taşınır (o dosyanın baş notu zaten ona atıf
  yapıyor). Kullanıcılar: `ShellBanners.xaml:69`, `ShellTopBar.xaml:209`.
- `PlatformIcons.xaml:118,122` → `OD.Bg.Elevated` `OD.Brush.Surface2`,
  `OD.Fg.Secondary` `OD.Brush.TextDim` olur (`DynamicResource` kalır).

**Kaçamak stillerin kaldırılması:** Controls.xaml'deki beş
`ContentPresenter.Resources` bloğu (56, 131, 341, 406, 479) silinir.

**`App.xaml` merge sırası:**
`Colors → Metrics → Motion → Icons → Base → Controls → PlatformIcons`.
`Base`, `Controls`'ten önce gelir ki keyed stiller örtük olanı yenebilsin.
`DarkControls.xaml` hem listeden hem diskten silinir.

## Doğrulama

### Risk A — stil bağlanmamış (ölçülebilir)

1. **Yeni kalıcı bekçi testi.** `Views/` altındaki her `.xaml` taranır;
   izlenen kontrol tiplerinden (`ListBox`, `PasswordBox`, `CheckBox`,
   `ComboBox`, `TabItem`, `TextBox`, `Button`) stilsiz bir kullanım kalırsa
   test kırmızı. Muafiyet listesi `Base.xaml`'in içeriğiyle **aynı gerekçeyi**
   taşır (`TextBlock`, `MenuItem`, `ContextMenu`, `Separator`). Tek seferlik
   bir kontrol değil, kalıcı bekçi: ileride eklenen stilsiz bir kontrolü de
   yakalar.

   **"Stilli" sayılmanın üç yolu var, testin üçünü de bilmesi şart** — yoksa
   bugünün kodunda bile yanlış kırmızı verir:
   1. Öğenin kendi `Style=` özniteliği (veya `<X.Style>` eleman sözdizimi).
   2. Aynı dosyada tanımlı **yerel örtük stil** (`<Style TargetType="X">`,
      `x:Key` yok) — 5 yerde kullanılıyor.
   3. Ebeveynin **`ItemContainerStyle`** setter'ı — `TabItem` ve
      `ComboBoxItem` bugün tamamen buradan geliyor.
   Dosya düzeyinde granülerlik yeter: bir görünüm kendi `TextBox`'larını
   bilerek yerel stille biçimlendiriyorsa, aradığımız hata o değil.
2. **`ThemeMergeTests.cs:28` güncellenir** — `ExistingDictionaries`
   `["DarkControls.xaml","PlatformIcons.xaml"]` → `["PlatformIcons.xaml"]`;
   `NewDictionaries`'e `"Base.xaml"` eklenir, böylece çakışma taraması onu da
   kapsar.
3. **Mevcut 942 test yeşil kalır.** Özellikle gerçekten XAML yükleyen 153 WPF
   testi (`MainShellViewComposition`, `GateComposition`, `DrawerHostLayout`,
   `ShellPrintSlotLayout`): `Base.xaml`'de eksik bir token kalırsa bunlar
   `XamlParseException` ile patlar. Asıl güvence bu.
4. **`dotnet build OrderDeck.App` → 0 hata, 0 uyarı.** Silinen bir
   `StaticResource` anahtarı derleme zamanında yakalanmaz (WPF çalışma anında
   çözer), bu yüzden build tek başına yeterli değil — 3. madde asıl ölçüt.

### Risk B — renk yanlış görünüyor (göz kararı)

İki PR ayrı olduğu için iki ayrı tur:

- **PR 1 sonrası:** tek soru — "her yer kırmızı/siyah palette mi, mavi kalan
  var mı?". Hover'ın geçici kaybolması beklenen ara durum.
- **PR 2 sonrası:** 12 sayfa + 10 çekmece + 2 gate. Öncelikli bakılacaklar:
  `LoginGate` (`PasswordBox` artık e-posta kutusuyla aynı mı), `SettingsPage`
  (12 anahtar + 2 yeni checkbox), `ChatPanel` / `PrintQueuePanel` (`ListBox`
  seçim idiyomu), `CustomerDetailDrawer` (5 `TabItem`), sağ tık menüleri
  (örtük kalan tek görünür grup).

**Faz 4a'dan devreden görsel kararlar** PR 2'nin turunda birlikte kapatılır
(aynı ekranlara bakılıyor): `GateBrand` rozet yarıçapı, `BootGate`
"Hazırlanıyor…" puntosu, `LoginGate`/`RestoreGate` hata satırı zıplaması,
kayıt modunda `IsDefault`, `RestoreGate` tamamlanınca odak.

## Kapsam dışı

- **Offscreen render / görsel snapshot testi.** Faz 3'te denendi; host zemini
  gibi tuzakları var ve palet değişikliğinin kendisi tüm snapshot'ları
  geçersiz kılar. Bu fazda maliyeti değerinden büyük.
- **Yeni kontrol tipleri, yeni ekran, yerleşim değişikliği.** Bu faz yalnız
  stil kaynağını değiştiriyor.
- **PostgreSQL geçişi ve stok sistemi.** Kullanıcı kararı (2026-08-08): arayüz
  yenilemesinin tüm fazları bitmeden başlamıyor. Faz 4b o kapının anahtarı.

## WPF tuzakları (bu fazda geçerli, ölçüldü)

- **Örtük stil mirası yener.** Bir kontrolün kendi `Foreground` setter'ı,
  örtük `TextBlock` stilinin altında kalır. Bu fazın `TextBlock` silme kararı
  tam olarak bunu ortadan kaldırmak için.
- **`ControlTemplate.Resources` çalışmaz**, `ContentPresenter.Resources`
  çalışır — üretilen `TextBlock` şablonun sözlüğüne bakmıyor. (Kaçamakları
  silerken bu bilgi gerekmeyecek ama kayıtta kalsın.)
- **`Application.Current.Resources` süreç başına tek ve paylaşılan**,
  `ResourceDictionary` thread-safe değil, xUnit koleksiyonları paralel koşuyor.
  WPF'e dokunan her test `OrderDeck.Tests/App/ThemeTestHost.cs` düzeneğinden
  geçmeli. WPF testi düzensiz kırmızı verirse ilk şüpheli bu.
- **Bricolage'ın gerçek aile adı `Bricolage Grotesque 14pt`**; kısaltılmış hâli
  kısmi eşleşme yapıp sentezlenmiş 900 ağırlığı çeker.
