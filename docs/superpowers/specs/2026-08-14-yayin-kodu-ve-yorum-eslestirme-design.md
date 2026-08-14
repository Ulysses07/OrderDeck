# Yayın kodu + yorumdan varyant eşleştirme — tasarım

*2026-08-14 · Stok sistemi, Faz 1b'nin kalan WPF ayağı*

## Neden

Faz 1b'nin sunucu ayağı (stok defteri) ve panel ekranları canlı, WPF katalog
replikası da indi (#262). Ama replika **kullanılmıyor**: izleyici yorumuna
yazdığı bedeni hiçbir yer okumuyor, `Label` katalog kimliği taşımıyor, sipariş
senkronu `CatalogAware = false` gidiyor. Yani defter çalışıyor ama besleyeni yok.

Bu tasarımı yazarken kod modelinde daha temelde bir sorun ortaya çıktı ve
tasarımın kapsamı ona göre büyüdü — aşağıdaki ilk bölüm onu anlatıyor.

## Ortaya çıkan asıl sorun: kodun tek olması

Faz 1a'da tek bir kod kavramı vardı: `Product.Code` (`A1`, `A2`…), elle
değiştirilebilir, ve `ProductVariant.VariantCode` ondan **türetiliyordu**
(`A12-SIYA-M`). Bu model iki ayrı ihtiyacı tek alana yüklüyor:

1. **Sistem kimliği** — rafta, barkodda, sayımda, aramada aynı şeyi göstermeli
   ve asla değişmemeli.
2. **Canlı eşleşme anahtarı** — yayıncının yayında söylediği, izleyicinin
   yoruma yazdığı sözcük. Yayıncılar buna akılda kalan isimler veriyor: `ATEŞ`,
   `DENİZ`, `KRAL`.

Bu ikisi aynı alan olamaz. Türetilmiş kod ürün adı/kodu düzenlenince sessizce
değişiyor, rafa yapıştırılmış etiket yalan söylemeye başlıyor; öte yandan
`A12-SIYA` yayında söylenecek bir şey değil.

**Karar: kod ikiye ayrılıyor.**

| Kod | Seviye | Kim verir | Benzersizlik | Değişir mi | Nerede görünür |
|---|---|---|---|---|---|
| **Stok kodu** `SK00001` | ürün | sistem (sayaç) | lisans başına | **hayır** | ürün kartı, arama, raf |
| **Yayın kodu** `ATEŞ` | ürün + satıcı ekseni değeri | operatör, stok girerken | lisans başına, **kalıcı** | hayır | kod kutusu, **canlı eşleşme** |

Varyant seviyesinde kod **yoktur**. Varyantın kimliği `Guid`, ekranda kendi
eksen değerleriyle görünür (`ATEŞ · M`).

### Çözümleme zinciri

```
Operatör kod kutusuna:  ATEŞ
                          ↓  ProductBroadcastCode araması
              ürün SK00001 "Elbise"  +  satıcı ekseni değeri "Siyah"
                          ↓
İzleyici yorumu: "ateş m"  →  aktif kodu at, kalanı eşleştir → "M"
                          ↓
              varyant (Siyah, M)  →  ProductId + ProductVariantId
```

`SK00001` ürünü tekilleştirir, `ATEŞ` satıcı eksenini sabitler, yorum izleyici
eksenini verir. Üçü birlikte tek varyantı verir.

### Neden yayın kodu satıcı ekseninin *değerine* yapışıyor

Elbise, Renk = satıcı ekseni (Siyah/Mavi), Beden = izleyici ekseni (S/M/L) →
6 varyant. Ama yayın kodu **2 tane**: Siyah = `ATEŞ`, Mavi = `DENİZ`. Bedeni
zaten yorum söylüyor, dolayısıyla 6 koda gerek yok.

Bu aynı zamanda spec'in "satıcı ekseni barkod okutunca sabitlenir" boşluğunu
kapatıyor: barkod okutma Faz 1c'de, ama yayın kodu satıcı eksenini **bugün**
sabitliyor. İki eksenli ürünlerde kırılım Faz 1c'yi beklemeden çalışıyor.

Eksensiz üründe (kolye) ve satıcı ekseni olmayan üründe (ruj — tek eksen, rolü
izleyici) kod doğrudan ürüne yapışır: `SellerAxisValue = null`. Özel durum kodu
yazılmaz, aynı tablo aynı sorgu.

### `VariantCode` kaldırılıyor

Bugünkü `VariantCode`'un üç işi var ve üçünün de daha basit karşılığı var:

| Bugünkü işi | Yerine |
|---|---|
| Aynı üründe mükerrer varyantı engellemek (`VariantCodeTakenAsync`) | `UNIQUE (ProductId, Axis1Value, Axis2Value)` — asıl kural zaten buydu, kod üzerinden dolaylı uygulanıyordu |
| Varyantları sıralamak (`OrderBy(v => v.VariantCode)`) | Zaten duran `SortOrder`, sonra eksen değerleri |
| Eksensiz üründe ekran etiketi | Ürün adı / `SK00001` |

Peşinden şunlar da düşer:

- `ProductVariant.Axis1Code` / `Axis2Code` (Türkçe→ASCII kırpma: `Yeşil`→`YESI`)
- `Services.Catalog.VariantCodeBuilder`
- Panelde "eksen kod parçasını elle düzelt" alanları
- `CatalogLimits.VariantCode`, `CatalogLimits.AxisCode`
- Sistem spec'indeki **"Code128 ASCII kodlar" teknik tuzağı** — konusuz kalıyor

`ProductVariant.Barcode` **kalır** (nullable, Faz 1c'de dolar).

## Sunucu değişiklikleri

**Yeni tablo `ProductBroadcastCode`**

```
Id               Guid, PK
LicenseId        Guid, NOT NULL
ProductId        Guid, NOT NULL, FK → Product (Cascade)
SellerAxisValue  nvarchar(CatalogLimits.AxisValue = 60), NULL  -- null = satıcı ekseni yok
Code             nvarchar(CatalogLimits.BroadcastCode = 32), NOT NULL  -- operatörün yazdığı hâli
CodeNormalized   nvarchar(CatalogLimits.BroadcastCode = 32), NOT NULL
CreatedAt        datetimeoffset

UNIQUE (LicenseId, CodeNormalized)
INDEX (LicenseId, ProductId)
```

- Normalleştirme `SearchNormalizer.Normalize` — #261'de kurulan tek kural
  (benzersizlik, panel araması ve canlı eşleşme aynı normalleştirmeyi paylaşır).
- `UNIQUE` indeksi "bir kod bir daha asla kullanılamaz" kuralını tek satırda
  uygular; ürün arşivlense de kod serbest kalmaz.
- `SellerAxisValue` serbest metne bağlı bir yumuşak anahtar. Panelde eksen
  değeri adı değişirse (`Siyah` → `Siyah 2`) kod **aynı transaction'da** taşınır
  — panel zaten varyant satırlarını güncelliyor.

**`Product.Code`**

- Biçim `SK` + 5 hane sıfır dolgulu sayaç (`SK00001`). Sayaç lisans başına,
  geri gitmez.
- **Sistem üretir, elle değiştirilemez** — düzenleme ucu kaldırılır.
- Gerekçe: kâğıda basılan hiçbir şey yalan olmamalı. Kod değişebilir olduğu
  sürece rafın üstündeki etiket zamanla ekrandan sapar ve bunu kimse fark etmez.
  Yan fayda: kod sabitleşince `Barcode`'un "basım anını dondur" numarasına
  ileride gerek kalmaz.

Yeni sabit `CatalogLimits.BroadcastCode = 32` eklenir; uzunluk sınırı hem
`OnModelCreating`'de hem istek DTO'sunda **aynı sabitten** okunur
(`CatalogLimits`'in kendi kuralı: InMemory testler `HasMaxLength`'i yok saydığı
için ayrışma testte yeşil, prod'da 500 oluyor).

**Silinenler:** `ProductVariant.VariantCode`, `Axis1Code`, `Axis2Code`,
`VariantCodeBuilder`, `AxisCodeDeriver`, `CatalogLimits.VariantCode`,
`CatalogLimits.AxisCode`, `PanelProductsController.SyncVariantCodes`,
`VariantCodeTakenAsync`.

**`LicensesWpfCatalogPullController`** anlık görüntüye yayın kodlarını ekler.
Çekme **tam anlık görüntü** kuralı değişmiyor (bkz. plan 1'in kilit kararı).

**Göç:** prod'da `SELECT COUNT(*) FROM Products` = 0 (2026-08-13 ölçümü) → veri
taşıma yok. Yine de göç geriye dönük veri varmış gibi yazılır.

## Panel değişiklikleri (ayrı repo: OrderDeck-Shopper)

- Ürün kartı: `SK00001` **salt-okunur** rozet. Kod düzenleme alanı kaldırılır.
- Stok giriş ekranı: **satıcı ekseni değeri başına bir yayın kodu kutusu**.

```
Elbise                         stok kodu: SK00001
  Siyah   yayın kodu: [ATEŞ ]     S[5]  M[8]  L[3]
  Mavi    yayın kodu: [DENİZ]     S[4]  M[6]  L[2]
```

- **Yayın kodu zorunlu değil.** Depoya mal girip henüz yayına çıkarmamak meşru;
  kodu olmayan ürün yalnız canlıda çağrılamaz.
- Çakışmada hata: *"Bu yayın kodu daha önce kullanılmış."* — kod serbest
  bırakılmaz.
- Eksen kod parçası düzenleme alanları kaldırılır.

## WPF — kod kutusu

`ActiveCode` **serbest metin kalır**. Çözümleme **yalnız yayın koduna** bakar
(`ProductBroadcastCode` replikası) → ürün + satıcı ekseni değeri. Bulunamazsa
**"tanımlı değil"** — bugünkü davranış aynen sürer.

Çözülünce ürün kartı satıcı ekseni değerini de gösterir (`Elbise · Siyah`).

**Stok kodu kod kutusunda aranmaz.** `SK00001` ürün kartında *görünür* ama
eşleşme anahtarı değildir. Gerekçe: iki eksenli bir üründe stok kodu satıcı
eksenini söylemez (`SK00001` = Siyah mı Mavi mi?), yani çözümleme yarım kalır ve
çekmecenin "yalnız izleyici eksenini sorar" kuralı bozulurdu. Yayın kodu
atanmamış ürün canlıda çağrılamaz — bu, "yayın kodu zorunlu değil" kararının
bilinen ve kabul edilen sonucu.

Sohbet panelindeki `ChatFilter` (alt dize içeren mesajları süzme) **ayrı bir
şey** ve değişmiyor; süzme ile eşleştirme birbirine karışmaz.

## WPF — `AxisValueMatcher`

Girdi: yorum metni + aktif ürünün izleyici ekseni değerleri (kapalı küme, 5-6
öğe). Serbest metin ayrıştırma yok.

**Adımlar**

1. Yorumdan **aktif yayın kodu çıkarılır** (`"ateş m"` → `"m"`). Yoksa kodun
   kendisi bir eksen değeriyle çakışabilir.
2. Kalan metin boşluk ve noktalamadan **jetonlara** bölünür.
3. Her jeton normalleştirilir: büyük/küçük harf, Türkçe→ASCII (`ç→c`, `ğ→g`,
   `ı→i`, `ö→o`, `ş→s`, `ü→u`), dolgu sözcükleri atılır (`beden`, `bedeni`,
   `numara`, `no`), eşanlamlılar eşlenir (`medium`/`orta`→`M`, `küçük`/`small`→`S`,
   `büyük`/`large`→`L`).
4. Her jeton eksen değerleriyle **birebir** karşılaştırılır. **Alt dize
   araması yapılmaz.**

**Alt dize değil jeton — neden**

| Yorum | Jetonlar | Sonuç |
|---|---|---|
| `ateş m` | `m` | **M** → tek eşleşme |
| `ateş xl` | `xl` | **XL** → tek eşleşme. Alt dize olsaydı `L` de eşleşir, boşuna çekmece açılırdı |
| `ateş m l` | `m`, `l` | 2 eşleşme → çekmece, ikisi işaretli |
| `ateş ml` | `ml` | 0 tam eşleşme → birleşim denemesi (aşağıda) |
| `bana da` | — | 0 eşleşme → çekmece boş |

**Birleşim denemesi**

Jeton hiçbir eksen değerine tam uymuyorsa, eksen değerlerinin bir dizisi olarak
bölünmeye çalışılır:

- **Tam eşleşme varsa bu adım hiç çalışmaz.** `XL` → XL, biter. Bu kural
  olmasaydı `XL` → `X` + `L` diye bölünürdü.
- **Tek bir bölünme** çıkarsa o değerler çekmecede **önceden işaretli** açılır
  (`ml` → M ve L işaretli).
- **Birden fazla** bölünme çıkarsa hiçbir şey işaretlenmez, çekmece boş açılır.
- Aynı değer birden fazla kez çıkarsa **tek** işaret olur (`mm` → M). Sipariş
  satırında adet alanı yok; iki adet isteniyorsa operatör iki kez çift tıklar.

Bu "sessizce tahmin" değildir: çekmece akışı zaten kesiyor ve operatör
onaylamadan satır yazılmıyor. Öneri yalnız tık sayısını azaltır.

**Yakın tahmin (fuzzy) yasak.** `l` yazan `1` mi `L` mi belirsizdir; yanlış
beden = yanlış kargo = iade + kargo bedeli.

## WPF — varyant seçici çekmece

**Ne zaman açılır:** operatör bir mesaja çift tıkladığında, **yalnız** aktif kod
katalogda çözülmüş **ve** ürünün izleyici ekseni varsa **ve** eşleşme sayısı 1
değilse (0 veya 2+).

**Açılmadığı durumlar** — çift tık bugünkü gibi anında satır yazar:

- kod katalogda yok ("tanımlı değil")
- ürünün izleyici ekseni yok (kolye, çanta)
- tam bir eşleşme var (sessizce yazılır)

**Davranış:** akışı **keser**; çekmece kapanmadan başka mesaja geçilemez.
İzleyici ekseni değerleri onay kutusu olarak listelenir. Birden fazla
işaretlenirse **her biri ayrı sipariş satırı** olur.

**`Esc` → sipariş hiç oluşmaz.** Mesaj sohbette kalır, operatör isterse tekrar
çift tıklar.

**Sonuç:** izleyici ekseni olan bir üründe varyantsız sipariş satırı
**oluşturulamaz**. İzleyici beden söylemediyse operatör sormak zorunda. Bu
bilinçli bir katılık: yanlış bedenin bedeli iade + iki yön kargo.

## `Label` + senkron

- `Label`'a `ProductId` ve `ProductVariantId` (ikisi de nullable) — yeni WPF
  göçü (numara plan yazılırken verilir; son göç 026).
- Sunucudaki `Order` entity'sinde alanlar zaten var; **FK değildirler**
  (silinmiş bir ürüne referans veren tek sipariş bütün senkron paketini 500'e
  düşürüp outbox'ı kilitlerdi).
- `SessionOrderSyncService` artık `CatalogAware = true` gönderir.
- Defter kuralı değişmiyor: varyant biliniyorsa varyanttan, bilinmiyorsa
  üründen, ürün de bilinmiyorsa **deftere hiç dokunulmaz**.

## Katalogu kullanmayan yayıncı

Katalog tamamen **eklemeli**. Hiç ürün tanımlamamış bir yayıncı için hiçbir şey
değişmez:

- Kod kutusuna serbest metin yazar, katalogda bulunmaz.
- Ürün kartı "tanımlı değil" görünür, sipariş satırı kodu metin olarak taşır,
  baskı aynen çalışır.
- **Varyant seçici hiç açılmaz.**
- Sunucuya `ProductId = null` gider; `CatalogAware = true` ile birlikte bu,
  sunucunun okuduğu *"operatör ürünü belirleyemedi"* durumudur → deftere
  dokunulmaz. `CatalogAware` bayrağı #261'de tam bu ayrım için eklendi.

Aynı şey ürün ürün karışık da çalışır: bazı ürünleri katalogda tanımlı, bazıları
değil olan yayıncı ikisini yan yana kullanabilir.

## Reddedilen seçenekler

| Seçenek | Neden reddedildi |
|---|---|
| Yayın kodunu **her varyanta** ayrı vermek | 6 varyanta 6 kod; yayıncının kaçındığı `ATEŞ-SİYAH-M` birleşik koduna geri dönüş |
| Yayın kodunu **ürüne** vermek | İki renkli üründe ikinci kodu koyacak yer yok; renk de izleyici eksenine düşer, satıcı ekseni kavramı boşalır |
| Yayın kodunu `ProductVariant`'a kolon olarak koymak | Aynı kod 3 satırda tekrarlanır → `UNIQUE` indeks kurulamaz → "bir daha asla" kuralı elle kontrole düşer, sessiz çakışmanın kapısı |
| Kodu **arşivlenince serbest bırakmak** | Hangi ürün olduğu belirsizleşir; kullanıcının kararı: kod bir kere kullanıldı mı bitti |
| Stok kodunun elle değiştirilebilmesi | Türetilmiş varyant kodları sessizce değişir, raftaki etiket ekrandan sapar, dışa çıkmış PDF/WhatsApp kayıtları çözümsüz kalır |
| Kod kutusunun stok kodunu da çözmesi | İki eksenli üründe `SK00001` satıcı eksenini söylemez → çözümleme yarım kalır, çekmece renk sormak zorunda kalırdı |
| Çekmecenin akışı kesmemesi | Kullanıcı kararı: izleyici ekseni olan üründe varyant belirlenmeden satır yazılmasın |
| Çekmecede **"bedensiz ekle"** çıkışı | Kullanıcı kararı: izleyici ekseni olan üründe varyantsız satır hiç oluşmasın |
| `Esc`'in varyantsız satır yazması | Aynı gerekçe |
| Yakın tahmin (fuzzy eşleştirme) | `l` → `1` mi `L` mi; akıllı olmaya çalışmak sessizce yanlış yapmak demek |
| `ML` gibi jetonları koşulsuz bölmek | Aynı kural `XL`'i `X`+`L` yapar; ayrıca bazı ürünlerde `ML` ve `S/M` gerçek birer beden |

## Faz 1c'ye bilerek bırakılanlar

**Barkod okutunca tek varyantı tanıma.** `ATEŞ` bunu yapamaz — üç bedeni birden
kapsıyor. Bugün bu yükün kaynağı `VariantCode`'du; kalkınca Faz 1c kendi yükünü
üretmek zorunda (`ATEŞ-M` gibi bir birleşim, varyantın `Guid`'i, ya da o zaman
eklenecek değişmez bir varyant sıra numarası). `ProductVariant.Barcode` kolonu
bu iş için duruyor. **Bu boşluk bilerek bırakıldı**; Faz 1c'de sürpriz olmasın.

## Kapsam dışı

- Ürünün `DefaultPrice`'ının fiyat kutusuna otomatik dolması.
- WPF'te stok bakiyelerinin gösterilmesi (`LicensesWpfStockPullController`) —
  ayrı plan.
- Barkod üretimi, etiket PDF'i, okutma, arşivleme — Faz 1c.

## Plan bölünmesi

Bu tasarım **üç** uygulama planına bölünür:

1. **Sunucu + panel — kod modeli.** `ProductBroadcastCode` tablosu ve ucu,
   `Product.Code` → `SK00001` (sistem üretir), `VariantCode`/`Axis*Code`/
   `VariantCodeBuilder` temizliği, `UNIQUE (ProductId, Axis1Value, Axis2Value)`,
   katalog çekme ucuna yayın kodları, panel ekranları. **WPF'e dokunmaz.**
2. **WPF — eşleştirme + çekmece.** Replika şeması (yayın kodu tablosu gelir,
   `VariantCode`/`Axis*Code` düşer), `Label`'a `ProductId` + `ProductVariantId`,
   kod kutusu çözümlemesi, `AxisValueMatcher`, varyant seçici çekmece,
   `CatalogAware = true`. Göç numaraları 025/026'nın ardından sırayla verilir.
3. **WPF — stok bakiyeleri.** Ayrı plan, bu tasarımın kapsamı dışında.

Sıra bağlayıcı: 2 numara 1 numaranın ucuna bağımlı.

## Kabul ölçütleri

1. Panelde iki renkli bir ürüne iki yayın kodu yazılır; aynı kod ikinci bir
   ürüne yazılmaya çalışılınca hata döner.
2. Arşivlenmiş bir ürünün yayın kodu başka ürüne verilemez.
3. `Product.Code` düzenleme ucu yoktur; yeni ürün `SK00001`, `SK00002`… alır.
4. WPF kod kutusuna `ATEŞ` yazılınca kart `Elbise · Siyah` gösterir.
5. `ateş m` yorumuna çift tık → çekmece **açılmaz**, satır `ProductVariantId` =
   (Siyah, M) ile yazılır.
6. `ateş xl` → tek eşleşme, çekmece açılmaz (alt dize eşleşmesi olmadığının
   kanıtı).
7. `ateş ml` → çekmece açılır, M ve L **önceden işaretli** gelir.
8. `bana da` → çekmece **boş** açılır; `Esc` → **sipariş oluşmaz**.
9. `ateş m l` → çekmecede ikisi işaretli; onay → **iki ayrı sipariş satırı**.
10. Katalogda olmayan bir kod yazılıp çift tıklanınca çekmece açılmaz, satır
    bugünkü gibi yazılır, sunucuda hiç stok hareketi oluşmaz.
11. Eksensiz ürün (kolye) → çekmece açılmaz, stok üründen düşer.
