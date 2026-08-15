# Ürün arşivleme + yayın kodlarının serbest bırakılması

## Bağlam

Bugün bir ürüne yayın kodu verildiği anda o ürün **kalıcı** hâle geliyor:
`PanelProductsController.Delete` (satır 533) `product-has-broadcast-codes` ile
409 dönüyor ve mesaj "Arşivleyebilirsiniz" diyor — **ama arşivleme diye bir şey
yok.** `Product.IsArchived` alanı var, `LicenseDbContext` indeksi var, liste
süzgeci var (satır 117), WPF çekme ucu süzüyor (satır 131) — fakat alanı
`true` yapan tek bir kod yolu bile yok. `IsArchived`'ın repodaki tek yazımı
`PanelProductsController.cs:313`'teki `IsArchived = false`.

Sonuç: operatör yanlışlıkla açtığı, hiç satılmamış bir ürün kartından
kurtulamıyor ve hata mesajı olmayan bir çıkış kapısını gösteriyor.

## Kural (kullanıcı kararı)

> **Yayın kodu olan ürün = yayında satılabilen ürün.**

Bundan türeyen üç davranış:

1. **Arşive alınan ürünün yayın kodları silinir.** Arşivdeki ürün yayında
   satılamaz, dolayısıyla koda ihtiyacı yoktur. Arşivde *kodsuz* durur.
2. **Arşivden çıkarılan ürün için yeni yayın kodları istenir.** Kod, satılabilir
   kümeye dönüşün parçasıdır.
3. **Stok hareketi olmayan ürün silinebilir**, kodları da onunla gider
   (`ProductBroadcastCodes` FK'sı zaten `Cascade` —
   `LicenseDbContext.cs:722`). Hareket varsa silme yasağı **aynen kalır**:
   hareketler `OrderId` taşıyor (`StockLedgerWriter`), yani "hareket var" ≈
   "geçmiş sipariş var" ve o siparişlerin dayanağı silinemez.

Bu, "yayın kodu asla serbest kalmaz" aksiyomunun **bilinçli olarak** gevşetilmesi.
Kabul edilen bedel aşağıda "Kabul edilen riskler"de yazılı.

## Neden güvenli — bugünkü mimariden gelen destek

- `LicensesWpfCatalogPullController.cs:131` → WPF çekme ucu `!p.IsArchived`
  süzüyor. Arşivlenen ürün WPF'e **hiç gitmiyor**.
- `CatalogSyncService.cs:166` → WPF replikası `_repo.Replace(...)` ile **tam
  anlık görüntü** yazıyor, artımlı birleştirme değil. Yani arşivlenen ürün bir
  sonraki senkron turunda (≤5 dk) WPF replikasından **düşüyor**.

Yani 1. kuralın dayandığı invariant kod yazılmadan da geçerli; bu plan onu
yazıya döküp arayüzünü açıyor.

## Sunucu (LiveDeck) — değişecekler

### 1. Arşivleme uçları — `PanelProductsController`

İki ayrı uç, tek bir `PUT {archived:bool}` değil: iki yönün bekçileri farklı ve
tek uçta ikisi de tek gövdede karışırdı.

```
POST /api/panel/products/{id:guid}/archive
POST /api/panel/products/{id:guid}/unarchive
```

`[AllowStockStaff]` — silme ucuyla aynı yetki sınıfı.

**`archive`:**
1. Lisans + sahiplik doğrula (`ResolveActiveLicenseAsync` + `LoadAsync`), sonra
   bekçiler — sıra `Delete`'teki gerekçenin aynısı: sahiplik önce, yoksa başka
   kiracının ürün durumu 409/404 farkından sızar.
2. Zaten arşivliyse `204` (idempotent).
3. ~~**Açık yayın bekçisi:** `_db.StreamSessions.AnyAsync(s => s.LicenseId == ...
   && s.EndedAt == null)` → 409 `broadcast-in-progress`.~~ **Yazıldı, sonra
   2026-08-15'te kaldırıldı** — gerekçe aşağıda.
4. `_db.ProductBroadcastCodes.RemoveRange(...)` — ürünün tüm kod satırları
   (güncel + emekli), `LicenseId` süzgeciyle (depo kuralı).
5. `IsArchived = true`, `ArchivedAt = now`, `UpdatedAt = now`. `UpdatedAt` şart:
   WPF çekmesi ve panel listesi bu kolonun indeksinden besleniyor.
6. Yanıt: `ToDtoAsync(product)`.

**`unarchive`:**
1. Aynı sahiplik doğrulaması. Arşivde değilse `204`.
2. `IsArchived = false`, `ArchivedAt = null`, `UpdatedAt = now`.
3. Yanıt gövdesi ürün DTO'su — kodsuz döner, panel uyarıyı oradan kurar.
   **Kodları uçta zorunlu tutmuyoruz** (gövdede kod listesi istemek operatörü
   tek hamlede hepsini doldurmaya mahkûm ederdi); zorlama panel tarafında
   ısrarcı uyarı + kod bölümüne yönlendirme ile yapılıyor. Sunucu tarafında
   ürünün kodsuz olması zaten hiçbir şeyi bozmuyor: kodsuz ürün yayında
   eşleşmez, o kadar.

Yayın bekçisinin sınırı yazıya geçirilecek: `StreamSession` **pasif replika**
(WPF authoritative). WPF çevrimdışıyken kapanmamış bir oturum sonsuza dek
"açık" görünebilir, ya da tersi. Bu yüzden bekçi **doğruluk bariyeri değil,
kullanıcı hatası kalkanı**; gerçek bariyer çekme ucu süzgeci + WPF'in
`Replace`'i.

> **Sonradan not (2026-08-15): bekçi kaldırıldı.** Yukarıdaki "sonsuza dek açık
> görünebilir" ihtimali plan yazıldıktan iki gün sonra gerçekleşti: 13 Ağustos'ta
> başka bir makinede açılan bir oturumun kapanış push'u hiç gelmedi, lisansta
> arşivleme kilitlendi ve ancak prod DB'ye elle yazarak açıldı. Kilit
> kendiliğinden çözülemiyor, operatörün panelden çıkışı yok:
> `SessionRepository.GetUnsynced` yalnız `SyncedAt IS NULL` satırlarını
> gönderdiği için bir oturum ömrü boyunca tam **iki** kez push ediliyor (açılış +
> kapanış), heartbeat yok. "UpdatedAt bayatsa yok say" da işe yaramaz: heartbeat
> olmadığı için `UpdatedAt` gerçek 3,5 saatlik bir yayında da başlangıçta donuyor,
> hayaleti temizleyecek kadar kısa her pencere gerçek yayını da öldürür.
> Kaybedilen şey yalnız kullanıcı hatası kalkanıydı; o uyarı artık panelin onay
> kutusunda. Doğruluk bariyeri (çekme ucu süzgeci + `Replace`) yerinde.

### 2. `Delete` bekçisinin sadeleşmesi — `PanelProductsController.cs:525-537`

`product-has-broadcast-codes` bloğu **kalkıyor**. Kodlar ürünle birlikte
cascade ile gidiyor. `product-has-stock-movements` bekçisi ve mesajı kalıyor;
"Arşivleyebilirsiniz" cümlesi artık gerçek bir ucu işaret ediyor.

### 2b. Arşivdeki ürüne kod yazma yasağı — `PanelBroadcastCodesController.Put`

Plan yazılırken atlanmıştı, uygulama sırasında çıktı: `Put`'un arşiv bekçisi
yoktu. Bekçisiz hâli sessiz ve **kalıcı** bir kaçak — arşivlenen ürüne
sonradan yazılan kod:

- yayında hiçbir zaman eşleşmez (çekme ucu `!p.IsArchived` süzüyor),
- ama kalıcı olarak rezerve olur,
- ve `Archive` zaten arşivli üründe erken döndüğü için **bir daha silinmez**.

Yani "yayın kodu olan ürün = yayında satılabilen ürün" kuralı panelin kod
bölümünü gizlemesine bağlı kalırdı. `Put` artık arşivli üründe 409
`product-archived` veriyor; panel gizlemesi yalnız kolaylık.

Aynı yerde iki metin de güncellendi: sınıfın "kod serbest bırakılamaz" diyen
XML doc'u (artık yanlış) ve `broadcast-code-taken` 409'unun son cümlesi — çıkış
kapısını (arşivle ya da sil) söylemezse operatör elindeki tek çözümü aramaz.

### 3. Eksen değiştirme yasağı — `PanelProductsController.cs:451`

**Kod değişmiyor.** Yasak "kod var mı" üstünde; arşiv kodları sildiği için
arşivdeki üründe yasak kendiliğinden kalkıyor, arşivden çıkıp yeni kod alınca
geri geliyor. Doğru davranış: eski kod artık ortada olmadığı için yeni eksende
sessizce başka bir kırılıma bağlanamaz. Yorumuna tek cümlelik not düşülecek
(arşiv yolunun bu yasağı meşru şekilde açtığı).

### 4. Kod yeniden atama uyarısı — **ELENDİ**

Değerlendirildi ve kullanıcı kararıyla kapsam dışı bırakıldı: serbest kalan
kodun **yeniden kullanılabilmesi bu işin amacı**, engellenecek yan etkisi değil.
Riskin tek somut hâli olan "eski yayın videosundan sipariş" pratikte
gerçekleşmiyor — mezat yayınları canlı izlenip canlı satılıyor, kayıt üzerinden
alışveriş olmuyor. Uyarı mekanizması operatöre her yeniden kullanımda gereksiz
bir onay ekranı çıkarırdı.

### 5. Raporlar — **doğrulandı, değişiklik gerekmiyor**

Endişe şuydu: kod metni artık kalıcı bir kimlik değil, dolayısıyla geçmiş bir
siparişi kod metninden ürüne çözen her yer sessizce bozulur ("ATEŞ" damgalı eski
sipariş, bugün "ATEŞ"e sahip olan ürüne çözülür).

Taranan yerler: `Controllers/Panel/*` (Orders, Stats, Search, BroadcastCodes),
`Services/*`, WPF tarafında `StreamReportViewModel` / `PeriodReportViewModel`,
`OrderDeck.Core/Sales`. **Kod metninden ürüne çözen tek bir yer yok** — bütün
yollar `Order.ProductId` / `Order.ProductVariantId` kullanıyor.

`Order.Code`'un geçtiği yerler yalnız etiket/arama:
`PanelOrdersController` DTO alanı, `PanelSearchController`'ın
`o.Code.Contains(term)` süzgeci (sonucu sipariş, ürün değil). İkisi de kodun
yeniden kullanılmasından etkilenmiyor.

(Barkod yerine id kullanılıyor olması da iyi: `ProductVariant.Barcode` nullable,
barkoda dayalı rapor boşluk bırakırdı.)

### 6. Testler — `PanelProductsControllerTests`

- Arşivleme ürünün yayın kodlarını siliyor; `GET .../broadcast-codes` boş.
- Arşivlenen ürün `GET /products` varsayılan listesinde yok, `includeArchived=true` ile var.
- Arşivlenen ürün WPF çekme ucunda yok (`LicensesWpfCatalogPullControllerTests` zaten `archiveFirst` kurgusu taşıyor).
- Açık `StreamSession` varken arşivleme **geçiyor** (bekçi kaldırıldı, bkz. 1. bölümdeki sonradan not); test bekçinin geri gelmesine karşı duruyor.
- Kodu olan + hareketi olmayan ürün artık **silinebiliyor** (eski 409 gitti).
- Hareketi olan ürün hâlâ 409 `product-has-stock-movements`.
- Arşiv → eksen değiştir → arşivden çıkar akışı 409 vermiyor.
- `unarchive` ürünü kodsuz geri getiriyor.
- (4. madde alınırsa) daha önce satılmış kod başka ürüne verilirse 409, `confirm=true` ile geçiyor.

## Panel (OrderDeck-Mobile) — değişecekler

- `api/catalog.ts`: `useArchiveProduct` / `useUnarchiveProduct` mutasyonları.
- `UrunScreen.tsx`: "Arşivle" butonu + onay diyaloğu. Onay metni **kaybı
  giriş anında** söyler: *"Bu ürünün N yayın kodu silinecek. Arşivden
  çıkarırsan yeniden kod vermen gerekir."*
- Arşivdeki üründe: rozet + "Yayın kodu yok — arşivden çıkarınca yeniden ver"
  uyarısı; yayın kodu bölümü gizli/pasif.
- "Arşivden çıkar" sonrası kod bölümüne yönlendirme + kod girilene kadar duran
  uyarı şeridi.
- Katalog listesinde "Arşivi göster" anahtarı (`includeArchived` sunucuda hazır).
- Silme butonunun hata metni artık gerçekten arşive yönlendiriyor.
- Testler: arşivleme onayı, arşivdeki üründe kod bölümünün kapalı olması,
  arşivden çıkışta uyarının görünmesi.

## Kabul edilen riskler (kullanıcı onaylı)

1. **Serbest kalan kod yeniden atanabilir.** Bu bir yan etki değil, işin
   **amacı**. Teorik zarar (eski yayın videosundan gelen yorumun yeni ürüne
   düşmesi) kullanıcı tarafından gerçek dışı bulundu: mezat yayınları canlı
   izlenip canlı satılıyor, kayıt üzerinden sipariş gelmiyor.
2. **Emekli kod geçmişi kaybolur** — panelin "eski kodlar" satırı arşivlenen
   üründe boşalır. Kullanıcı: "önemli değil".
3. **Eksen kilidi arşiv üzerinden aşılabilir hâle gelir.** Kullanıcı: "sorun
   değil"; ayrıca yukarıda gerekçelendirildiği gibi güvenli.
4. **`StreamSession` bekçisi bayat okuyabilir** (pasif replika). Gerçek bariyer
   çekme süzgeci.

## Kapsam dışı

- Plan 3/3 (WPF yorum eşleştirme + varyant seçici + uyumluluk kalkanının
  kaldırılması). **Sıra:** bu plan önce — 3/3 yayın kodlarını WPF'e taşıyor ve
  arşiv kuralı o taşımanın varsayımı olacak.
- Arşivleme için Hangfire otomatik işi (Faz 1c).
- Kod metni üzerinden geçmiş rapor arama ekranı.

## Yayın

İki PR:
- `feat/urun-arsivleme` (LiveDeck, base `master`)
- `feat/urun-arsivleme-panel` (OrderDeck-Mobile, base `main`) — sunucu merge
  edilip prod'a çıktıktan sonra.

Commit'siz duran `.gitignore` / `.claude/launch.json` / `docs/` dosyaları bu
PR'lara karıştırılmayacak.
