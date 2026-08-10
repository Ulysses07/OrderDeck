# OrderDeck Masaüstü Arayüz Yenilemesi — Tasarım Dokümanı

**Tarih:** 2026-08-07
**Kapsam:** `OrderDeck.App` (WPF, `net10.0-windows`)
**Referans:** [`docs/design/yayin-ekrani-referans.html`](../../design/yayin-ekrani-referans.html)

---

## 1. Neden

Kullanıcının şikâyeti: uygulama "çok bariz AI tarafından yapılmış" görünüyor ve
kullanımı zor. Bu izlenimin koddaki karşılığı ölçüldü:

| Bulgu | Sayı |
|---|---|
| Farklı `FontSize` değeri | 18 |
| Sabit hex renk kullanımı / benzersiz renk | 342 / 120 |
| El yazması `ControlTemplate` | 100 |
| Animasyon | 0 |
| İkon yerine emoji/sembol karakteri | 52 kullanım / 17 benzersiz |
| `Window` kökü | 25 (`MainWindow` + 24 dialog) |
| XAML dosyası / satır (toplam) | 32 / 5419 |
| — bunun view'lar (`Views/` + `MainWindow`) | 26 / 3857 |

İki ayrı problem var:

1. **Görsel tutarsızlık.** Ölçek yok; her ekran kendi font boyutunu, rengini,
   boşluğunu uyduruyor. `web/app/globals.css` içindeki marka sistemi (Haziran
   2026 rebrand'inden beri orderdeckapp.com'da canlı) masaüstünde hiç
   kullanılmıyor.
2. **Akış problemi.** Her şey ayrı pencerede açılıyor. Yayın sırasında açılan
   bir pencere sohbeti kapatıyor; ürün kodunu yazan izleyici pencerenin
   arkasında kalıyor. Bu kozmetik değil, işi doğrudan bozan bir sorun.

Hareketin olmaması da ayrı bir eksik: uygulama canlı yayın aracı, ama ekranda
hiçbir şey canlı değil. Mesaj akışı, sipariş onayı, sayaç değişimi — hepsi
sessizce yer değiştiriyor.

## 2. Ne yapılacak

Ölçülmüş tasarım sistemi (renk, ölçü, hareket, bileşen) kurulacak ve tüm
arayüz **tek pencereye** taşınacak. Referans olarak, teşhis edilen sorunların
hepsine cevap veren çalışır bir HTML mockup üretildi
(`docs/design/yayin-ekrani-referans.html`); tasarım sistemi bu dosyadan
çıkarıldı ve WPF'e o dosya üzerinden port edilecek.

---

## 3. Renk tokenları

Tek doğruluk kaynağı `web/app/globals.css`. Mockup'ın renkleri (kıl payı
farklıydı) web değerlerine hizalandı — iki ayrı "doğru" tutmak, 120 rastgele
rengin oluşma biçiminin ta kendisi.

**15 renk + beyaz:**

| Grup | Token | Değer |
|---|---|---|
| Yüzey | `Bg` | `#090A0E` |
| | `Surface` | `#0F111A` |
| | `Surface2` | `#161A26` |
| Kenarlık | `Border` | `rgba(255,255,255,.07)` |
| | `BorderStrong` | `rgba(255,255,255,.13)` |
| Metin | `Text` | `#F4F2EC` |
| | `TextDim` | `#A6ACBA` |
| | `TextMute` | `#868C9C` |
| Anlamsal | `Accent` | `#FF4A38` |
| | `AccentHot` | `#FF6A5A` |
| | `AccentDeep` | `#E23A2A` |
| | `AccentInk` | `#180603` |
| | `Amber` | `#FFB23E` |
| | `Success` | `#2DD06F` |
| | `Info` | `#4D8DF6` |
| Sabit | `OnAccent` | `#FFFFFF` |

**Kurallar:**

- `Danger` ayrı bir renk değil, `Accent`'in takma adı. Yıkıcı eylem birincilden
  **renkle değil konumla** ayrılır: birincil dolu buton, yıkıcı hayalet bağlantı.
- **Platform rengi tokenı yok.** Referans mockup platform rozetini "YT/IG/TT/FB"
  kısaltması + marka rengi tint olarak çiziyor; bu kalıp daha önce kullanıldı ve
  **Google itiraz etti** (kısaltma + marka kırmızısı = izinsiz marka kullanımı).
  Çözüm `Themes/PlatformIcons.xaml` içinde zaten uygulanmış durumda: her
  platformun **kendi resmi ikonu, resmi renginde, değiştirilmeden**. Bu doküman
  o çözümü korur — mockup'ın rozet biçimi kullanılmayacak. Ayrıntı için o
  dosyanın baş notuna bakın (kaynak künyesi dahil).
- Bu listenin dışında renk yok. Yeni renk gerekiyorsa önce buraya eklenir.

## 4. Ölçü tokenları

**Tip ölçeği — 6 basamak** (bugün 18):

| Token | Değer | Kullanım |
|---|---|---|
| `F0` | 11px | mikro etiket, rozet, zaman damgası |
| `F1` | 12.5px | ikincil metin, kullanıcı adı, çip |
| `F2` | 14px | **gövde (varsayılan)** |
| `F3` | 20px | panel başlığı, ürün adı/fiyatı |
| `F4` | 32px | istatistik değeri, aktif fiyat |
| `F5` | 64px | aktif ürün kodu (tek yerde) |

**Boşluk — 7 basamak:** `Sp1 2` · `Sp2 4` · `Sp3 8` · `Sp4 12` · `Sp5 16` ·
`Sp6 20` · `Sp7 24`. Her biri hem `double` hem `Thickness` olarak tanımlanır
(WPF'te CSS `gap` karşılığı yok, boşluk `Margin` ile verilir).

**Köşe yarıçapı — 5 basamak:** `RXs 4` (çip, kod parçacığı) · `RSm 6` (input,
küçük buton) · `RMd 8` (satır, buton, banner) · `RLg 10` (panel, kart) ·
`RFull 999` (hap).

**İkon — 4 basamak:** `IcSm 14` · `IcMd 16` · `IcLg 20` · `IcXl 26`.
(Mockup'ta 12/13/14/15/16/17/26 serbestti; XAML'de `Path`/`Viewbox` ölçüsü
olacağı için ölçeğe indirildi.)

**Hareket — 3 süre + 2 easing:**

| Token | Değer | Kullanım |
|---|---|---|
| `DurFast` | 150ms | hover, odak geçişi |
| `DurBase` | 350ms | giriş-çıkış (mesaj, kuyruk satırı, banner, çekmece) |
| `DurSlow` | 850ms | vurgu (yeşil flaş, onay nabzı) |
| `EaseOut` | `CubicEase / EaseOut` | genel |
| `EaseSpring` | `BackEase / EaseOut, Amplitude 0.3` | buton, çip, geri bildirim |

**Sabit ölçüler:** `WSide 224` / `WSideMin 64` (ikon modu) · `WRight 400` ·
`WDrawer 400` · `HTopbar 56` · `WContentMax 1760` · `WAppMin 960` ·
`HAppMin 720` · `WAppStart 1440` · `HAppStart 900` · `HBtn 46` ·
`WStreamBtn 132`.

Faz 1'de düzeltilenler (2026-08-08, hepsi gerekçesiyle `Metrics.xaml`'de):

- `WRight`/`WDrawer` 344 → **400**: kod ve fiyat kutuları üstteki tam genişlik
  şeritten sağ sütuna indi, 344'te nefes almıyorlardı.
- `WAppMin` 1280 → **960**: 1920'lik ekranda yarım pencere tam 960 ve operatör
  uygulamayı yayın/tarayıcı yanına böyle koyuyor.
- **`WAppStart`/`HAppStart` yeni.** Pencere `1280x800` açılıyordu — hem 1360
  hem 850 kırılımının altında, yani uygulama **her açılışta** indirgenmiş
  yerleşimle başlıyordu. İndirgeme dar ekranın çaresi olmalı, varsayılan hâl
  değil.
- **`WStreamBtn` yeni.** Yayın butonu iki durumda iki farklı metin taşıyor;
  içeriğe göre ölçülünce durum değişince genişliğini oynatıp yanındaki butonu
  kaydırıyordu.

Silinen ölü token'lar: `ChatTimeColumn`, `QueueNoColumn` — hiçbir view
tüketmedi. Kullanılmayan token, kullanılmayan `Style` ile aynı borç (§9).

## 5. Bileşen envanteri

13 aile. Bugünkü 100 el yazması `ControlTemplate` bunlara indirgenecek.

| # | Aile | Varyantlar |
|---|---|---|
| 1 | Buton | birincil (dolu `Accent`, `HBtn`) · ikincil (`Surface2` + kenarlık) · hayalet (yalnız metin) · bağlantı-eylem (altı çizili) |
| 2 | Nav öğesi | pasif · aktif (sol `Accent` şeridi) · sayaçlı · ikon modu |
| 3 | Input | arama (ikon + odakta `Info` kenarlık) · kod kutusu (mono `F5` + odak halkası) · fiyat (altı kesikli çizgi, hatada sarsıntı) · **ipucu metni: `Tag` dolu olan her kutu placeholder kazanır** |
| 4 | Panel | gövde · başlık satırı (alt kenarlıklı) · sabit alt yuva |
| 5 | Rozet & hap | sayaç (dolu `Accent`) · platform (resmi ikon, `PlatformIcons.xaml`'den; kısaltma ve renkli zemin yok) · durum noktası (`Success`/`Amber`/`TextMute`) · sayı hapı · klavye tuşu |
| 6 | Banner | `Amber` / `Info` / `Accent` tonları + "+N daha" kesikli toplama satırı |
| 7 | Liste satırı | sohbet (5 sütun: saat, platform, kullanıcı, mesaj, etiket; eşleşme vurgusu) · kuyruk (3 sütun: sıra no, ad, adet; ilk satır öne çıkar, çıkışta sağa kayar) |
| 8 | Boş durum | ortalanmış açıklama metni |
| 9 | Yüzen bildirim | "N yeni mesaj" hapı (akış yukarı kaydırılmışken belirir) |
| 10 | Açılır kutu | hover popover (sığmayan platform sayaçları) |
| 11 | İstatistik hücresi | sol kenarlıkla ayrık · hover'da gizle/göster butonu · değişimde flaş |
| 12 | Canlı göstergesi | nabız animasyonlu nokta + süre sayacı |
| 13 | **Çekmece** | sağdan kayan bağlamsal panel (aşağıda) |

## 6. Tek pencere kararı

**Hiçbir şey pop-up olarak açılmayacak.** Bugünkü 24 `Window`, üç kalıba dağılır:

### 6.1 Sayfa (12 view)

Sol nav'dan gidilir, tüm içerik alanını kaplar. Yayın dışında bakılan şeyler:

`SettingsDialog` · `BlacklistDialog` · `StreamHistoryDialog` ·
`StreamReportDialog` · `PeriodReportDialog` · `SupportRequestsDialog` ·
`AccountDialog` · `BackupTransferDialog` · `RestoreDialog` · `BulkSmsDialog` ·
`ShipmentThresholdDialog` · `ShortcutHelpDialog`

### 6.2 Çekmece (10 view)

Sağdan kayar, **sohbet solda görünür kalır**. Yayın sırasında açılan her şey:

`CustomerSearchDialog` · `CustomerDetailDialog` · `PhoneEntryDialog` ·
`NewGiveawayDialog` · `ShipmentDirectiveDialog` · `AddBalanceDialog` ·
`DekontEkleDialog` · `AddToBlacklistDialog` · `CancelLabelDialog` ·
`FacebookPagePickerDialog`

**Davranış:**

- Genişlik `WDrawer` (400px) — sağ panelle aynı. Sağdan kayarak açılır
  (`DurBase`, `EaseOut`).
- **Sağ panelin üstünü örter.** Sohbet genişliği hiç değişmez; feda edilen
  ürün kartı ve kuyruk listesi olur.
- `Esc` kapatır. Açılışta odak çekmecenin ilk girdisine gider, kapanışta
  çağıran öğeye döner. `Tab` çekmece içinde döner.

**Faz 2a düzeltmeleri (2026-08-08, altyapı yazılırken ölçüldü):**

- ~~Aynı anda tek çekmece açık olur; ikincisi açılırsa birincinin yerini
  alır.~~ **Yanlış.** Bugünkü diyaloglar İÇ İÇE açılıyor, üç seviyeye kadar:
  `DekontEkleDialog → ShipmentDirectiveDialog → ShipmentThresholdDialog` ve
  `CustomerSearchDialog → CustomerDetailDialog`. Tek yuva bu zinciri ifade
  edemez, host bir **yığın** tutuyor (`DrawerStack`). Alttakiler solar
  (Opacity .35) ve tıklama almaz; üstteki kapanınca geri döner.
- **Çekmece modal değil.** Tam ekran bir perde denendi ve ekran görüntüsüyle
  bakıldı: sohbeti okunmaz hâle getiriyordu. Katman yalnız sağ sütunu örtüyor,
  kabuğun geri kalanı tıklanabilir kalıyor. Bilinçli ödünç — yayın sırasında
  operatörün sohbetten kopmaması, yanlışlıkla arkaya tıklama riskinden ağır
  basıyor.
- ~~**AÇIK MADDE:** "Alttaki yazdır yuvası yerinde kalır" maddesi HENÜZ
  karşılanmıyor.~~ **Kapandı (2026-08-10).** Yazdır/Sil/Temizle şeridi
  `PrintQueuePanel`'den çıkıp `PrintSlot`'a taşındı. Sağ sütun iki satır:
  üstte çekmecenin örttüğü alan, altta yuva. Şeridin içeriği taşınırken
  değişmedi.

```
┌────────┬──────────────────────┬───────────────┐
│  nav   │       SOHBET         │▓ MÜŞTERİ ARA ▓│
│        │  (hiç değişmez)      │▓             ▓│
│        │                      │▓             ▓│
│        │                      ├───────────────┤
│        │                      │ Sırada: #33   │
│        │                      │ [  Yazdır  ]  │
└────────┴──────────────────────┴───────────────┘
```

### 6.3 Satır içi onay

"Emin misin?" için ayrı yüzey yok. Sorunun kaynağı olan satır/buton yerinde
soruya dönüşür ("Sil → Emin misin? Evet · Vazgeç"), `Esc` veya odak kaybı
iptal eder.

### 6.4 Shell öncesi (2 view)

`LoginDialog` ve `FirstRunWizard` shell henüz yokken çalışır. Bunlar ayrı
pencere değil, **aynı pencerenin tam-ekran durumu** olur. Böylece uygulamada
gerçekten tek `Window` kalır.

## 7. Duyarlı davranış

WPF'te `@media` yok. Pencere boyutu `MainWindow.SizeChanged` üzerinden
ViewModel'de iki bool'a çevrilir, `DataTrigger` ile bağlanır:

| Durum | Eşik | Etki |
|---|---|---|
| `IsCompact` | genişlik < 1360px | sol nav ikon moduna iner (`WSideMin`), etiketler gizlenir, bağlantı adları yalnız nokta olur |
| `IsShort` | yükseklik < 850px | ürün görseli 142 → 56px, kod `F5` 64 → 44px, panel dolguları bir basamak iner, "sıradaki" satırı gizlenir |

Pencere alt sınırı `WAppMin` × `HAppMin` (1280 × 720). İçerik `WContentMax`
(1760px) ile sınırlanır, fazlası ortalanır — ultrawide ekranda sohbet
okunamayacak kadar uzamaz.

## 8. WPF karşılıkları

Yeni sözlükler mevcut `OrderDeck.App/Themes/` klasörüne eklenir (ayrı klasör
açılmaz):

| Dosya | İçerik |
|---|---|
| `Colors.xaml` | 16 `SolidColorBrush` |
| `Metrics.xaml` | font boyutları, boşluklar (`double` + `Thickness`), yarıçaplar, ikon ölçüsü, sabit ölçüler |
| `Motion.xaml` | 3 `Duration` + 2 easing fonksiyonu |
| `Icons.xaml` | `Geometry` kaynakları — 17 emoji buradan gider |
| `Controls.xaml` | 13 bileşen ailesi için `Style` |

**Mevcut sözlüklerin akıbeti** (`Themes/`, bugün 1445 satır):

- `PlatformIcons.xaml` (125) — **korunur.** Marka izni açısından çözülmüş iş.
- `DarkControls.xaml` (811) — yeni sistemle çakışır (kendi renk/ölçü değerleri
  var). Faz 1-3'te ilgili view'lar dönüştükçe küçülür, Faz 4'te silinir.
- `SettingsTheme.xaml` (434) — Faz 3'te `SettingsDialog` sayfaya dönüşünce silinir.
- `GiveawayTheme.xaml` (75) — Faz 2'de `NewGiveawayDialog` çekmeceye dönüşünce silinir.

Hiçbiri Faz 0'da değiştirilmez; eski ve yeni sistem bir süre yan yana yaşar.

**Doğrudan karşılığı olanlar:**

| CSS | XAML |
|---|---|
| `grid-template-columns: 40px 28px auto 1fr auto` | `ColumnDefinition Width="40 / 28 / Auto / * / Auto"` |
| `text-overflow: ellipsis` | `TextTrimming="CharacterEllipsis"` |
| `font-variant-numeric: tabular-nums` | `Typography.NumeralAlignment="Tabular"` |
| `cubic-bezier(.2,.8,.3,1)` | `CubicEase EasingMode=EaseOut` |
| `cubic-bezier(.34,1.56,.64,1)` | `BackEase EaseOut, Amplitude≈0.3` |
| `@keyframes` | `Storyboard` + `DoubleAnimation` / `ColorAnimation` |
| `border-radius` | `CornerRadius` (`Border` sarmalayıcı) |

**Uyarlama gerektirenler:**

- **`gap` yok** → öğe `Style`'ında `Margin`. Boşluk tokenları `Thickness`
  olarak da tanımlandığı için ölçek korunur.
- **`box-shadow`** → `DropShadowEffect` pahalı ve bulanık. Mockup'taki 4 gölge
  de yükseltilmiş öğede (popover, yüzen bildirim, buton hover); **ince
  kenarlıkla taklit edilecek**, `Effect` kullanılmayacak.
- **`letter-spacing` yok.** WPF `TextBlock`'ta harf aralığı desteklenmiyor.
  Mockup mikro etiketlerde `.11em` kullanıyor. **Vazgeçiliyor** — 11px'te fark
  küçük, `Glyphs`'e inmenin bakım maliyeti büyük.
- **`prefers-reduced-motion`** → `SystemParameters.ClientAreaAnimation`
  yanlışsa tüm `Storyboard`'lar atlanır.

**Fontlar** repo'ya gömülecek (IBM Plex Sans, JetBrains Mono, Bricolage
Grotesque — hepsi OFL, gömme serbest). Google Fonts'a çalışma zamanı bağımlılığı
olmayacak; uygulama çevrimdışı da doğru görünmeli.

## 9. Fazlar

Her faz kendi PR'ı.

**Faz 0 — tema altyapısı.** Token sözlükleri (`Colors` / `Metrics` / `Motion` /
`Icons`) + gömülü fontlar. Hiçbir view'a dokunulmaz, görsel değişiklik olmaz.
Sonraki her fazın ön şartı.

`Controls.xaml` Faz 0'da **boş oluşturulur, doldurulmaz.** Bileşen `Style`'ları
tüketildikleri fazda yazılır (çoğu Faz 1'de). Hiç kullanılmayan 13 `Style`'ı
önden yazmak, doğrulanmamış tasarım borcu üretmek olur.

**Faz 1 — `MainShellView`.** Referans mockup'ın kapsadığı ekran: sol nav, üst
şerit, bildirim şeridi, aktif ürün şeridi, sohbet, sağ panel, yazdır yuvası.
Değerin çoğu burada — yayın sırasında bakılan tek ekran.

### 9.1 Faz 1'in veri katmanı istisnası (karar 2026-08-08)

Mockup'ın sağ panelindeki ürün kartı, uygulamada karşılığı **olmayan** üç şey
gösteriyor: ürün adı, ürün fotoğrafı, beden başına stok. Kalan her alan mevcut
veriden geliyor (yayın istatistikleri `LabelRepository.GetSessionTotals()` ile
zaten var; süre / saat / sıra numarası ucuz türetme).

Kullanıcı kararı: kart **gerçek veriyle** çalışacak — boş kabuk çizilmeyecek.
Bu, §11'in "veri katmanı kapsam dışı" kuralını yalnız bu üç alan için deler.
Sınırlar dar tutuldu:

- **Yalnız WPF-yerel SQLite.** Sunucuda tablo yok, senkron yok, R2 yok. Sebep:
  PostgreSQL göçü UI bitmeden başlamayacak (kullanıcı kararı, 2026-08-08) ve
  stok spec'i satır 246 *"WPF'in yerel SQLite'ı etkilenmiyor"* diyor — bu dilim
  göçten etkilenmediği için ileride yeniden üretilmesi gerekmiyor.
- **Şema (migration 024):** `Product(Code PK, Name, PhotoPath, UpdatedAt)` ve
  `ProductSize(Code, Size PK, Quantity, SortOrder)`. Fotoğraf
  `%LOCALAPPDATA%\OrderDeck\products\` altında dosya; tabloda **yalnız dosya
  adı** tutulur (mutlak yol değil — profil taşınırsa kayıt bozulmasın).
- **Fiyat alanı YOK.** Karttaki fiyat, hero'daki aktif fiyat girişinin aynısı;
  yeni alan icat edilmiyor.
- **Grid yalnız gösterir.** Etiket kuyruğa girince stok **düşmez**; `Label`'a
  beden alanı eklenmez, sohbet mesajından beden ayıklanmaz. Adetleri operatör
  kartta satır-içi düzenler. Otomatik düşüş stok projesine kalıyor.
- Kart, kayıt yoksa satır-içi "ürünü tanımla" moduna düşer (ad, fotoğraf, beden
  seti + adetler). Pop-up yok — §6 kararı burada da geçerli.

Stok projesine kalanlar: sunucu tabloları, çoklu varyant ekseni (renk),
hareket tabanlı defter, barkod/Code128, R2, panel stok giriş ekranı, maliyet,
arşivleme, WhatsApp bildirimi. Bu dilimdeki düz `Quantity` alanı, stok projesi
geldiğinde hareket defterine dönüşecek.

**Faz 2 — çekmece altyapısı + 10 çekmece view'ı.** `Window` kökleri
`UserControl`'e çevrilir, çekmece host'u shell'e eklenir. İkiye bölündü
(karar 2026-08-08, kullanıcı): önce altyapı, sonra view dönüşümü.

- **Faz 2a — altyapı.** `Drawer` / `DrawerStack` / `IDrawerService`,
  `DrawerHost`, `MessageDrawer` (MessageBox'ın çekmece karşılığı),
  `IDialogService`'e `ConfirmAsync` + `ShowAsync`. Hiçbir view dönüşmez,
  uygulama birebir aynı çalışır.
- **Faz 2b — view dönüşümü.** ~~Kolaydan zora üç grup: (1) ViewModel'i
  olmayan dördü…~~ **Gruplama değişti (2026-08-10, ölçümle).** Çekmeceyi
  ancak shell'e erişebilen kod açabilir: `DrawerHost` shell'in içinde.
  "ViewModel'i olmayan dört diyalog"un hiçbiri shell'den açılmıyor; hepsi
  hâlâ `Window` olan bir konteynerin içindeki ViewModel'lerden açılıyor.
  Çekmece o modal pencerenin ARKASINDA kalır, operatör ulaşamaz, `await`
  hiç dönmez. Yani dönüşüm kolaydan zora değil **dıştan içe** gitmek
  zorunda. Ölçülen bağımlılık ağacı:

  ```
  shell → NewGiveawayDialog                    (çocuğu yok, tek çağrı yeri)
  shell → DekontEkleDialog     → ShipmentDirective, ShipmentThreshold
  shell → CustomerDetailDialog → AddBalance, CancelLabel, BackupTransfer
  shell → CustomerSearchDialog → PhoneEntry
  ```

  Sıra: (1) `NewGiveawayDialog`; (2) `CustomerDetailDialog` + çocukları;
  (3) `CustomerSearchDialog` + `PhoneEntryDialog`; (4) `DekontEkleDialog` +
  çocukları — zincir mantığı code-behind'dan ViewModel'e taşınarak.
  `FacebookPagePickerDialog` (SettingsDialog'un içinde) ve
  `AddToBlacklistDialog`'un manuel yolu (BlacklistDialog'un içinde) **Faz
  3'e bağlı** — konteynerleri sayfa olmadan dönüştürülemez.
  `AddToBlacklistDialog`'un shell'den açılan iki çağrısı da onunla birlikte
  bekliyor: sınıf tek, iki yol aynı anda dönüşmeli.

**Faz 3 — sayfa navigasyonu + 12 sayfa view'ı.** Sol nav gerçek navigasyona
bağlanır.

**Faz 4 — `LoginDialog` + `FirstRunWizard`.** Tam-ekran shell durumlarına
çevrilir; son iki `Window` kökü kalkar.

## 10. Doğrulama

- Faz 0 sonunda: `dotnet build OrderDeck.App/OrderDeck.App.csproj` temiz,
  uygulama görsel olarak değişmemiş.
- Her faz sonunda: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` geçer.
- Faz 1 sonunda kullanıcı gerçek yayında dener: sohbet akışı, kod girişi,
  kuyruk, yazdırma.
- Faz 4 sonunda `grep -c "<Window" OrderDeck.App/**/*.xaml` → yalnız
  `MainWindow.xaml`.
- Faz 4 sonunda ölçüm tekrarı: benzersiz `FontSize` ≤ 6, sabit hex renk 0,
  emoji ikon 0, `Window` kökü 1.

## 11. Kapsam dışı

- `web/` ve shopper mobil uygulaması — bu doküman yalnız masaüstünü kapsıyor.
- Chrome eklentisi arayüzü.
- İş mantığı, veri katmanı. Bu yenileme **yalnız sunum katmanı**; hiçbir
  özellik eklenmiyor veya kaldırılmıyor. **İki istisna:**
  1. Faz 1'in ürün kartı — bkz. §9.1. Kapsamı orada dar sınırlarla yazılı.
  2. **ViewModel davranışı, Faz 2b'de kaçınılmaz.** `ShowDialog()` BLOKLAYAN
     bir çağrı ve `bool?` döndürüyor; çekmece bloklamaz. 26 çağrı yerinin
     14'ü dönüş değerine göre dallanıyor. Akışı `await` edilebilir hâle
     getirmek ViewModel'lere dokunmak demek — özellikle
     `CustomerDetailViewModel.CancelSelected()`,
     `MainShellViewModel.StartGiveaway()` ve `EndStream()`. Değişen şey
     KONTROL AKIŞI; iş kuralları aynı kalıyor.
- Karanlık/aydınlık tema seçeneği — tek tema (koyu) var, öyle kalıyor.
- PostgreSQL göçü ve stok sistemi — ayrı spec'ler. **Sıralama kararlaştırıldı
  (2026-08-08):** ikisi de bu yenilemenin tüm fazları bitmeden başlamayacak.
  Faz 1'in §9.1 dilimi bu kuralı bozmuyor, çünkü sunucuya hiç dokunmuyor.
