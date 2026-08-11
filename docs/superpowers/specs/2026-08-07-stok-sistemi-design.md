# Stok Sistemi — Tasarım

> **Durum: ONAYLANDI (2026-08-11).** Tasarım tartışması bitti, plan aşamasına
> geçiliyor. İlk uygulama hedefi **Faz 1a**.
>
> Tartışma başlangıcı: 2026-08-07 · Son revizyon: 2026-08-11

## Amaç

OrderDeck'e gerçek bir stok sistemi eklemek. Kullanıcının referansı **Nebim V3**
("nebim gibi, hatta tüm detayları olsun") — yani ürün kartı, varyant, barkod,
stok hareketi seviyesinde bir yapı.

## Kapsam dışı (kullanıcı kararı)

- **Muhasebe, e-fatura, e-irsaliye.** "Şu an lazım değil." Bunlar zaten kendimiz
  yazmamamız gereken, GİB/özel entegratör mevzuatına bağlı işler; doğrusu mevcut
  muhasebe programına (Paraşüt/Logo/Netsis) aktarımdır.
- **Tedarikçi / satınalma / cari hesap.** Kullanıcı: "hiçbir zaman da ihtiyaç
  yok." Maliyet elle girilen tek bir sayı olarak kalır.
- **Depo / lokasyon.** Tek depo var, kavram hiç açılmayacak.
- **Kargo/paketleme barkod doğrulaması.** Önerilmişti (paket hazırlanırken ürünü
  okutup siparişin beklediği varyantla karşılaştırmak), kullanıcı "stok girişi ve
  yayın yeterli" dedi. Sonradan ayrı iş olarak eklenebilir.
- **Shopper (müşteri mobil uygulaması) entegrasyonu.** Ürün fotoğrafının
  Shopper'da gösterilmesi şimdilik yapılmayacak. Kullanıcının Shopper için ayrı
  planı var (uygulamayı ürün satışı yapılabilen bir yapıya çevirmek); stok/katalog
  o iş için temel oluşturur, ayrı proje olarak sonra ele alınır.

## Mevcut durumun tespiti (kod doğrulamalı)

- **Ürün diye bir kayıt yok.** "Ürün", operatörün her seferinde elle yazdığı
  `(kod, fiyat)` çiftinden ibaret.
- Siparişe yazılan kod **izleyicinin yorumundan gelmiyor**; operatörün ekrandaki
  "Kod" kutusundan geliyor:
  [MainShellView.xaml:277](../../../OrderDeck.App/Views/MainShellView.xaml#L277) →
  [MainShellViewModel.cs:651](../../../OrderDeck.App/ViewModels/MainShellViewModel.cs#L651).
  Yorumu ayrıştıran tek satır kod yok.
- **Sipariş satırında adet alanı yok.** Sunucudaki
  [Order](../../../OrderDeck.LicenseServer/Domain/Order.cs) ve WPF'in `Label`'ı
  birebir aynı şekle sahip; her çift tık **1 adetlik** yeni bir satır üretir.
  Stok düşümü bu yüzden her zaman `−1`'dir; adet kavramı eklenmeyecek.
- Aynı kod bir yayın içinde sınırsız kez kullanılabiliyor. "A12'den kaç sattım"
  bugün sadece eşleşen satır sayısı.
- Raporlar koda göre hiç gruplamıyor (müşteri × gün).
- **Yedek sistemi zaten var:** `IsTentativeBackup`, `ParentLabelId`,
  `IsBackupPromoted` (`LabelService.cs:242-337`). Geçici yedekler "Y" damgasıyla
  basılıyor ama onaylanana kadar ciroya girmiyor.

---

# Faz ayrımı

Faz 1 tek plana sığmayacak kadar büyük olduğu için üçe bölündü. Her parça tek
başına çalışır ve bir sonrakinin temelidir.

| Faz | Kapsam | WPF'e dokunur mu |
|---|---|---|
| **1a — Katalog** | Kategori ağacı · ürün kartı · eksen/varyant modeli · fotoğraf (R2) · panelde CRUD ekranı · stok elemanı rolü | Hayır |
| **1b — Stok defteri + yayın bağı** | Stok hareketi tablosu · `ProductVariantId` bağı · eksen değeri eşleştirme · varyant seçici · WPF senkron · iptal/iade ters hareketi | Evet |
| **1c — Barkod** | Kod üretimi · etiket PDF'i · okutma (stok girişi + yayın) · arşivleme | Evet |
| **Faz 2 — WhatsApp** | Otomatik ürün bildirimi (IMAGE header + şablon onayı) · yedek bildirimi | Hayır |

**Neden bu sıra:** 1a hiç WPF'e dokunmadan tek başına değerlidir (eleman stok
girmeye hemen başlar). 1b olmadan 1c'nin okutacağı bir bağ yoktur. Faz 2 Meta
şablon onayına bağlı bir bekleme süresi taşır ve Faz 1 bitmeden test bile
edilemez.

---

# Veri modeli

## Kategori — sınırsız derinlik, ürün tek kategoride

Kullanıcının ERP alışkanlığı: `Erkek > Üst Giyim > Tişört`. Üç veya daha fazla
seviye gerekiyor.

- `Category`: `Id`, `LicenseId`, `ParentCategoryId` (nullable, kendine referans),
  `Name`, `Path`, `SortOrder`, `IsActive`
- **`Path` = id tabanlı yol**, örn. `/3/8/21/`. "Erkek"e tıklayınca alt ağacın
  tamamını getirmek için tek `StartsWith(path)` yeterli — özyinelemeli sorgu
  (recursive CTE) gerekmez.
  **Neden id tabanlı:** yalnız rakam ve `/` içerir, yani PostgreSQL'in harf
  duyarlılığından etkilenmez. İsim tabanlı yol olsaydı göçte arama davranışı
  sessizce değişirdi.
- Kategori taşınırsa alt ağacın `Path` değerleri toplu güncellenir.
- **Döngü koruması zorunlu:** bir kategori kendi alt ağacına taşınamaz.
- Ürün ağacın **herhangi bir seviyesine** bağlanabilir; yaprak olma zorunluluğu
  yok. Filtreleme her zaman alt ağacı kapsar.
- Ürün **tek** kategoride bulunur (çoklu kategori/etiket modeli reddedildi —
  bkz. Reddedilenler).

## Ürün (model)

- `Product`: `Id`, `LicenseId`, `CategoryId`, `Code`, `Name`, `DefaultPrice`,
  `Cost`, `IsArchived`, `ArchivedAt`, `CreatedAt`, `UpdatedAt`
- Eksen tanımı (aşağıya bakınız): `Axis1Name`, `Axis1Role`, `Axis2Name`,
  `Axis2Role` — hepsi nullable.
- **Maliyet Faz 1a'da kartta yer alır** (kullanıcı erken istedi). Ürün bazlı kâr
  = satış fiyatı − maliyet.
- Fotoğraf **ürün seviyesinde** tutulur, varyantta değil.

### Ürün kodu — otomatik üretilir, elle değiştirilebilir

Yayıncı kod uydurmakta zorlanıyor. Çözüm ikisi birden:

- Kart açılınca kod alanı **dolu gelir** (`A1`, `A2`, `A3`…). Hiçbir şey
  düşünmeden kaydedilebilir.
- Üzerine yazılabilir; elle yazılırsa benzersizlik kontrol edilir, çakışırsa
  uyarır.
- Önek ayarlanabilir (varsayılan `A`); `A999`dan sonra `B1`e geçer.
- **Sayaç geri gitmez.** Arşivlenen ürünün kodu tekrar kullanılmaz — yoksa eski
  siparişler yanlış ürünü gösterir.
- Benzersizlik **lisans başına** (`LicenseId` + `Code` benzersiz indeks). Her
  yayıncının kendi `A1`i olur.
- Otomatik atama yarışa dayanıklı olmalı: iki eleman aynı anda kart açarsa
  benzersiz indeks ihlalinde yeniden denenir.

## Eksen modeli — iki eksen, adı ve **rolü** ürün kartında

Sabit "Renk + Beden" adlandırması çanta ve kozmetikte kırılıyordu: çantanın
bedeni yok (sahte "Tek Beden" değeri girmek gerekirdi), rujda "renk" değil
**ton** var, parfümde **hacim** var, kremde hiç varyant yok.

Eksenlerin gerçek anlamı renk/beden değil, **rol**:

- **Satıcı ekseni** — barkod okutunca **sabitlenen** eksen (satıcı o parçayı
  elinde tutuyor)
- **İzleyici ekseni** — okutmadan sonra **açık kalan**, yorumdan gelen eksen

Ürün kartı her eksen için iki şey söyler: **adı ne** ve **hangi rolde**. Eksenler
tek tek kapatılabilir.

| Ürün | Eksen 1 | Eksen 2 | Varyant kodu | Yayında okutunca |
|---|---|---|---|---|
| Kolye | — | — | `K12` | doğrudan satılır |
| Çanta | Renk *(satıcı)* | — | `C45-SIYA` | doğrudan satılır, soru yok |
| Ev tekstili | Renk *(satıcı)* | — | `E7-BEJ` | doğrudan satılır |
| Tişört | Renk *(satıcı)* | Beden *(izleyici)* | `A12-SIYA-M` | renk sabitlenir, beden yorumdan |
| Ayakkabı | Renk *(satıcı)* | Numara *(izleyici)* | `S9-TABA-38` | aynı |
| Ruj | Ton *(izleyici)* | — | `R3-103` | seri açılır, tonu izleyici yazar |
| Parfüm | Koku *(satıcı)* | Hacim *(izleyici)* | `P2-VANI-50` | koku sabit, hacmi izleyici yazar |
| Krem | — | — | `KR8` | doğrudan satılır |

**Kazanımlar:**

- "Tek Beden" gibi sahte değer yok. Eksen kapalıysa hiç sorulmaz, kodda da yer
  kaplamaz.
- Ruj çözülüyor: tek eksen ama rolü *izleyici*. Önceki tasarımda 1. eksen hep
  satıcıya sabitliydi ve bu durum modellenemiyordu.
- Eşleştirme motoru değişmiyor — hâlâ "o an açık ürünün kendi eksen değerlerine
  karşı eşleştir". Eşleştirilen liste bazen bedendir, bazen ton.
- Ek tablo maliyeti sıfır.

### Eksen değeri girişi — set bağlayıcı değil, sadece kısayol

Önceki tasarımda "hazır beden setleri" (`S-M-L-XL`, `36-42`) üründe **saklanıyor**
ve varyant satırlarını bağlayıcı biçimde üretiyordu. Sorun: `S-M-L-XL` seçtin ama
mala sadece M ve L geldi — kartta hiç almadığın S ve XL satırları duruyor. Stok
eksiye düşebildiği için (aşağıya bakınız) bunlar yayında yanlışlıkla satılabilir.

**Karar: set üründe saklanmaz, yalnızca bir doldurma kısayoludur.**

- Eksen değerleri serbest yazılır: `M, L` → iki varyant satırı üretilir.
- İki eksen de doluysa varyant satırları **kartezyen çarpımdan** üretilir
  (`Siyah, Beyaz` × `M, L` → 4 satır). Üretilen listeden istenmeyen satırlar
  silinebilir — kartezyen çarpım da bağlayıcı değil, başlangıç önerisidir.
- `S-M-L-XL` / `36-42` / `Tek Beden` düğmeleri **sadece o kutuyu doldurur**;
  sonrasında elle silinip eklenebilir.
- Kart kaydedildikten sonra da varyant eklenip çıkarılabilir (mal sonradan
  gelir).

Böylece "eksik set" diye bir durum kalmaz: kartta ne varsa elde o vardır.

## Varyant

- `ProductVariant`: `Id`, `ProductId`, `Axis1Value`, `Axis1Code`, `Axis2Value`,
  `Axis2Code`, `VariantCode`, `Barcode`, `IsActive`
- Eksensiz üründe de **tek bir varyant satırı** oluşur. Böylece stok her zaman
  aynı yapıdan okunur, özel durum kodu yazılmaz.
- `VariantCode` = `Product.Code` + eksen kod parçaları, `-` ile birleşik.

### Kod parçası ASCII'ye türetilir (Code128 tuzağı)

Barkod sembolü **Code128** ve Code128 **ASCII** kodlar. `ç ğ ı İ ö ş ü` ASCII'de
yok — yani "Yeşil" doğrudan barkoda giremez.

**Kural:** eksen değerinin **görünen adı** serbest kalır; **kod parçası** ondan
otomatik türetilir (Türkçe karakter sadeleştirilir, büyük harfe çevrilir, 4
karaktere kısaltılır).

| Görünen | Kod parçası |
|---|---|
| Siyah | `SIYA` |
| Yeşil | `YESI` |
| Bej | `BEJ` |
| M | `M` |
| 38 | `38` |
| 103 Nude | `103` |

Türetilen kod **elle düzeltilebilir** (`SIYA` yerine `SYH`). Aynı ürün içinde
çakışırsa uyarır.

Bu kural aynı zamanda göç kuralını da karşılıyor: kodlar **yazma anında** büyük
harfe normalize edildiği için sorguda `ToUpper()` kullanılmaz ve eşleştirme
sağlayıcının collation'ına hiç bağlanmaz.

## Stok hareketi — defter, mutlak sayı değil

Bakiye **hiçbir yerde mutlak sayı olarak yazılmaz**, hareketlerin toplamıdır.

- `StockMovement`: `Id`, `LicenseId`, `ProductId`, `ProductVariantId` (nullable),
  `Quantity` (işaretli), `Reason`, `OrderId` (nullable), `OccurredAt`,
  `CreatedByUserId`, `Note`
- `Reason`: giriş · satış · iptal/iade · sayım farkı · arşivden dönüş

Kimse mutlak değeri ezmediği için iki taraf aynı anda aynı varyanta dokunsa bile
çakışma olmaz. Offline biriken satış hareketleri sonradan itildiğinde de sonuç
doğru kalır.

### Stok hangi seviyeden düşer

**Kural: siparişin bağlandığı en dar seviyeden.**

| Durum | Hareket |
|---|---|
| Kod kutusunda `A12`, varyant belirlenmemiş | `ProductId` dolu, `ProductVariantId` **boş** → üründen `−1` |
| Varyant belirlendi (okutma + yorum eşleşmesi ya da seçici) | `ProductVariantId` dolu → o varyanttan `−1` |

Çanta örneği: 10 stok var, yayıncı kod kutusuna `A12` yazdı, mesaja çift tıkladı
→ A12'nin toplamından 1 düşer. Beden/renk sorulmaz, seçici çıkmaz, akış durmaz.
Bu **varyantlı üründe de** geçerli — yayıncı varyantı belirtmediyse toplamdan
düşer.

**Bilerek kabul edilen sonuç:** kırılımsız satış yapılan üründe **toplam doğru,
varyant kırılımı eksik** kalır. "A12'den 10 sattım" doğrudur, "kaçı M'di"
bilinmez. Ürün listesi kırılımsız satış adedini gösterir ki yayıncı farkında
olsun; isterse sayım hareketiyle düzeltir.

Gerekçe: yayında **hız hiçbir zaman kırılım uğruna feda edilmez.**

### Stok bitince — uyar ama izin ver

- Kalan 0 iken sipariş **yine oluşur**, stok eksiye düşer, listede kırmızı
  görünür.
- Gerekçe: elde olup sisteme girilmemiş mal olabilir; canlı yayında satışı
  durdurmak pahalı.
- **Tasarım açısından kritik sonucu:** rezervasyon/kilit mekanizmasına gerek yok.
  Offline replikasyonu güvenli kılan şey tam olarak bu.

## Sipariş ↔ varyant bağı

`ProductId` ve `ProductVariantId` (ikisi de nullable) **iki yere birden** eklenir:

1. WPF yerel SQLite'taki `labels` tablosu
2. Sunucudaki [Order](../../../OrderDeck.LicenseServer/Domain/Order.cs) entity'si

Mevcut `SessionOrderSyncService` itme akışı bu iki alanı da taşıyacak şekilde
genişletilir.

**Geçmiş veri:** eski kayıtlar ürünlere geriye dönük bağlanmayacak. Stok sıfırdan
başlar; eski satırlarda iki alan da `null` kalır.

---

# Yayın içi akış

**Satış modu: model açık, tüm değerler aynı anda satışta.** Satıcı "bu tişört 500
TL, bedeninizi yazın" der; S/M/L aynı anda satıştadır. Tek beden açıp sırayla
ilerleme kullanılmıyor.

## Barkod okutma → modeli + satıcı eksenini açar

Satıcı eline aldığı parçayı okutur → sistem etiketin bağlı olduğu **modeli** ve
**satıcı ekseninin değerini** aktif eder (`A12 · Siyah`); **izleyici ekseni açık
kalır**. Kod ve fiyat alanları kendiliğinden dolar, elle yazma yok. Farklı renge
geçmek için o renkten bir parça okutulur.

Barkod etiketi **varyant başına** basılır (stok elemanı her parçaya yapıştırır),
çünkü stok varyant seviyesinde tutulur. Okutma, varyantı bulup üst modeline
çıkar.

Teknik not: barkod okuyucular klavye gibi davranır (HID). Ana pencerede hızlı
giriş + Enter yakalayan bir dinleyici yeterli; sürücü veya donanım entegrasyonu
gerekmez.

## İzleyici ekseni eşleştirme

Serbest metin ayrıştırılmaz; yalnızca **o an açık ürünün kendi eksen değerlerine**
karşı eşleştirilir (5-6 seçenekli kapalı küme → isabet yüksek).

Normalizasyon: büyük/küçük harf, Türkçe karakter, boşluk ve noktalama temizlenir;
dolgu kelimeler atılır ("beden", "bedeni", "numara", "no"); eş anlamlılar eşlenir
(`m`/`M`/`medium`/`orta` → **M**, `küçük`/`small` → **S**, `38 numara`/`38no` →
**38**).

| Durum | Davranış |
|---|---|
| Tek eşleşme | Varyant otomatik seçilir, sipariş satırında görünür |
| Sıfır eşleşme ("bana da", boş, olmayan `XXL`) | Varyant seçici açılır |
| Birden fazla eşleşme ("M ve L", "38-39") | **Tahmin etmez**, seçici açılır |

**Kural: sistem asla sessizce tahmin etmez.** Yanlış beden = yanlış kargo = iade +
kargo bedeli. Bir fazla tıkın maliyeti bunun yanında önemsiz.

**Bilinçli olarak yapılmayacak:** yakın tahmin (fuzzy matching). `l` yazan `1` mi
`L` mi belirsizdir; böyle durumlarda akıllı olmaya çalışmak sessizce yanlış
yapmaktan başka işe yaramaz.

## Varyant seçici — çoklu seçim

Müşteri birden fazla değer istediğinde ("M ve L") operatör seçiciden **istenenleri
işaretler** ve **her biri ayrı sipariş satırı** olur. Sipariş satırında adet alanı
olmadığı için 2 beden = 2 satır = 2 ayrı `−1`.

Yani seçici sadece belirsizliği çözmez, **kasten çoklu ekleme aracıdır.**

Seçilen varyant her zaman sipariş satırında görünür ve **baskıdan önce
değiştirilebilir**.

## Fiyat

Ürün kartındaki fiyat **varsayılan**; yayında operatör değiştirebilir (indirim
vb.). Siparişe **o anki fiyat damgalanır** — bugünkü davranışın aynısı. Kart
fiyatı geçmiş siparişleri etkilemez.

---

# Barkod

- **Sembol: Code128.** Serbest metin taşır, varyant kodunu doğrudan kodlar,
  ücret/kayıt gerektirmez. EAN-13 perakende standardıdır ama GS1 üyeliği ve firma
  öneki satın almayı gerektirir; kendi deposunda kullanılacağı için gereksiz.
- Ürünlerin hazır barkodu yok → barkodu **biz üretiyoruz** ve etiketi biz
  basıyoruz.
- **Okuma iki yerde:** stok girişi (eleman malı stoğa alırken) ve yayın (ürün
  okutulunca kod/fiyat/satıcı ekseni otomatik dolar).

## Etiket basma — panelden PDF, termal yazıcı

**Çelişki ve çözümü:** mevcut etiket basma altyapısı WPF'te
(`OrderDeck.Labeling/LabelPrinter.cs`, Windows yazıcı altyapısı,
`AppSettings.PrinterName`). Ama barkod etiketini basacak kişi stok elemanı ve o
panelde (web) çalışıyor; tarayıcı Windows yazıcı altyapısını süremez.

**Karar:** panel **barkodlu etiket PDF'i üretir**, eleman tarayıcının yazdır
penceresinden **termal etiket yazıcısına** basar. Elemanın makinesine kurulum
gerekmez.

Sonuçları:

- Sunucuya iki yeni yetenek girecek: **barkod çizimi** ve **PDF üretimi**.
  Bugünkü PdfPig yalnızca PDF *okuyor*. **Kütüphane kararı Faz 1c'nin ilk adımı**
  ve **lisans koşulları doğrulanmalı** — bu ticari bir ürün, gelir eşiğine bağlı
  ticari lisans isteyen kütüphaneler var.
- PDF sayfa boyutu etiket boyutuna birebir eşit üretilmeli.
- **Bilinen tuzak:** tarayıcı yazdırma penceresi varsayılan olarak ölçekleyebilir
  ("sayfaya sığdır"). Barkod ölçeklenirse okunmaz. Yazıcı sürücüsünde doğru etiket
  boyutu tanımlı olmalı ve yazdırma %100 ölçekle yapılmalı. Kurulum talimatı
  gerekiyor.
- Termal yazıcı Windows sürücüsü üzerinden çalışacak (Argox/Zebra/TSC/Xprinter
  hepsi sürücüyle gelir). ZPL/EPL gibi yazıcıya özel dil kullanılmayacak —
  tarayıcıdan ham veri gönderilemez.

**Etiket boyutu ayarlanabilir** (çok yayıncılı ürün, sabitlenemez). Lisans başına
ayar; PDF sayfa boyutu bu ayardan üretilir.

**Etiket içeriği:** barkod + ürün adı + fiyat + varyant kodu.

> Not — kullanıcıya iletildi, kararı bilerek verdi: yayında indirim yapılırsa
> etiketteki fiyat gerçek satış fiyatından farklı kalır (fiyat basım anındaki kart
> fiyatıdır). Ayrıca fiyat etiketi müşteriye giden ürünün üstünde kalır.

---

# Fotoğraf

**Amaç:** "birine ürün eklediğim zaman o ürünün fotoğrafını WhatsApp üzerinden
otomatik göndersin, fiyat bilgisi ile beraber."

- Depolama: **Cloudflare R2**, `BroadcastPost` deseniyle birebir —
  `MediaObjectKey`, `MediaContentType`, `MediaSizeBytes`, `MediaWidth/Height`.
  SigV4 tuzakları daha önce çözüldü.
- Yüklenen görsel **yeniden boyutlandırılıp sıkıştırılacak**; orijinal ham dosya
  saklanmayacak.
- Ürün başına fotoğraf sayısı sınırlı olacak.
- Fotoğraf **ürün (model) seviyesinde** tutulur, varyant başına değil — aksi halde
  sayı 12 katına çıkar.

**Maliyet:** R2 $0.015/GB-ay, egress ücretsiz, ilk 10 GB ücretsiz. 200 yayıncı ×
5.000 ürün × 1 foto (200 KB) = 200 GB ≈ **$3/ay**. Gerçekçi senaryoda önemsiz.

## WhatsApp gönderimi (Faz 2)

**Var:** WhatsApp Cloud API prod'da canlı (FB app'ten ayrı: `1539000484386031`),
`R2WhatsAppMediaStore`, `WhatsAppServiceWindow` (24 saatlik servis penceresi
takibi), `WaSendAttempt` audit kayıtları.

**Yok:** `CloudApiWhatsAppSender.SendTemplateAsync` yalnızca **body** parametresi
destekliyor — görsel başlık (IMAGE header) yok. Eklenecek.

**Meta kuralı akışı belirliyor:**

- Müşteri son 24 saatte yazdıysa → serbest mesaj, fotoğraf + fiyat doğrudan gider,
  **ücretsiz**
- Yazmadıysa → **onaylı şablon** şart; görsel başlıklı yeni "utility" şablonu Meta
  onayından geçmeli (`odeme_hatirlatma` sürecinin aynısı)

Maliyet engel değil: Türkiye'de utility mesajlarına 2026-07-01'de %84 indirim
geldi, marketing ~$0.013. Yayında 200 sipariş = birkaç dolar.

---

# Yedek sistemi

Kullanıcı kararı: yedek sistemi bugünkü haliyle sürer.

**Stok kuralı: geçici yedek stoktan düşmez.** Sadece asıl sipariş düşer.

- Asıl sipariş oluşur → `−1`
- Geçici yedek oluşur → stok hareketi **yok**
- Asıl iptal olur → `+1`
- Yedek öne alınır → `−1`

Net sonuç doğru kalır, çift sayma olmaz. Yedek de düşseydi tek ürün için iki adet
düşerdi.

## İptal / iade — stok geri artar

İptal (`CancelledAt`) ve iade stoğu **otomatik** geri artırır (`+1`), ayrı bir
hareket satırı olarak. Silme değil, ters hareket — defter izlenebilir kalır.

## Yedek bildirimi (Faz 2)

Asıl sipariş iptal olduğunda, o ürünün yedeğindeki kişiye **WhatsApp mesajı**
gider: *"bu ürün iptal oldu, size ekleyelim mi?"*

- **Evet/Hayır düğmeli interactive mesaj.** Serbest metin cevap beklenmiyor.
  Servis penceresi içindeyse ücretsiz; dışındaysa hızlı-cevap düğmeli şablon Meta
  onayından geçmeli. Düğme cevabı webhook'a `button` tipinde döner.
- **Düğme kullanıldığı için cevap kesin** → onay gelince yedek **otomatik öne
  alınır** (`IsBackupPromoted`), operatör onayı beklenmez.
- **Teklif sırayla gider:** önce 1. yedeğe. **1 saat** içinde cevap gelmezse
  teklif düşer, 2. yedeğe geçer. Aynı anda birden fazla kişiye teklif gitmez.
  Süre ayarlanabilir olacak.
- Aynı anda tek açık teklif olduğu için "üzgünüz, satıldı" mesajı gerekmez.
- Yarış durumu: onay geldiğinde ürün başkasına gitmiş olabilir. Stok eksiye
  düşebildiği için bu bir kilit sorunu değil, ama kullanıcıya ne döneceği planda
  tanımlanmalı.

---

# Mimari

## Sunucu = ana kayıt, WPF = yerel replika

- Ürün kartı, kategori, varyant, stok hareketi **sunucuda** yaşar.
- Stok elemanı **Panel'den** girer → Windows kurulumu yok, tabletten girilebilir,
  yayın sürerken çalışabilir. Panel altyapısı hazır (`Controllers/Panel/`:
  müşteri, sipariş, ödeme, kargo, operatör yönetimi).
- WPF katalog + stok bakiyelerini **yerel SQLite'a çeker**.
  **Gerekçe (pazarlık konusu değil):** uygulama bugün internetsiz de sipariş
  alabiliyor. Stok yalnız sunucuda olursa yayının ortasında bağlantı kopunca
  sipariş almak durur — en pahalı anda gerileme olur.
- Satışlar yerelde oluşur, sunucuya itilir.

**Mevcut desenler kullanılacak, yeni mimari icat edilmeyecek:**

- Çekme (sunucu → WPF, cursor'lu, idempotent): `IntakeFormSyncService`
- İtme (WPF → sunucu): `SessionOrderSyncService`, `ShipmentSyncService`,
  `PaymentSyncService`

## WPF'e hareket değil, **bakiye anlık görüntüsü** iner

Tüm hareket geçmişini WPF'e indirmek gereksiz ve büyür. Bunun yerine:

- Sunucu her ürün/varyant için **hesaplanmış bakiyeyi** cursor'lu olarak gönderir
  (`UpdatedAt` üzerinden artımlı).
- WPF ekranda gösterirken bu anlık görüntüden **henüz itilmemiş yerel satışları**
  düşer.
- **Gösterilen = sunucu bakiyesi − yerel bekleyen hareketler.**

Böylece çevrimdışıyken de ekrandaki sayı doğru ilerler; bağlantı gelince
hareketler itilir ve bir sonraki anlık görüntü zaten onları içerir.

## Çok yayıncı (multi-tenant)

Sunucudaki her varlık `LicenseId` ile kiracıya bağlı (`BroadcastPost`, `Order`,
`Payment`, `Shipment`, `OperatorUser`…). **Kategori, ürün, varyant ve stok
hareketi de `LicenseId` alır.** Mevcut kural, yeni iş değil.

Mevcut Panel controller convention testi deseni yeni uçlara da uygulanacak; yeni
entity'lerin `LicenseId` filtresi olmadan sorgulanamayacağı testle korunacak.

## Stok elemanı — ayrı hesap, yalnız stok yetkisi

Elemanın **kendi hesabı** olur. Mevcut `OperatorUser` altyapısı üzerine, stok
yetkisiyle sınırlı bir rol.

**Yetki: yalnız stok.** Ürün kartı açar, stok girer, etiket basar. **Müşteri,
sipariş, ödeme ve ciro bilgilerini göremez.**

Gerekçe: dışarıdan alınan bir çalışanın müşteri telefon/adres bilgisine ve ciroya
erişmemesi gerekiyor. KVKK açısından da doğru olan bu.

Bu, panelde **yetkiye göre kısıtlanmış ilk rol** olacak. Kural: **varsayılan
olarak her uç kapalı**, açıkça izin verilenler açılır.

## Arşivleme

Katalog tek yönlü büyür (ürün kartı silinemez, sipariş geçmişi ona bağlı). Yıllar
içinde 150.000 kalemlik bir katalogda arama/okutma/senkron yavaşlar.

**Kural: 6 ay hareket görmeyen ürün arşive alınır.**

- Aktif katalogdan, aramadan ve WPF'in indirdiği listeden çıkar
- Kaydı durur; geçmiş sipariş açıldığında ürün bilgisi görünür
- Barkodu okutulursa "bu ürün arşivde, geri alalım mı?" denir — mal geri gelirse
  tek tıkla canlanır
- Arşiv ürünlerinin görselleri R2'nin ucuz katmanına taşınır
  ($0.015 → $0.01/GB-ay)
- **Koşturucu: Hangfire yinelenen işi** (sunucuda Hangfire zaten kurulu ve prod'da
  çalışıyor). Günde bir kez yeter.

Fotoğrafların bir süre sonra silinmesi **planlanmıyor** — arşivde ucuz katmanda
süresiz kalır.

---

# Kapasite ve PostgreSQL göçü (tarihsel kayıt — göç ERTELENDİ)

**Prod'da SQL Server `Express` çalışıyor** (`deploy/docker-compose.yml:10` →
`MSSQL_PID: Express`). Express'in **veritabanı başına 10 GB sert limiti** var;
aşıldığında yazma işlemleri yavaşlamaz, **durur**.

## Ölçüm (2026-08-11, prod'da salt-okunur sorgu)

| Ölçü | Değer |
|---|---|
| `OrderDeckLicense` ayrılmış dosya | 72,0 MB |
| **Gerçekten kullanılan** | **24,3 MB** |
| Express limiti | 10 240 MB |
| **Doluluk** | **%0,24** |
| `Licenses` / `Activations` | **3 / 3** |
| En büyük tablo | `Orders` — 7.204 satır / 3,2 MB |

Sipariş başına ~465 byte. 10 GB'a çarpmak için **~23 milyon sipariş** gerekiyor.
Bugünkü zirve hızla (2026-07: 4.625 sipariş/ay) yüzyıllar sürer; yayıncı sayısı
100 katına çıksa bile ~4 yıl.

## Karar geçmişi

- **2026-08-07:** PostgreSQL'e geçilecek, göç stoktan ÖNCE yapılacak.
- **2026-08-08 (revize):** sıra tersine döndü — **arayüz → stok → göç**. "Önce
  göç" gerekçeleri ölçümle çürüdü.
- **2026-08-11 (doğrulandı):** prod ölçümü yukarıdaki tabloyu verdi. **Göç
  ertelendi, stok önce.**

Çürüyen gerekçeler:

- ~~Migration'lar sağlayıcıya özgü, stok SQL Server'a yazılırsa iş iki kez
  yapılır.~~ Sunucuda **31 migration** var ve **hiçbirinde ham SQL yok**
  (`migrationBuilder.Sql` = 0; uygulama kodunda `FromSql`/`ExecuteSql` = 0 —
  2026-08-11'de yeniden doğrulandı). Göçte migration'lar tek tek taşınmıyor,
  hepsi silinip Npgsql için tek baz migration üretiliyor. 31 yerine 37 olması işi
  değiştirmez.
- ~~Stok en çok satır ekleyecek özellik; önce yazıp sonra taşımak veriyi
  büyütür.~~ Bugünkü katalog ≈60 MB mertebesinde; 10 GB tavanı 200 yayıncılı
  senaryonun sorunu.

## Göçün gerçek riski (stoktan bağımsız, stok yazılınca büyümüyor)

1. **`License.RowVersion`**
   ([Domain/License.cs:25](../../../OrderDeck.LicenseServer/Domain/License.cs#L25)) —
   SQL Server `rowversion`'ın Postgres karşılığı yok, Npgsql `xmin`'e eşliyor ve
   şekli farklı. Bu alan aktivasyon slot yarışını engelliyor; sessizce bozulursa
   iki makine aynı lisans slotunu kapar.
2. **Büyük/küçük harf duyarlılığı** — 43 LINQ `Contains`/`StartsWith` çağrısı var;
   Postgres varsayılan olarak duyarlı, SQL Server değil. Arama davranışı sessizce
   değişir.

**747 sunucu testi InMemory sağlayıcıyla koşuyor** → ikisini de yapısal olarak
yakalayamaz. Göçün asıl işi tablo taşımak değil, bu testleri **gerçek Postgres'e**
karşı koşturacak altyapıyı kurmak.

**Göçün tetikleyicisi tarih değil**, şu ikisinden biri: yayıncı sayısının çift
haneye çıkması, ya da prod veritabanının birkaç GB'a yaklaşması.

## Stok yazılırken uyulacak iki kural (göçü ucuz tutmak için)

1. Ürün/varyant/barkod/kategori kodları **yazma anında** büyük harfe ve ASCII'ye
   normalize edilsin, sorguda `ToUpper()` ile değil. Kategori yolu id tabanlı
   olsun. Böylece eşleştirme sağlayıcının collation'ına hiç bağlanmaz.
2. **Ham SQL yok** kuralı korunsun (bugünkü sıfır durumu bozulmasın).

**Göç ayrı bir proje olarak ele alınacak** (kendi spec'i ve planı). Neden Postgres:
ücretsiz, boyut sınırı yok, Npgsql EF Core'un Microsoft dışı en olgun sağlayıcısı,
aynı Docker compose'da çalışır, ileride yönetilen hizmete (Neon/Supabase/RDS)
geçme seçeneği açık kalır. WPF'in yerel SQLite'ı etkilenmiyor.

---

# Reddedilen seçenekler

| Seçenek | Neden reddedildi |
|---|---|
| **Sınırsız N eksen (Shopify modeli)** | "Hangi eksen izleyicinin?" sorusu belirsizleşir; kartezyen varyant üreteci ve UI karmaşıklaşır, canlı yayında hız kaybettirir. İki eksen + rol tüm örnekleri karşılıyor. |
| **Ürün birden fazla kategoride (etiket modeli)** | Ara tablo gerektirir ve "bu kategoride kaç ürün var" sayımını çift saydırır. Tek kategori + alt ağaç filtresi yeterli. |
| **Kategori önekli ürün kodu** (`TIS-001`) | Ürün kategorisi değişince kod ya yalan söyler ya değişmek zorunda kalır. Kod bir kimliktir, anlam taşımamalı. |
| **Yayın overlay'i** (aktif ürün kodunu yayına basmak) | Gerekliliği ortadan kalktı: izleyici zaten kod yazmıyor, kod operatörün kutusunda duruyor. Ayrıca telefondan yayın yapılıyorsa masaüstü overlay yayına fiziksel olarak giremez. |
| **Yakın tahmin (fuzzy matching)** | `l` yazan `1` mi `L` mi belirsizdir; sessizce yanlış beden göndermek iade + kargo bedeli demek. |
| **EAN-13 barkod** | GS1 üyeliği ve firma öneki satın almak gerekir; kendi deposunda kullanılacağı için gereksiz. |
| **Katalogu ayrı veritabanına koymak** | Express limiti veritabanı başına olduğu için kapasiteyi ikiye katlar ama çözüm değil erteleme — veritabanları arası `JOIN` yapılamaz, yedekleme ve migration iki kat karmaşıklaşır. |
| **SQL Server Standard / Developer** | Standard çekirdek başına binlerce dolar. Developer ücretsiz ve sınırsız ama **prod kullanımı lisansa aykırı**. |

---

# Açık konular

- [ ] **Barkod çizimi + PDF üretimi kütüphanesi** (Faz 1c'nin ilk adımı). Lisans
      koşulları doğrulanmalı — ticari üründe gelir eşiğine bağlı lisans isteyen
      kütüphaneler var.
- [ ] **Ürün fotoğrafının başka yerlerde görünmesi** (WPF ürün seçicide, yayın
      ekranında) — ertelendi, Faz 1a'yı bloke etmiyor.
- [ ] **Yedek teklifinde yarış durumu**: onay geldiğinde ürün başkasına gitmişse
      kullanıcıya ne dönülecek? (Faz 2'de tanımlanacak.)
- [ ] **WPF yerel replikanın sınırı**: çok büyük kataloglarda yalnız aktif
      ürünlerin yerelde tutulması gerekebilir. Faz 1b planında ölçülmeli.
