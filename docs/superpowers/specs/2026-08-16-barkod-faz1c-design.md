# Faz 1c — Barkod (tasarım)

**Tarih:** 2026-08-16
**Kaynak:** `docs/superpowers/specs/2026-08-07-stok-sistemi-design.md` (satır 341-393)
**Durum:** onaylandı, uygulama planı bekliyor

Stok sisteminin dört parçasından üçüncüsü. 1a (katalog) ve 1b (stok defteri +
yayın bağı) bitti; bu belge 1c'yi tanımlıyor. Faz 2 (WhatsApp) ayrı.

---

## Kaynak spec'te ölen varsayım

Kaynak spec şunu diyor: *"barkot yükü varyant kodudur, ama basım anındaki hâli
`ProductVariant.Barcode`'a kopyalanıp bir daha değişmez"*. Gerekçesi doğruydu:
`VariantCode` ürün kodundan türetiliyordu, ürün kodu `A1 → B7` olunca rafa
yapıştırılmış bütün etiketler sessizce geçersiz kalıyordu.

Ama **`VariantCode` kavramı plan 3/3'te sistemden tamamen kaldırıldı**
(`920c40c`). Bugün varyantın kimliği `Id` + normalize eksen değerleri; kod yalnız
ürün + satıcı ekseni seviyesinde, ayrı bir kaynakta (`ProductBroadcastCode`)
yaşıyor. Yani barkodun içine ne yazılacağı yeniden karara bağlandı.

Bu tasarımın çıkış noktası: **yükü anlamsız yapalım.** Türetilmişlik ortadan
kalkınca "basım anında dondur" mekaniğine de gerek kalmıyor.

---

## Kütüphane lisansı — kaynak spec'in "ilk adım" dediği madde

Kaynak spec bu maddeyi Faz 1c'nin ilk adımı ilan etmişti: *"bu ticari bir ürün,
gelir eşiğine bağlı ticari lisans isteyen kütüphaneler var"*. Doğrulama
2026-08-16'da yapıldı:

| İhtiyaç | Aday | Lisans | Karar |
|---|---|---|---|
| Barkod kodlama | **ZXing.Net 0.16.11** | **Apache 2.0**; `net5.0+` hedefinde çekirdek sınıflar hiçbir görüntü kütüphanesine bağlı değil | ✅ **seçildi** |
| | BarcodeLib | Apache 2.0 ama `System.Drawing.Common`'a bağlı | ⚠️ WPF'te sorun değil, sunucuda olurdu |
| PDF üretimi | PDFsharp 6.2.4 | Gerçek MIT, `net10.0` hedefi var | ✅ ama **gerek kalmadı** |
| | QuestPDF | "Community" = kaynak-açık, **MIT değil**; **$1M yıllık ciro eşiği** (şirketin toplam cirosu) | ❌ eşikli |
| | iText7 | AGPL | ❌ |

**Sonuç: PDF kütüphanesi hiç gerekmiyor** (basım kararı bunu ortadan kaldırdı,
aşağıya bakın). Tek yeni paket ZXing.Net, yalnız WPF tarafında.

---

## Kararlar

### Barkod

- **Sembol: Code128.** Ucuz 1D lazer okuyucuların standart repertuvarında
  (EAN/UPC, Code39, Code128, ITF). Kaynak spec'in kararı korunuyor.
- **Yük: 10 haneli, lisans içinde artan numara** (`0000000001`). Anlamsız —
  hiçbir iş verisine bağlı değil, dolayısıyla ürün kodu/isim/eksen/fiyat
  değişse de etiket asla geçersizleşmez.
- **Kapsam: varyant başına bir barkod.** Stok varyant seviyesinde tutuluyor;
  ürün seviyesinde barkod okutulunca hangi renk/beden olduğu belli olmaz,
  düşülecek defter satırı bulunamaz. "Renk × Beden" ürününde 6 varyant = 6
  barkod.

**Neden 10 hane / neden anlamlı metin değil** — çizgili barkodun genişliği
karakter sayısıyla doğru orantılı büyür. 203 dpi termal yazıcıda güvenli çubuk
kalınlığı 0,25 mm:

| Yük | Kodlama | Genişlik | 40 mm etikete |
|---|---|---|---|
| 10 haneli sayı | Code128-C (2 hane/simge) | ≈ 27,5 mm | sığar |
| `SK00001-KIRMIZI-M` (17 krk) | Code128-B | ≈ 55 mm | sığmaz |
| Varyant GUID'i (32 hane) | Code128-B | ≈ 102 mm | **basılamaz** |

GUID'i 40 mm'ye sıkıştırmak 0,098 mm çubuk gerektirir; 203 dpi yazıcının tek
noktası 0,125 mm — okuma hatasından önce baskı hatası çıkar.

**Neden lisans başına sayaç, global değil:** numaralar 1'den başlıyor ve kısa
kalıyor. Okutma zaten lisans kapsamında çözümleniyor, global benzersizliğe
ihtiyaç yok. Benzersizlik indeksi bu yüzden `(LicenseId, Barcode)`.

### Barkod alanı ve doldurulması

- Alan **elle yazılabilir/okutulabilir**. Ürünün üstünde zaten tedarikçi barkodu
  varsa eleman onu okutur, o kullanılır — o ürün için etiket basmaya gerek
  kalmaz.
- **Boş bırakılırsa sunucu kaydetme anında üretir.**
- Panelde "Oluştur" düğmesi kalır ama bir *kapı* değil, bir *kolaylık*:
  numarayı kaydetmeden önce görmek isteyen için.

**Kuralın doğru ifadesi:** "kullanıcı barkod yazmak zorunda" değil, **"barkodsuz
varyant var olmasın"**. İkincisi daha zayıf bir şart ve gelecekteki Excel toplu
girişini bedavaya çözüyor: barkod sütunu doluysa o kullanılır, boşsa sunucu
üretir, hiçbir satır bu yüzden reddedilmez. Değişmez kural her iki yolda da
korunduğu için kolon NOT NULL kalabilir.

**Doğrulama:** lisans içinde benzersiz · **en fazla 64 karakter** · Code128'in
basabildiği karakterler (ASCII 32-126). Numara üretici, elle girilmiş bir
değerle çakışırsa bir sonraki numaraya atlar.

64 uydurma değil, şemada zaten var:
[`CatalogLimits.Barcode = 64`](../../../OrderDeck.LicenseServer/Domain/CatalogLimits.cs)
ve kolon `nvarchar(64)`. Bizim ürettiğimiz numara 10 hane; 64 yalnız elle
girilen tedarikçi barkodlarının tavanı. **Uzun bir tedarikçi barkodunu biz
basmayız** — zaten basılı bir etiketle geldiği için elle giriliyor; 60 mm etikete
sığmayacak bir yükü basmaya çalışmak yerine etiket basma ekranı o varyantı
"barkodu hazır" diye işaretler ve atlar.

### Basım — yalnız WPF, doğrudan yazıcıya

Kaynak spec panelden PDF üretmeyi seçmişti; gerekçesi tekti: *"etiketi basacak
kişi stok elemanı ve o panelde çalışıyor"*. Kullanıcı doğruladı ki basım
yayıncının makinesinden yapılacak. Karar değişti — **etiket WPF'ten doğrudan
yazıcıya gider**, ERP'lerin (Logo, Netsis, Nebim, SAP) yaptığı gibi.

Bu kararın sildikleri:

| Panelden PDF olsaydı | WPF'ten doğrudan basımda |
|---|---|
| Tarayıcının "sayfaya sığdır" ölçeklemesi — kaynak spec'in en büyük bilinen riski | Yok; yazıcıya doğrudan gidiyor |
| Sunucuya PDF kütüphanesi (+ lisans eşiği tartışması) | Gerekmez |
| Sunucuda ikinci bir etiket-boyutu ayarı | `AppSettings.LabelWidthMm/HeightMm` tek kaynak |
| Sıfırdan çizim altyapısı | `LabelPrintDocument` yolu yeniden kullanılır |
| Elemana "%100 ölçekle bas" kurulum talimatı | Gerekmez |

### Etiket

**Boyut: 60 × 30 mm** — uydurulmuş bir ölçü değil, uygulamada zaten var:
`AppSettings.LabelWidthMm = 60`, `LabelHeightMm = 30`, `LabelGapMm = 5`
([AppSettings.cs:10](../../../OrderDeck.Core/Settings/AppSettings.cs)). Yayıncıların
elinde bu ruloyu kullanan termal yazıcı zaten var; sipariş etiketiyle **aynı
rulo**, yeni alım yok.

60 mm genişlik barkod için bol: 10 haneli yük 0,25 mm çubukla 27,5 mm. Çubukları
X ≈ 0,4 mm basıyoruz (≈ 44 mm) — okuma toleransı belirgin biçimde yükseliyor.

**İçerik:**

```
┌──────────────────────────────────────┐
│ Zarif Kolye                          │  ürün adı (sığmazsa kırpılır)
│                                      │
│ █▐▌█▐▌▌██▌▐█▐▌██▐▌█▐█▌▐█▐▌█▐▌▌██     │  Code128 çubukları
│ 0000012345                           │  numara (gözle okunur)
│ Buz                        89,90 ₺   │  yayın kodu · fiyat
└──────────────────────────────────────┘
                60 × 30 mm
```

Numara bilerek yazılıyor: okuyucu arızalandığında ya da barkod yırtıldığında
elle giriş yolu açık kalsın.

### Okutma

**Yayında (WPF):** okuyucu HID klavye gibi davranır, okunan metin mevcut **kod
kutusuna** düşer. Çözümleyici önce yayın kodu tablosuna bakar, bulamazsa
`Barcode` kolonuna bakar; eşleşirse ürün kartı o ürüne ve varyantın **satıcı
eksenine** kurulur.

- Ayrı bir kutu/mod yok: operatörün en hızlı olması gereken anda odak
  değiştirmesi gerekmiyor.
- Çakışma riski yok: yayın kodları kısa kelime, üretilen barkodlar 10 haneli saf
  sayı. Yine de panele bekçi konuyor: **10 haneli saf sayı yayın kodu olarak
  kabul edilmez.**
- **İzleyici ekseni okutmadan gelmez.** Operatörün elindeki fiziksel parçanın
  bedeni, o üründen alan her müşterinin bedeni değil; izleyici ekseni her alıcı
  için yorumdan çözülmeye devam eder (plan 3/3'teki `AxisValueMatcher`).
- Çözümleme **yerel replikadan** yapılır → **çevrimdışı çalışır**. Yayın
  akışının ağ hıçkırığına dayanması gereken tek yeri burası.

**Panelde (stok girişi):** stok ürün ekranının arama kutusu barkodu da tanır;
okutunca varyant seçilir.

---

## Değişecek yerler

### Sunucu (`OrderDeck.LicenseServer`)

| Ne | Ayrıntı |
|---|---|
| `ProductVariant.Barcode` | `string?` → `string` (NOT NULL); uzunluk sınırı `CatalogLimits.Barcode` (zaten kurulu) |
| Benzersiz indeks | `(LicenseId, Barcode)` |
| Yeni `BarcodeCounter` | `LicenseId` (PK) + `Next` (long). Atomik artırma. |
| Yeni `BarcodeAllocator` servisi | `Next(licenseId, count)` — sayacı atomik artırır, üretilen numara elle girilmiş bir değerle çakışırsa atlar |
| `VariantRequest` | `Barcode` alanı eklenir (bugün yok — panel barkod yazamıyor) |
| Doldurma noktası | `PanelProductVariantsController`'ın **üç yazma yolu**: `Create` (satır 58), `CreateBulk` (satır 169), `Update` (satır 205). Barkod boşsa `SaveChangesAsync`'ten önce `BarcodeAllocator`'dan alınır. Depoda başka `new ProductVariant` yok — grep ile doğrulandı. |
| `POST /api/panel/barcodes/next?count=N` | Panel formunun "Oluştur" düğmesi için N numara |
| Yayın kodu bekçisi | 10 haneli saf sayı yayın kodu olarak reddedilir (409/400) |
| Göç | Sayaç tablosu + mevcut varyantlara geri-doldurma + kolonu NOT NULL yapma + indeks |
| `PanelProductVariantsController` XML doc (satır 20) | "barkot yükü basım anında yazılır" cümlesi ölü varsayıma dayanıyor, düzeltilir |

**Zaten hazır olan** (bu fazda iş yok): `ProductVariant.Barcode` kolonu,
`CatalogLimits.Barcode = 64`, `LicenseDbContext` eşlemesi, panel okuma DTO'ları
(`PanelProductsController:55/801`, `PanelProductVariantsController:433`) ve
katalog pull ucunun `Barcode`'u göndermesi
([LicensesWpfCatalogPullController.cs:44/169](../../../OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs)).

### Panel (`OrderDeck-Mobile/apps/panel`)

| Dosya | Ne |
|---|---|
| `screens/UrunScreen.tsx` | Varyant tablosuna `Barkod` sütunu; satır başına "Oluştur"; tablo başına "Boşları doldur" |
| `screens/StokUrunScreen.tsx` | Arama kutusu barkodu da tanır → okutunca varyant seçilir |

### WPF

**Replika zaten barkod taşıyor.** `CatalogVariant.Barcode` göç 025'te
**nullable `TEXT`** olarak açılmış, 027'de tablo yeniden kurulurken korunmuş,
`CatalogReplicaRepository` yazıp okuyor ve `CatalogSyncService.ToVariants`
(satır 312-314) tel modelinden eşliyor. Yani bu fazda yeni kolon, yeni alan ya
da yeni eşleme **yok**; alan bugün boş geliyor çünkü sunucu doldurmuyor.

Kolonun sunucuda NOT NULL, replikada nullable kalması bilinçli — plan 3/3'ün
dersi. `CatalogVariant.VariantCode` yerelde `TEXT NOT NULL` olduğu için, sunucu
o alanı göndermeyi bıraktığında eski istemcilerde INSERT patladı, işlem geri
döndü ve **katalog senkronu sessizce öldü** (hata `CatalogSyncService`'te
Warning'e yutuluyor). Replika sunucunun sıkılığını taklit etmez.

| Dosya / yer | Ne |
|---|---|
| Göç `031_catalog_variant_barcode_index.sql` | Yalnız `CREATE INDEX IX_CatalogVariant_Barcode ON CatalogVariant(Barcode)` — kolon zaten var, 027'de indeks kurulmamış |
| `CatalogReplicaRepository.FindVariantByBarcode(string)` (yeni) | Barkoddan varyant → ürün |
| `BroadcastCodeResolver` | Yayın kodunda bulunamayan girdi için barkod araması (aşağıda) |
| `OrderDeck.Labeling/BarcodeLabelDocument.cs` (yeni) | ZXing `Code128Writer` → `BitMatrix` → `System.Drawing` dikdörtgenleri |
| `Views/Drawers/BarcodeLabelDrawer.xaml` (yeni) | Etiket basma çekmecesi (yerleşim aşağıda) |
| `ProductCard.xaml` | Varyant listesinin altına "Etiket bas" düğmesi |
| `ActiveProductBar.xaml:18` kod kutusu | `MaxLength` 32 → 64 · `CharacterCasing="Upper"` **kaldırılır** |
| `Directory.Packages.props` + `OrderDeck.Labeling.csproj` | ZXing.Net |

**Kod kutusundaki iki düzeltme neden şart:** kutu bugün 32 karakterle sınırlı
(yorumu "MaxLength = CatalogLimits.ProductCode") ve yazılanı büyük harfe
çeviriyor. Kutu artık barkod da kabul ettiğine göre tavanı
`max(ProductCode, Barcode) = 64` olmalı; yoksa uzun tedarikçi barkodu **sessizce
kırpılır**. `CharacterCasing` de küçük harf içeren bir tedarikçi barkodunu
sessizce bozar. Kaldırmanın bedeli yok: yayın kodu araması zaten
büyük/küçük harf duyarsız (`FindBroadcastCode` normalize edilmiş kolondan
bakıyor), barkod araması ise **birebir** eşleşmeli.

**Barkod çözümlemesinin sonucu:** barkod → varyant → ürün + o varyantın satıcı
ekseni değeri. Buradan aynı `BroadcastCodeResolution` kuruluyor — yani kart,
operatör o ürünün yayın kodunu elle yazmış gibi açılıyor. O ürün/satıcı-ekseni
çifti için **yayın kodu yoksa okutma reddedilir** ("bu ürünün yayın kodu yok"):
izleyiciler yoruma kodu yazarak sipariş veriyor, kodsuz ürün zaten satılamaz.

### Etiket basma ekranı — nereye oturuyor

Yeni bir sayfa **değil**, `ProductCard`'ın altındaki "Etiket bas" düğmesiyle açılan
bir **çekmece** (`BarcodeLabelDrawer`). Gerekçe: WPF'te katalog/ürün gezinme
sayfası yok (`Views/Pages/` altında sekiz sayfa var, hiçbiri katalog değil) ve
yenisini eklemek ürün arama, sayfalama, kategori ağacı demek — hepsi panelde
zaten var. Kart ise ürünü **zaten seçili** tutuyor: operatör kod kutusuna kodu
yazıyor, kart o ürünün satıcı eksenine kurulmuş varyantları gösteriyor. Çekmece
tam o listeyi alıyor.

Çekmece içeriği: kartta görünen her varyant için bir satır (eksen değeri +
barkod + adet kutusu, öntanımlı 0) ve bir "Bas" düğmesi. Adedi 0 olan satır
basılmaz. Barkodu 60 mm'ye sığmayacak kadar uzun olan satır "barkodu hazır"
etiketiyle pasif gelir. Çekmece kalıbı `VariantPickerDrawer` ile aynı
(`MainShellViewModel` üzerinden açılıyor).

---

## Kabul edilen ödünçler

1. **Etiketteki fiyat ve yayın kodu basım anının kopyasıdır.** Fiyat sonradan
   değişirse ya da yayın kodu (arşivleme sonrası) başka ürüne geçerse rafta
   duran etiket yanlış bilgi gösterir. Barkod bundan etkilenmez — o dondurulmuş
   bir numara ve okutma her zaman doğru varyantı bulur. Fiyat için bu ödünç
   kaynak spec'te zaten bilinçli olarak kabul edilmişti; yayın kodu aynı kefede.
2. **Etikette eksen değeri yok** (kullanıcı kararı: ürün adı + barkod + numara +
   yayın kodu + fiyat). Sonucu: iki eksenli üründe farklı varyantların etiketi
   gözle birebir aynı görünür, yalnız çubuklar farklıdır — eleman yapıştırırken
   karıştırabilir. Sorun yaşanırsa çözüm ucuz: etikete `Kırmızı · M` satırı
   eklemek.
3. **Hiç etiket basılmayan varyantlar da numara yakar.** 10 milyarlık uzayda
   maliyet değil.
4. **Etiket boyutu sunucuda tutulmuyor.** Basım yalnız WPF'te olduğu için
   yerel `AppSettings` tek kaynak; panelin bu değeri bilmesine gerek yok.

---

## Kapsam dışı

- **Excel ile toplu stok/ürün girişi** — ayrı spec, ayrı plan. Bu tasarım o işi
  bilerek kolaylaştırıyor (boş barkod sütunu sunucuda doluyor), ama içermiyor.
  Excel işinin kendi doğrulama listesine yazılacak madde: dosya içinde tekrar
  eden barkod ve veritabanıyla çakışan barkod satır bazında reddedilmeli —
  yakalayacak olan bu tasarımın kurduğu benzersizlik indeksi.
- Panelden PDF etiket üretimi.
- QR / DataMatrix (2D). Gerekirse ileride: GUID bile 10×10 mm'ye sığar ama
  kameralı okuyucu gerekir (1D lazerin 2-3 katı).
- EAN-13 / GS1 üyeliği.
- **Ürün arşivleme** — kaynak spec bunu 1c'ye yazmıştı ama plan 3/3'te bitti
  (`325d204`).
- Barkod okuyucunun prefix ile programlanması.

---

## Açık konular

- Termal yazıcı marka/model önerisi kullanıcıya ayrıca verilecek; tasarımı
  etkilemiyor (hepsi Windows sürücüsüyle gelir, ZPL/EPL kullanılmıyor).

---

## Doğrulama

**Otomatik:**
- Sunucu: sayaç artırmanın atomikliği · lisans içinde benzersizlik · elle
  girilen değerle çakışınca atlama · boş barkodun kaydetmede dolması · geri
  doldurma göçünün her varyanta numara vermesi · 10 haneli saf sayının yayın
  kodu olarak reddi
- WPF: replika kolonunun senkronla dolması · `BroadcastCodeResolver`'ın barkod
  yolu (yayın kodu önce, barkod sonra) · yayın kodu olmayan ürünün barkodunun
  reddi · Code128 kodlamasının bilinen bir yük için bilinen modül dizisini
  üretmesi · etiket yerleşiminde uzun ürün adının kırpılması · kod kutusunun 64
  karakteri kırpmadan ve harf durumunu bozmadan alması

**Elle (kullanıcı):**
1. Panelde varyant yarat, barkod alanını boş bırak → kaydettikten sonra numara
   dolu geliyor mu
2. Tedarikçi barkodunu okut → o değer korunuyor mu
3. WPF'te etiket bas → 60×30 rulodan çıkan etikette çubuklar tam, kırpılma yok
4. Basılan etiketi lazer okuyucuyla yayın ekranındaki kod kutusuna okut → ürün
   kartı doğru ürüne kuruluyor mu
5. Ağ kesikken aynı okutma → yine çalışıyor mu (replikadan)
6. Panelde stok girişinde okutma → doğru varyant seçiliyor mu
