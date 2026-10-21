# Ortak Bakiye (Kişi Havuzu) — Tasarım

**Tarih:** 2026-10-11 (inceleme 2026-10-12) · **Durum:** sahip kararları alındı (§10), tasarım incelemesi (§13) ve spec incelemesi (§14) işlendi, bir sahip kararı açık (§10), satış senkronu bağımlılıkları işlendi (2026-10-16, §15), uygulama planı bekliyor · **İlgili:** `2026-10-10-banka-havale-onay-design.md` (+ PR #505 §2 "Kişi"), PR #506 (WPF ödeme isteği kişi anahtarı), `2026-10-13-yayin-etiket-senkron-design.md` (çok bilgisayarlı yayın/etiket senkronu; bu spec ondan **sonra** uygulanır, §15)

Kaynaklar: LiveDeck `origin/master` 7f8d1efa + PR #506 (`fix/odeme-istegi-kisi` 71c2f079; PR #506 sunucu dosyalarına dokunmuyor, sunucu satırları master'la aynı), OrderDeck-Mobile `origin/main` 76fcf8f, OrderDeck-Shopper `origin/master` 1261aef. Satır numaraları bu kesite göredir. Sahadaki v0.9.9'a ait satırlar ayrıca "master" diye belirtilir. Üç aday tasarım iki hakemle karşılaştırıldı; kazanan "satır defteri aynen kalır, havuz okumada toplanır, düşüm çok bacaklı yazılır" modelidir. Hakemlerin aşıları ve ölümcül bulguları §13'te tek tek eşlendi.

## 1. Amaç ve kapsam

**Sorun.** Bakiye bugün kişi başına değil, asıl kayıt (platform satırı) başına tutuluyor. Sunucuda bakiye kodunun hiçbir yeri `GroupId`'yi okumuyor.
- Müşterinin Instagram satırına 100 ₺ iade yazılmış olsun. PR #506 ödeme isteğini kişinin sahip satırından (ör. telefonlu TikTok satırı) açar.
- Önizleme o satırın bakiyesini okur, satır yoksa 0 döner (`LicensesCustomerBalanceApplyController.cs:84-90`).
- WPF `MarkNoBalance` yazar (`PaymentRequestService.cs:1043-1045`). Kesinleşmiş iş bir daha sorulmaz (`:957-966`), Instagram'daki 100 ₺ o satışta hiç kullanılmaz.

Sahibin kararı (K0): "Müşterilerin ayrı olması yanlış; farklı platform olsa bile aynı müşteri kartında olmalı."

**Amaç**
- Bir kişinin (aynı `GroupId`'deki asıl kayıtlar) **tek** kullanılabilir bakiyesi olur: üye satırların bakiyelerinin toplamı.
- "Ödeme iste" bu havuzdan düşer. Para gerekirse birden çok satırdan çekilir; çekim tek işlemde yazılır ve tek işlemde geri alınır.
- Para hiçbir zaman taşınmaz. Her kuruş hangi satıra iade edildiyse orada durur, oradan çekilir, geri alınınca oraya döner. Ayırma (K1) bu sayede veri işlemi gerektirmez.
- Uç sözleşmeleri geriye uyumlu kalır: alan eklenir, hiçbir alan kaldırılmaz ya da anlamı tersine dönmez.
- Veri göçü yok; şema yalnız genişler.

**Kapsam dışı**
- Kalıcı kişi kimliği (`PersonId`) ya da kişi anahtarlı bakiye tablosu. `GroupId` birleşmede ve ayırmada değişiyor; kalıcı anahtar olamaz (§13, tasarım 1).
- Birleşmede parayı tek satıra toplama (tasarım 3).
- Kimlik birleşmesinin olağan kipte kişiyi bölebilmesi ("grup açığı", `CustomerIdentityMergeJob.cs:511-512` yalnız `--at-twins`'te birleştiriyor). Havuz bu durumda küçülür; ayrı karar (§11).
- Tek üyeyi gruptan çıkarma. WPF'te yalnız grubun tamamını dağıtan "Ayır" var (`CustomerRepository.cs:616-622`).
- İade uçlarının idempotency anahtarı (WPF'in yeniden denemesi iadeyi iki kez yazabilir, `OrderDeck.App/AppHost.cs:418`). Bugün de var; ayrı iş.
- Paneldeki "henüz müşteri uygulamasıyla bağlı değil" metni (`CustomerBalanceCard.tsx:30-41`). Ayrı hata.
- Banka spec'inin metni. Gereken düzeltmeler §9'da listelenir; #505 birleştikten sonra ayrı bir docs PR'ında uygulanır.

## 2. Kavramlar

- **Kayıt (satır):** `WpfCustomerProjection`'ın asıl kaydı (`MergedIntoId IS NULL`). Bakiye satırı `(LicenseId, WpfCustomerId)` başına tektir (`LicenseDbContext.cs:854`). Kopyalar (`MergedIntoId` dolu) bakiye taşımaz; birleştirici onların parasını asıl kayda ekler (`CustomerIdentityMerger.cs:79-116`).
- **Kişi:** PR #505 §2'deki tanım. Aynı `GroupId`'yi paylaşan asıl kayıtlar; `GroupId` boşsa yalnız kaydın kendisi. İstek anındaki hâliyle okunur: ayrılan satırlar ayrı kişidir. İki ek:
  - Karşılaştırma ham değer üzerinde ve **ordinaldir** (WPF `CustomerGroup.cs:10-22` ile birebir; " g" ile "g" ayrı gruptur). Sunucuda kolon BIN2 collation'a alınır (§3.1). SQL Server'ın `=`'i sondaki boşluğu BIN2'de de yok saydığı için kesin eşleme bellekte ordinal yapılır (§4.13).
  - KVKK ile silinmiş (`PurgedAt` dolu) üye kişide kalır, havuza girer ve çekilebilir (K3). Silinmiş üyenin kişisi sunucudaki `S`'den okunur; grubun canlı üyeleri yeni numaraya geçince silinmiş üye onları izler (§4.12).
- **Sunucu kişisi `S(a)`:** `a` istekteki Id'nin asıl kaydı (`CustomerIdResolver.CanonicalOfAsync`, `:25-42`). `a`'nın `GroupId`'si boşsa `S = {a}`; değilse lisansta `GroupId`'si `a`'nınkiyle ordinal eşit olan bütün asıl kayıtlar (silinmişler dahil).
- **WPF kişisi `W`:** havuz kipinde WPF'in preview ve apply'a eklediği `memberIds`. Kişinin GUID'li bütün üyeleri, silinmişler ve sahip dahil; `PaymentRequestService.cs:1096`'daki aday listesiyle aynı süzgeç. Sunucu her Id'yi asıl kayda çözer. `W`'nin yalnız **canlı** üyeleri kullanılır; silinmiş üyeleri sunucu `S`'den alır (§4.0-4).
- **Çekim kümesi `P`:** apply ve preview'ın gerçekten okuduğu ve çektiği satırlar. Tanımı ve 409 kuralları §4.0'da.
- **Havuz:** `Σ Balance` (`S` üzerinden; çekimde `P` artı `S∖P`'nin eksi bakiyeleri, §4.0-4). **Kullanılabilir** = `max(0, havuz)`.
- **Çekim (draw):** bir eksi para hareketinin bir ya da birden çok satıra bölünmüş hâli. Üç türü var: ödeme düşümü (`purchase-deduction`), paneldeki eksi elle ayar (`manual-adjustment`) ve artı bir hareketin iptali (`reversal`, K5).
- **Bacak:** çekimin tek satırdaki defter satırı. Bacak, satırın o anki pozitif bakiyesinden büyük olamaz. Bu yüzden hiçbir çekim bir satırı eksiye düşürmez (D2).
- **Çapa bacağı:** çekimin isteğe ait satırdaki bacağı: WPF'te işin sahip satırı, panelde hedef hareketin ya da rotanın satırı. Çapa çekimin kimliğidir:
  - WPF'te `Id` = idempotency anahtarı (`LicensesCustomerBalanceApplyController.cs:286`).
  - `OriginalAmount`, `SaleScope`, `Reason` ve `ReversesTransactionId` yalnız çapada tutulur.
  - **Tutarı 0 olabilir** (satırda para yoksa).
- **`DrawGroupId`:** bacakları çapaya bağlayan yeni kolon. Değeri çapanın `Id`'si; çapada kendi `Id`'si. `NULL` = eski, tek bacaklı satır; kendi başına bir gruptur. Çapa ⇔ `DrawGroupId IS NULL OR DrawGroupId = Id`.
- **Grup geri alması:** bir çekimin **her** bacağı için bir `reversal` satırı. Her biri `ReversesTransactionId = bacağın Id'si` taşır (N01 bacak başına hakem) ve parayı bacağın kendi satırına geri ekler. Geri alınan çekim eski, tek bacaklı bir satırsa (`DrawGroupId NULL`) tek `reversal` satırı bugünkü biçimde, `DrawGroupId = NULL` ile yazılır; satır kipinde yeni biçim hiç oluşmaz.
- **Kip:** lisans başına iki kip var: **satır kipi** (bugünkü davranış) ve **havuz kipi** (`License.PersonBalancePoolSince` dolu). Kipler arası geçiş ve kapı: §7.2.
- **Wh:** PR #506 ile bu spec'in WPF değişikliklerini (W-havuz, §6.1) birlikte taşıyan ilk Velopack sürümü. Havuz kipinin istemci alt sınırıdır. Sunucudaki sayısal değeri `PersonBalance:MinClientVersion` ayarıdır (§7.2).

## 3. Veri modeli (S1)

### 3.1 Şema (tek EF göçü, `Program.cs:868` açılışta uygular)

| Tablo | Değişiklik | Gerekçe |
|---|---|---|
| `CustomerBalanceTransactions` | `DrawGroupId uniqueidentifier NULL` (`CustomerBalanceTransaction.cs` + `LicenseDbContext.cs:862-891`) | Bacakları bağlar. |
| `CustomerBalanceTransactions` | `IX_CustomerBalanceTransactions_DrawGroupId`, filtre `[DrawGroupId] IS NOT NULL`, **tekil değil** | Grup okuması ve geri alma. Tekil olamaz: kimlik birleşmesi aynı grubun iki bacağını aynı satıra taşıyabilir (`CustomerIdentityMerger.cs:75-77`). |
| `WpfCustomerProjections` | `GroupId` → `UseCollation("Latin1_General_100_BIN2")` | Kolonun varsayılan collation'ı harf farkını yok sayıyor (`CustomerGroupUnion.cs:41-44` notu). Kişi eşitliği WPF'teki gibi ordinal olmalı; BIN2 harf farkını SQL'de (sorgu, `verify`'ın gruplaması, indeks) ayırır. Sondaki boşluğu SQL'in `=`'i hiçbir collation'da ayırmaz; onu bellekteki süzgeç ayırır (§4.13). Emsal: `IdentityKey` (`LicenseDbContext.cs:682-690`). |
| `WpfCustomerProjections` | `(LicenseId, GroupId)` indeksi, filtre `[MergedIntoId] IS NULL AND [GroupId] IS NOT NULL` | Bugün `GroupId` üzerinde indeks yok (`:693`, `:712-723`); kişi sorgusu lisansın bütün kayıtlarını tarıyor. |
| `Licenses` | `PersonBalancePoolSince datetimeoffset NULL` | Havuz kipi bayrağı. S1'de hiçbir kod okumaz ve yazmaz; S2 okur, yalnız CLI yazar (§7.2). |
| `PersonBalanceBaselines` (yeni) | `LicenseId`, `WpfCustomerId`, `Balance`, `LedgerSum`, `CapturedAt`, `ReviewedAt NULL`; PK `(LicenseId, WpfCustomerId)` | `verify`'ın taban çizgisi: S1 öncesinden kalan eksi satırlar ve D1 bozulmaları. Günlük uyarı yalnız tabana göre yeni sapmada çıkar; `enable` tabanın sahipçe incelenmiş olmasını ister (§7.2, §8.1). Yalnız `verify` CLI yazar. |

- Collation değişikliği: sütuna bağlı indeks yok. Otomatik istatistik (`_WA_Sys_*`) `ALTER COLUMN`'u engelleyebilir; göç önce varsa onu düşürür. Göç testi bunu gerçek SQL Server'da doğrular (§8.2).
- Göç açılışta, Kestrel'den önce koşar ve `/ready`'nin 60 sn penceresine sığmalıdır. Prod SQL Server **Express** sürümünde çalışıyor (`deploy/docker-compose.yml:25`, `MSSQL_PID: Express`); `ONLINE` indeks işlemi Express'te yok, göçte kullanılmaz (kullanılsa göç hata verir, `/ready` düşer, imaj geri alınır). Süre riski S1'den önce ölçülür: Burak prod'da iki tablonun satır sayısını salt okunur bir sorguyla alır (§7.1 adım 0). Sınıra yakınsa iki indeks ayrı bir göçe ayrılır ve o göç yayın dışı bir bakım deploy'unda çıkar.
- Geri doldurma yok. Eski satırların `DrawGroupId`'si `NULL` kalır ve tek bacaklı çekim sayılır.

### 3.2 Satır biçimleri

| Satır | `WpfCustomerId` | `Amount` | `Kind` | `OriginalAmount` / `SaleScope` | `ReversesTransactionId` | `DrawGroupId` |
|---|---|---|---|---|---|---|
| Düşüm çapası | sahip satır | `≤ 0` (0 olabilir) | `purchase-deduction` | ProductTotal / kapsam | — | kendi `Id` (= anahtar) |
| Düşüm bacağı | başka üye | `< 0` | `purchase-deduction` | `NULL` / `NULL` | — | çapa |
| Elle eksi çapası (K5) | rota satırı | `≤ 0` | `manual-adjustment` | — | — | kendi `Id` |
| Elle eksi bacağı | başka üye | `< 0` | `manual-adjustment` | — | — | çapa |
| İptal çapası (K5, artıyı geri alma) | iptal edilen hareketin satırı | `≤ 0` | `reversal` | — | iptal edilen hareket | kendi `Id` |
| İptal bacağı | başka üye | `< 0` | `reversal` | — | `NULL` | çapa |
| Grup geri alma satırı (çekimin her bacağı için) | bacağın satırı | `≥ 0` | `reversal` | — | **o bacak** | geri alma çapasının `Id`'si; geri alınan çekim eski tek bacaklıysa (`DrawGroupId NULL`) `NULL` |

- Bir grubun bütün satırları **tek** `SaveChanges`'te ve **aynı** `CreatedAt` ile yazılır. Listeler bu yüzden grubu bölmeden sıralar.
- Bacakların `Reason`'ı `NULL`'dır. Açıklama çapada durur.
- Çekim türü bir iptalde yalnız çapa `ReversesTransactionId` taşır. Grup geri almasında ise her satır kendi bacağını taşır. İkisinde de N01 tekil indeksi (`LicenseDbContext.cs:880-882`) hakemdir: ilkinde hareket başına tek iptal, ikincisinde bacak başına tek geri alma.
- `DrawGroupId` dolu satırlar yalnız havuz kipinde yazılan çekimlerden ve onların geri almalarından doğar (geri alma `disable`'dan sonra satır kipinde de yazılabilir). Havuz kipi hiç açılmamış bir lisansta, S1 ve S2 kodu altında da, `DrawGroupId` dolu satır yoktur.

### 3.3 Değişmezler

- **D1** Satır başına `Balance = Σ Amount` (bugünkü kural, `CustomerBalance.cs:3-6`).
- **D2** Çekimler (düşüm, elle eksi, çekim türü iptal) hiçbir satırı eksiye düşürmez: bacak ≤ satırın o anki pozitif bakiyesi. Kapsam dışı: kimlik birleştirici bir kopyanın bakiyesini işaretine bakmadan asıl kayda ekler (`CustomerIdentityMerger.cs:89-91`, karar 2026-10-05); eksi kopya asıl kaydı eksiye düşürebilir. Bu bir çekim değildir.
- **D3** Havuz kipinde bir çekimden sonra kişinin toplamı `Σ_{S∪P} ≥ 0`'dır; çekimden önce `Σ_{S∪P} ≤ 0` ise çekim yapılmaz. Kişi kilidi ve `S∖P`'deki eksi bakiyelerin hesaba katılması bunu sağlar (§4.0-4, §4.13).
- **D4** Her grupta tam bir çapa vardır; bacaklar çapayla aynı `Kind`'ı taşır. Bacaklarda `SaleScope` ve `OriginalAmount` `NULL`'dır.
- **D5** Bir çekimin bacaklarının ya hepsi geri alınmıştır ya hiçbiri.
- **D6** `purchase-deduction` grubunda `−Σ bacak ≤ çapa.OriginalAmount`.

Uygulama kodu D2, D3 ve D5'i sağlar. Altısını da `verify` denetler. Eski veride önceden var olan eksi satırlar ve D1 bozulmaları taban çizgisine alınır; uyarı yalnız tabana göre yeni sapmada çıkar (§8.1).

### 3.4 Veri göçü yok

Satır bakiyeleri ve defter olduğu gibi kalır; havuz okumada toplanır. Havuz kipi bir lisansta açıldığı an, o lisansta kişinin farklı satırlarına dağılmış bakiyeler (ör. Instagram'a dönmüş iade) kullanılabilir hâle gelir. Hangi kişilerin etkileneceğini ön kontrol raporu önceden listeler (§7.3).

## 4. Sunucu davranışı

### 4.0 Ortak servis, kip ve kişi çözümü

Yeni servis `Services/CustomerSync/PersonBalance.cs` (scoped):
- **S1'de gelenler** (okuyucular):
  - `MembersAsync(licenseId, a)` → `S(a)` ve bakiyeleri
  - `AggregateHistory` → hareketleri gruba göre toplar
  - `ReverseGroupAsync`
- **S2'de gelenler:**
  - `ResolveDrawSetAsync(licenseId, a, memberIds)` → `P` ya da 409
  - `PlanDraw`: saf fonksiyon
  - `DrawAsync`: çekim yazımı + kişi kilidi

**Kip.** S2 kodu her istekte `License.PersonBalancePoolSince`'e bakar:
- Boşsa satır kipidir: bütün uçlar bugünkü gibi çalışır, çapa tek bacaklıdır, `DrawGroupId` `NULL`'dır.
- S1 bayrağı hiç okumaz. S2'den S1'e otomatik geri alma her lisansı satır kipine indirir (§7.4).

**`P` ve 409 kuralları (havuz kipi, preview/apply).** `a` = istekteki Id'nin asıl kaydı, `W` = asıl kayda çözülmüş `memberIds`. `a` her zaman `W`'ye eklenir.

1. `a`'nın projeksiyonu sunucuda yoksa:
   - `W`'nin hiçbir Id'si de sunucuda yoksa → **409 `no-balance`** (kesin). Bakiye satırı projeksiyona FK (`:851-852`); hiçbir üyede para olamaz. Yeni açılmış, henüz gönderilmemiş müşteri böylece bekletilmez.
   - Sunucuda en az bir üye varsa → **409 `customer-not-synced`** (K2). Çapa yazılamaz (hareket projeksiyona FK, `LicenseDbContext.cs:867-868`).
2. `W`'deki bir Id'nin projeksiyonu sunucuda yoksa o Id atılır. Henüz senkronlanmamış satırın bakiye satırı olamaz: bakiye satırı projeksiyona FK (`:851-852`), panel uçları projeksiyonu bulamayan Id'yi reddeder (`CustomerIdResolver.cs:69-81`).
3. `W` ile `S(a)`'nın **canlı** (sunucuda silinmemiş) üyeleri karşılaştırılır:
   - **a)** `W`'de olup `S(a)`'da olmayan canlı üye varsa → **409 `customer-not-synced`**.
   - **b)** `S(a)`'da olup `W`'de olmayan, bakiyesi `> 0` canlı üye varsa → **409 `customer-not-synced`**. Kesişimle çekmek o parayı görmezden gelirdi: önizleme 0 ya da eksik döner, WPF `MarkNoBalance` yazar ve kesinleşen iş bir daha sorulmaz (`PaymentRequestService.cs:1043-1055`, `:957-966`). K2'nin çok PC'li hâli budur.
   Olası durumlar: WPF birleştirdi ya da ayırdı, sunucu henüz görmedi; başka bir PC birleştirdi ya da ayırdı, bu PC henüz çekmedi. Hepsi bir gönderim + çekme turuyla kapanır (§6.1-b).
4. `P = (W ∩ S(a))'nın canlı üyeleri ∪ S(a)'nın silinmiş üyeleri`.
   - `S∖W`'de kalan canlı üyelerin bakiyesi kural 3b gereği `≤ 0`'dır. Bunlar ya operatörün ayırdığı ama henüz gönderilmemiş, ya da başka PC'nin eklediği ama bu PC'nin henüz görmediği satırlardır; para onlardan çekilmez. Sıfır olanlar yok sayılır. **Eksi** olanlar havuza sayılır ve kilitlenir (§4.13): çekimin havuzu `Σ = Σ_P + Σ_{S∖P, Balance < 0}`'dır. Bu PC'nin henüz görmediği bir borç havuzu açmaz (D3).
   - Silinmiş üyeler `W`'den değil `S`'den alınır. WPF silinmiş satırı göndermiyor (`WpfCustomerProjectionSyncService.cs:190-194`) ve akıştan gelen silinmiş öğeyi yalnız silme kararı olarak uyguluyor (`CustomerChangesPullService.cs:426-427`). Bu yüzden silinmiş satırın yerel grup numarası bilgisayarlar arasında ayrışabilir. Sunucuda ise silinmiş üye canlı üyelerin numara değişimini izler (§4.12). Böylece apply, preview, panel, kart, Shopper ve Ayır onayı aynı silinmiş üyeleri görür (K3).

**Sürüm denetimi (havuz kipi).** Havuz kipinde `memberIds` zorunludur; yoksa 400 `member-ids-required`. İstemci sürümü `User-Agent`'tan okunur, satış senkronunun S1'inin getirdiği ortak yardımcıyla (`Services/Sync/ClientVersion.cs`: `FromUserAgent`, `IsAtLeast`; `LicensesController` de onu kullanıyor). Bu spec ayrıştırıcıyı taşımaz, yeni bir ayrıştırıcı da yazmaz: `PersonBalance:MinClientVersion` ve `SalesSync:MinClientVersion` aynı karşılaştırmayı paylaşır (§15). Sürüm `PersonBalance:MinClientVersion`'dan (Wh, §7.2) eskiyse, ayrıştırılamıyorsa ya da hiç yoksa preview ve yeni yazım yapacak apply **409 `client-update-required`** döner. Gerekçe §7.2'de. Yeniden oynatma (anahtar sunucuda kayıtlı) bu denetimden önce çalışır ve hiç engellenmez.

**Panel, Shopper ve kart okumaları** WPF üye listesi taşımaz; hepsi `S(a)` üzerinden okur. Senkron penceresinde kartın gösterdiği havuz ile apply'ın çektiği küme kısa süre farklı olabilir (§11).

### 4.1 `GET preview` (`LicensesCustomerBalanceApplyController.cs:74-91`)

- **Satır kipi:** bugünkü gibi. Yanıta `PersonPool = false` eklenir; S1 bu alanı hep `false` döner.
- **Havuz kipi:**
  - Sırayla: sürüm denetimi, ardından `P` ve 409 kuralları.
  - `Balance = max(0, Σ)`; `Σ` = `P`'nin bakiyeleri + `S∖P`'nin eksi bakiyeleri (§4.0-4).
  - `UpdatedAt` = `P`'deki son para hareketinin `CreatedAt`'i; hareket yoksa satırların en yeni `UpdatedAt`'i. Böylece kişi kilidinin ilerlettiği jeton "güncellendi" zamanını oynatmaz.
  - `PersonPool = true`.
  - Yanıttaki Id istekteki Id olarak kalır.
- **Örnek (eksi satır havuzu kilitler):** A = 50, B = −60 → Σ = −10 → `Balance = 0`. Satır kipinde A'nın 50'si kullanılabiliyordu. Bu kişi ön kontrolde listelenir; havuz kipi o lisansta eksi satır varken sahibin onayı olmadan açılmaz (§7.3).

### 4.2 `POST apply` (`:224-362`)

Doğrulamalar (`:230-251`), sahiplik (`:253`), asıl kayda çözme (`:257`) ve **yeniden oynatma** (`:261-269`) önce, bugünkü sırayla çalışır. Satır kipinde akış değişmez; aşağıdaki kapsam kuralı iki kipte de geçerlidir.

**Kapsam başına tek düşüm (satış senkronu §6.6, iki bakiye kipinde).** Kural bu spec'ten önce, satış senkronunun S3'üyle bugünkü apply'a gelir. Bu spec'in S2'si apply'ı `DrawAsync` olarak yeniden yazarken onu korur.
- **Koşul:** lisansın satış senkronu modu On ∧ `SaleScope` dolu ∧ anahtar yeni (yeniden oynatmadan sonra). `legacy:` öneki ayırt edici değildir: miras işin replay'i asıl kapsamla gelir, kural ona da uygulanır. Mod Off'ta kural yok.
- **Kural:** `sp_getapplock('bal:{lisans}:{asıl kayıt}:{kapsam}')` alınır; ardından `/scope` ucunun yüklemi (§4.3) çalışır: aynı asıl kayıt, aynı kapsam, `purchase-deduction`, geri alınmamış. Böyle bir çapa varsa 409 `scope-has-deduction {transactionId, appliedAmount, productTotal}`. Kilit, yüklem ve yazım aynı işlemdedir.
- **Çapa düzeyinde kalır.** Bacaklar `SaleScope` taşımadığı için yüklem yalnız çapada tutar (Örnek 1: `scope(I, session:42)` → 204). Aynı kişinin başka satırındaki çapayı 409 yapmak #506'nın meşru çok izli akışını bozardı (§13 sonuç notu); o iz #506'nın birleştirme sorusuna düşer. Başka üyedeki izi grup senkronu gecikmesinde görünür kılan, satış senkronunun ödeme kirasıdır (`members-stale`, üye kesişimi).
- **Her denemede.** Kilit işlem kapsamlıdır. Adım 6'nın denemeleri ayrı işlemlerse kilit aralarında bırakılır ve araya aynı kapsamda başka anahtarlı bir düşüm girebilir. Bu yüzden yüklem her denemede `LookupAsync`'ten sonra yeniden çalışır; ikinci deneme `no-balance` değil `scope-has-deduction` döner.
- **Havuz kipinde tutar grubun toplamıdır.** 409'daki `appliedAmount` çapanın `Amount`'u değil `−Σ(grup)`'tur (`transactions/{id}` ile aynı, §4.4). Çapanın kendi payı 0 olabilir (Örnek 1'de K = 0,00); çapa tutarı dönseydi WPF'in benimsediği düşüm (`AdoptRemoteResult`) 0 ₺ görünür, revizyon adımı müşteriden parayı ikinci kez isterdi.

Havuz kipinde:

1. Sürüm denetimi, ardından `P` (§4.0). Para yalnız `P`'den çekilir. `S∖P`'nin eksi satırları okunur ve kilitlenir, çekilmez.
2. `P`'nin ve `S∖P`'nin eksi üyelerinin bakiye satırları izlenen nesneler olarak yüklenir. **Ardından üyelik yeniden okunur:** `S(a)` ve bu satırların `GroupId`'leri. Fark varsa plan adım 1'den kurulur. Böylece üyelik okuması ile bakiye yüklemesi arasında kaydedilen bir ayırma da görülür; ikinci okumadan sonra kaydedilen ayırmayı kanca yakalar (§4.11). `Σ ≤ 0` ise **409 `no-balance`** (bugünkü başlık; WPF bunu kesin "bakiye yok" sayar, `PaymentRequestService.cs:1341-1347`).
3. `applied = min(Amount, max(0, Σ), ProductTotal)`; `≤ 0` ise `nothing-to-apply` (bugünkü gibi, `:279-281`).
4. **Plan (`PlanDraw`, saf):** her adımda o satırdan `min(max(0, bakiye), kalan)` alınır. Sıra:
   1. Çapa satırı. Alıcının kendi parası önce kullanılır; satırlar arası hareket en aza iner (K1).
   2. Silinmiş üyeler (`S`'den), bakiye büyükten küçüğe. Ayırmadan sonra erişilemez kalabilecek para önce kullanılır (K3).
   3. Canlı üyeler, bakiye büyükten küçüğe; eşitlikte Id ("N", ordinal).
5. **Yazım:**
   - Çapa: `Id = anahtar ?? NewGuid`, `Amount = −çapadanAlınan` (0 olabilir), `OriginalAmount = ProductTotal`, `SaleScope`, `DrawGroupId = Id`.
   - Bacaklar: `Id = NewGuid`, `DrawGroupId = çapa`, ortak `CreatedAt`.
   - Çekilen satırların `Balance`'ı düşer.
   - **`P`'nin bakiye satırı olan bütün üyelerine ve `S∖P`'nin eksi üyelerine** jeton dokunuşu yapılır (kişi kilidi, §4.13).
   - Çapa satırının bakiye satırı yoksa `Balance = 0` ile açılır ve kilide girer. 0'lık satır D1'i bozmaz. Gerekçe: bakiye satırı olmayan bir sahip kimlik birleşmesiyle aynı anda kopyaya dönerse çapa hiçbir jetona çarpmadan kopyada kalırdı (§4.12).
6. Tek `SaveChanges`; EF bunu tek işlemde yazar. `EnableRetryOnFailure` tanımlı değil (`Program.cs:115`).
   - **2. ve sonraki her denemenin başında**, anahtar varsa `LookupAsync` yeniden koşar; kayıt bulunursa onun sonucu oynatılır. Aynı anahtarlı başka bir istek araya girip havuzu boşalttıysa ikinci deneme `no-balance` değil kazananın sonucunu döner. Bugünkü döngü (`:326-330`) bunu yapmıyor; WPF `no-balance`'ı kesin sayar (`PaymentRequestService.cs:1341-1347`).
   - **Jeton çakışmasında** (`DbUpdateConcurrencyException`) ve çapanın bakiye satırını açarken tekil `(LicenseId, WpfCustomerId)` ihlalinde: `ChangeTracker.Clear()`, adım 1'den yeniden; en çok 3 deneme. Bugün yalnız satır yeniden yükleniyor (`:316-340`). Burada üyelik de değişmiş olabileceği için plan baştan kurulur. Üçüncü çakışmadan sonra **503 `balance-busy`**: WPF'in dayanıklılık katmanı aynı anahtarla yeniden dener; o da düşerse iş belirsiz kalır ve mesaj gitmez.
   - **PK ihlali** (aynı anahtarla yarış): bütün bacaklarla birlikte geri alınır, kazananın sonucu oynatılır (`:341-358`, değişmez).
   - **S2 uygulama notu (S1'den):** `EnableRetryOnFailure` olmadığı için varsayılan yürütme stratejisi kilitlenme kurbanını (1205) `DbUpdateException` olarak değil, `InvalidOperationException`'a sarılı fırlatır. Çok satırlı yazım döngüleri kilitlenmeyi `catch (Exception ex) when (PersonBalance.IsDeadlock(ex))` ile yakalar; yalnız `DbUpdateException`'a bakan süzgeç hiç tutmaz, kurban 500 alır. `try` denemenin okumalarını da kapsar: yazarlar birbirini kilitleyince aynı anahtarın kuyruğunda bekleyen okuma da kurban seçilebilir (satır okunurken gelen 1205 çıplak `SqlException`'dır). İkisi de S1'in grup geri almasında (§4.5) Testcontainers §8.2-3'te görüldü; kanıt `PersonBalanceReversalDeadlockTests`.
7. Yanıt: `TransactionId = çapa`, `AppliedAmount = applied` (bütün bacakların toplamı), `RemainingBalance = max(0, Σ − applied)` (`Σ`, §4.0-4).

**Örnek 1 — çok bacaklı düşüm.** Kişi: T (TikTok; sahip: GUID ve geçerli telefon, `PaymentPerson.cs:112-124`), I (Instagram), Y (YouTube). Bakiyeler T = 0, I = 100, Y = 30; havuz 130. Satış `session:42`, ProductTotal 120, Amount 120, anahtar K. Sonuç `applied = min(120, 130, 120) = 120`.

| Id | Satır | Amount | Kind | OriginalAmount | SaleScope | DrawGroupId |
|---|---|---|---|---|---|---|
| K | T | 0,00 | purchase-deduction | 120,00 | session:42 | K |
| L1 | I | −100,00 | purchase-deduction | — | — | K |
| L2 | Y | −20,00 | purchase-deduction | — | — | K |

- Bakiyeler sonra T = 0, I = 0, Y = 10; havuz 10.
- Yanıt `(K, 120, 10)`. `transactions/K` → `AppliedAmount = 120`, `Reversed = false`.
- `scope(T, session:42)` → K/120. `scope(I, session:42)` → 204: bacak kapsam taşımaz.

**İdempotency ve A11 (`LookupAsync`, `:385-440`)**
- Anahtar yine tek bir defter satırının PK'sı: çapa. Çapa her zaman istekteki (sahip) satırda olduğu için `sameCustomer` (`:418-419`) **değişmez**. Kişi düzeyine çıkarmak, ayırmadan sonra aynı anahtarın başka bir kişiye yeniden oynatılması kapısını açardı. #506'nın sahip sabitlemesi ve `ReassignOwner`'ı aynen geçerli.
- `OriginalAmount` karşılaştırması çapada (`:421`).
- Tutar karşılaştırması `−tx.Amount > req.Amount` yerine `−Σ(grup) > req.Amount` (`:422`).
- **Bacak anahtar olamaz:** bulunan satır çapa değilse (`DrawGroupId` dolu ve `≠ Id`) "foreign" sayılır ve 404 döner (`:393-399` koluna eklenir). İstemcinin ürettiği bir Guid'in sunucunun ürettiği bacak Id'sine denk gelmesi pratikte imkânsız; kural yalnız savunma.
- `SaleScope` karşılaştırmaya bugünkü gibi girmez (`:407-412`).
- Oynatmadaki `RemainingBalance` = o anki havuz (`Σ`, §4.0-4; `memberIds` yoksa `S`). WPF bu alanı okumuyor.
- Yeniden oynatma sürüm ve üyelik denetiminden **önce** çalışır. Kayıtlı bir anahtar hiçbir zaman `customer-not-synced` ya da `client-update-required` almaz.

### 4.3 `GET scope` (`:123-154`)

- Yalnız çapalar aranır. Bugünkü süzgeç zaten yeterli: bacaklar `SaleScope` taşımaz (`:145`).
- `AppliedAmount = −Σ(grup)`; bugün `−t.Amount` (`:150`).
- Satır başına çalışmaya devam eder. #506 her GUID'li üyeyi ayrı soruyor ve sonuçları `TransactionId` ile tekilleştiriyor (`PaymentRequestService.cs:1099-1118`). Kapsamı kişi düzeyine çıkarmak gerekmez.
- Yanıta `PersonPool` eklenir (S1'de `false`). WPF bunu birleştirme notu için kullanır (§6.1-d).

### 4.4 `GET transactions/{id}` (`:180-205`)

- Id bir bacaksa çapaya çözülür.
- `AppliedAmount = −Σ(grup)`.
- `Reversed` = çapanın geri alma satırı var mı. Grup geri alması tek işlemde yazıldığı için çapanınki grubun tamamını temsil eder (D5).
- WPF yalnız `Reversed`'i okuyor (`PaymentRequestService.cs:1192-1207`).
- `transaction-not-found` başlığı aynen kalır (`:195-199`).

### 4.5 `POST transactions/{id}/reverse` — WPF (`:450-519`)

S1 (grup geri alması):
- Id bir bacaksa çapaya çözülür. Çapanın geri alma satırı varsa → 409 `already-reversed` (ön kontrol, `:464-466`).
- Grubun **her** bacağı için bir `reversal` yazılır: `Amount = −bacak.Amount` (`≥ 0`), `ReversesTransactionId = bacak.Id`, `DrawGroupId` = çapanın geri alma satırının `Id`'si, ortak `CreatedAt`.
- Geri alınan düşüm eski, tek bacaklı bir satırsa (`DrawGroupId NULL`) tek `reversal` satırı `DrawGroupId = NULL` ile yazılır: satır biçimi bugünküyle aynı (§3.2).
- Para her bacağın **güncel** satırına eklenir: bacağın `WpfCustomerId`'si çekim anındaki değil, geri alma sırasında okunan değerdir (kimlik birleşmesi bacağı asıl kayda taşımış olabilir, §4.12). Tutarı 0 olan bacakta bakiye satırına dokunulmaz; `:471-472`'deki `NotFound` kolu yalnız tutar > 0 iken anlamlıdır. Satır yoksa açılır (artı tutar her zaman güvenli; panel yazıcısının kalıbı, `PanelCustomerBalanceController.cs:299-314`).
- Tek `SaveChanges`. **Jeton çakışmasında** (ve satır açarken tekil ihlalde) `ChangeTracker.Clear()`; bacaklar, bacakların güncel `WpfCustomerId`'leri, çapanın geri alınıp alınmadığı ve bakiye satırları taze okunur, grup baştan kurulur; en çok 3 deneme, sonra 503 `balance-busy`. Bugünkü "yeniden yükle, deltayı yeniden uygula" kalıbı (`:502-510`) yalnız tek satırda doğrudur: çok satırlı bir `SaveChanges`'te EF yalnız çakışan girdiyi döndürür, öbür satırlar bellekteki `eski + delta` değerini korur; deltayı her satıra yeniden eklemek çakışmayan satırlara parayı iki kez yazardı.
- **N01:** bacaklardan birinde tekil ihlal → işlemin tamamı geri alınır → 409 `already-reversed`. İstemci bunu başarı sayar (`PaymentRequestService.cs:1412-1415`).
- Para yalnız eklendiği için eksi kontrolü ve kişi kilidi gerekmez.

**Örnek 2 — revizyon (WPF eski düşümü geri alır, sonra taze apply yapar; `LicensesCustomerBalanceApplyController.cs:444-449`).** Örnek 1'den sonra satış toplamı 120'den 90'a iner.
- `reverse(K)` → R0 (T, +0, Reverses K, DrawGroupId R0), R1 (I, +100, Reverses L1, DrawGroupId R0), R2 (Y, +20, Reverses L2, DrawGroupId R0). Bakiyeler T = 0, I = 100, Y = 30.
- Taze apply K′ (90) → K′ (T, 0), L3 (I, −90). Sonuç I = 10, Y = 30; havuz 40.

### 4.6 Panel `GET` (`PanelCustomerBalanceController.cs:63-93`) — panel ve WPF kartı

- **Satır kipi:** `Balance` bugünkü gibi rota satırınınki. Hareketler rota satırınınki, ama gruplara toplanmış hâlde (aşağıda).
- **Havuz kipi:** `Balance = Σ_S Balance`. Eksi olabilir; borç operatöre görünmeli. Hareketler `S`'nin bütün üyelerinden gelir.
- **Toplama (`AggregateHistory`, iki kipte de):**
  - Aynı `DrawGroupId ?? Id`'li satırlar tek satır olur.
  - `Id` = çapa; `Amount` = grubun **bu kişideki** bacaklarının toplamı. Ayırmadan sonra öbür kişide kalan bacaklar sayılmaz; böylece listedeki tutarların toplamı her zaman `Balance`'a eşit olur.
  - `Kind`, `OriginalAmount`, `ShippingDeducted`, `Reason` ve `ReversesTransactionId` çapadan gelir.
  - Son N **grup** döner (`take`, `:66-69`).
- **Yeni alan `Members[{wpfCustomerId, balance, purged}]`** (S1'den itibaren, iki kipte de `S` üzerinden): üye başına satır bakiyesi. WPF'in Ayır onayı bunu kullanır (§6.1-e). Ek alan; panel istemcisi yok sayar.
- `UpdatedAt` = son para hareketi (§4.1).

### 4.7 Panel `refund-full` / `refund-net` (`:99-158`)

Değişmez: artı hareket rota satırına yazılır (`ApplyTransactionAsync`, `:274-339`). Havuz okumada topladığı için hangi satıra yazıldığı yalnız ayırmada önem taşır (K1). İlk satır yarışındaki 500 (`:310-312`) bugünkü gibi kalır.

### 4.8 Panel `manual-adjustment` (`:164-193`) — K5

- **Artı tutar:** değişmez.
- **Eksi tutar, satır kipinde:** bugünkü gibi (`disallowNegative`, 409 `insufficient-balance`).
- **Eksi tutar, havuz kipinde:** `DrawAsync` ile çok bacaklı çekim yapılır.
  - Çapa rota satırında: `manual-adjustment`, `Reason` zorunlu (`:171-172`).
  - Kalan, `S`'nin öbür pozitif üyelerinden çekilir. Sıra §4.2'deki planla aynı; panelde `W` yok, `P = S`.
  - Havuz tutara yetmezse hiçbir şey yazılmaz → 409 `insufficient-balance`. Kısmi ayar yok; bugünkü hep-ya-hiç kuralı.
  - Kişi kilidi `S`'nin bakiye satırlarına uygulanır.
- **Örnek:** A = 10, B = 40, rota A, "−30 elle düzeltme". Çapa (A, −10, Reason=…), bacak (B, −20) → A = 0, B = 20.

### 4.9 Panel `reverse` (`:197-246`) — K5

- **Üyelik koşulu:** bugün hareket rota satırının olmalı (`t.WpfCustomerId == canonicalId`, `:207-211`).
  - **Çekim grubuna ait hedef** (`DrawGroupId` dolu): grubun herhangi bir satırı rota satırındaysa (satır kipi) ya da `S(rota)`'daysa (havuz kipi) kabul edilir; hedef çapaya çözülür. **S1'den itibaren, iki kipte de.** Panel listesi grubu çapa `Id`'siyle gösterir (§4.6); çapa başka satırda olsa da rota satırındaki bacaktan açılan iptal bulunur. `disable`'dan sonra da (§7.2) bu düzeltme yolu açık kalır.
  - **`DrawGroupId` boş hedef:** satır kipinde koşul aynen kalır. Havuz kipinde hedef `S(rota)`'da olmalı: panel kişinin birleşik listesini gösterdiği için başka üyenin hareketi de iptal edilebilmeli.
- **Hedefe göre davranış:**
  - **Düşüm (çapa ya da bacak, `purchase-deduction`):** grup geri alması (§4.5). Tek satırı geri almak yarım bir geri alma bırakırdı.
  - **Elle eksi grubu:** grup geri alması.
  - **Artı hareket (`refund-full`, `refund-net`, artı `manual-adjustment`):**
    - Satır kipinde bugünkü gibi.
    - Havuz kipinde **çekim türü iptal**: çapa iptal edilen hareketin **kendi satırında** (rota satırında değil), `Kind = reversal`, `ReversesTransactionId` = hedef hareket; kalan `S`'nin öbür pozitif üyelerinden bacak olarak çekilir. Havuz yetmezse 409 `insufficient-balance` (hep-ya-hiç).
  - **`reversal` satırları:**
    - Çekim grubuna ait olanlar (çekim türü iptalin çapası ya da bacağı, grup geri alma satırı; `DrawGroupId` dolu) → **409 `not-reversible`**, S1'den itibaren iki kipte de. Böyle satırlar yalnız havuz kipinde doğar (§3.2); havuz kipi hiç açılmamış bir lisansın davranışı değişmez.
    - Eski `reversal` satırı (`DrawGroupId NULL`): satır kipinde bugünkü gibi (davranış değişmez), havuz kipinde 409 `not-reversible`.
    - Panel arayüzü zaten yalnız iade ve elle ayar satırlarında iptal düğmesi gösteriyor (`CustomerBalanceCard.tsx:154-166`); ret yalnız API'yi etkiler. "İptalin iptali"nin çok bacaklı zincirini kapatır. Banka spec'inin `bank-overpayment` reddi (§6) aynen geçerli.
- **S1 tabanı:** `DrawGroupId` dolu her hedef S1'de ya grup olarak geri alınır ya reddedilir. S2 elle geri alınıp S1'e dönülse bile bir K5 iptal grubu bugünkü tek satır yoluyla yarım geri alınamaz (§7.4).
- **Hakem:** N01. Eşzamanlı iki iptalde kaybeden, bütün bacaklarıyla birlikte geri alınır → 409 `already-reversed` (`:237-242`).

**Örnek 4 — iade iptali (K5).** Kişi {A, B}.
- A'ya iade R (+80) yazıldı; sonra bir düşüm A'dan 50 çekti: A = 30. B'de kendi iadesinden 60 var: B = 60. Havuz 90.
- Panel R'yi iptal ediyor (−80). Bugün A = 30 < 80 olduğu için 409 dönerdi.
- Havuz kipinde: çapa V (A, −30, `reversal`, Reverses R, DrawGroupId V), bacak (B, −50, `reversal`, DrawGroupId V). Sonuç A = 0, B = 10.
- B = 20 olsaydı havuz 50 < 80 olurdu → 409, hiçbir şey yazılmaz.

### 4.10 Shopper `GET` (`ShopperBalanceController.cs:38-76`) — K4

- Bağlantının satırı önce asıl kayda çözülür. Bugün çözülmüyor (`:50-57`); birleştirmede bağlantı taşındığı için şimdilik yeterli, ama kişi çözümü asıl kayıttan başlamalı.
- **Satır kipi:** bugünkü gibi; hareketler gruba toplanır.
- **Havuz kipi:** `Balance = Σ_S`. Hareketler `S`'nin bütün üyelerinden, toplanmış hâlde.
- **`Reason` maskesi:** açıklama yalnız grubun çapası bağlı satırdaysa döner. Başka üyeden gelen hareketlerde tutar ve tür görünür, `Reason = null`. Shopper istemcisi `reason` boşsa satırı göstermiyor (`BalanceHistoryModal.tsx:109-110`); istemci değişikliği gerekmez.
- Gizlilik notu: bugün shopper'ın görebildiği veri telefon kanıtına bağlı (`WpfCustomerLinkMatcher.cs:8-17`). Havuzda öbür üyelerin tutarları yayıncının gruplamasıyla görünür. Sahibin kararı K4 bunu kabul ediyor; serbest metin gizli kalıyor.

### 4.11 `GroupId` yazanlar: senkron, `group-customers`, `--at-twins`

- **Jeton kancası (S1).** `LicenseDbContext.SaveChanges(Async)` (`:198-213`) kaydetmeden önce şunu yapar:
  - `GroupId`'si değişmiş (ordinal) her izlenen projeksiyonu bulur;
  - varsa bakiye satırını önce izlenenlerde (`Local`), yoksa tek sorguyla yükler;
  - satırı değişmiş işaretler. Jetonu `StampVersions` ilerletir (aşağıda).
  - **Silinmiş üyenin numara izlemesi** (S1, K3, §4.12): canlı bir asıl üye `g` numarasından ayrıldıysa ve bu işlemden sonra `g`'de canlı asıl üye kalmadıysa (veritabanı + izlenen değişiklikler, ordinal), `g`'deki silinmiş asıl üyeler, `g`'yi boşaltan kayıttaki ayrılanların yeni numarasına taşınır, damgalı (`CustomerGroupUnion.StampAt`). Yeni numara boşsa (Ayır; ya da son canlı üye grupsuz bir asıl kayda kopya oldu, §10) ya da ayrılanlar aynı kayıtta farklı numaralara gittiyse silinmiş üye `g`'de tek kalır; sonuncusu günlüğe yazılır, hepsi `verify`'da görünür (§7.3-4). Bu taşıma da bir `GroupId` değişimidir; silinmiş üyenin bakiye jetonu aynı kancayla ilerler.
  - **Taşıma yalnız sunucuda** (2026-10-21, S1 incelemesi). WPF'te silinmiş satırın numarası değişmez (§4.12): akıştaki silinmiş öğe yalnız silme kararıdır, `group-customers` ve `--at-twins` yalnız sunucuda koşar, elle Birleştir silinmiş satırı yalnız birleştiren PC'de taşır. Sunucunun kişisi bir PC'nin yerel kişisinden silinmiş üyeler kadar fazla olabilir ve hiçbir çekme turu bunu kapatmaz. Bu yüzden sunucu grubunu istemcinin üye listesiyle karşılaştıran her denetim yalnız **canlı** üyeleri karşılaştırır: havuz kipinin kural 3'ü (§4.0-3) ve ödeme isteği kirasının (b) adımı (§15-7).
  - Sunucuda `GroupId`'ye yazan üç yer var: `CustomerFieldMerge.cs:54-56` (senkron), `CustomerGroupUnion.cs:69` (`group-customers` ve `--at-twins`) ve `CustomerIdentityMergeJob.cs:567`. Üçü de izlenen nesneler üzerinden yazıyor; kanca üçünü birden kapsar (numara izlemesi dahil: `MoveToGroup`'un silinmiş satırı atlaması, `:66`, bu yüzden değişmeden kalabilir).
  - Bir convention testi, sunucu kodunda `GroupId`'ye ham SQL ya da `ExecuteUpdate` ile yazım olmadığını sabitler; kanca öyle bir yazımı göremezdi.
- **`CustomerBalance` `StampVersions`'a eklenir** (`:174-192`, bugün `NetgsmAccount` ve `PaymentMatch`). Her değişmiş bakiye satırının jetonu `max(UtcNow, özgün + 1 tick)` olur. `UtcNow` atamasına güvenen bugünkü yazarlar saat geri giderse aynı değeri yazabilirdi; kişi kilidinin bir "dokunuşu" böylece hiçbir zaman kaybolmaz.
- **Etkisi (hakem aşısı):**
  - Apply üyeliği okur, bakiye satırlarını yükler, sonra üyeliği yeniden okur (§4.2-2). Ayırma bu adımlardan önce ya da arasında kaydedilirse ikinci okuma farkı görür ve plan taze üyelikle baştan kurulur. İkinci okumadan sonra kaydedilirse kanca, apply'ın yüklediği bakiye satırının jetonunu ilerletmiştir; apply'ın UPDATE'i tutmaz ve plan baştan kurulur.
  - Apply ayırmadan önce kaydedilirse sıra "apply ayırmadan önce oldu"dur; değişmez kurallar bozulmaz. Senkronun kancası bakiye satırını kaydetme anında yüklediği için senkron çoğunlukla çakışmaz. Apply kancanın yüklemesiyle senkronun kaydı arasında kaydedilirse senkron çakışır; var olan temizle-yeniden-uygula döngüsü (`LicensesWpfCustomersSyncController.cs:256-297`) partiyi taze okumayla uygular, ikinci çakışmada 409 döner ve WPF partiyi yeniden gönderir.
  - Birleştirme ve gruplama işleri çakışan grubu geri alıp sayar (`CustomerIdentityMergeJob.cs:297-304`; `CustomerGroupingJob.cs:230-243` hata çağırana çıkar); yeniden koşmak kalanları bulur.
  - Bakiye satırı olmayan üyenin üyeliği değişirse dokunulacak bir şey yoktur: para taşımaz. Ayrıntı ve kalan pencere §5.5'te.

### 4.12 Kimlik birleşmesi ve KVKK silmesi

- **`CustomerIdentityMerger.RepointReferencesAsync`** (`:39-139`) tek satır dışında değişmez:
  - Bacaklar öbür hareketlerle birlikte asıl kayda taşınır (`:75-77`), `DrawGroupId` korunur.
  - Aynı çekimin iki bacağı aynı satıra düşebilir. Sorun değil: indeks tekil değil, geri alma bacak başına yazılıyor.
  - Kopyanın bakiyesi asıl kayda eklenir (`:79-116`, karar 2026-10-05).
  - **Değişen satır:** kopyanın bakiye satırı, tutarı 0 olsa da damgalanır (`UpdatedAt = now`). Bugün yalnız `Balance != 0` iken dokunuluyor (`:95`). Çok bacaklı düşümden sonra bacak satırı tipik olarak 0'da kalır (Örnek 1: I = 0). Damga olmasa, bacak satırını okuyan eşzamanlı bir grup geri alması çakışmadan commit eder ve parayı kopya satıra yazar; bütün okumalar asıl kayda çözüldüğü için o para görünmez olurdu. Damgayla geri alma çakışır ve bacağın güncel satırıyla yeniden kurulur (§4.5).
  - Dar pencere kalır: birleştirici kopyanın hareketlerini bakiye satırından önce okur (`:75`, `:93`), arada yazılan bir hareket kopyada kalabilir (bugün de iade için aynı). `verify`'ın "kopya satırda hareket ya da bakiye" kalemi bunu görür (§7.3-8); düzeltme sahiple.
- **`ShopperPurgeService`** (`:219-222`) değişmez. Silinen satırın bakiyesi ve hareketleri yerinde kalır. Silinmiş satır kişisel alan yazımlarına kapalı kalır (`CustomerFieldMerge.cs:42`), ama **grup numarası canlı üyeleri izler** (§4.11'deki kanca, S1):
  - İki grup birleşince (WPF elle Birleştir, form telefonu, `group-customers`, `--at-twins`, kimlik birleşmesi işi) canlı üyeler yeni numaraya geçer; eski numarada canlı üye kalmayınca silinmiş üye de geçer. WPF'teki elle Birleştir ve form telefonu yolu silinmiş satırı yerelde zaten taşıyor (`CustomerRepository.cs:422-429`, `:1141-1144`); sunucu böylece aynı sonuca varır.
  - Ayır'da (yeni numara boş) silinmiş üye eski numarada tek kalır. WPF de onu yerelde ayırır (`UnmergeGroup`, `:616-622`); iki tarafta da ayrı kişidir.
  - Numara kişisel veri değildir; silinmiş satırın kişisel alanlarına dokunulmaz. Akıştaki silinmiş öğe WPF'te yalnız silme kararı olarak uygulanır (`CustomerChangesPullService.cs:426-427`); öbür bilgisayarlardaki yerel numarası değişmez. Bu sorun değil, çünkü silinmiş üyeler `S`'den okunur (§4.0-4).
  - **Seçilmeyen yol:** WPF'in silinmiş satır için yalnız `GroupId` birimini göndermesi ve `CustomerFieldMerge`'in silinmiş hedefte bu birimi kabul etmesi. Gönderim protokolünü, WPF'in silinmiş satır atlamasını (`WpfCustomerProjectionSyncService.cs:180-194`) ve akış uygulamasını değiştirirdi; eski sürümlü PC'ler göndermediği için yine ayrışırdı. Sunucu kuralı tek yerde ve sürümden bağımsız.
  K3 gereği silinmiş üye apply, preview, panel, kart, Shopper ve Ayır onayında aynı `S` üzerinden havuza girer. Silinmiş satırdan çekilmiş bir bacak geri alınınca para o satıra döner ve yine havuzdan erişilir.
  - **K3'ün istisnası Ayır:** bütün canlı üyeler ayrılınca silinmiş üye tek kalır ve parası hiçbir canlı kayda bağlı değildir (K1: her kayıt kendi parasıyla). Onay penceresi bunu gösterir (§6.1-e). Aynı sonuç, numaranın son canlı üyesi grupsuz bir asıl kayda kopya olunca da doğar; orada onay penceresi yoktur. Sahip kararı bekliyor (§10 açık kalanlar).
  - **Geçmiş:** S1'den önceki numara değişikliklerinde geride kalmış silinmiş satırlar olabilir (bugün `MoveToGroup` ve senkron silinmiş satırı taşımıyor). Kanca geçmişe dönük çalışmaz. `verify` "grubunda canlı üye kalmamış, bakiyesi ≠ 0 silinmiş satır" sayısını ve toplamını basar (§7.3-4); eski numaranın geçmişi tutulmadığı için hangi kişiye ait olduğu kanıtlanamaz, sahiple tek tek incelenir.

### 4.13 Eşzamanlılık özeti

| Yazım | Dokunduğu jetonlar | Hakem | Çakışmada |
|---|---|---|---|
| WPF apply (havuz) | `P`'nin bakiye satırı olan **bütün** üyeleri, `S∖P`'nin eksi üyeleri, çapa satırı (yoksa açılır) | `UpdatedAt` jetonu; PK = anahtar; üyeliğin ikinci okuması | Her denemede önce `LookupAsync`; planı baştan kur, 3 deneme, sonra 503 `balance-busy`; PK ihlalinde oynat |
| WPF apply, satış senkronu mod On ∧ kapsam dolu ∧ anahtar yeni (iki kipte) | Yukarıdakiler (havuz) ya da çapa satırı (satır kipi) | `sp_getapplock('bal:{lisans}:{asıl kayıt}:{kapsam}')` + `/scope` yüklemi, kilit ve yazım aynı işlemde (§4.2) | 409 `scope-has-deduction`, tutarı `−Σ(grup)`; her denemede Lookup'tan sonra yeniden |
| Panel eksi işlemler (K5, havuz) | `S`'nin bakiye satırı olan bütün üyeleri | `UpdatedAt`; iptalde N01 | Baştan kur, 3 deneme; N01'de 409 `already-reversed` |
| Grup geri alması (WPF ve panel) | Bacakların **güncel** satırları | `UpdatedAt` jetonu; N01 (bacak başına) | `ChangeTracker.Clear`, bacaklar ve satırlar taze okunur, grup baştan kurulur, 3 deneme; N01'de bütün grup geri alınır → 409 `already-reversed` |
| Artı hareketler (iade, artı ayar) | Rota satırı | `UpdatedAt` | Bugünkü döngü (`PanelCustomerBalanceController.cs:320-338`) |
| `GroupId` değişimi (senkron, işler; silinmiş üyenin numara izlemesi dahil) | Değişen satırın bakiye satırı (kanca) | `UpdatedAt` | Senkron: taze okumayla bir kez, sonra 409; işler: grubu geri al ve say |
| Kimlik birleşmesi | Kopya (tutarı 0 olsa da) ve asıl bakiye satırları | `UpdatedAt`, tekil `(LicenseId, WpfCustomerId)` | Bugünkü gibi |

- **Kişi kilidi neden bütün üyelere dokunuyor:** A = 100, B = −60, C = 50 (Σ 90) iken iki apply aynı anda koşsun. Biri A'dan 90 çeker, öbürü C'den 50. Satırları ortak olmadığı için çakışmazlar ve havuz −50'ye düşer. İki apply de A, B ve C'nin jetonuna dokunduğunda ikincisinin UPDATE'i tutmaz; taze planda Σ = 0'dır ve `no-balance` döner (D3).
- **Bedeli:** aynı kişiye aynı anda gelen yazımlar sıraya girer. Bu nadirdir.
- **Okuma** READ COMMITTED; açık işlem ve satır kilidi yok. Doğruluk jetonlardan ve tek `SaveChanges`'ten gelir. InMemory jeton yarışını kısmen taklit eder, tekil indeksleri uygulamaz; yarışlar yalnız Testcontainers'ta kanıtlanır (§8.2).
- **Collation:** kişi sorgusu SQL'de `GroupId == a.GroupId` ile (BIN2, indeksli) aday daraltır; sonuç bellekte `string.Equals(p.GroupId, a.GroupId, StringComparison.Ordinal)` ile ikinci kez süzülür.
  - Gerekçe: SQL Server'ın `=` ve `<>`'i ANSI dolgu kuralıyla her collation'da, BIN2'de de, sondaki boşluğu yok sayar (`'g1 ' = 'g1'` doğru). Sunucu `GroupId`'yi kırpmadan saklıyor (`CustomerFieldMerge.cs:260-261`); WPF ham değeri karşılaştırıyor (`CustomerGroup.cs:10-12`). Süzgeç olmasa `S("g1")` WPF'in ayrı saydığı `"g1 "` satırlarını içerirdi; panel, Shopper ve K5 (`P = S`) başka bir kişinin parasını gösterir ve çekerdi.
  - Kalıp `CustomerGroupUnion.LoadGroupRowsAsync`'teki gibi (`:45-58`: SQL aday daraltır, kesin eşleme bellekte), yalnız kırpmadan. Gruplama işlerinin kırparak karşılaştırması (`KeyOf`, `.Trim()`: bütün Unicode boşlukları, `:35-36`) o işlerde kalır. Ayrışma riskini `verify` sayar (§7.3-5); sayı 0 değilse `enable` reddeder.

## 5. Grup değişiklikleri

### 5.1 Katılma

Katılma yolları: elle Birleştir (`CustomerRepository.cs:393-436`), form telefonu (`:1124-1145`), YouTube benimseme (`CustomerService.cs:114-126` → `SetGroupId`, `CustomerRepository.cs:375-378`), `group-customers`, `--at-twins`.
- Veri işlemi yoktur. Sunucu yeni `GroupId`'yi gördüğü an üyenin bakiyesi havuza girer. WPF'ten bu en geç 60 sn sürer (`WpfCustomerProjectionSyncHostedService.cs:13`).
- İki grup birleşirse havuzlar kendiliğinden toplanır. Eski hareketler kendi satırlarında kalır.
- Senkron öncesi pencere: K2, §5.5.

### 5.2 Ayırma ("Ayır", `UnmergeGroup`, `CustomerRepository.cs:616-622`) — K1

- Veri işlemi yoktur. Her satır kendi parasıyla kalır: ona yapılan iadeler, eksi ondan çekilen bacaklar, artı ona dönen geri almalar.
- Başka satırdan alınmış para, o satış geri alınmadıkça kaynağına dönmez. Geri alınırsa her bacak kendi satırına döner; ayrılmış olsa bile.
- Ayır, onaydan önce üye başına tutarları gösterir (§6.1-e).

**Örnek 3 — ayırma.** Örnek 1'in sonu: T = 0, I = 0, Y = 10.
- Onay penceresinde: "TikTok: 0,00 TL · Instagram: 0,00 TL · YouTube: 10,00 TL". Ayırınca üç ayrı kişi, aynı tutarlar.
- Sonra K satışı iptal edilirse (toplam 0'a iner, S5 akışı K'yi geri alır): T +0, I +100, Y +20 → I = 100, Y = 30. I'nin parası I'ye döner, T'ye değil.

**Örnek — yanlış birleştirme.**
- Harcama olmadıysa: X = 100, Y = 50 birleştirildi → Ayır → yine 100 / 50. Para hiç taşınmadığı için eksiksiz döner.
- Harcama olduysa: Y'nin malı için 80'lik istek, sahip Y → çapa Y −50, bacak X −30 → X = 70, Y = 0. Ayır → X = 70, Y = 0. X'in 30'u o satış geri alınana kadar dönmez. Onay penceresi bunu ayırmadan önce gösterir.

**Sunucuya varış.**
- Ayırma satır satır ve 500'lük partilerle gelir (`WpfCustomerProjectionSyncService.cs:51`, `:174-199`). Arada sunucu yarım bir kişi görür.
- Ayıran PC'nin isteği `W` küçüldüğü için ayrılan satırlardan çekmez (§4.0-4). Ayrılan bir satırda para varsa, ayırma gönderilene kadar 409 `customer-not-synced` alır (§4.0-3b); tetiklenen tur onu gönderir.
- Ayırma sunucuya ulaştıktan sonra, onu henüz çekmemiş başka bir PC'nin isteği 409 `customer-not-synced` alır (§4.0-3a). Ulaşmadan önceki istek eski üyelikle çeker; bu, "ayırmadan önce oldu" diye sıralanabilir bir sonuçtur (§5.5).
- Silinmiş üye: WPF Ayır'ı silinmiş satırı da yerelde ayırır; satır gönderilmez (`:190-194`). Sunucuda canlı üyeler ayrılınca silinmiş üye eski numarada tek kalır (§4.12); iki tarafta da ayrı kişidir. Parası hiçbir canlı kayda bağlı değildir. Onay penceresi bunu "silinmiş hesap — ayırınca kullanılamaz" diye ayrıca gösterir. K3'ün bu istisnası sahip kararı bekliyor (§10).

### 5.3 Kimlik birleşmesi (`MergedIntoId`)

§4.12'deki gibi değişmez. Olağan kipte kimlik birleşmesi kişiyi bölebiliyor ("grup açığı"). Havuz bu durumda sessizce küçülür; para kaybolmaz, yalnız o kişiden erişilemez. Kapsam dışı (§1, §11).

### 5.4 KVKK silmesi — K3

Örnek: kişi {A, P}; P silinmiş ve 40 ₺ tutuyor, A = 0.
- 30'luk ödeme: çapa A 0, bacak P −30 (silinmiş üyeler önce) → P = 10.
- Satış geri alınırsa P +30 → P = 40. Para yine havuzdan kullanılabilir, çünkü sunucu P'yi kişide sayıyor (`S`).
- A sonra B ile (grup g0) birleştirilir ve WPF numara olarak g0'ı seçerse (`CustomerRepository.cs:415`) sunucuda A g0'a geçer, g1'de canlı üye kalmaz ve P de g0'a geçer (§4.11). g1 seçilirse B g1'e gelir; P zaten oradadır. Kişi {A, B, P}; P'nin 40'ı her PC'den, panelden ve Shopper'dan aynı görünür. Bugünkü kodda P g1'de tek kalırdı (`MoveToGroup` ve senkron silinmiş satırı taşımıyor, `CustomerGroupUnion.cs:66`, `CustomerFieldMerge.cs:42`).

### 5.5 Yarış pencereleri

| Pencere | Ne olur | Sonuç |
|---|---|---|
| Birleştir'den hemen sonra ödeme (≤ 60 sn, sahip satır sunucuda yok ya da grubu eski) | §4.0-1 ya da §4.0-3 | 409 `customer-not-synced`. WPF mesajı bekletir, iş taze kalır, bir gönderim + çekme turu tetikler (K2). Asla sessiz "bakiye yok" olmaz. Kişinin hiçbir üyesi sunucuda yoksa kesin `no-balance` (para olamaz, §4.0-1). |
| Ayıran PC, ayırma gönderilmeden ödeme istiyor | `W` küçük; ayrılan satırda para varsa 409, yoksa `P = W ∩ S` | Ayrılan satırlardan çekilmez. |
| Başka PC'nin ayırması ya da birleştirmesi bu PC'ye henüz inmemiş | `W ⊄ S` → 409; `S∖W`'de parası olan canlı üye → 409; `S∖W`'deki üyeler 0 ya da eksiyse `P = W ∩ S`, eksiler havuza sayılır | Ya bekletilir ya operatörün gördüğü küme çekilir. Yanlış satırdan çekilmez, görülmeyen para kesin "bakiye yok"a dönmez, görülmeyen borç havuzu açmaz. |
| Apply'ın okuması ile yazması arasında bir üyenin `GroupId`'si değişiyor | Üyeliğin ikinci okuması farkı görür; ikinci okumadan sonraysa kanca para tutan satırın jetonunu ilerletir | Plan taze üyelikle yeniden kurulur ya da senkron yeniden uygular. |
| Üyeliğin ikinci okuması ile kayıt arasında **para tutmayan** bir üyenin `GroupId`'si değişiyor (bakiye satırı yok) | Jeton yok | Apply eski üyelikle tamamlanır. Sıralanabilir bir sonuç: "apply ayırmadan önce oldu". Çekilen her satırın jetonu tuttuğu için çekilen para o an gerçekten o satırdaydı; geri alınırsa kaynağına döner. Değişmez kural bozulmaz. |
| Damgasız eski `GroupId` iki tarafta farklı kalmış (yakınsamaz) | Kalıcı `W ⊄ S` ya da `S ⊄ W` | Bu kişide kalıcı `customer-not-synced`. Bugün 570 grubun 570'i eşit (`CustomerGroupingJob.cs:17-19`); sözleşme değil, ölçüm. Sunucu her 409'u lisans ve satır Id'siyle günlüğe yazar. Kurtarma: operatör kişiyi WPF'te yeniden birleştirir; damgalı yazım yakınsatır. Risk §11'de. |
| Sahip satır hiç gönderilemiyor ya da sunucuda gruplanmış, parası olan bir satır bu PC'ye hiç inmiyor | Kalıcı §4.0-1 ya da §4.0-3b | W-havuz'un sahip kuralı gönderilemeyen satırı (`IsServerAcceptable`) seçmez (§6.1-a). Kalan neden: öğesi kurulamayan bozuk değer (`WpfCustomerProjectionSyncService.cs:227-235`) ya da hiç inmeyen satır. WPF aynı kişide 5 dakikadan uzun süren `customer-not-synced`'i ayrı metinle gösterir (§6.1-b); kurtarma operatörde (satırı düzelt ya da Ayır). |

## 6. İstemciler

### 6.1 WPF — W-havuz (PR #506'nın üstüne, aynı Velopack sürümünde = Wh)

Sahip kuralı (a), Ayır onayı (e) ve kart düzeltmesi (f) dışındaki yeni davranışların hepsi sunucu sinyaline bağlı. Wh, S1 ya da havuz kipi kapalı bir sunucuyla bu üçü dışında bugünkü gibi çalışır; üçü de satır kipinde doğrudur (sahip kuralı satır kipinde, sunucuda hiç olmayan bir satırın 0 bakiyesi yerine gerçek bir satırı okutur). Bu yüzden Wh'nin havuz kipi açılmadan önce sahaya çıkması güvenlidir; havuz kipi de Wh'siz açılamaz (§7.2).

**a) `memberIds`**
- `GetBalancePreviewAsync` (`LicenseApiClient.cs:392-399`) ve `ApplyBalanceAsync` (`:441-446`; istek DTO'su `CustomerBalanceApplyRequest`) kişinin GUID'li üye Id'lerini taşır. Kaynak `PaymentPerson.Members`, `PaymentRequestService.cs:1096`'daki süzgeçle; silinmişler dahil (`PaymentPersonSql.cs:24-37`).
- Preview çağrısı (`PaymentRequestService.cs:1034`) kişiyi zaten elinde tutar.
- `ReplayAsync` (`:1315-1362`) yalnız işi bilir; üyeleri çağrı anında `GetPaymentPerson(job.CustomerId)` (`CustomerRepository.cs:607-612`) ile okur.
- Eski sunucu bilinmeyen sorgu parametresini ve gövde alanını yok sayar.
- **Sahip kuralı:** `PaymentOwnerRule.IsOwnerCapable` (`PaymentPerson.cs:108`) "GUID" yerine "GUID ve sunucunun kabul edeceği satır" olur. `IsServerAcceptable` (`WpfCustomerProjectionSyncService.cs:109`, kullanımı `:215-222`; Platform/Username denetimi) saf bir fonksiyon olarak Core'a taşınır ve iki yer aynı tanımı kullanır. `PaymentPersonMember`'a (`PaymentPerson.cs:11-16`) `Platform` ve `Username` eklenir; ikisi de senkronlanan, yakınsayan alanlar. Gönderilemeyen satır sahip olsaydı çapa hiç yazılamaz, kişide kalıcı `customer-not-synced` olurdu. Bilgisayarlar arası tutarlılık bozulmaz: gönderilemeyen satır başka bilgisayara hiç inmez, orada zaten aday değildir. Kural yalnız yeni işin sahibini seçer (`PaymentPerson.cs` belge yorumu); var olan işler üye taramasıyla bulunur.

**b) `customer-not-synced` (K2)**
- Preview'da: `:1037-1042`'deki genel `catch`'ten önce `ValidationException { Code: "customer-not-synced" }` yakalanır (kod eşlemesi `LicenseApiClient.ThrowMappedAsync`'te 4xx başlığından). Sonuç `BalanceOutcome` (`:619-630`) içinde yeni `NotSynced` bayrağıdır.
- Apply'da: `ReplayAsync`'in genel kolu (`:1357-1360`) zaten belirsiz bırakıyor; bayrak sonuçla birlikte taşınır.
- İki yerde de:
  - **`MarkNoBalance` yazılmaz**, iş taze ya da anahtarlı-belirsiz kalır;
  - mesaj gitmez;
  - fire-and-forget bir müşteri senkron turu tetiklenir: `CustomerChangesPullService.PullOnceAsync` (`:159`). Bu tur önce gönderir (`:226`), sonra çeker; kural 3'ün iki kolu da kapanır (ayırmayı ya da birleştirmeyi bu PC yaptıysa gönderim, başka PC yaptıysa çekme). Yalnız gönderim turunu (`WpfCustomerProjectionSyncService.SyncOnceAsync`) tetiklemek ikinci kolu 30 sn'lik çekme döngüsüne (`CustomerChangesPullHostedService.cs:18`) bırakırdı. Tur kilidi (`_tourGate`) üst üste binmeyi önler; bekleyen bir tetik varken yenisi eklenmez.
- `PaymentRequestResult`'a `BalanceNotSynced` eklenir (`:15`). `PaymentResultPresenter.Notify` (`:47-60` yanına) uyarı gösterir: başlık "Henüz eşitlenmedi", metin "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz."
- Aynı kişide `customer-not-synced` 5 dakikadan uzun sürerse (ilk görülme anı bellekte, kişi anahtarı başına) metin değişir: "Bu kişinin hesaplarından biri sunucuya ulaşmıyor; mesaj gönderilmedi. Müşteri kaydını kontrol edin ya da grubu ayırıp yeniden birleştirin." Günlüğe kişinin satır Id'leri yazılır (ad, telefon yok).
- Banka W2 açısından `BalanceUncertain` gibidir: mesaj gitmediği için beklenti üretilmez.

**c) `client-update-required`.** Wh ve sonrası bu yanıtı yalnız `PersonBalance:MinClientVersion` ileride yükseltilirse alır. Genel `BalanceUncertain` yolu yeterli; metin değişmez.

**d) Birleştirme notu ("iade o hesaba döner") — üç durum.** Not iki yerde kuruluyor: yerel planın sorusu (`PaymentRequestService.cs:760-768`, `PersonJob.cs:145-146`) ve sunucu izlerinin sorusu (`:1127-1131`, hep `RefundsToOtherRow: true`). `PaymentPrompt`'a `bool? PersonPool` eklenir:
- Sunucu izlerinin sorusunda kaynak, aynı akışta alınan `GET scope` yanıtlarındaki `PersonPool` (§4.3); kesin bilgi.
- Yerel planın sorusu hiçbir sunucu çağrısından önce kuruluyor (`PaymentRequestService.cs:738`, `SettleScopeAsync`'ten önce). Burada değer her zaman `null`'dır (iki kipte de doğru nötr metin). Önceki yanıtlardan bellekte tutulan bir değer kullanılmaz: `enable` ya da `disable` oturum ortasında kipi değiştirebilir ve bayat değer yanlış not gösterirdi.

`PaymentResultPresenter.WithOtherRowNote` (`:185-189`):

| `PersonPool` | Not |
|---|---|
| `false` (satır kipi) | Bugünkü metin: "…iade o hesaba döner, bu istekte kullanılmaz." Doğru. |
| `true` (havuz kipi) | Not **yok**. İade kişinin bakiyesine döner ve birleşik istekte kullanılır. |
| `null` (bilinmiyor) | "Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner." İki kipte de doğru. |

Belge yorumları `:125-127` ve `PaymentRequestService.cs:71-73` buna göre güncellenir. `RefundsToOtherRow` alanı kalır: satır kipi geçiş boyunca sürüyor. Sıra kararı: §7.5.

**e) Ayır onayı (K1)**
- `CustomerSearchViewModel.UnmergeGroup` (`:258-265`) bugün onaysız çalışıyor; async olur. Kartın GUID'li bir üyesiyle `GetCustomerBalanceAsync` (`LicenseApiClient.cs:463-471`) çağrılır; `Members[]` (§4.6) yerel satırlara eşlenir, platform ve kullanıcı adı yerelden gelir.
- Herhangi bir üyede bakiye ≠ 0 ise onay sorulur:
  > **Grubu ayır** — Ayırınca her hesap kendi bakiyesiyle kalır: Instagram @…: 70,00 TL · TikTok @…: 0,00 TL · (silinmiş hesap): 10,00 TL — ayırınca kullanılamaz. Başka hesaptan bu kişinin alışverişine kullanılmış tutarlar, o satış geri alınmadıkça geri dönmez. [Ayır] [Vazgeç]
- Bütün tutarlar 0'sa bugünkü gibi sorusuz ayrılır. GUID'li üye yoksa da öyle (sunucuda bakiye olamaz).
- Çağrı başarısızsa genel uyarı sorulur: "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır." [Ayır] [Vazgeç]
- Gösterilen tutar okuma anınındır. Pencere açıkken başka PC'nin işlemi değiştirebilir; bilgi amaçlıdır, `UnmergeGroup`'un kendisi değişmez.
- Satır kipinde de doğrudur (ayırma bakiyeye dokunmaz); kip ayrımı gerekmez.

**f) Müşteri kartı**
- `CustomerDetailViewModel.TryGetProjectionId` (`:195-200`): açılan satırın Id'si GUID değilse kişinin sahip satırı kullanılır (`GetPaymentPerson(id).OwnerId`, sahip kuralı `PaymentPerson.cs:122-124`).
- Bakiye okuması (`:202-228`) ve "Bakiye Ekle" hedefi (`:230-250` → `AddBalanceDrawer.xaml.cs:106-117`) bu Id'ye gider. Açılan satır GUID'liyse bugünkü gibi o satır.
- "Server eşlemesi yok" metni (`:207`) yalnız kişinin hiç GUID'li üyesi yoksa çıkar.
- Sonuç: arama, sohbet ve kara listeden açılan kartlar aynı havuzu gösterir. Panel GET havuz kipinde bütün üyeler için aynı toplamı döner.

**g) `PersonJob` kalan iş kuralı** (`PersonJob.cs:75-80`) aynen kalır, yalnız gerekçe yorumu güncellenir. Yeni gerekçe: A11 çapa satırı düzeyinde karşılaştırıyor; para taşıyan işi geri alıp sahip satırda yeniden uygulamak, havuz kipinde gereksiz para hareketi, satır kipinde indirim kaybı demek.

**h) Değişmeyenler:**
- `scope`, `transactions/{id}` ve `reverse` çağrıları (`PaymentRequestService.cs:1105`, `:1198`, `:1409`);
- WhatsApp alanları: `{bakiye}` artık havuzdan düşülen tutar, anlamı aynı;
- arama ve rapor başlatıcıları.

### 6.2 v0.9.9 (sahadaki sürüm)

Havuz kipi açık bir lisansta v0.9.9 hiç yeni yazım yapamaz: 409 `client-update-required` alır, mesajı "Bakiye doğrulanamadı" uyarısıyla engeller (master `PaymentRequestService.cs:680` önizleme `catch`'i). Bu tepki koddan çıkarıldı; pilottan önce elle doğrulanır (§8.5). Sebep karışık filoda çift düşüm:
- arama kartı işi `Primary` satırında grup toplamıyla açıyor (master `CustomerSearchViewModel.cs:331-339`);
- yayın raporu B satırında yalnız B'nin toplamıyla açıyor, aynı `session:` kapsamında (master `StreamReportViewModel.cs:234-247`).
- Bugün iadeler tek satırda toplandığı için ikinci iş çoğunlukla 0 görür. Havuzda ikisi de aynı havuzdan çeker.
- Örnek: havuz 500, kart işi 300, rapor işi 200 → B'nin malı için 200 iki kez düşülür.

Satır kipindeki lisanslarda v0.9.9 bugünkü gibi çalışır.

### 6.3 Panel (OrderDeck-Mobile `apps/panel`) — sürüm gerekmez

- Sözleşme aynı (`customerBalance.ts:27-106`). Havuz, birleşik ve toplanmış liste sunucudan gelir. `Members` ek alanı yok sayılır.
- İptal düğmesi (`CustomerBalanceCard.tsx:154-166`) başka üyenin iade ve elle ayar satırında da çalışır (§4.9). 409 `insufficient-balance` yalnız havuz yetmezse döner.
- `useManualAdjustment` (`customerBalance.ts:79-92`) bugün hiçbir ekranda kullanılmıyor; K5 sunucuda tanımlı, arayüz eklemek kapsam dışı.

### 6.4 Shopper (OrderDeck-Shopper `apps/shopper`) — sürüm gerekmez

`useShopperBalance` (`queries.ts:281-293`), `BalanceChip` (`ProfilScreen.tsx:379-395`) ve `BalanceHistoryModal` aynı uçtan okur. Havuzu, toplanmış geçmişi ve maskelenmiş `Reason`'ı sunucu döner (K4). Etiketler (`queries.ts:295-310`) yeterli: yeni tür yok.

## 7. Sürüm sırası ve geçiş

### 7.1 Sıra

**Satış senkronundan sonra** (sahip sırası: önce yayın/etiket senkronu, sonra ortak bakiye, sonra banka havalesi). Bu spec'in S1'i, satış senkronunun S3'ü (`2026-10-13-yayin-etiket-senkron-design.md` §13) master'a girdikten sonra başlar. İkisi aynı denetleyiciyi (`LicensesCustomerBalanceApplyController`) değiştiriyor; S3 apply'a kapsam kuralını ekler, bu spec'in S1'i scope/transactions/reverse'i, S2'si apply'ı yeniden yazar (§4.2). Ayrıntı §15.

| Adım | Ne | Koşul ve doğrulama |
|---|---|---|
| 0 | Bu spec onayı; PR #505 merge; prod'da iki tablonun salt okunur satır sayımı (`SELECT COUNT(*)` `WpfCustomerProjections` ve `CustomerBalanceTransactions`, Burak koşar) | Prod'a yazım yok. Göç süresi kestirimi; sınıra yakınsa indeksler ayrı göçe (§3.1) |
| 1 | **S1** merge ve otomatik deploy | Yayın penceresi dışında (Paz/Pzt/Çar/Per 20:00–01:00 TR değil). `/ready` ve göç (§8.5). Para davranışı değişmez; silinmiş üyelerin numarası canlı üyeleri izlemeye başlar (§4.11) |
| 2 | `person-balance verify` → ön kontrol raporu sahibe (§7.3); `verify --capture-baseline --apply` taban çizgisini yazar | Rapor salt okunur; taban yalnız `PersonBalanceBaselines`'a yazar. Burak koşar |
| 3 | Gözlem ≥ 1 gün; günlük `verify` işi koşar | D4, D5, D6 ve yetim bacak 0; tabana göre sapma yok (yeni ya da daha da eksiye inen eksi satır, yeni ya da değişen D1 farkı, yeni kopya satır) |
| 4 | Sahip ön kontrolün eksi satırlarını, D1 bozulmalarını, grubu dağılmış silinmiş satırlarını ve kopya satır kalemini inceler (§7.3) | Her satır için düzeltme ya da "olduğu gibi kalsın" kararı, sahibin onayıyla; sonra `verify --mark-reviewed --license <id> --apply`. **S2'den önce** (M2) |
| 5 | **S2** merge ve deploy | Bütün lisanslar satır kipinde; satır kipinin davranışı değişmez. Otomatik geri alma S1'e döner (güvenli, §7.4) |
| 6 | **W-havuz** (#506'nın üstüne) merge; **Wh** Velopack sürümü; PC'ler sırayla güncellenir; `PersonBalance:MinClientVersion` = Wh | Satır kipinde Wh = #506 davranışı + Ayır onayı + kart düzeltmesi + sahip kuralı (§6.1-a) |
| 7 | Pilottan önce karışık filo denemesi (§8.5, test lisansında); sonra pilot lisansta `person-balance enable --license <id>` önce kuru, sonra `--apply` | Kapı koşulları (§7.2). Prod'a yazım, Burak'ın onayıyla, yayın penceresi dışında |
| 8 | Saha kontrolleri (§8.5) | Gerçek etkiden: DB satırı, yanıt, ekran |
| 9 | Öbür lisanslar tek tek | Her biri için 4, 7 ve 8 |
| 10 | Banka spec düzeltmeleri (§9), docs PR | #505'ten sonra; banka PR5'ten önce |

S2 ile Wh'nin sırası serbesttir: Wh, S1'e ve kipi kapalı S2'ye karşı da doğru çalışır. Kapı (adım 7) ikisini de ister.

**Wh ⊇ Ws.** Wh, satış senkronunun WPF değişikliklerini (Ws: W1–W3) de taşır; yani Ws'den önce yayımlanmaz. Havuz kipinin kapısı bütün PC'leri Wh+ istediği için hepsi satış senkronunu da taşır. Bu, `enable`'ın ön koşuluyla mekanik olarak sağlanır (§7.2-4).

### 7.2 Kapı — lisans başına yönetici anahtarı + istemci alt sınırı (mühendislik kararı M1)

- **Anahtar:** `License.PersonBalancePoolSince`. Yalnız CLI yazar: `person-balance enable|disable --license <id> [--apply]`. Kalıp `merge-customer-identities` ve `group-customers` ile aynı (`Program.cs:44-55`), varsayılan kuru koşu.
- **Alt sınırın kaynağı:** `PersonBalance:MinClientVersion` yapılandırma anahtarı. Ortam değişkeni `PersonBalance__MinClientVersion`, `deploy/docker-compose.yml`'nin ortam bloğuna öbür anahtarlar gibi `${…:-}` ile eklenir (`:163-196` kalıbı); değer compose'un yanındaki `.env`'de durur, sır değil. Wh yayımlanınca Burak sürüm numarasını yazar. Wh'nin numarası S2 yazılırken bilinmediği için koda gömülmez; alt sınır ileride yükseltilebilir.
  - Karşılaştırma metin sırasıyla değil `System.Version` ile yapılır ("0.9.10" > "0.9.9"). Sürüm `OrderDeck-WPF/x.y.z`'den satış senkronunun ortak yardımcısıyla ayrıştırılır (`ClientVersion.FromUserAgent` ve `IsAtLeast`, §15). Yardımcının ek kuralı geçerlidir: `+` sonrası atılır; `-` sonrası ön sürüm eki aynı numaranın yayımlanmış sürümünden önce gelir ("0.9.11-rc1" "0.9.11" alt sınırını geçmez), iki farklı ek sıralanmaz (kapalı hata). Ayrıştırılamayan ya da olmayan sürüm eski sayılır. `Activation.AppVersion` (`Activation.cs:19-20`) metin olarak kalır, karşılaştırma aynı yardımcıyla yapılır.
  - Anahtar boşsa ya da ayrıştırılamıyorsa `enable --apply` reddeder. Havuz kipindeki bir lisansta anahtar boş bulunursa yeni yazımlar 409 `client-update-required` alır ve günlüğe hata yazılır (fail-closed). Açılışta anahtar boşken havuz kipinde lisans varsa uyarı günlüğe yazılır.
- **`enable` ön koşulları** (kuru koşu durumlarını sayı olarak basar; biri tutmazsa `--apply` reddedilir):
  1. Son `PersonBalance:ActivePcWindowDays` (varsayılan 14) gün içinde görülmüş ve devre dışı bırakılmamış **her** etkinleştirmenin `AppVersion`'ı ≥ `PersonBalance:MinClientVersion` (`Activation.cs:13-20`; `AppVersion` yoksa eski sayılır). Banka spec'i §5-5 ile aynı ölçüt.
  2. Lisansta `verify`'ın D4, D5, D6, yetim bacak ve kırpma farkı (§7.3-5) sayıları 0.
  3. Lisansın taban satırlarının hepsi sahipçe incelenmiş (`ReviewedAt` dolu, §7.3) ve tabana göre yeni sapma yok (yeni eksi satır, daha da eksiye inen satır, yeni ya da değişen D1 farkı, yeni kopya satır; §8.1).
  4. **Wh ⊇ Ws** (§7.1, §15): `SalesSync:MinClientVersion` dolu ve `PersonBalance:MinClientVersion` ≥ `SalesSync:MinClientVersion` (aynı `ClientVersion.IsAtLeast` karşılaştırması). Böylece 1. koşulu geçen her PC satış senkronunu da taşır. Kuru koşu iki değeri yan yana basar.
- **Çalışma anı koruması:** havuz kipinde alt sınırdan eski istemcinin yeni yazımı 409 `client-update-required` alır (§4.0). Kapı açıldıktan sonra haftalarca kapalı kalmış eski bir PC dönerse para yolunda fail-closed olur ve günlüğe lisans ile sürüm yazılır.
- **`disable`** (kill switch) lisansı satır kipine indirir. Çok bacaklı geçmiş doğru okunmaya ve geri alınmaya devam eder (S1 okuyucuları). Panelde çekim grubunun iptali satır kipinde de grubun herhangi bir satırından açılabilir (§4.9).

**Neden dinamik kapı değil** (banka §5-5 gibi her istekte "bütün PC'ler Wh+ mı" diye bakmak):
- Banka kapısının güvenli tarafı "sor"dur; kapı kapanınca yalnız soru sorulur.
- Burada kapının kapanması kipi değiştirir. Eski bir PC'nin tek bir heartbeat'i havuzu yayın ortasında satır kipine düşürür: shopper'ın gördüğü bakiye ve kullanılabilir tutar zıplar; iade edilmiş para bir anda yeniden kullanılamaz olur. Güvenli bir geri dönüş tarafı yok.
- Açılış zaten bir insan adımı istiyor (ön kontrol, eksi satır incelemesi).
- Eski PC riskini anahtarın kendisi değil, çalışma anı koruması kapatıyor.

### 7.3 Ön kontrol raporu ve eksi satırlar (M2)

`person-balance verify [--license <id>] [--details] [--capture-baseline | --mark-reviewed] [--apply]`. Varsayılan salt okunurdur ve yalnız sayı basar. `--capture-baseline` ve `--mark-reviewed` yalnız `PersonBalanceBaselines`'a (§3.1) ve yalnız `--apply` ile yazar. `--details` yalnız eksi ve kural bozan satırların `WpfCustomerId`'lerini (GUID; ad, telefon yok) konsola basar; dosyaya yazılmaz. Raporun içeriği:
1. **Davranışı değişecekler:** en az iki üyesinde sıfır olmayan bakiye olan kişi sayısı ve bunların havuz toplamı. Açılış günü kullanılabilir bakiyesi artacak müşteriler.
2. Eksi satır sayısı ve toplamı (tabanda / taban dışı). Eksi bir satır kişinin havuzunu küçültür (§4.1 örneği).
3. D1 bozulan satırlar (`Balance ≠ Σ Amount`; tabanda / taban dışı). Bugün bunu çalışırken denetleyen bir şey yok.
4. Silinmiş üyede kalan bakiye (sayı, toplam). Bunlardan **grubunda canlı asıl üye kalmamış** olanlar ayrıca (sayı, toplam): S1 öncesi bir numara değişiminde geride kalmış olabilirler (§4.12).
5. **Kırpma farkı:** `DATALENGTH(GroupId) <> DATALENGTH(LTRIM(RTRIM(GroupId)))` sayısı ve sekme ya da satır sonu içeren numara sayısı (`GroupId LIKE '%[' + CHAR(9) + CHAR(10) + CHAR(13) + ']%'`). Uzunlukla ölçülür, çünkü `<>` sondaki boşluğu yok sayar. Sunucu işlerinin `KeyOf`'u (`.Trim()`) ile WPF'in ham karşılaştırmasının ayrışma riski; 0 değilse `enable` reddeder.
6. Kişi havuzu < 0 olan kişi sayısı (taban satırlarıyla açıklananlar ayrı).
7. D4, D5, D6 ve yetim bacak sayıları (S1'den önce hepsi 0). D2'nin izi 2. madde (taban dışı eksi satırlar), D3'ünkü 6. madde.
8. **Kopya satır:** `MergedIntoId`'si dolu projeksiyona bağlı hareket sayısı ve sıfır olmayan bakiye sayısı (tabanda / taban dışı). Bugünkü birleştirici kopyanın her şeyini asıl kayda taşıdığı için yeni kopya satır olmamalı (§4.12); eski birleştirmelerden kalan varsa tabana girer.
9. Lisans başına aktif PC sürüm dağılımı (sürüm → adet).
10. İki tablonun satır sayısı (göç süresi; S1'den önce aynı sayım elle yapılır, §7.1 adım 0).

**Taban çizgisi.** `--capture-baseline --apply` (§7.1 adım 2) her lisansın o anki eksi satırlarını, D1 bozulmalarını ve 8. maddenin kopya satırlarını `Balance` ve `LedgerSum` (satıra bağlı hareketlerin toplamı) ile yazar. Bunlar S1 öncesinden kalmadır; yeni kod onları üretmez. Sahip bunları tek tek gözden geçirir (S2'den önce, §7.1 adım 4). Her biri için ya bir düzeltme seçilir ya da sahip "olduğu gibi kalsın" der; ardından `--mark-reviewed --license <id> --apply` `ReviewedAt`'i doldurur. 4. maddenin "grubu dağılmış silinmiş satır" kalemi de aynı incelemeden geçer. Bugünkü artı yazım yolları WPF'in ve panelin "Bakiye Ekle"sidir (iade türüyle yazar); `manual-adjustment` ucunun ekranı yok. D1 bozulmasının düzeltmesi ayrı bir karardır. Bu tasarım eksi satırları kendiliğinden kapatmaz.

### 7.4 Geri dönüş kuralları ve deploy notu

- **S2 deploy'u düşerse** (`/ready` 60 sn) imaj S1'e döner, göç geri alınmaz. S1 bayrağı okumaz; bütün lisanslar satır kipine iner. Genişlet/daralt kuralı tutar: veriyi yeni biçimde yazan iş (S2) okuyucudan (S1) sonra çıkıyor. S2'nin ilk deploy'unda henüz hiçbir lisans havuz kipinde olmadığı için otomatik geri alma çok bacaklı veriyle karşılaşmaz.
- **S2 havuz verisi oluştuktan sonra elle S1'e dönülürse** (revert ile deploy): S1 bacakları okur ve WPF reverse'inde grup olarak geri alır. Panel reverse'inde `DrawGroupId` dolu her hedefi grup olarak geri alır ya da `not-reversible` ile reddeder (§4.9); K5 iptal grubu tek satır yoluyla yarım geri alınamaz. Para kaybı yok. S1 testleri S2 biçimindeki satırları elle ekleyip bunu sabitler (§8.3).
- **Havuz kipinde sorun:** `person-balance disable`. Veri işlemi gerekmez.
- **WPF'te alt sınırdan eskiye dönen PC:** havuz kipindeki lisansta 409 `client-update-required` alır (fail-closed).
- **YASAK — S1 öncesi imaja elle sabitleme.** Herhangi bir lisansta çok bacaklı bir çekim oluştuysa S1 öncesi imaja dönülmez. Tespit: `SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE DrawGroupId IS NOT NULL AND DrawGroupId <> Id` > 0 (bir bacak ya da çok bacaklı geri almanın çapa dışı satırı var). Tek bacaklı çekimleri eski kod doğru geri alır; havuz kipi açılmadan `DrawGroupId` hiç dolmaz (§3.2), bu yüzden sorgu havuz açılmadan doğru dönmez. Eski kod yalnız çapayı geri alır (`LicensesCustomerBalanceApplyController.cs:456-491`); çapa 0 olabilir ve öbür bacaklardan düşülen para geri dönmez. Ayrıca `scope` ve `transactions/{id}` yalnız çapa tutarını döner.
- `deploy/README.md`'ye, var olan "PR-1 geri dönüş tabanı" notunun biçiminde bir uyarı eklenir:
  > **UYARI — Ortak bakiye S1 tabanı.** `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` dosyasını İÇERMEYEN bir imaja (etiketin kaynağında `git cat-file -e <sha>:OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` başarısızsa o imaj S1 öncesidir), `DrawGroupId`'si dolu ve kendi `Id`'sinden farklı bir defter satırı (bir bacak) oluştuktan sonra bu elle yoldan ASLA geçme. Kontrol: `SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE DrawGroupId IS NOT NULL AND DrawGroupId <> Id`. Eski reverse yalnız çapayı (tutarı 0 olabilir) geri alır; müşterinin öbür hesaplarından düşülen para geri dönmez. Otomatik geri alma bir önceki imaja döner ve güvenlidir.

### 7.5 #506'daki notun güvenli sırası (M3)

- #506 master'a olduğu gibi girebilir. Notu satır kipinde **doğrudur**.
- #506'yı tek başına taşıyan bir Velopack sürümü gerekirse (ör. banka W2 takvimi) çıkabilir. Bu sürüm Wh değildir; o PC'ler havuz kipi kapısını geçemez.
- W-havuz notu kipe bağlar (§6.1-d). Havuz kipi ancak bütün aktif PC'ler Wh+ iken açılabilir (§7.2). Notun yanlış görüneceği tek durum: kapıdan sonra dönen, yalnız #506 taşıyan bir PC (son 14 günde görülmediği için kapıya sayılmamış).
  - O PC yerel planın sorusunu ve kipsiz notu ("…bu istekte kullanılmaz") sunucuya gitmeden gösterir (`PaymentRequestService.cs:738`, `SettleScopeAsync`'ten önce).
  - "Evet" denirse düşümleri geri alır. Sunucu geri almayı S1'den beri her istemci için grup olarak yapar; para kendi satırlarına döner. Ardından önizleme 409 `client-update-required` alır ve mesaj gitmez.
  - Para kaybı yok; yanlış not o PC güncellenene kadar görülebilir. Günlükte `client-update-required` olarak görünür (§8.5).
  - Reverse ucuna sürüm kapısı **konmaz**: geri alma sunucuda her istemci için doğru; kapı eski PC'nin yarım kalmış revizyonunu belirsiz bırakırdı.
- Notu kaldırıp S2'yi beklemek satır kipinde doğru bir uyarıyı silerdi. Notu olduğu gibi bırakıp havuzu açmak ise operatöre yanlış bilgi verirdi. İkisinden de kaçınılır.

## 8. Doğrulama

### 8.1 `verify` CLI ve günlük iş

- §7.3'teki komut (S1).
- Aynı denetimler Hangfire'da günlük koşar: `person-balance-verify`, `Program.cs:889-1031`'deki tekrarlayan işler bloğuna eklenir.
- Çıktı sayılardır; PII yok. `Admin:AlertEmail`'e (kalıp `BackupRestoreDrillJob.cs:106`, aynı durum için günde bir kez) **yalnız yeni sapma** yazılır:
  - D4, D5, D6 ya da yetim bacak sayısı 0 değil (eski veride bunlar yok, taban gerekmez);
  - taban dışı bir eksi satır ya da tabandaki bir satırın `Balance`'ının tabandakinden düşük olması (D2'nin izi);
  - taban dışı bir D1 bozulması ya da tabandaki bir satırın `Balance − LedgerSum` farkının değişmesi;
  - taban dışı bir kopya satır (§7.3-8);
  - **havuz kipindeki** bir lisansta kişi havuzu `< 0` olan ve bu durumu taban satırlarıyla açıklanamayan kişi (D3'ün izi). Açıklanabilir: kişinin bütün eksi satırları tabanda ve hiçbiri tabandaki değerinin altında değil.
- Lisansın tabanı henüz yoksa (S1 deploy'u ile §7.1 adım 2 arası) eksi, D1 ve kopya satır kalemleri yalnız sayı olarak günlüğe yazılır; uyarı çıkmaz. Böylece S1 gününden başlayıp kabul edilmiş satırlar için de susmayan, gerçek bir bozulmayı gizleyen günlük uyarı oluşmaz.
- D3 bir çalışma anı kuralıdır; kişi kilidi ve Testcontainers 1 ve 9 (§8.2) onu kanıtlar. `verify` yalnız kişi düzeyindeki sonucu izler.
- Kazanan tasarımın "değişmez kuralı çalışırken denetleyen iş yok" riskini kapatır.

### 8.2 Testcontainers (gerçek SQL Server, `SqlServerContainerFixture`)

InMemory tekil indeks ve jeton yarışını uygulamaz. Bu yüzden şunlar gerçek SQL Server'da kanıtlanır:
1. Aynı kişinin iki farklı üyesinden, farklı anahtarlarla paralel apply; eksi bakiyeli üye varken (A = 100, B = −60, C = 50). Havuz hiçbir an < 0 olmamalı, D1 tutmalı. Haritadaki "paralel apply için gerçek SQL testi yok" boşluğu da kapanır. (S2)
2. Aynı anahtarla çok bacaklı apply yarışı: tek bacak kümesi yazılır, kaybeden kazananın sonucunu oynatır. (S2)
3. Çok bacaklı grup geri alması yarışı: WPF ∥ WPF ve WPF ∥ panel. Ya hepsi ya hiçbiri (D5); biri 409 `already-reversed` alır. (S1)
4. Çok bacaklı grup geri alması ∥ bacak satırlarından birine eşzamanlı artı yazım (panel iadesi): her satırda D1 tutar, hiçbir satıra delta iki kez eklenmez (§4.5). (S1)
5. Grup geri alması ∥ kimlik birleşmesi, bacak satırı 0'da kalmış kopyaya dönerken: para asıl kayda yazılır, kopyada kalmaz (§4.12). (S1)
6. Ayırma senkronu (`GroupId = NULL` partisi) ∥ apply; ayırma, bakiye yüklemesinin önünde, üyeliğin iki okuması arasında ve ikinci okumadan sonra kaydedilir. Ayrılan para tutan satırdan ayırmadan sonra çekim yok; senkron çakışırsa taze okumayla yeniden uygular (§4.2-2, §4.11). (S2)
7. Apply ∥ `CustomerIdentityMergeJob` (grup geri alınır ve sayılır, `:297-304`) ve apply ∥ `--at-twins` birliği. (S2)
8. Apply ∥ `group-customers` `ApplyAsync`. (S2)
9. K5 panel eksi işlemi ∥ apply: aynı havuzdan iki çekim, havuz ≥ 0. (S2)
10. Aynı anahtarla iki istek ve araya giren üçüncü bir yazım: ikincinin ilk denemesi jeton çakışmasıyla geri alınır, ilki commit eder; ikinci deneme `no-balance` değil kazananın sonucunu oynatır (§4.2-6). (S2)
11. Göç: `GroupId` collation değişimi, sütunda otomatik istatistik varken; filtreli indeksler; yeni tablo (`CustomerProjectionUniqueIdentityMigrationTests` deseni). Bu test **Express** sürümünde koşar (`MSSQL_PID=Express`; prod `deploy/docker-compose.yml:25`). Ortak fikstür (`SqlServerContainerFixture.cs:29-31`) sürüm vermediği için Developer koşuyor; Express'te olmayan bir seçeneği (ör. `ONLINE`) yakalayamazdı. (S1)
12. Kanca: senkronun `GroupId` değişikliği bakiye jetonunu ilerletir, apply çakışır. (S1)
13. Silinmiş üyenin numara izlemesi: iki grup senkronla birleşince (üyeler ayrı partilerde de gelse) silinmiş üye yeni numaraya geçer; Ayır'da eski numarada kalır; `group-customers` ve `--at-twins` sonrası silinmiş üye hedefte (§4.11). (S1)
14. Var olan dört eşzamanlılık testi (`CustomerBalanceConcurrencyTests.cs:43`, `:85`, `:112`, `:194`) satır kipinde yeşil kalır; havuz kipinde de koşturulur. (S2)
15. Satış senkronu mod On, havuz kipi: aynı çapa ve kapsam için farklı anahtarlı iki paralel apply. Tek çapa grubu yazılır, öbürü 409 `scope-has-deduction` alır ve tutarı `−Σ(grup)`'tur (çapanın payı 0 olan plan dahil). Satış senkronu S3'ünün satır kipi testi `DrawAsync`'e taşındıktan sonra da yeşil kalır (§4.2, §15). (S2)

### 8.3 InMemory ve birim

- `PlanDraw` tablo testleri: çapa önce, silinmiş önce, büyük bakiye önce, Id ordinal eşitliği, eksi satır atlanır, tutar kırpma.
- Preview havuzu; kural 1'in iki kolu (hiç üye yoksa kesin `no-balance`, en az bir üye varsa `customer-not-synced`); kural 3a ve 3b; `S∖W`'de yalnız sıfır ya da eksi üyeler varken kesişim ve `Σ`'ya eklenen eksiler (A = 100, B = −60, `W = {A}` → kullanılabilir 40); silinmiş üyelerin `S`'den alınması, `W`'deki silinmiş üyenin yok sayılması; `client-update-required` (`System.Version` sırası: "0.9.10" > "0.9.9", ekli ve ayrıştırılamayan sürüm); boş `MinClientVersion`; `member-ids-required`.
- Kişi sorgusunun ordinal süzgeci: `"g1"` ile `"g1 "` ve `"G1"` ayrı kişi.
- Örnek 1–4'ün birebir satırları; `scope` yalnız çapada; `transactions/{id}` toplamı; Lookup `−Σ`; bacak anahtar → 404; çapanın bakiye satırı yoksa 0 ile açılır.
- Geri alma biçimi: eski tek bacaklı düşümün geri alması `DrawGroupId = NULL` (havuz kipi açılmamış lisansta hiç `DrawGroupId` dolu satır oluşmaz); çok bacaklı grubun geri alması grup satırlarıyla.
- Panel: toplanmış liste, `take` grup sayar, `Members`, başka üyenin iadesini iptal (K5); çekim grubuna ait `reversal` → 409 `not-reversible` (iki kip); eski `reversal` satırı satır kipinde bugünkü gibi, havuz kipinde 409.
- **S1 tabanı (S2 biçimindeki satırlar elle eklenerek, kip kapalı):** panel reverse K5 iptal grubunu reddeder, elle eksi grubunu ve düşüm grubunu grup olarak geri alır; rota satırında yalnız bacağı olan grubun iptali (çapa başka satırda) bulunur (`disable` sonrası yol, §4.9).
- Birleştirici: sıfır bakiyeli kopyanın bakiye satırı da damgalanır.
- Shopper: havuz, `Reason` maskesi, silinmiş üyenin dahil olması.
- **Satır kipi regresyonu:** bayrak kapalıyken var olan bakiye testlerinin hepsi değişmeden yeşil. A11 testleri `LicensesCustomerBalanceApplyControllerTests.cs:316-406` ve kopya Id'si `:930-1003`.

### 8.4 WPF

- `memberIds` preview ve apply'a gider, replay dahil.
- `customer-not-synced` → `BalanceNotSynced`; `MarkNoBalance` yazılmaz, iş taze kalır, `PullOnceAsync` turu tetiklenir; 5 dakikadan uzun sürerse ayrı metin.
- Sahip kuralı: gönderilemeyen (`IsServerAcceptable` tutmayan) GUID'li satır sahip seçilmez; var olan işin sahibi değişmez.
- Not, üç durumuyla; yerel planın sorusunda her zaman `null` metni. `PaymentResultPresenterTests`, `PersonScopePlanTests`, `PaymentJobStoreContractTests` ve `PaymentRequestServiceTests.Person` `RefundsToOtherRow`'u sabitliyor; kip durumuyla genişletilir.
- Ayır onayının metni; bütün tutarlar 0'ken soru yok; çağrı hatasında genel uyarı.
- GUID'siz satırdan açılan kartın sahip satıra düşmesi.

### 8.5 Saha kontrolleri (gerçek etkiden)

- **S1 sonrası:**
  - `__EFMigrationsHistory`'de göç var;
  - iki indeks, yeni tablo ve `GroupId` collation'ı `sys.indexes` / `sys.tables` / `sys.columns`'ta görünüyor;
  - `verify` koşuyor ve sayı basıyor; `--capture-baseline --apply` sonrası günlük iş uyarı üretmiyor;
  - bir müşteride preview yanıtı öncekiyle aynı, `PersonPool = false`.
- **S2 sonrası (kip kapalı):** aynı müşteride preview, apply ve reverse bugünkü gibi; DB'de yeni satırların `DrawGroupId`'si `NULL`.
- **Karışık filo denemesi** (pilottan önce, bir test lisansında havuz kipi açıkken; hakem 2'nin aşısı):
  - v0.9.9 kurulu bir PC'den bakiyeli bir müşteriye ödeme iste. Beklenen: mesaj engellenir, iş `NoBalance` olmaz, apply belirsiz kalır, sunucu günlüğünde ve metrikte `client-update-required` görünür.
  - Aynısı yalnız #506 taşıyan bir sürümle: yerel plan sorusu çıkarsa "Evet" sonrası geri alma, düşülen parayı her bacağın kendi satırına döndürür (DB'de bakılır); sonra önizleme engellenir ve mesaj gitmez (§7.5).
- **Pilot lisansta `enable` sonrası** (iki üyesinde bakiyesi olan bilinen bir kişi):
  - WPF'te ödeme isteği önizlemesi havuzu gösteriyor;
  - DB'de aynı `DrawGroupId`'li bacaklar var;
  - revizyon her bacağı kendi satırına döndürüyor;
  - panel kartı ve Shopper toplamı gösteriyor, başka üyenin `Reason`'ı Shopper'da görünmüyor;
  - Ayır onayı doğru dağılımı gösteriyor (Vazgeç ile çıkılır);
  - yeni bir satırla Birleştir → hemen Ödeme iste → "Henüz eşitlenmedi", ≤ 60 sn sonra havuz.
- **Günlükte** `client-update-required` ve `customer-not-synced` sayıları izlenir.

### 8.6 Metrikler

`OrderDeckMetrics`'e (`Services/Observability/OrderDeckMetrics.cs`) dört sayaç eklenir: `customer-not-synced`, `client-update-required`, `balance-busy` ve çok bacaklı çekim (bacak sayısı etiketiyle).

## 9. Banka spec'ine etkisi (K6)

Bu PR banka spec'ini **değiştirmez**. Aşağıdaki düzeltmeler #505 birleştikten sonra tek bir docs PR'ında uygulanır. Satır numaraları master'dandır; #505'ten sonra birkaç satır kayar, bölüm numarası esastır.

1. **§2 "Kişi" (#505'in eklediği paragraf):** cümle eklenir. "Müşteri bakiyesi kişi havuzudur: kişinin üye satırlarındaki bakiyelerin toplamı (`2026-10-11-ortak-bakiye-design.md` §2, §4). `GroupId` karşılaştırması BIN2/ordinaldir."
2. **§2.8 (`:78`):** "`bank-overpayment` artı ve tek satırlı bir harekettir (`DrawGroupId` `NULL`). Geri alınması ortak bakiye çekimiyle yapılır (§11.2)."
3. **§4.1 (`:116`):** "fazla varsa bakiye satırı ekler" → "fazla varsa onaylanan beklentinin sahip satırına (`PaymentExpectation.WpfCustomerId`, asıl kayda çözülmüş) `bank-overpayment` satırı ekler".
4. **§4.3 (`:127`):** fazlanın hedefi tanımlanır. "Fazla, tahsis sırasındaki son beklentinin sahip satırına yazılır. #505 §2.1'deki belirlenimli sahip kuralı yüzünden bir kişinin beklentileri çoğunlukla aynı satırdadır; `ReassignOwner` ya da geçiş dönemi yüzünden farklıysa son tahsis edilen beklentinin satırı seçilir. Havuz okumada toplandığı için satır seçimi yalnız ayırmadan sonraki sahipliği belirler (ortak bakiye K1)."
5. **§6 `overpayment-refunded` (`:209`):** "`bank-overpayment` satırını `reversal` ile kapatır" → "fazlayı ortak bakiye çekimiyle geri alır (§11.2'deki kural, eksik kalan uyarıyla)".
6. **§11.2 "Fazla" maddesi (`:302`):**
   - "bakiyede ne varsa o kadarı geri alınır" → "kişi havuzunda (`max(0, Σ üye bakiyesi)`, ortak bakiye §4.0'daki `S`) ne varsa o kadarı geri alınır".
   - Yazım: çekim türü iptal (ortak bakiye §3.2). Çapa fazla satırının kendi satırında, `Kind = reversal`, `ReversesTransactionId` = fazla satırı; kalan kişinin öbür pozitif üyelerinden bacak olarak, `DrawGroupId` = çapa. Hiçbir satır eksiye düşmez. Çekilemeyen kalan olay satırına ve uyarıya yazılır.
   - Lisans satır kipindeyse "bakiye" fazla satırının kendi satırıdır (bugünkü anlam).
   - `disallowNegative` ve "satır başına tek geri alma" gerekçesi şöyle güncellenir: N01 yalnız çapada hakemdir.
7. **§15 PR sırası:** PR5'in fazla yazımı ve §11.2 geri alması `PersonBalance.DrawAsync`'i (ortak bakiye S2) kullanır. Bağımlılık: banka PR5 ⊇ ortak bakiye S2 kodu (kip kapalı olabilir).
8. **§16 sahip kararları tablosu:** satır eklenir. "7 | Ortak bakiye (2026-10-11) | Fazla, onaylanan beklentinin sahip satırına yazılır; §11.2'deki 'bakiyede ne varsa' = kişi havuzu (ortak bakiye K6)."
9. **§1 kapsam dışı (#505 metni):** değişmez. Beklenti birleştirmesi hâlâ sunucuda yapılmıyor; bakiye ise artık kişi düzeyinde. Karışmasın diye bir cümle: "Bakiye kişi düzeyindedir (ortak bakiye spec'i); beklenti satırı sahip satır düzeyinde kalır."

## 10. Sahip kararları (2026-10-11, kesin)

| # | Konu | Karar |
|---|---|---|
| K0 | Tek bakiye | Kişi başına tek ortak bakiye: "Müşterilerin ayrı olması yanlış; farklı platform olsa bile aynı müşteri kartında olmalı." (§1, §4) |
| K1 | Ayır | Her kayıt kendi parasını tutar: ona yapılan iadeler − ondan çekilen bacaklar + ona dönen bacaklar. B'den A'nın alışverişine çekilen para, o satış geri alınmadıkça B'ye dönmez. Ayır, onaydan önce hesap başına tutarları gösterir. (§5.2, §6.1-e) |
| K2 | Senkron öncesi | Birleştirmeden hemen sonra, sunucu kaydı ya da grubu görmeden (≤ ~60 sn) ödeme isteği "henüz eşitlenmedi, birazdan tekrar dene" der: mesaj bekletilir, iş taze kalır (WPF bunu `BalanceUncertain` gibi ele alır). Asla sessizce "bakiye yok" denmez. (§4.0, §6.1-b) |
| K3 | KVKK | Silinmiş üyenin bakiyesi kişinin öbür kayıtlarınca kullanılabilir kalır (havuzda çekilebilir kaynak; para müşterinin). Silinmiş satıra dönen bacak geri almaları da aynı yoldan erişilebilir kalır. (§4.0-4, §4.12, §5.4) |
| K4 | Shopper | Shopper uygulaması kişi toplamını gösterir. Öbür kayıtlardan gelen hareketlerin tutarı ve türü görünür, `Reason` metni gizlenir. (§4.10) |
| K5 | Panel eksi işlemler | İade iptali ya da eksi elle ayarda hedef kayıtta yeterli para yoksa kalan öbür üyelerden çekilir (çok bacaklı), bugünkü 409 yerine. (§4.8, §4.9) |
| K6 | Banka | Havale fazlası onaylanan beklentinin sahip satırına yazılır. Banka spec'i §11.2'deki "bakiyede ne varsa" = havuz. (§9) |

**Mühendislik kararları**

| # | Karar | Gerekçe |
|---|---|---|
| M1 | Havuz kipi lisans başına yönetici anahtarıyla (`PersonBalancePoolSince`, CLI) açılır. Açılışta bütün aktif PC'ler `PersonBalance:MinClientVersion` (Wh) ve üstü olmalı; çalışmada eski istemcinin yeni yazımı 409 `client-update-required` alır. Alt sınır yapılandırmadan okunur, `System.Version` ile karşılaştırılır. | Dinamik kapının güvenli geri dönüş tarafı yok (§7.2). Fail-closed. Wh'nin numarası S2 yazılırken bilinmez. |
| M2 | Açılıştan önce salt okunur ön kontrol ve taban çizgisi. Eksi satırlar, D1 bozulmaları ve kopya satırlar sahiple **S2 deploy'undan önce** incelenir (§7.1 adım 4); `enable` taban incelenmeden ya da tabana göre sapma varken reddeder. Günlük uyarı yalnız yeni sapmada çıkar. | Eksi satır havuzu kilitler (§4.1). Kabul edilmiş eski veri her gün uyarı üretip gerçek bir bozulmayı gizlememeli. |
| M3 | #506 olduğu gibi girebilir; not W-havuz'da kipe bağlanır (üç durum); Wh = #506 + W-havuz; kapı Wh ister. | Not satır kipinde doğru, havuz kipinde yanlış; havuz kipi Wh'siz açılamaz (§7.5). |
| M4 | K2, WPF'in üye listesiyle (`memberIds`) uygulanır. `W ⊄ S` → 409; `S∖W`'de parası olan canlı üye → 409; hiçbir üye sunucuda yoksa kesin `no-balance`. `P` = `W ∩ S`'nin canlı üyeleri ∪ `S`'nin silinmiş üyeleri; `S∖P`'nin eksileri havuza sayılır ve kilitlenir. | Sunucu senkron gecikmesini kendi başına göremez. Kesişim, operatörün ayrı gördüğü satırdan çekmeyi engeller; 3b görülmeyen parayı kesin "bakiye yok"a çevirmez; eksilerin sayılması görülmeyen borcun havuzu açmasını engeller. |
| M5 | `GroupId` kolonu BIN2 collation; kişi sorgusunun sonucu bellekte ordinal süzülür. | WPF ordinal karşılaştırıyor; CI kolonda "WPF ile aynı kişi" iddiası SQL'de tutmazdı. BIN2 harf farkını SQL'de ayırır; sondaki boşluğu SQL `=` hiçbir collation'da ayırmaz, onu bellekteki süzgeç ayırır. Emsal `IdentityKey`. |
| M6 | Kişi kilidi: apply ve K5 çekimleri kişinin bütün bakiye satırlarının jetonuna dokunur. `GroupId` değişimi kanca ile jetonu ilerletir. `CustomerBalance` `StampVersions`'a girer. Apply üyeliği bakiye yüklemesinden sonra yeniden okur. | D3 ve "ayırma ile apply" yarışı, var olan F02 jeton kalıbıyla kapanır; açık işlem ve kilit gerekmez. |
| M7 | Çekim sırası: çapa → silinmiş üyeler → canlı üyeler (bakiye büyükten küçüğe, Id ordinal). | Alıcının parası önce kullanılır; ayırmada erişilemez kalabilecek para önce harcanır. Sıra belirlenimli. |
| M8 | Çekim grubuna ait `reversal` satırı iki kipte de (S1'den), eski `reversal` satırı yalnız havuz kipinde iptal edilemez (409 `not-reversible`). Bacak, çapasına çözülür. | Çok bacaklı "iptalin iptali" zincirini kapatır; satır kipinin davranışı değişmez; S2 elle geri alınsa da K5 iptal grubu yarım kalmaz. Panel arayüzü zaten sunmuyor. |
| M9 | Yanıtlardaki `UpdatedAt` = son para hareketi. | Kilit dokunuşu "güncellendi" zamanını oynatmaz (kazanan tasarımın 6. riski). |
| M10 | A11 ve `scope` çapa satırı düzeyinde kalır. | Kişi düzeyindeki A11, ayırmadan sonra anahtarı başka kişiye oynatırdı. #506 üye başına scope'u zaten tekilleştiriyor. |
| M11 | Silinmiş üyenin grup numarası sunucuda canlı üyeleri izler (SaveChanges kancası, S1); havuzun silinmiş üyeleri `S`'den okunur. | K3'ü numara değişimine karşı korur; WPF'te gönderim protokolü değişmez; bütün okuyucular aynı silinmiş üyeleri görür (§4.12). |
| M12 | Grup geri alması çakışmada grubu baştan kurar; tek satırlık "deltayı yeniden uygula" kullanılmaz. Birleştirici sıfır bakiyeli kopyanın bakiye satırını da damgalar. | Çok satırlı `SaveChanges`'te delta çakışmayan satırlara iki kez eklenirdi; sıfır bakiyeli kopyaya giden para görünmez olurdu (§4.5, §4.12). |
| M13 | Tek bacaklı eski düşümün geri alması `DrawGroupId = NULL` yazılır. | Satır kipinde yeni biçim hiç oluşmaz; §7.4'teki tespit sorgusu ve saha kontrolleri tutarlı kalır. |
| M14 | W-havuz'da sahip kuralı gönderilebilir satırı (`IsServerAcceptable`) tercih eder; `customer-not-synced` çekme turunu tetikler, 5 dakikadan uzun sürerse ayrı metin gösterilir. | Gönderilemeyen sahip kalıcı 409 üretirdi; kural 3'ün öbür kolu çekmeyle kapanır (§6.1-a, §6.1-b). |

**Açık kalanlar:**
- **Sahip kararı bekliyor — Ayır'da silinmiş hesabın parası (K3 istisnası).** Bütün canlı hesaplar ayrılınca silinmiş hesap tek kalır; parası hiçbir canlı kayda bağlı değildir ve kullanılamaz (§4.12, §5.2). Bu tasarımın varsayılanı: Ayır serbest, onay penceresi tutarı "silinmiş hesap — ayırınca kullanılamaz" diye gösterir. Seçenek: silinmiş üyesinde parası olan kişide Ayır engellenir. Karar Ayır onayının metnini ve davranışını belirler (W-havuz); S1 ve S2'yi bloklamaz.
  - **Aynı durum: kopyaya dönüşte grupsuz asıl kayıt** (2026-10-21, S1 incelemesi). Kimlik birleşmesi `--at-twins` dışında (olağan kipteki iş, senkronun kopya yolu) `GroupId`'leri birleştirmez (aşağıdaki ikinci madde). Bir numaranın son canlı üyesi grupsuz bir asıl kayda kopya olursa kancanın hedefi boştur (§4.11); silinmiş üye eski numarada tek kalır. Ayır'dan farkı: onay penceresi yoktur ve kanca bunu günlüğe yazmaz; `verify`'ın §7.3-4 kalemi ("grubunda canlı asıl üye kalmamış") görür. Ayır kararının bu duruma da uygulanıp uygulanmayacağı aynı kararla belirlenir.
- Kimlik birleşmesinin olağan kipte ve senkron kopya yolunda da `GroupId`'leri birleştirmesi (§5.3). Ayrı karar.
- Kesinleşmiş `NoBalance` işin, havuza sonradan para gelince yeniden sorulması (§11). Ayrı karar.

## 11. Riskler ve bilerek yapılmayanlar

**Riskler**
1. **Yanlış gruplama artık parayı taşır.** Bugün `GroupId`'nin bakiyeye etkisi yok. Havuzla birlikte yanlış bir birleştirme başkasının iadesini harcatabilir. Yolları: elle birleştirme (keyfi numara, `CustomerRepository.cs:415`), `group-customers`, `--at-twins`, form telefonu. Azaltıcılar:
   - ön kontrol raporunun 1. maddesi açılış günü etkilenenleri listeler;
   - harcanmamış para Ayır ile eksiksiz geri gelir;
   - Ayır onayı dağılımı gösterir.
2. **Shopper gizliliği:** öbür üyelerin tutar ve türleri görünür; serbest metin gizli (K4).
3. **Kalıcı `customer-not-synced`:** damgasız eski `GroupId` iki tarafta farklı kalırsa, sahip satırın öğesi bozuk bir değer yüzünden hiç kurulamıyorsa ya da sunucuda gruplanmış ve parası olan bir satır bu PC'ye hiç inmiyorsa o kişide ödeme isteği bakiye akışında takılır (§5.5). Damgasız numara ölçümde 0; gönderilemeyen sahip W-havuz'un sahip kuralıyla elendi. WPF 5 dakikadan sonra ayrı metin gösterir, günlükte görünür; kurtarma satırı düzeltmek ya da yeniden Birleştir.
4. **Kart ile çekim kümesi farkı:** panel, kart ve Shopper `S`'yi, apply `W ∩ S`'nin canlı üyelerini ve `S`'nin silinmiş üyelerini kullanır. Fark yalnız canlı üyelerde ve senkron penceresinde (≤ 60 sn) olur; `S∖W`'deki üyede para varsa apply 409 döner (kesin "bakiye yok" olmaz). Silinmiş üyeler iki tarafta da `S`'den okunur.
5. **Eski PC dönerse** havuz kipindeki lisansta o PC'den bakiyeli ödeme isteği durur (`client-update-required`); güncelleme gerekir. Bilinçli fail-closed.
6. **Tutarı 0 olan çapalar** ham defterde görünür. Bütün listeler toplanmış döner; toplamayan gelecekteki bir okuyucu (dışa aktarma) "0 TL düşüm" gösterebilir. `AggregateHistory` kullanılmalı.
7. **Kesinleşmiş iş önbelleği:** `NoBalance` olmuş iş tekrar paylaşımda bakiyeyi yeniden sormuyor (`PaymentRequestService.cs:957-966`). Havuza sonradan para gelirse ya da kişiye yeni üye katılırsa o kapsam güncellenmez. Bugünle aynı davranış, ama kişi büyüdükçe daha sık görülür.
8. **Kişi kilidi** aynı kişiye eşzamanlı yazımları sıraya sokar. Üç çakışmadan sonra 503; WPF aynı anahtarla yeniden dener.
9. **Geride kalmış silinmiş satırlar:** numara izlemesi S1'den sonraki değişiklikleri kapsar, geçmişe dönük değildir. Daha önce dağılmış silinmiş satırların parası kişiden erişilemez kalır; `verify` sayar (§7.3-4), sahiple incelenir.
10. **Birleştiricinin dar penceresi:** kopyanın hareketleri ile bakiye satırı ayrı okunuyor (§4.12); arada yazılan bir hareket kopyada kalabilir. Bugün de var; `verify`'ın kopya satır kalemi görür.

**Bilerek yapılmayanlar**
- Kalıcı `PersonId` ve kişi anahtarlı tablo (tasarım 1): sunucu kişisi WPF kartından sessizce büyüyebiliyordu; tek seferlik para taşıyan kesim gerekiyordu.
- Tek satıra toplama (tasarım 3): yanlış birleştirmede para geri bölünemiyordu; sonuç zamanlamaya bağlıydı.
- Veri göçü ve geri doldurma.
- `Balance >= 0` CHECK kısıtı: eski eksi satırlar göçü düşürürdü. Kural kodda ve `verify`'da.
- Dinamik sürüm kapısı (§7.2).
- "İptalin iptali" (M8).
- Kişi düzeyinde A11 ve kişi düzeyinde `scope` (M10).
- Panel ve Shopper istemci sürümü: sözleşme aynı.
- İade uçlarının idempotency'si (§1).

## 12. PR planı

| PR | Repo | İçerik | Efor |
|---|---|---|---|
| **S1** | LiveDeck (sunucu) | Göç (§3.1; taban tablosu dahil, `ONLINE` yok); `PersonBalance` okuyucuları (ordinal süzgeçli kişi sorgusu); `scope`, `transactions/{id}`, Lookup `−Σ`; grup geri alması (WPF ve panel; çakışmada baştan kurma; tek bacaklıda `DrawGroupId = NULL`); panel reverse'inde `DrawGroupId` dolu hedefler (iki kipte üyelik, grup geri alma ya da `not-reversible`); toplanmış listeler (panel, Shopper); panel `Members[]`; `PersonPool = false` alanları; `CustomerBalance` → `StampVersions`; `GroupId` kancası, silinmiş üyenin numara izlemesi ve convention testi; birleştiricinin sıfır bakiyede de damgalaması; `person-balance verify` (taban, inceleme işareti, yeni kalemler) ve günlük iş; `deploy/README` uyarısı; Testcontainers 3, 4, 5, 11, 12, 13. Para davranışı değişmez. | 3,75 gün |
| **S2** | LiveDeck (sunucu) | Kip bayrağı; `ResolveDrawSetAsync` (`memberIds`, kural 1–4: kesin `no-balance`, 3a/3b, silinmiş üyeler `S`'den, `S∖P` eksileri); sürüm denetimi (`PersonBalance:MinClientVersion`, satış senkronunun `ClientVersion` yardımcısı — taşıma yok, §15 — ve compose ortam satırı); `PlanDraw` ve `DrawAsync` (üyeliğin ikinci okuması, her denemede Lookup, çapanın bakiye satırı, §6.6 `scope-has-deduction` kuralının çapa düzeyinde korunması ve tutarın `−Σ(grup)` olması, §4.2); `enable`'ın Wh ⊇ Ws koşulu (§7.2-4); havuzlu preview ve apply; panel ve Shopper havuz okumaları ve `Reason` maskesi; K5 (elle eksi, iade iptali, havuz kipi üyelik koşulu); eski `reversal` → `not-reversible` (havuz kipi); `person-balance enable/disable`; metrikler; Testcontainers 1, 2, 6–10, 14, 15. | 4 gün |
| **W-havuz** | LiveDeck (WPF, #506'nın üstüne) | `memberIds`; `BalanceNotSynced`, `PullOnceAsync` tetikleme ve 5 dakika metni; sahip kuralı (`IsServerAcceptable` Core'a, `PaymentPersonMember`'a Platform/Username); üç durumlu not (yerel planda `null`); Ayır onayı; kart sahip yedeği; `PersonJob` yorumu; testler. Wh Velopack sürümüyle birlikte. | 1,75 gün |
| **D-banka** | LiveDeck (docs) | §9'daki dokuz düzeltme. #505'ten sonra. | 0,25 gün |
| — | OrderDeck-Mobile, OrderDeck-Shopper | Değişiklik yok | 0 |

**Toplam ≈ 9,75 gün.** Kazanan tasarımın tahmini 7 gündü; fark aşılardan (K2 üye listesi, kapı ve CLI, K5, Ayır onayı, kanca, collation göçü, günlük `verify`) ve ikinci incelemeden (silinmiş üyenin numara izlemesi, taban çizgisi, grup geri almanın yeniden kurulması, kural 3b, sahip kuralı) geliyor.

**Akış:** satış senkronu S3 master'da (§15) → satır sayımı → S1 → `verify` raporu ve taban → (≥ 1 gün gözlem) → sahip tabanı inceler → S2 → W-havuz + Wh (S2'den önce de çıkabilir) → PC'ler Wh+, `MinClientVersion` yazılır → karışık filo denemesi → pilot `enable` → saha kontrolü → öbür lisanslar. D-banka, #505 birleşince, banka PR5'ten önce. Her merge yayın penceresi dışında.

## 13. Tasarım incelemesi — bulgu kararları

Kaynak: 2026-10-11 tasarım karşılaştırması (üç aday, iki hakem). Kazanan: "Kişi havuzu" (hakem puanları 8 ve 7). "T1/D1" = kalıcı `PersonId` tasarımı, "T3/D3" = toplama tasarımı.

| # | Kaynak | Önem | Konu | Karar |
|---|---|---|---|---|
| 1 | Hakem 2, "üçünde ortak" | Ölümcül | Senkron öncesi pencere kalıcı `MarkNoBalance` üretiyor | İşlendi §4.0 (`memberIds`, 409 `customer-not-synced`), §6.1-b, K2 |
| 2 | Hakem 2, "üçünde ortak" | Ölümcül | Karışık filoda aynı mal için çift düşüm | İşlendi §7.2 (kapı + `client-update-required`), §6.2, M1 |
| 3 | Hakem 1 aşısı (T1) | Önemli | `GroupId` değişimi ile apply yarışı, `CustomerBalances.UpdatedAt` dokunuşu | İşlendi §4.11 (SaveChanges kancası: senkron, `group-customers`, `--at-twins`), §4.13, §5.5 |
| 4 | Hakem 1 aşısı (T1) | Önemli | `GroupId` için ordinal collation | İşlendi §3.1 (BIN2), M5 |
| 5 | Hakem 2, D2 | Küçük | Eksi satır havuzu kilitliyor | İşlendi §4.1 örneği, §7.3, `enable` reddi, M2 |
| 6 | Hakem 2, D2 / aşı (D3) | Küçük | Silinmiş üyeden çekilen bacak dönünce erişilemez | İşlendi K3: silinmiş üye `S` üzerinden havuzda, numarası canlı üyeleri izler (§4.0-4, §4.11, §4.12, §5.4). İlk sürümde `W` üzerindendi; ikinci incelemede değişti (§14-1) |
| 7 | Hakem 1 aşısı, hakem 1 zayıflık 4, hakem 2 | Önemli | S2'den sonra S1 öncesi imaja sabitleme | İşlendi §7.4 (yasak, tespit sorgusu, `deploy/README` notu) |
| 8 | Hakem 1 ve 2 aşısı (T1/D1) | Önemli | Salt okunur `verify` CLI, ön kontrol ve periyodik | İşlendi §7.3, §8.1 (günlük iş + uyarı e-postası) |
| 9 | Hakem 1 ve 2 aşısı (T3/D1/D3) | Önemli | Testcontainers yarış listesi | İşlendi §8.2 (14 madde, ikinci incelemeyle) |
| 10 | Hakem 1 ve 2 aşısı (T3/T1) | Önemli | Ayır onayı, üye başına kırılım | İşlendi §4.6 `Members[]`, §6.1-e, K1 |
| 11 | Hakem 1 ve 2 aşısı (T3/D1) | Önemli | Shopper `Reason` maskesi | İşlendi §4.10, K4 |
| 12 | Hakem 1 aşısı (T3), zayıflık 2 | Önemli | Paneldeki eksi işlemler satırla sınırlı | İşlendi §4.8, §4.9, K5 |
| 13 | Hakem 1, zayıflık 3 | Küçük | 0 tutarlı çapa gürültüsü; okuma uçları bacakları toplamalı | İşlendi §4.3–§4.10 (`AggregateHistory`), risk 6 |
| 14 | Kazanan tasarım riski 6 | Küçük | `UpdatedAt` kilit dokunuşuyla ilerliyor | İşlendi M9 |
| 15 | Kazanan tasarım riski 9 | Önemli | Değişmez kuralı çalışırken denetleyen iş yok | İşlendi §8.1 |
| 16 | Hakem 1 sahip sorusu 9 | Önemli | #506 notunun sırası | İşlendi §6.1-d, §7.5, M3 |
| 17 | Hakem 1 sahip sorusu 6 | Küçük | Kimlik birleşmesinin grup açığı | Bilerek yapılmadı (§1, §5.3, §10 açık kalanlar) |
| 18 | Hakem 2, D2 (c) | Küçük | Ayırma ile apply yarışı (geri alınabilir) | İşlendi 3 ile; kalan pencere §5.5'te gerekçeli |

Sonuç: iki hakemin bütün aşıları (8 + 7, örtüşenler birleşik) ve kazananı ya da "üçünde ortak"ı ilgilendiren bütün ölümcül bulgular işlendi. Hakem 2'nin "v0.9.9'un bu 409'a tepkisi elle doğrulanmalı" aşısı ilk sürümde yalnız koddan çıkarılmıştı; ikinci incelemede §8.5'e saha denemesi olarak eklendi (§14-29). Bir bulgu bilerek kapsam dışı bırakıldı (17). Önerilenden farklı ama eşdeğer bir çözümle işlenenler:
- 2: "aynı kişi × kapsamda başka satırda çapa varsa 409" yerine kapı + istemci alt sınırı. #506'nın meşru çok izli birleştirme akışını bozmamak için.
- 3: yalnız senkron ucu yerine SaveChanges kancası. Üç `GroupId` yazarını birden kapsar.

## 14. Spec incelemesi (2026-10-12) — bulgu kararları

Mercekler: **para** = para güvenliği, **kod** = kod tutarlılığı, **karar** = eksiklik ve karar. "Birleşik" aynı tasarım kararına bağlanan bulguları gösterir. Her bulgu `origin/master` + PR #506 kesitinde koda karşı doğrulandı; reddedilen bulgu yok, iki bulguda önerilen seçeneklerden biri gerekçeyle seçilmedi.

| # | Mercek | Önem | Konu | Karar |
|---|---|---|---|---|
| 1 | para | Önemli | K3, silinmiş üyenin numarası değişmezse tutuyor; birleşmede silinmiş üye kişiden düşüyor | İşlendi §4.11 (kanca: silinmiş üye numarayı izler), §4.0-4 (silinmiş üyeler `S`'den; `W`'deki yok sayılır), §4.12, §5.4, §7.3-4, §8.2-13, M11. WPF'in silinmiş satır için `GroupId` göndermesi seçilmedi: protokolü ve WPF'in silinmiş satır atlamasını değiştirirdi, eski sürümlerle yine ayrışırdı (§4.12). Ayır istisnası sahibe (§10) (birleşik: 11, 21) |
| 2 | para | Önemli | Grup geri almasında "deltayı her satıra yeniden uygula" çakışmayan satıra iki kez para yazar | İşlendi §4.5 (çakışmada `ChangeTracker.Clear`, taze okuma, grubu baştan kur), §4.13, §8.2-4, M12 |
| 3 | para | Küçük | Birleştirici sıfır bakiyeli kopyaya dokunmuyor; eşzamanlı geri alma ya da 0'lık çapa kopyada kalıyor | İşlendi §4.12 (kopyanın bakiye satırı 0'da da damgalanır), §4.5 (bacağın güncel satırı), §4.2-5 (çapanın bakiye satırı 0 ile açılır, kilide girer), §7.3-8 (kopya satır kalemi), §8.2-5, M12. Birleştiricinin hareket/bakiye okuması arasındaki dar pencere bugün de var; `verify` görür (risk 10) |
| 4 | para | Küçük | BIN2 sondaki boşluğu ayırmıyor; `verify` kırpma kalemi sondaki boşluğu bulamıyor | İşlendi §2, §3.1, §4.13 (bellekte ordinal süzgeç), §7.3-5 (`DATALENGTH` ve sekme/satır sonu), §7.2 (0 değilse `enable` reddeder), M5 (birleşik: 10) |
| 5 | para | Küçük | Hiç senkronlanamayan sahip satır kalıcı 409; yeni müşteri boşuna bekletiliyor | İşlendi §4.0-1 (hiçbir üye sunucuda yoksa kesin `no-balance`), §6.1-a (sahip kuralı `IsServerAcceptable`), §6.1-b (5 dakika metni), §5.5, M14 (birleşik: 14) |
| 6 | para | Küçük | D3 boş; `S ⊄ W`'de görülmeyen eksi üye hesaba katılmıyor; kabul edilmiş eksiler her gün uyarı | İşlendi §3.3 D3 (`Σ_{S∪P}`), §4.0-4 (`S∖P` eksileri havuza sayılır ve kilitlenir), §4.2, §4.13, §8.1 (taban çizgisi), §8.3 (birleşik: 17, 20) |
| 7 | para | Küçük | S2 elle geri alınırsa S1'in panel reverse'i K5 iptal grubunu yarım bırakıyor | İşlendi §4.9 ("S1 tabanı": `DrawGroupId` dolu hedef S1'de grup olarak geri alınır ya da reddedilir), §7.4, §8.3 (S2 biçimli satırlarla S1 testi), M8 (birleşik: 25) |
| 8 | para | Küçük | Yeniden denemede replay denetimi atlanıyor; aynı anahtarlı istek `no-balance` alabiliyor | İşlendi §4.2-6 (2. ve sonraki her denemede `LookupAsync`), §4.13, §8.2-10 |
| 9 | kod | Önemli | `ONLINE` indeks Express'te yok; göç testi Developer sürümünde yeşil yanıyor | İşlendi §3.1 (`ONLINE` çıkarıldı; sınıra yakınsa indeksler ayrı göçe), §7.1 adım 0 (S1'den önce elle satır sayımı), §8.2-11 (göç testi Express'te) (birleşik: 23) |
| 10 | kod | Önemli | "Bayt bayt, bellekte süzgece gerek yok" yanlış; `verify` 5. madde ve `KeyOf` farkı | İşlendi (birleşik: 4) |
| 11 | kod | Önemli | Silinmiş üyede `W` ile `S` kalıcı ayrışıyor; Ayır onayı ve kart onu göstermiyor | İşlendi (birleşik: 1). `memberIds` alan, `W` üzerinden üye başına bakiye dönen ayrı okuma ucu seçilmedi: sunucu kuralıyla bütün okuyucular aynı `S`'yi görüyor, ek uç gerekmiyor. §11 risk 4 düzeltildi |
| 12 | kod | Önemli | `S ⊄ W` ve para `S∖W`'de: önizleme 0, iş `NoBalance` kesinleşiyor (K2'ye aykırı) | İşlendi §4.0-3b (parası olan `S∖W` canlı üyesi → 409 `customer-not-synced`), §5.2, §5.5, §8.3, M4 |
| 13 | kod | Küçük | Üyelik bakiye yüklemesinden önce okunuyor; kanca aradaki ayırmayı görmüyor, §8.2-4 kanıtlanamaz | İşlendi §4.2-2 (bakiye yüklemesinden sonra üyelik yeniden okunur), §4.11 (cümle gerçek davranışa göre düzeltildi), §5.5, §8.2-6, M6 |
| 14 | kod | Küçük | Gönderilemeyen sahip satır havuz kipinde kalıcı 409 | İşlendi (birleşik: 5) |
| 15 | kod | Küçük | §7.4 tespit sorgusu S1'in tek bacaklı geri almasıyla ilk gün doğru dönüyor | İşlendi §3.2 ve §4.5 (tek bacaklı geri almada `DrawGroupId = NULL`), §7.4 (sorgu `DrawGroupId <> Id`'ye daraltıldı, README metni), M13 (birleşik: 24) |
| 16 | kod | Küçük | `disable` sonrası panel, bacağı tutan satırdan grup iptalinde 404 | İşlendi §4.9 (çekim grubu hedefinde üyelik iki kipte de grubun herhangi bir satırı), §7.2, §8.3 (birleşik: 27) |
| 17 | kod | Küçük | D2 birleştirici yüzünden tutmuyor; günlük uyarı ilk günden gürültü | İşlendi §3.3 D2 (birleştirici kapsam dışı), §8.1 (birleşik: 6, 20) |
| 18 | kod | Küçük | K2 tetiklemesi yalnız gönderimi çalıştırıyor; öbür PC'nin ayırması 30 sn bekliyor | İşlendi §6.1-b (`CustomerChangesPullService.PullOnceAsync`: gönderir ve çeker), §8.4 |
| 19 | kod | Küçük | §7.5 "hiç oluşmaz" mutlak değil (#506'lı Wh öncesi PC kapıdan sonra dönerse) | İşlendi §7.5 (iddia daraltıldı; para kaybı yok, §8.5 denemesi). Reverse ucuna sürüm kapısı **reddedildi**: geri alma sunucuda her istemci için grup olarak doğru, kapı eski PC'nin revizyonunu belirsiz bırakırdı (birleşik: 28) |
| 20 | karar | Önemli | Günlük `verify` S1 gününden susmayan uyarı; adım 3 koşulu sağlanamaz; D3 totolojik; D2 tutmuyor | İşlendi §3.1 (`PersonBalanceBaselines`), §3.3, §7.1 adım 2–4, §7.2-3, §7.3 (taban), §8.1 (yalnız yeni sapma; taban yoksa uyarı yok), M2 (birleşik: 6, 17) |
| 21 | karar | Önemli | K3 yeniden numaralamada bozuluyor; Ayır'daki istisna sahibe sorulmamış | İşlendi (birleşik: 1). Ayır istisnası §10 açık kalanlarda sahip kararı olarak yazıldı, §5.2 ve §4.12'de işaretli |
| 22 | karar | Önemli | Wh'nin sayısal değerinin kaynağı ve sürüm karşılaştırması tanımsız | İşlendi §2, §4.0, §7.2 (`PersonBalance:MinClientVersion`, compose ortam satırı, `System.Version`, boşsa `enable` reddeder ve havuz kipinde fail-closed), §7.1 adım 6, §8.3, §12, M1 |
| 23 | karar | Küçük | Ön kontrol S1 içinde, göçten önce ölçemiyor; `ONLINE` Express'te yok | İşlendi (birleşik: 9) |
| 24 | karar | Küçük | Tek bacaklı geri almada `DrawGroupId` dolu: §4.0, §8.5 ve §7.4 ile çelişki | İşlendi (birleşik: 15); iki seçenek birlikte uygulandı |
| 25 | karar | Küçük | `not-reversible` kipe bağlı değil; satır kipinin davranışı değişiyor | İşlendi §4.9, M8: eski `reversal` satırına kural yalnız havuz kipinde; çekim grubuna ait `reversal` iki kipte (bu satırlar yalnız havuz kipinde doğar), §7.1 adım 5 (birleşik: 7) |
| 26 | karar | Küçük | M2 "S2'den önce" diyor, sıra incelemeyi S2'den sonraya koyuyor | İşlendi §7.1 (inceleme adım 4'e, S2'nin önüne alındı), M2 |
| 27 | karar | Küçük | `disable` sonrası panel grup iptali 404 | İşlendi (birleşik: 16) |
| 28 | karar | Küçük | `PersonPool` önbelleği `enable`/`disable` sonrası bayat; §7.5 iddiası | İşlendi §6.1-d (yerel planda önbellek yok, hep `null` nötr metin; sunucu izlerinde aynı akışın `scope` yanıtı), §7.5, §8.4 (birleşik: 19) |
| 29 | karar | Küçük | Hakem 2'nin v0.9.9 elle doğrulama aşısı §8.5'te yok, §13 tümünü işlendi sayıyor | İşlendi §8.5 (karışık filo denemesi: v0.9.9 ve yalnız #506), §6.2, §7.1 adım 7, §13 sonuç notu |

## 15. Satış senkronu bağımlılıkları (2026-10-16)

Kaynak: `2026-10-13-yayin-etiket-senkron-design.md` §6.6, §6.7 ve §13 madde 2. O spec bu zorunlulukları "D-ortak-bakiye docs PR'ı" ile buraya taşımayı öngörüyordu; bu bölüm odur. Satış senkronu ortak bakiyeye bağımlı değildir; bağımlılık tek yönlüdür.

| # | Zorunluluk | Bu spec'te |
|---|---|---|
| 1 | **Sıra.** Ortak bakiye, satış senkronunun S3'ü master'a girdikten sonra başlar. İkisi `LicensesCustomerBalanceApplyController`'a dokunuyor: S3 apply'a kapsam kuralını ve mod On'da preview/apply için `client-update-required`'ı ekler; bu spec'in S1'i scope, transactions ve reverse'i, S2'si apply'ı yeniden yazar. | §7.1 (giriş notu), §12 akış |
| 2 | **Kapsam başına tek düşüm** (§6.6 orada). S2'nin `DrawAsync`'i kuralı **çapa** düzeyinde korur; aynı kişinin başka satırındaki çapa 409 olmaz (#506'nın çok izli akışı). Havuz kipinde 409'un tutarı `−Σ(grup)`'tur. | §4.2, §4.13, §8.2-15, §12 S2 |
| 3 | **Ortak sürüm yardımcısı.** Satış senkronunun S1'i `ClientVersion`'ı getirdi (`Services/Sync/ClientVersion.cs`; `LicensesController` de kullanıyor). `PersonBalance:MinClientVersion` ve `SalesSync:MinClientVersion` aynı karşılaştırmayı paylaşır. Eski S2 adımı "UA ayrıştırıcının ortak yardımcıya taşınması" bu yüzden düşer. | §4.0, §7.2, §12 S2 |
| 4 | **Wh ⊇ Ws.** Havuz kipinin kapısı bütün PC'leri Wh+ ister; böylece hepsi satış senkronunu da taşır. `enable`, `PersonBalance:MinClientVersion` ≥ `SalesSync:MinClientVersion` değilse reddeder. | §7.1, §7.2-4 |
| 5 | **Aynı üye kümesi.** Havuz kipinin `memberIds`'i ile satış senkronunun ödeme kirasınınki (`payment-requests/begin`) aynı kaynaktan gelir (kişinin bütün üyeleri). PersonKey bu spec'in `S(a)` kişisidir; S1'in BIN2 göçü gelene kadar satış senkronu ham `GroupId`'yi bellekte ordinal karşılaştırır (§4.13'ün yöntemi), BIN2'den sonra da doğru kalır. | §4.0, §4.13 |
| 6 | **Satış senkronunun sürüm kapısı korunur.** Satış senkronu S3, mod On'da `preview`, `scope`, `transactions/{id}/reverse` ve `apply`'a 409 `client-update-required` kapısını koydu (orada §4.7, §6.5-3); `transactions/{id}` kapısızdır. Bu spec'in S1'i `scope` ve `reverse`'i, S2'si `preview` ve `apply`'ı yeniden yazarken bu kapıyı korur. Kapı düşerse sahada kalmış v0.9.10, başka bilgisayarın teslim edilmiş düşümünü `scope` ile benimser ya da `reverse` ile geri alır. Bu kapı satış senkronu moduna bağlıdır; havuz kipinin `PersonBalance:MinClientVersion` kapısından (§4.0) ayrıdır, ikisi birlikte çalışır. | §4.0, §12 S1–S2 |
| 7 | **Kiranın (b) denetimi yalnız canlı üyeleri ister** (2026-10-21, S1 Görev 10 incelemesi; **sahip onayı S1 merge'ünden önce**). §4.11'in numara izlemesi silinmiş üyeyi sunucuda taşır, WPF'te taşımaz. `begin`'in (b) adımı sunucu grubunun silinmişler dahil bütün asıl üyelerini isteseydi: g1 = {A, P silinmiş}, g0 = {B}; `group-customers` ya da bir PC'nin Birleştir'i A'yı g0'a alır, kanca P'yi g0'a taşır; öbür PC'lerin yerel g0'ı {A, B} kalır, `members-stale` hiçbir müşteri akışı turuyla kapanmaz ve ödeme isteği kalıcı olarak engellenir (fail-closed). S1'den önce sunucu silinmiş satırı hiç taşımadığı için sunucu ⊆ istemci tutuyordu; bu bir regresyon olurdu. **Karar (§4.0-3'ün kuralı):** (b) yalnız sunucuda silinmemiş üyelerin `memberIds`'te olmasını ister. Arama (c–e) ve kaydın üyeleri (h) silinmişleri taşımaya devam eder: silinmiş sahipli ya da silinmiş üyeye yazılmış kayıtlar bulunur. Toplam (f) istemcinin üyeleriyle sayılır: (b)'den sonra canlı sunucu üyeleri zaten onlardadır (S1'den önceki fiilî küme), istemcide olmayan silinmiş üyenin siparişi sayılsaydı `stale-total` kalıcı olurdu. Satış senkronu spec'inin §6.4-b'si ("grubundaki asıl üyeler") böylece canlı üyelere daralır. 409'un `members` gövdesi değişmez (sunucu grubunun bütün asıl üyeleri). | §4.0-3, §4.11, §4.12 |

**Kira ve havuz bağımsızdır.** Havuz kipi düşülen tutarı değiştirir; kira (kişi×kapsamda tek `Sending`, "zaten istendi" sorusu) ve kapsam kuralı ondan bağımsız çalışır. Kira kişi düzeyindedir, kapsam kuralı çapa düzeyinde; grup senkronu gecikmesinde başka üyedeki izi kira görünür kılar (`members-stale` canlı üyelerle, 7. satır; üye kesişimi, sunucu üyeleriyle uzlaştırma).
