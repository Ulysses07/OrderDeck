# Ortak Bakiye (Kişi Havuzu) — Tasarım

**Tarih:** 2026-10-11 · **Durum:** sahip kararları alındı (§10), tasarım incelemesi işlendi (§13), uygulama planı bekliyor · **İlgili:** `2026-10-10-banka-havale-onay-design.md` (+ PR #505 §2 "Kişi"), PR #506 (WPF ödeme isteği kişi anahtarı)

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
  - Karşılaştırma ham değer üzerinde ve **ordinaldir** (WPF `CustomerGroup.cs:10-22` ile birebir). Sunucuda kolon BIN2 collation'a alınır (§3.1).
  - KVKK ile silinmiş (`PurgedAt` dolu) üye kişide kalır, havuza girer ve çekilebilir (K3).
- **Sunucu kişisi `S(a)`:** `a` istekteki Id'nin asıl kaydı (`CustomerIdResolver.CanonicalOfAsync`, `:25-42`). `a`'nın `GroupId`'si boşsa `S = {a}`; değilse lisansta `GroupId`'si BIN2 eşit olan bütün asıl kayıtlar (silinmişler dahil).
- **WPF kişisi `W`:** havuz kipinde WPF'in preview ve apply'a eklediği `memberIds`. Kişinin GUID'li bütün üyeleri, silinmişler ve sahip dahil; `PaymentRequestService.cs:1096`'daki aday listesiyle aynı süzgeç. Sunucu her Id'yi asıl kayda çözer.
- **Çekim kümesi `P`:** apply ve preview'ın gerçekten okuduğu ve çektiği satırlar. Tanımı ve 409 kuralları §4.0'da.
- **Havuz:** `Σ Balance` (`P` ya da `S` üzerinden). **Kullanılabilir** = `max(0, havuz)`.
- **Çekim (draw):** bir eksi para hareketinin bir ya da birden çok satıra bölünmüş hâli. Üç türü var: ödeme düşümü (`purchase-deduction`), paneldeki eksi elle ayar (`manual-adjustment`) ve artı bir hareketin iptali (`reversal`, K5).
- **Bacak:** çekimin tek satırdaki defter satırı. Bacak, satırın o anki pozitif bakiyesinden büyük olamaz. Bu yüzden hiçbir yeni yazım bir satırı eksiye düşürmez.
- **Çapa bacağı:** çekimin isteğe ait satırdaki bacağı: WPF'te işin sahip satırı, panelde hedef hareketin ya da rotanın satırı. Çapa çekimin kimliğidir:
  - WPF'te `Id` = idempotency anahtarı (`LicensesCustomerBalanceApplyController.cs:286`).
  - `OriginalAmount`, `SaleScope`, `Reason` ve `ReversesTransactionId` yalnız çapada tutulur.
  - **Tutarı 0 olabilir** (satırda para yoksa).
- **`DrawGroupId`:** bacakları çapaya bağlayan yeni kolon. Değeri çapanın `Id`'si; çapada kendi `Id`'si. `NULL` = eski, tek bacaklı satır; kendi başına bir gruptur. Çapa ⇔ `DrawGroupId IS NULL OR DrawGroupId = Id`.
- **Grup geri alması:** bir çekimin **her** bacağı için bir `reversal` satırı. Her biri `ReversesTransactionId = bacağın Id'si` taşır (N01 bacak başına hakem) ve parayı bacağın kendi satırına geri ekler.
- **Kip:** lisans başına iki kip var: **satır kipi** (bugünkü davranış) ve **havuz kipi** (`License.PersonBalancePoolSince` dolu). Kipler arası geçiş ve kapı: §7.2.
- **Wh:** PR #506 ile bu spec'in WPF değişikliklerini (W-havuz, §6.1) birlikte taşıyan ilk Velopack sürümü. Havuz kipinin istemci alt sınırıdır.

## 3. Veri modeli (S1)

### 3.1 Şema (tek EF göçü, `Program.cs:868` açılışta uygular)

| Tablo | Değişiklik | Gerekçe |
|---|---|---|
| `CustomerBalanceTransactions` | `DrawGroupId uniqueidentifier NULL` (`CustomerBalanceTransaction.cs` + `LicenseDbContext.cs:862-891`) | Bacakları bağlar. |
| `CustomerBalanceTransactions` | `IX_CustomerBalanceTransactions_DrawGroupId`, filtre `[DrawGroupId] IS NOT NULL`, **tekil değil** | Grup okuması ve geri alma. Tekil olamaz: kimlik birleşmesi aynı grubun iki bacağını aynı satıra taşıyabilir (`CustomerIdentityMerger.cs:75-77`). |
| `WpfCustomerProjections` | `GroupId` → `UseCollation("Latin1_General_100_BIN2")` | Kolonun varsayılan collation'ı harf farkını yok sayıyor (`CustomerGroupUnion.cs:41-44` notu). Kişi eşitliği WPF'teki gibi bayt bayt olmalı. Emsal: `IdentityKey` (`LicenseDbContext.cs:682-690`). |
| `WpfCustomerProjections` | `(LicenseId, GroupId)` indeksi, filtre `[MergedIntoId] IS NULL AND [GroupId] IS NOT NULL` | Bugün `GroupId` üzerinde indeks yok (`:693`, `:712-723`); kişi sorgusu lisansın bütün kayıtlarını tarıyor. |
| `Licenses` | `PersonBalancePoolSince datetimeoffset NULL` | Havuz kipi bayrağı. S1'de hiçbir kod okumaz ve yazmaz; S2 okur, yalnız CLI yazar (§7.2). |

- Collation değişikliği: sütuna bağlı indeks yok. Otomatik istatistik (`_WA_Sys_*`) `ALTER COLUMN`'u engelleyebilir; göç önce varsa onu düşürür. Göç testi bunu gerçek SQL Server'da doğrular (§8.2).
- Göç açılışta, Kestrel'den önce koşar ve `/ready`'nin 60 sn penceresine sığmalıdır. Ön kontrol (§7.3) iki tablonun satır sayısını ölçer; sınıra yakınsa indeks oluşturma `ONLINE`'a çevrilir.
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
| Grup geri alma satırı (çekimin her bacağı için) | bacağın satırı | `≥ 0` | `reversal` | — | **o bacak** | geri alma çapasının `Id`'si |

- Bir grubun bütün satırları **tek** `SaveChanges`'te ve **aynı** `CreatedAt` ile yazılır. Listeler bu yüzden grubu bölmeden sıralar.
- Bacakların `Reason`'ı `NULL`'dır. Açıklama çapada durur.
- Çekim türü bir iptalde yalnız çapa `ReversesTransactionId` taşır. Grup geri almasında ise her satır kendi bacağını taşır. İkisinde de N01 tekil indeksi (`LicenseDbContext.cs:880-882`) hakemdir: ilkinde hareket başına tek iptal, ikincisinde bacak başına tek geri alma.

### 3.3 Değişmezler

- **D1** Satır başına `Balance = Σ Amount` (bugünkü kural, `CustomerBalance.cs:3-6`).
- **D2** Yeni yazımlar hiçbir satırı eksiye düşürmez: bacak ≤ satırın o anki pozitif bakiyesi.
- **D3** Havuz kipinde çekimden sonra kullanılabilir havuz ≥ 0. Kişi kilidi bunu sağlar (§4.13).
- **D4** Her grupta tam bir çapa vardır; bacaklar çapayla aynı `Kind`'ı taşır. Bacaklarda `SaleScope` ve `OriginalAmount` `NULL`'dır.
- **D5** Bir çekimin bacaklarının ya hepsi geri alınmıştır ya hiçbiri.
- **D6** `purchase-deduction` grubunda `−Σ bacak ≤ çapa.OriginalAmount`.

Uygulama kodu D2, D3 ve D5'i sağlar. Altısını da `verify` denetler (§8.1).

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

1. `a`'nın projeksiyonu sunucuda yoksa → **409 `customer-not-synced`** (K2). Çapa yazılamaz (hareket projeksiyona FK, `LicenseDbContext.cs:867-868`).
2. `W`'deki bir Id'nin projeksiyonu sunucuda yoksa o Id atılır. Henüz senkronlanmamış satırın bakiye satırı olamaz: bakiye satırı projeksiyona FK (`:851-852`), panel uçları projeksiyonu bulamayan Id'yi reddeder (`CustomerIdResolver.cs:69-81`).
3. `W`'de sunucuda **silinmemiş** olup `S(a)`'da **olmayan** bir üye varsa → **409 `customer-not-synced`**. İki olası durum var:
   - WPF birleştirdi, sunucu henüz görmedi;
   - başka bir PC ayırdı, bu PC henüz çekmedi.
   İkisi de senkron turuyla kapanır.
4. `P = (W ∩ S(a)) ∪ {W'de olup sunucuda silinmiş üyeler}`.
   - `S`'de olup `W`'de olmayan canlı üyeler çekilmez. Bunlar ya operatörün ayırdığı ama henüz gönderilmemiş satırlardır, ya da başka PC'nin eklediği ama bu PC'nin henüz görmediği satırlardır. Operatör bu satırları ekranında bu kişide görmüyor; para onlardan çekilmez.
   - Silinmiş üyenin `GroupId`'si sunucuda donuk kalır (`CustomerFieldMerge.cs:42`). WPF onu kişide sayıyorsa havuza girer (K3).

**Sürüm denetimi (havuz kipi).** Havuz kipinde `memberIds` zorunludur; yoksa 400 `member-ids-required`. İstemci sürümü `User-Agent`'tan okunur; ayrıştırıcı `LicensesController.cs:123-135`'ten ortak bir yardımcıya taşınır. Sürüm Wh'den eskiyse ya da hiç yoksa preview ve yeni yazım yapacak apply **409 `client-update-required`** döner. Gerekçe §7.2'de. Yeniden oynatma (anahtar sunucuda kayıtlı) bu denetimden önce çalışır ve hiç engellenmez.

**Panel, Shopper ve kart okumaları** WPF üye listesi taşımaz; hepsi `S(a)` üzerinden okur. Senkron penceresinde kartın gösterdiği havuz ile apply'ın çektiği küme kısa süre farklı olabilir (§11).

### 4.1 `GET preview` (`LicensesCustomerBalanceApplyController.cs:74-91`)

- **Satır kipi:** bugünkü gibi. Yanıta `PersonPool = false` eklenir; S1 bu alanı hep `false` döner.
- **Havuz kipi:**
  - Sırayla: sürüm denetimi, ardından `P` ve 409 kuralları.
  - `Balance = max(0, Σ_P Balance)`.
  - `UpdatedAt` = `P`'deki son para hareketinin `CreatedAt`'i; hareket yoksa satırların en yeni `UpdatedAt`'i. Böylece kişi kilidinin ilerlettiği jeton "güncellendi" zamanını oynatmaz.
  - `PersonPool = true`.
  - Yanıttaki Id istekteki Id olarak kalır.
- **Örnek (eksi satır havuzu kilitler):** A = 50, B = −60 → Σ = −10 → `Balance = 0`. Satır kipinde A'nın 50'si kullanılabiliyordu. Bu kişi ön kontrolde listelenir; havuz kipi o lisansta eksi satır varken sahibin onayı olmadan açılmaz (§7.3).

### 4.2 `POST apply` (`:224-362`)

Doğrulamalar (`:230-251`), sahiplik (`:253`), asıl kayda çözme (`:257`) ve **yeniden oynatma** (`:261-269`) önce, bugünkü sırayla çalışır. Satır kipinde akış değişmez. Havuz kipinde:

1. Sürüm denetimi, ardından `P` (§4.0). `W`'nin dışından hiçbir satır okunmaz ve yazılmaz.
2. `P`'nin bakiye satırları izlenen nesneler olarak yüklenir. `Σ ≤ 0` ise **409 `no-balance`** (bugünkü başlık; WPF bunu kesin "bakiye yok" sayar, `PaymentRequestService.cs:1341-1347`).
3. `applied = min(Amount, max(0, Σ), ProductTotal)`; `≤ 0` ise `nothing-to-apply` (bugünkü gibi, `:279-281`).
4. **Plan (`PlanDraw`, saf):** her adımda o satırdan `min(max(0, bakiye), kalan)` alınır. Sıra:
   1. Çapa satırı. Alıcının kendi parası önce kullanılır; satırlar arası hareket en aza iner (K1).
   2. Sunucuda silinmiş üyeler, bakiye büyükten küçüğe. Ayırmadan sonra erişilemez kalabilecek para önce kullanılır (K3).
   3. Canlı üyeler, bakiye büyükten küçüğe; eşitlikte Id ("N", ordinal).
5. **Yazım:**
   - Çapa: `Id = anahtar ?? NewGuid`, `Amount = −çapadanAlınan` (0 olabilir), `OriginalAmount = ProductTotal`, `SaleScope`, `DrawGroupId = Id`.
   - Bacaklar: `Id = NewGuid`, `DrawGroupId = çapa`, ortak `CreatedAt`.
   - Çekilen satırların `Balance`'ı düşer.
   - **`P`'nin bakiye satırı olan bütün üyelerine** jeton dokunuşu yapılır (kişi kilidi, §4.13). Çapa satırının bakiye satırı yoksa ve çapa 0 ise satır açılmaz: 0'lık hareket D1'i bozmaz, bugün de apply bakiye satırı açmıyor.
6. Tek `SaveChanges`; EF bunu tek işlemde yazar. `EnableRetryOnFailure` tanımlı değil (`Program.cs:115`).
   - **Jeton çakışmasında** (`DbUpdateConcurrencyException`): `ChangeTracker.Clear()`, adım 1'den yeniden; en çok 3 deneme. Bugün yalnız satır yeniden yükleniyor (`:316-340`). Burada üyelik de değişmiş olabileceği için plan baştan kurulur. Üçüncü çakışmadan sonra **503 `balance-busy`**: WPF'in dayanıklılık katmanı aynı anahtarla yeniden dener; o da düşerse iş belirsiz kalır ve mesaj gitmez.
   - **PK ihlali** (aynı anahtarla yarış): bütün bacaklarla birlikte geri alınır, kazananın sonucu oynatılır (`:341-358`, değişmez).
7. Yanıt: `TransactionId = çapa`, `AppliedAmount = applied` (bütün bacakların toplamı), `RemainingBalance = max(0, Σ_P)`.

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
- Oynatmadaki `RemainingBalance` = o anki havuz (`P`; `memberIds` yoksa `S`). WPF bu alanı okumuyor.
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
- Para her bacağın kendi satırına eklenir. Tutarı 0 olan bacakta bakiye satırına dokunulmaz; `:471-472`'deki `NotFound` kolu yalnız tutar > 0 iken anlamlıdır. Satır yoksa açılır (artı tutar her zaman güvenli; panel yazıcısının kalıbı, `PanelCustomerBalanceController.cs:299-314`).
- Tek `SaveChanges`. Jeton çakışmasında bugünkü "yeniden yükle, deltayı yeniden uygula" kalıbı (`:502-510`) her satır için.
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

- **Üyelik koşulu:** bugün hareket rota satırının olmalı (`t.WpfCustomerId == canonicalId`, `:207-211`). Havuz kipinde grubun herhangi bir bacağı `S(rota)`'daysa kabul edilir. Panel kişinin birleşik listesini gösterdiği için başka üyenin hareketi de iptal edilebilmeli. Satır kipinde koşul aynen kalır.
- **Hedefe göre davranış:**
  - **Düşüm (çapa ya da bacak, `purchase-deduction`):** grup geri alması (§4.5). Tek satırı geri almak yarım bir geri alma bırakırdı.
  - **Elle eksi grubu:** grup geri alması.
  - **Artı hareket (`refund-full`, `refund-net`, artı `manual-adjustment`):**
    - Satır kipinde bugünkü gibi.
    - Havuz kipinde **çekim türü iptal**: çapa iptal edilen hareketin **kendi satırında** (rota satırında değil), `Kind = reversal`, `ReversesTransactionId` = hedef hareket; kalan `S`'nin öbür pozitif üyelerinden bacak olarak çekilir. Havuz yetmezse 409 `insufficient-balance` (hep-ya-hiç).
  - **`reversal` satırları:** 409 `not-reversible` (yeni). Panel arayüzü zaten yalnız iade ve elle ayar satırlarında iptal düğmesi gösteriyor (`CustomerBalanceCard.tsx:154-166`). Ret, "iptalin iptali"nin çok bacaklı zincirini kapatır. Banka spec'inin `bank-overpayment` reddi (§6) aynen geçerli.
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
  - Sunucuda `GroupId`'ye yazan üç yer var: `CustomerFieldMerge.cs:54-56` (senkron), `CustomerGroupUnion.cs:69` (`group-customers` ve `--at-twins`) ve `CustomerIdentityMergeJob.cs:567`. Üçü de izlenen nesneler üzerinden yazıyor; kanca üçünü birden kapsar.
  - Bir convention testi, sunucu kodunda `GroupId`'ye ham SQL ya da `ExecuteUpdate` ile yazım olmadığını sabitler; kanca öyle bir yazımı göremezdi.
- **`CustomerBalance` `StampVersions`'a eklenir** (`:174-192`, bugün `NetgsmAccount` ve `PaymentMatch`). Her değişmiş bakiye satırının jetonu `max(UtcNow, özgün + 1 tick)` olur. `UtcNow` atamasına güvenen bugünkü yazarlar saat geri giderse aynı değeri yazabilirdi; kişi kilidinin bir "dokunuşu" böylece hiçbir zaman kaybolmaz.
- **Etkisi (hakem aşısı):**
  - Apply'ın okuduğu üye kümesinden **para tutan** bir satır araya giren bir ayırmayla çıkarsa: ayırma önce yazılırsa apply jeton çakışması alır ve planı taze üyelikle baştan kurar. Apply önce yazılırsa senkron çakışır ve var olan temizle-yeniden-uygula döngüsü (`LicensesWpfCustomersSyncController.cs:256-297`) partiyi taze okumayla uygular; ikinci çakışmada 409 döner ve WPF partiyi yeniden gönderir.
  - Birleştirme ve gruplama işleri çakışan grubu geri alıp sayar (`CustomerIdentityMergeJob.cs:297-304`; `CustomerGroupingJob.cs:230-243` hata çağırana çıkar); yeniden koşmak kalanları bulur.
  - Bakiye satırı olmayan üyenin üyeliği değişirse dokunulacak bir şey yoktur: para taşımaz. Ayrıntı ve kalan pencere §5.5'te.

### 4.12 Kimlik birleşmesi ve KVKK silmesi

- **`CustomerIdentityMerger.RepointReferencesAsync`** (`:39-139`) değişmez:
  - Bacaklar öbür hareketlerle birlikte asıl kayda taşınır (`:75-77`), `DrawGroupId` korunur.
  - Aynı çekimin iki bacağı aynı satıra düşebilir. Sorun değil: indeks tekil değil, geri alma bacak başına yazılıyor.
  - Kopyanın bakiyesi asıl kayda eklenir (`:79-116`, karar 2026-10-05). İki satıra da `UpdatedAt` yazdığı için süren bir apply çakışır ve yeniden planlar.
- **`ShopperPurgeService`** (`:219-222`) değişmez. Silinen satırın bakiyesi ve hareketleri yerinde kalır, `GroupId`'si donar (`CustomerFieldMerge.cs:42`, `CustomerGroupUnion.cs:66`). K3 gereği havuza girer:
  - apply'da `W` üzerinden;
  - panel, kart ve Shopper'da `S` üzerinden.
  Silinmiş satırdan çekilmiş bir bacak geri alınınca para o satıra döner ve yine havuzdan erişilir.

### 4.13 Eşzamanlılık özeti

| Yazım | Dokunduğu jetonlar | Hakem | Çakışmada |
|---|---|---|---|
| WPF apply (havuz) | `P`'nin bakiye satırı olan **bütün** üyeleri | `UpdatedAt` jetonu; PK = anahtar | Planı baştan kur, 3 deneme, sonra 503 `balance-busy`; PK ihlalinde oynat |
| Panel eksi işlemler (K5, havuz) | `S`'nin bakiye satırı olan bütün üyeleri | `UpdatedAt`; iptalde N01 | Baştan kur, 3 deneme; N01'de 409 `already-reversed` |
| Grup geri alması (WPF ve panel) | Bacakların satırları | N01 (bacak başına) | Deltayı yeniden uygula; N01'de bütün grup geri alınır |
| Artı hareketler (iade, artı ayar) | Rota satırı | `UpdatedAt` | Bugünkü döngü (`PanelCustomerBalanceController.cs:320-338`) |
| `GroupId` değişimi (senkron, işler) | Değişen satırın bakiye satırı (kanca) | `UpdatedAt` | Senkron: taze okumayla bir kez, sonra 409; işler: grubu geri al ve say |
| Kimlik birleşmesi | Kopya ve asıl bakiye satırları | `UpdatedAt`, tekil `(LicenseId, WpfCustomerId)` | Bugünkü gibi |

- **Kişi kilidi neden bütün üyelere dokunuyor:** A = 100, B = −60, C = 50 (Σ 90) iken iki apply aynı anda koşsun. Biri A'dan 90 çeker, öbürü C'den 50. Satırları ortak olmadığı için çakışmazlar ve havuz −50'ye düşer. İki apply de A, B ve C'nin jetonuna dokunduğunda ikincisinin UPDATE'i tutmaz; taze planda Σ = 0'dır ve `no-balance` döner (D3).
- **Bedeli:** aynı kişiye aynı anda gelen yazımlar sıraya girer. Bu nadirdir.
- **Okuma** READ COMMITTED; açık işlem ve satır kilidi yok. Doğruluk jetonlardan ve tek `SaveChanges`'ten gelir. InMemory jeton yarışını kısmen taklit eder, tekil indeksleri uygulamaz; yarışlar yalnız Testcontainers'ta kanıtlanır (§8.2).
- **Collation:** kişi sorgusu `GroupId == a.GroupId` BIN2 kolon üzerinde ordinaldir. Bellekte ikinci bir süzgece gerek yok. `CustomerGroupUnion.LoadGroupRowsAsync`'in kırparak karşılaştırması (`:45-58`) gruplama işlerinde kalır. Kırpma farkının sahada olup olmadığını `verify` sayar (§8.1).

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
- Ayıran PC'nin isteği `W` küçüldüğü için ayrılan satırlardan çekmez (§4.0-4).
- Ayırma sunucuya ulaştıktan sonra, onu henüz çekmemiş başka bir PC'nin isteği 409 `customer-not-synced` alır (§4.0-3). Ulaşmadan önceki istek eski üyelikle çeker; bu, "ayırmadan önce oldu" diye sıralanabilir bir sonuçtur (§5.5).
- Silinmiş satırlar gönderilmez (`:190-194`). Sunucudaki kopyası eski numarada tek başına kalır; ayırmadan sonra parası erişilemez olabilir. Onay penceresi bunu "silinmiş hesap" diye ayrıca gösterir.

### 5.3 Kimlik birleşmesi (`MergedIntoId`)

§4.12'deki gibi değişmez. Olağan kipte kimlik birleşmesi kişiyi bölebiliyor ("grup açığı"). Havuz bu durumda sessizce küçülür; para kaybolmaz, yalnız o kişiden erişilemez. Kapsam dışı (§1, §11).

### 5.4 KVKK silmesi — K3

Örnek: kişi {A, P}; P silinmiş ve 40 ₺ tutuyor, A = 0.
- 30'luk ödeme: çapa A 0, bacak P −30 (silinmiş üyeler önce) → P = 10.
- Satış geri alınırsa P +30 → P = 40. Para yine havuzdan kullanılabilir, çünkü WPF P'yi kişide sayıyor.

### 5.5 Yarış pencereleri

| Pencere | Ne olur | Sonuç |
|---|---|---|
| Birleştir'den hemen sonra ödeme (≤ 60 sn, sahip satır sunucuda yok ya da grubu eski) | §4.0-1 ya da §4.0-3 | 409 `customer-not-synced`. WPF mesajı bekletir, iş taze kalır, bir senkron turu tetikler (K2). Asla sessiz "bakiye yok" olmaz. |
| Ayıran PC, ayırma gönderilmeden ödeme istiyor | `W` küçük, `P = W ∩ S` | Ayrılan satırlardan çekilmez. |
| Başka PC'nin ayırması ya da birleştirmesi bu PC'ye henüz inmemiş | `W ⊄ S` → 409 ya da `S ⊄ W` → `P = W ∩ S` | Ya bekletilir ya operatörün gördüğü küme çekilir; yanlış satırdan çekilmez. |
| Apply'ın okuması ile yazması arasında para tutan bir üyenin `GroupId`'si değişiyor | Kanca jetonu ilerletir | Çakışma; plan taze üyelikle yeniden kurulur ya da senkron yeniden uygular. |
| Aynı arada **para tutmayan** bir üyenin (ör. sıfır bakiyeli çapa) `GroupId`'si değişiyor | Jeton yok | Apply, eski üyelikle tamamlanır. Bu, sıralanabilir bir sonuç: "apply ayırmadan önce oldu". Çekilen her satırın jetonu tuttuğu için çekilen para o an gerçekten o satırdaydı; geri alınırsa kaynağına döner. Değişmez kural bozulmaz. |
| Damgasız eski `GroupId` iki tarafta farklı kalmış (yakınsamaz) | Kalıcı `W ⊄ S` | Bu kişide kalıcı `customer-not-synced`. Bugün 570 grubun 570'i eşit (`CustomerGroupingJob.cs:17-19`); sözleşme değil, ölçüm. Sunucu her 409'u lisans ve satır Id'siyle günlüğe yazar. Kurtarma: operatör kişiyi WPF'te yeniden birleştirir; damgalı yazım yakınsatır. Risk §11'de. |

## 6. İstemciler

### 6.1 WPF — W-havuz (PR #506'nın üstüne, aynı Velopack sürümünde = Wh)

Yeni davranışların hepsi sunucu sinyaline bağlı. Wh, S1 ya da havuz kipi kapalı bir sunucuyla bugünkü gibi çalışır. Bu yüzden Wh'nin havuz kipi açılmadan önce sahaya çıkması güvenlidir; havuz kipi de Wh'siz açılamaz (§7.2).

**a) `memberIds`**
- `GetBalancePreviewAsync` (`LicenseApiClient.cs:392-399`) ve `ApplyBalanceAsync` (`:441-446`; istek DTO'su `CustomerBalanceApplyRequest`) kişinin GUID'li üye Id'lerini taşır. Kaynak `PaymentPerson.Members`, `PaymentRequestService.cs:1096`'daki süzgeçle; silinmişler dahil (`PaymentPersonSql.cs:24-37`).
- Preview çağrısı (`PaymentRequestService.cs:1034`) kişiyi zaten elinde tutar.
- `ReplayAsync` (`:1315-1362`) yalnız işi bilir; üyeleri çağrı anında `GetPaymentPerson(job.CustomerId)` (`CustomerRepository.cs:607-612`) ile okur.
- Eski sunucu bilinmeyen sorgu parametresini ve gövde alanını yok sayar.

**b) `customer-not-synced` (K2)**
- Preview'da: `:1037-1042`'deki genel `catch`'ten önce `ValidationException { Code: "customer-not-synced" }` yakalanır (kod eşlemesi `LicenseApiClient.ThrowMappedAsync`'te 4xx başlığından). Sonuç `BalanceOutcome` (`:619-630`) içinde yeni `NotSynced` bayrağıdır.
- Apply'da: `ReplayAsync`'in genel kolu (`:1357-1360`) zaten belirsiz bırakıyor; bayrak sonuçla birlikte taşınır.
- İki yerde de:
  - **`MarkNoBalance` yazılmaz**, iş taze ya da anahtarlı-belirsiz kalır;
  - mesaj gitmez;
  - fire-and-forget bir müşteri senkron turu tetiklenir (`WpfCustomerProjectionSyncService.SyncOnceAsync`, `:130`; tek tur kilidi `:39-40` üst üste binmeyi önler).
- `PaymentRequestResult`'a `BalanceNotSynced` eklenir (`:15`). `PaymentResultPresenter.Notify` (`:47-60` yanına) uyarı gösterir: başlık "Henüz eşitlenmedi", metin "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz."
- Banka W2 açısından `BalanceUncertain` gibidir: mesaj gitmediği için beklenti üretilmez.

**c) `client-update-required`.** Wh ve sonrası bu yanıtı yalnız alt sınır ileride yükseltilirse alır. Genel `BalanceUncertain` yolu yeterli; metin değişmez.

**d) Birleştirme notu ("iade o hesaba döner") — üç durum.** Not iki yerde kuruluyor: yerel planın sorusu (`PaymentRequestService.cs:760-768`, `PersonJob.cs:145-146`) ve sunucu izlerinin sorusu (`:1127-1131`, hep `RefundsToOtherRow: true`). `PaymentPrompt`'a `bool? PersonPool` eklenir:
- Sunucu izlerinin sorusunda kaynak, `GET scope` yanıtlarındaki `PersonPool` (§4.3); kesin bilgi.
- Yerel planın sorusunda kaynak, bu oturumda lisans için alınan son preview ya da scope yanıtındaki değer (bellekte, lisans başına); bilinmiyorsa `null`.

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

Havuz kipi açık bir lisansta v0.9.9 hiç yeni yazım yapamaz: 409 `client-update-required` alır, mesajı "Bakiye doğrulanamadı" uyarısıyla engeller (master `PaymentRequestService.cs:680` önizleme `catch`'i). Sebep karışık filoda çift düşüm:
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

| Adım | Ne | Koşul ve doğrulama |
|---|---|---|
| 0 | Bu spec onayı; PR #505 merge | Prod'a yazım yok |
| 1 | **S1** merge ve otomatik deploy | Yayın penceresi dışında (Paz/Pzt/Çar/Per 20:00–01:00 TR değil). `/ready` ve göç (§8.5). Davranış değişmez. |
| 2 | `person-balance verify` → ön kontrol raporu sahibe (§7.3) | Salt okunur; Burak koşar |
| 3 | Gözlem ≥ 1 gün; günlük `verify` işi koşar | Değişmez kural sayıları 0 |
| 4 | **S2** merge ve deploy | Bütün lisanslar satır kipinde, davranış değişmez. Otomatik geri alma S1'e döner (güvenli, §7.4) |
| 5 | **W-havuz** (#506'nın üstüne) merge; **Wh** Velopack sürümü; PC'ler sırayla güncellenir | Satır kipinde Wh = #506 davranışı + Ayır onayı + kart düzeltmesi |
| 6 | Sahip ön kontrolün eksi satırlarını ve kural bozan satırlarını inceler (§7.3) | Her satır için düzeltme ya da "olduğu gibi kalsın" kararı, sahibin onayıyla |
| 7 | Pilot lisansta `person-balance enable --license <id>` önce kuru, sonra `--apply` | Kapı koşulları (§7.2). Prod'a yazım, Burak'ın onayıyla, yayın penceresi dışında |
| 8 | Saha kontrolleri (§8.5) | Gerçek etkiden: DB satırı, yanıt, ekran |
| 9 | Öbür lisanslar tek tek | Her biri için 7 ve 8 |
| 10 | Banka spec düzeltmeleri (§9), docs PR | #505'ten sonra; banka PR5'ten önce |

S2 ile Wh'nin sırası serbesttir: Wh, S1'e ve kipi kapalı S2'ye karşı da doğru çalışır. Kapı (adım 7) ikisini de ister.

### 7.2 Kapı — lisans başına yönetici anahtarı + istemci alt sınırı (mühendislik kararı M1)

- **Anahtar:** `License.PersonBalancePoolSince`. Yalnız CLI yazar: `person-balance enable|disable --license <id> [--apply]`. Kalıp `merge-customer-identities` ve `group-customers` ile aynı (`Program.cs:44-55`), varsayılan kuru koşu.
- **`enable` ön koşulları** (kuru koşu durumlarını sayı olarak basar; biri tutmazsa `--apply` reddedilir):
  1. Son `PersonBalance:ActivePcWindowDays` (varsayılan 14) gün içinde görülmüş ve devre dışı bırakılmamış **her** etkinleştirmenin `AppVersion`'ı ≥ Wh (`Activation.cs:13-20`; `AppVersion` yoksa eski sayılır). Banka spec'i §5-5 ile aynı ölçüt.
  2. Lisansta `verify`'ın D4, D5, D6 ve yetim bacak sayıları 0.
  3. Lisansta eksi satır ve D1 bozulması yok, ya da sahip hepsini inceledi ve `--accept-reviewed` verildi (§7.3).
- **Çalışma anı koruması:** havuz kipinde Wh'den eski istemcinin yeni yazımı 409 `client-update-required` alır (§4.0). Kapı açıldıktan sonra haftalarca kapalı kalmış eski bir PC dönerse para yolunda fail-closed olur ve günlüğe lisans ile sürüm yazılır.
- **`disable`** (kill switch) lisansı satır kipine indirir. Çok bacaklı geçmiş doğru okunmaya ve geri alınmaya devam eder (S1 okuyucuları).

**Neden dinamik kapı değil** (banka §5-5 gibi her istekte "bütün PC'ler Wh+ mı" diye bakmak):
- Banka kapısının güvenli tarafı "sor"dur; kapı kapanınca yalnız soru sorulur.
- Burada kapının kapanması kipi değiştirir. Eski bir PC'nin tek bir heartbeat'i havuzu yayın ortasında satır kipine düşürür: shopper'ın gördüğü bakiye ve kullanılabilir tutar zıplar; iade edilmiş para bir anda yeniden kullanılamaz olur. Güvenli bir geri dönüş tarafı yok.
- Açılış zaten bir insan adımı istiyor (ön kontrol, eksi satır incelemesi).
- Eski PC riskini anahtarın kendisi değil, çalışma anı koruması kapatıyor.

### 7.3 Ön kontrol raporu ve eksi satırlar (M2)

`person-balance verify [--license <id>] [--details]` salt okunurdur ve yalnız sayı basar. `--details` yalnız eksi ve kural bozan satırların `WpfCustomerId`'lerini (GUID; ad, telefon yok) konsola basar; dosyaya yazılmaz. Raporun içeriği:
1. **Davranışı değişecekler:** en az iki üyesinde sıfır olmayan bakiye olan kişi sayısı ve bunların havuz toplamı. Açılış günü kullanılabilir bakiyesi artacak müşteriler.
2. Eksi satır sayısı ve toplamı. Eksi bir satır kişinin havuzunu küçültür (§4.1 örneği).
3. D1 bozulan satırlar (`Balance ≠ Σ Amount`). Bugün bunu çalışırken denetleyen bir şey yok.
4. Silinmiş üyede kalan bakiye (sayı, toplam).
5. `GroupId <> LTRIM(RTRIM(GroupId))` sayısı. Sunucu işlerinin kırpma karşılaştırmasıyla ayrışma riski.
6. Kişi havuzu < 0 olan kişi sayısı.
7. D4, D5, D6 ve yetim bacak sayıları (S1'den önce hepsi 0). D2'nin izi 2. madde (eksi satırlar), D3'ünkü 6. madde.
8. Lisans başına aktif PC sürüm dağılımı (sürüm → adet).
9. İki tablonun satır sayısı (göç süresi kestirimi).

Eksi satırlar ve D1 bozulmaları sahiple tek tek gözden geçirilir. Her biri için ya bir düzeltme seçilir ya da sahip "olduğu gibi kalsın" der (`--accept-reviewed`). Bugünkü artı yazım yolları WPF'in ve panelin "Bakiye Ekle"sidir (iade türüyle yazar); `manual-adjustment` ucunun ekranı yok. D1 bozulmasının düzeltmesi ayrı bir karardır. Bu tasarım eksi satırları kendiliğinden kapatmaz.

### 7.4 Geri dönüş kuralları ve deploy notu

- **S2 deploy'u düşerse** (`/ready` 60 sn) imaj S1'e döner, göç geri alınmaz. S1 bayrağı okumaz; bütün lisanslar satır kipine iner. S1 bacakları okur ve grup olarak geri alır; para kaybı yok. Genişlet/daralt kuralı tutar: veriyi yeni biçimde yazan iş (S2) okuyucudan (S1) sonra çıkıyor.
- **Havuz kipinde sorun:** `person-balance disable`. Veri işlemi gerekmez.
- **WPF'te Wh'den eskiye dönen PC:** havuz kipindeki lisansta 409 `client-update-required` alır (fail-closed).
- **YASAK — S1 öncesi imaja elle sabitleme.** Herhangi bir lisansta havuz kipi bir kez açıldıysa (`SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE DrawGroupId IS NOT NULL` > 0) S1 öncesi imaja dönülmez. Eski kod yalnız çapayı geri alır (`LicensesCustomerBalanceApplyController.cs:456-491`); çapa 0 olabilir ve öbür bacaklardan düşülen para geri dönmez. Ayrıca `scope` ve `transactions/{id}` yalnız çapa tutarını döner.
- `deploy/README.md`'ye, var olan "PR-1 geri dönüş tabanı" notunun biçiminde bir uyarı eklenir:
  > **UYARI — Ortak bakiye S1 tabanı.** `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` dosyasını İÇERMEYEN bir imaja (etiketin kaynağında `git cat-file -e <sha>:OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` başarısızsa o imaj S1 öncesidir), `DrawGroupId` dolu bir defter satırı oluştuktan sonra bu elle yoldan ASLA geçme. Eski reverse yalnız çapayı (tutarı 0 olabilir) geri alır; müşterinin öbür hesaplarından düşülen para geri dönmez. Otomatik geri alma bir önceki imaja döner ve güvenlidir.

### 7.5 #506'daki notun güvenli sırası (M3)

- #506 master'a olduğu gibi girebilir. Notu satır kipinde **doğrudur**.
- #506'yı tek başına taşıyan bir Velopack sürümü gerekirse (ör. banka W2 takvimi) çıkabilir. Bu sürüm Wh değildir; o PC'ler havuz kipi kapısını geçemez.
- W-havuz notu kipe bağlar (§6.1-d). Havuz kipi ancak bütün aktif PC'ler Wh+ iken açılabildiği için (§7.2), notun yanlış olacağı tek durum (havuz kipi + #506'nın kipsiz notu) hiç oluşmaz.
- Notu kaldırıp S2'yi beklemek satır kipinde doğru bir uyarıyı silerdi. Notu olduğu gibi bırakıp havuzu açmak ise operatöre yanlış bilgi verirdi. İkisinden de kaçınılır.

## 8. Doğrulama

### 8.1 `verify` CLI ve günlük iş

- §7.3'teki komut (S1).
- Aynı denetimler Hangfire'da günlük koşar: `person-balance-verify`, `Program.cs:889-1031`'deki tekrarlayan işler bloğuna eklenir.
- Çıktı sayılardır; PII yok. D1, D3 (havuz < 0), D4, D5, D6 ya da yetim bacak sayısı 0 değilse `Admin:AlertEmail`'e yazılır. Aynı durum için günde bir kez; kalıp `BackupRestoreDrillJob.cs:106`.
- Kazanan tasarımın "değişmez kuralı çalışırken denetleyen iş yok" riskini kapatır.

### 8.2 Testcontainers (gerçek SQL Server, `SqlServerContainerFixture`)

InMemory tekil indeks ve jeton yarışını uygulamaz. Bu yüzden şunlar gerçek SQL Server'da kanıtlanır:
1. Aynı kişinin iki farklı üyesinden, farklı anahtarlarla paralel apply; eksi bakiyeli üye varken (A = 100, B = −60, C = 50). Havuz hiçbir an < 0 olmamalı, D1 tutmalı. Haritadaki "paralel apply için gerçek SQL testi yok" boşluğu da kapanır.
2. Aynı anahtarla çok bacaklı apply yarışı: tek bacak kümesi yazılır, kaybeden kazananın sonucunu oynatır.
3. Çok bacaklı grup geri alması yarışı: WPF ∥ WPF ve WPF ∥ panel. Ya hepsi ya hiçbiri (D5); biri 409 `already-reversed` alır.
4. Ayırma senkronu (`GroupId = NULL` partisi) ∥ apply: ayrılan para tutan satırdan "ayırmadan sonra" çekim yok; senkron çakışırsa taze okumayla yeniden uygular.
5. Apply ∥ `CustomerIdentityMergeJob` (grup geri alınır ve sayılır, `:297-304`) ve apply ∥ `--at-twins` birliği.
6. Apply ∥ `group-customers` `ApplyAsync`.
7. K5 panel eksi işlemi ∥ apply: aynı havuzdan iki çekim, havuz ≥ 0.
8. Göç: `GroupId` collation değişimi, sütunda otomatik istatistik varken; filtreli indeksler (`CustomerProjectionUniqueIdentityMigrationTests` deseni).
9. Kanca: senkronun `GroupId` değişikliği bakiye jetonunu ilerletir, apply çakışır.
10. Var olan dört eşzamanlılık testi (`CustomerBalanceConcurrencyTests.cs:43`, `:85`, `:112`, `:194`) satır kipinde yeşil kalır; havuz kipinde de koşturulur.

### 8.3 InMemory ve birim

- `PlanDraw` tablo testleri: çapa önce, silinmiş önce, büyük bakiye önce, Id ordinal eşitliği, eksi satır atlanır, tutar kırpma.
- Preview havuzu; `customer-not-synced`'in iki kolu; `S ⊄ W` kesişimi; `client-update-required`; `member-ids-required`.
- Örnek 1–4'ün birebir satırları; `scope` yalnız çapada; `transactions/{id}` toplamı; Lookup `−Σ`; bacak anahtar → 404.
- Panel: toplanmış liste, `take` grup sayar, `Members`, başka üyenin iadesini iptal (K5), `reversal` → 409 `not-reversible`.
- Shopper: havuz, `Reason` maskesi, silinmiş üyenin dahil olması.
- **Satır kipi regresyonu:** bayrak kapalıyken var olan bakiye testlerinin hepsi değişmeden yeşil. A11 testleri `LicensesCustomerBalanceApplyControllerTests.cs:316-406` ve kopya Id'si `:930-1003`.

### 8.4 WPF

- `memberIds` preview ve apply'a gider, replay dahil.
- `customer-not-synced` → `BalanceNotSynced`; `MarkNoBalance` yazılmaz, iş taze kalır, senkron turu tetiklenir.
- Not, üç durumuyla. `PaymentResultPresenterTests`, `PersonScopePlanTests`, `PaymentJobStoreContractTests` ve `PaymentRequestServiceTests.Person` `RefundsToOtherRow`'u sabitliyor; kip durumuyla genişletilir.
- Ayır onayının metni; bütün tutarlar 0'ken soru yok; çağrı hatasında genel uyarı.
- GUID'siz satırdan açılan kartın sahip satıra düşmesi.

### 8.5 Saha kontrolleri (gerçek etkiden)

- **S1 sonrası:**
  - `__EFMigrationsHistory`'de göç var;
  - iki indeks ve `GroupId` collation'ı `sys.indexes` / `sys.columns`'ta görünüyor;
  - `verify` koşuyor ve sayı basıyor;
  - bir müşteride preview yanıtı öncekiyle aynı, `PersonPool = false`.
- **S2 sonrası (kip kapalı):** aynı müşteride preview, apply ve reverse bugünkü gibi; DB'de `DrawGroupId` `NULL`.
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
| M1 | Havuz kipi lisans başına yönetici anahtarıyla (`PersonBalancePoolSince`, CLI) açılır. Açılışta bütün aktif PC'ler Wh+ olmalı; çalışmada eski istemcinin yeni yazımı 409 `client-update-required` alır. | Dinamik kapının güvenli geri dönüş tarafı yok (§7.2). Fail-closed. |
| M2 | Açılıştan önce salt okunur ön kontrol. Eksi satırlar sahiple incelenir; `enable` eksi satır ya da D1 bozulması varken `--accept-reviewed` olmadan reddeder. | Eksi satır havuzu kilitler (§4.1). |
| M3 | #506 olduğu gibi girebilir; not W-havuz'da kipe bağlanır (üç durum); Wh = #506 + W-havuz; kapı Wh ister. | Not satır kipinde doğru, havuz kipinde yanlış; havuz kipi Wh'siz açılamaz (§7.5). |
| M4 | K2, WPF'in üye listesiyle (`memberIds`) uygulanır. `W ⊄ S` → 409; `P = W ∩ S` (+ silinmiş üyeler). | Sunucu senkron gecikmesini kendi başına göremez. Kesişim, operatörün ayrı gördüğü satırdan çekmeyi engeller. |
| M5 | `GroupId` kolonu BIN2 collation. | WPF ordinal karşılaştırıyor; CI kolonda "WPF ile aynı kişi" iddiası SQL'de tutmazdı. Emsal `IdentityKey`. |
| M6 | Kişi kilidi: apply ve K5 çekimleri kişinin bütün bakiye satırlarının jetonuna dokunur. `GroupId` değişimi kanca ile jetonu ilerletir. `CustomerBalance` `StampVersions`'a girer. | D3 ve "ayırma ile apply" yarışı, var olan F02 jeton kalıbıyla kapanır; açık işlem ve kilit gerekmez. |
| M7 | Çekim sırası: çapa → silinmiş üyeler → canlı üyeler (bakiye büyükten küçüğe, Id ordinal). | Alıcının parası önce kullanılır; ayırmada erişilemez kalabilecek para önce harcanır. Sıra belirlenimli. |
| M8 | `reversal` satırı iptal edilemez (409 `not-reversible`). Bacak, çapasına çözülür. | Çok bacaklı "iptalin iptali" zincirini kapatır. Panel arayüzü zaten sunmuyor. |
| M9 | Yanıtlardaki `UpdatedAt` = son para hareketi. | Kilit dokunuşu "güncellendi" zamanını oynatmaz (kazanan tasarımın 6. riski). |
| M10 | A11 ve `scope` çapa satırı düzeyinde kalır. | Kişi düzeyindeki A11, ayırmadan sonra anahtarı başka kişiye oynatırdı. #506 üye başına scope'u zaten tekilleştiriyor. |

**Açık kalanlar** (bu spec'i bloklamaz, ayrı karar):
- Kimlik birleşmesinin olağan kipte ve senkron kopya yolunda da `GroupId`'leri birleştirmesi (§5.3).
- Kesinleşmiş `NoBalance` işin, havuza sonradan para gelince yeniden sorulması (§11).

## 11. Riskler ve bilerek yapılmayanlar

**Riskler**
1. **Yanlış gruplama artık parayı taşır.** Bugün `GroupId`'nin bakiyeye etkisi yok. Havuzla birlikte yanlış bir birleştirme başkasının iadesini harcatabilir. Yolları: elle birleştirme (keyfi numara, `CustomerRepository.cs:415`), `group-customers`, `--at-twins`, form telefonu. Azaltıcılar:
   - ön kontrol raporunun 1. maddesi açılış günü etkilenenleri listeler;
   - harcanmamış para Ayır ile eksiksiz geri gelir;
   - Ayır onayı dağılımı gösterir.
2. **Shopper gizliliği:** öbür üyelerin tutar ve türleri görünür; serbest metin gizli (K4).
3. **Kalıcı `customer-not-synced`:** damgasız eski `GroupId` iki tarafta farklı kalırsa o kişide ödeme isteği bakiye akışında takılır (§5.5). Ölçümde 0. Günlükte görünür; kurtarma yeniden Birleştir.
4. **Kart ile çekim kümesi farkı:** panel, kart ve Shopper `S`'yi, apply `W ∩ S`'yi kullanır. Senkron penceresinde (≤ 60 sn) gösterilen havuz ile çekilebilir tutar farklı olabilir.
5. **Eski PC dönerse** havuz kipindeki lisansta o PC'den bakiyeli ödeme isteği durur (`client-update-required`); güncelleme gerekir. Bilinçli fail-closed.
6. **Tutarı 0 olan çapalar** ham defterde görünür. Bütün listeler toplanmış döner; toplamayan gelecekteki bir okuyucu (dışa aktarma) "0 TL düşüm" gösterebilir. `AggregateHistory` kullanılmalı.
7. **Kesinleşmiş iş önbelleği:** `NoBalance` olmuş iş tekrar paylaşımda bakiyeyi yeniden sormuyor (`PaymentRequestService.cs:957-966`). Havuza sonradan para gelirse ya da kişiye yeni üye katılırsa o kapsam güncellenmez. Bugünle aynı davranış, ama kişi büyüdükçe daha sık görülür.
8. **Kişi kilidi** aynı kişiye eşzamanlı yazımları sıraya sokar. Üç çakışmadan sonra 503; WPF aynı anahtarla yeniden dener.

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
| **S1** | LiveDeck (sunucu) | Göç (§3.1); `PersonBalance` okuyucuları; `scope`, `transactions/{id}`, Lookup `−Σ`; grup geri alması (WPF ve panel); toplanmış listeler (panel, Shopper); panel `Members[]`; `PersonPool = false` alanları; `CustomerBalance` → `StampVersions`; `GroupId` kancası ve convention testi; `person-balance verify` ve günlük iş; `deploy/README` uyarısı; Testcontainers 3, 8, 9. Davranış değişmez. | 3 gün |
| **S2** | LiveDeck (sunucu) | Kip bayrağı; `ResolveDrawSetAsync` (`memberIds`, `customer-not-synced`); sürüm denetimi (`client-update-required`, UA ayrıştırıcının ortak yardımcıya taşınması); `PlanDraw` ve `DrawAsync`; havuzlu preview ve apply; panel ve Shopper havuz okumaları ve `Reason` maskesi; K5 (elle eksi, iade iptali, üyelik koşulu); `reversal` → `not-reversible`; `person-balance enable/disable`; metrikler; Testcontainers 1, 2, 4–7, 10. | 3,5 gün |
| **W-havuz** | LiveDeck (WPF, #506'nın üstüne) | `memberIds`; `BalanceNotSynced` ve senkron tetikleme; üç durumlu not; Ayır onayı; kart sahip yedeği; `PersonJob` yorumu; testler. Wh Velopack sürümüyle birlikte. | 1,5 gün |
| **D-banka** | LiveDeck (docs) | §9'daki dokuz düzeltme. #505'ten sonra. | 0,25 gün |
| — | OrderDeck-Mobile, OrderDeck-Shopper | Değişiklik yok | 0 |

**Toplam ≈ 8,25 gün.** Kazanan tasarımın tahmini 7 gündü; fark aşılardan geliyor: K2 üye listesi, kapı ve CLI, K5, Ayır onayı, kanca, collation göçü, günlük `verify`.

**Akış:** S1 → (≥ 1 gün, `verify` raporu sahibe) → S2 → W-havuz + Wh (S2'den önce de çıkabilir) → PC'ler Wh+ → sahip eksi satırları onaylar → pilot `enable` → saha kontrolü → öbür lisanslar. D-banka, #505 birleşince, banka PR5'ten önce. Her merge yayın penceresi dışında.

## 13. Tasarım incelemesi — bulgu kararları

Kaynak: 2026-10-11 tasarım karşılaştırması (üç aday, iki hakem). Kazanan: "Kişi havuzu" (hakem puanları 8 ve 7). "T1/D1" = kalıcı `PersonId` tasarımı, "T3/D3" = toplama tasarımı.

| # | Kaynak | Önem | Konu | Karar |
|---|---|---|---|---|
| 1 | Hakem 2, "üçünde ortak" | Ölümcül | Senkron öncesi pencere kalıcı `MarkNoBalance` üretiyor | İşlendi §4.0 (`memberIds`, 409 `customer-not-synced`), §6.1-b, K2 |
| 2 | Hakem 2, "üçünde ortak" | Ölümcül | Karışık filoda aynı mal için çift düşüm | İşlendi §7.2 (kapı + `client-update-required`), §6.2, M1 |
| 3 | Hakem 1 aşısı (T1) | Önemli | `GroupId` değişimi ile apply yarışı, `CustomerBalances.UpdatedAt` dokunuşu | İşlendi §4.11 (SaveChanges kancası: senkron, `group-customers`, `--at-twins`), §4.13, §5.5 |
| 4 | Hakem 1 aşısı (T1) | Önemli | `GroupId` için ordinal collation | İşlendi §3.1 (BIN2), M5 |
| 5 | Hakem 2, D2 | Küçük | Eksi satır havuzu kilitliyor | İşlendi §4.1 örneği, §7.3, `enable` reddi, M2 |
| 6 | Hakem 2, D2 / aşı (D3) | Küçük | Silinmiş üyeden çekilen bacak dönünce erişilemez | İşlendi K3: silinmiş üye `W` üzerinden havuzda (§4.0-4, §4.12, §5.4) |
| 7 | Hakem 1 aşısı, hakem 1 zayıflık 4, hakem 2 | Önemli | S2'den sonra S1 öncesi imaja sabitleme | İşlendi §7.4 (yasak, tespit sorgusu, `deploy/README` notu) |
| 8 | Hakem 1 ve 2 aşısı (T1/D1) | Önemli | Salt okunur `verify` CLI, ön kontrol ve periyodik | İşlendi §7.3, §8.1 (günlük iş + uyarı e-postası) |
| 9 | Hakem 1 ve 2 aşısı (T3/D1/D3) | Önemli | Testcontainers yarış listesi | İşlendi §8.2 (10 madde) |
| 10 | Hakem 1 ve 2 aşısı (T3/T1) | Önemli | Ayır onayı, üye başına kırılım | İşlendi §4.6 `Members[]`, §6.1-e, K1 |
| 11 | Hakem 1 ve 2 aşısı (T3/D1) | Önemli | Shopper `Reason` maskesi | İşlendi §4.10, K4 |
| 12 | Hakem 1 aşısı (T3), zayıflık 2 | Önemli | Paneldeki eksi işlemler satırla sınırlı | İşlendi §4.8, §4.9, K5 |
| 13 | Hakem 1, zayıflık 3 | Küçük | 0 tutarlı çapa gürültüsü; okuma uçları bacakları toplamalı | İşlendi §4.3–§4.10 (`AggregateHistory`), risk 6 |
| 14 | Kazanan tasarım riski 6 | Küçük | `UpdatedAt` kilit dokunuşuyla ilerliyor | İşlendi M9 |
| 15 | Kazanan tasarım riski 9 | Önemli | Değişmez kuralı çalışırken denetleyen iş yok | İşlendi §8.1 |
| 16 | Hakem 1 sahip sorusu 9 | Önemli | #506 notunun sırası | İşlendi §6.1-d, §7.5, M3 |
| 17 | Hakem 1 sahip sorusu 6 | Küçük | Kimlik birleşmesinin grup açığı | Bilerek yapılmadı (§1, §5.3, §10 açık kalanlar) |
| 18 | Hakem 2, D2 (c) | Küçük | Ayırma ile apply yarışı (geri alınabilir) | İşlendi 3 ile; kalan pencere §5.5'te gerekçeli |

Sonuç: iki hakemin bütün aşıları (8 + 7, örtüşenler birleşik) ve kazananı ya da "üçünde ortak"ı ilgilendiren bütün ölümcül bulgular işlendi. Bir bulgu bilerek kapsam dışı bırakıldı (17). Önerilenden farklı ama eşdeğer bir çözümle işlenenler:
- 2: "aynı kişi × kapsamda başka satırda çapa varsa 409" yerine kapı + istemci alt sınırı. #506'nın meşru çok izli birleştirme akışını bozmamak için.
- 3: yalnız senkron ucu yerine SaveChanges kancası. Üç `GroupId` yazarını birden kapsar.
