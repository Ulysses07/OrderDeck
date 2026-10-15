# Yayın ve Etiket Senkronu (Çoklu Bilgisayar, Faz 2) — Tasarım

**Tarih:** 2026-10-13 · **Durum:** sahip kararları alındı (KVKK metin boşaltması: silinmez; aynı makinede geri yüklenen kopya: hemen devralır — §11), tasarım incelemesinin bütün aşıları ve ölümcül bulguları işlendi (§14), ikinci incelemenin 37 bulgusu ve üçüncü incelemenin 20 bulgusu işlendi (§15), uygulama planı bekliyor · **İlgili:** `2026-10-05-coklu-bilgisayar-senkron-design.md` (dal `docs/coklu-bilgisayar-senkron`; bu belge onun "Faz 2" satırının ve kargo kısmının yerini alır), `2026-10-11-ortak-bakiye-design.md`, `2026-10-10-banka-havale-onay-design.md`, PR #506 (ödeme isteği kişi anahtarı), PR #508 (Basılanlar, kod başına tek fiyat)

Kaynaklar: LiveDeck `origin/master` a126ff26. Kod ağacı 04f2b3d1 ile aynı, fark yalnız web sürüm bump'ı; #506 ve #508 dahil. Satır numaraları bu kesite göredir. Sahada v0.9.10 (yerel şema 48). Üç aday tasarım iki hakemle karşılaştırıldı. Hakem 1 "kapanan yayın sunucuya devredilir" (D3, 8,2) tasarımını, hakem 2 "yayın sahipliği + sunucuda doğrulanan komutlar" (D2, 7,5) tasarımını seçti. İkisi aynı mimarinin iki varyantı: canlı yayın sahibinde yerel, bitince sunucuya geçer, sonrası komutla değişir. Bu belge ikisini birleştirir. D1 (çok yazıcılı, istemci saatli birim damgası) elendi. İki hakemin bütün aşıları ve ölümcül bulguları §14'te tek tek eşlendi.

## 1. Amaç ve kapsam

**Sorun (bugün)**
- Yayın ve etiket yalnız gönderiliyor, başka bilgisayara inmiyor (`OrderDeck.App/Services/Sync/SessionOrderSyncService.cs:15-17` "Push-only"). Sunucuda Orders ve StreamSessions için çekme ucu yok.
- Sunucu sipariş satırını bütünüyle eziyor, son gelen kazanıyor (`OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs:271-294`; oturumda `:89-96`). Bayat bir tam satır başka bilgisayarın iptalini geri getirir ve stok defterini de geri sarar. Bayat oturum gönderimi `EndedAt`'i NULL'layıp yayını yeniden "açar" (`:92`).
- Katalog bilmeyen eski istemcinin güncellemesi `ProductId`'yi NULL'luyor (`:285-286`).
- `ParentLabelId`, `ShipmentId`, `PrintBatchId`, `PrintSeq`, `PriceCorrectedAt` ve `PaidPrice` yalnız yerelde duruyor. 048'in dört kolonu için `OrderDeck.Core/Storage/Migrations/048_label_print_batch.sql:28`; ParentLabelId ve ShipmentId için gönderim eşlemesi (`SessionOrderSyncService.cs:121-142`) ve sunucunun `SyncOrderItem`'ı (`LicensesSessionsSyncController.cs:156`), ikisinde de bu alanlar yok. Başka bilgisayar yedek ilişkisini, "dekontla ödendi" bilgisini ve kâğıt grubunu bilemez.
- Müşteri toplamları bilgisayar başına tutulan fark sayaçları (`OrderDeck.Core/Storage/Repositories/CustomerRepository.cs:311-321`). Sunucudan inen müşteri 0/0 ile açılıyor (`CustomerSyncRepository.cs:561`).
- Kargo dosyasının tutarı da WPF'te fark sayacı. Dosyaya bağlandıktan sonra iptal edilen etiket tutarda kalıyor: `LabelService.Cancel` dosyaya dokunmuyor (`OrderDeck.Core/Sales/LabelService.cs:125-162`), `ShipmentService.AttachLabels` yalnız ekliyor (`ShipmentService.cs:73-102`).
- Ödeme isteğinde bilgisayarlar arası koruma yalnız bakiye tarafında. Kişi kilidi süreç içi (`OrderDeck.Core/Customers/PaymentPersonGate.cs:30-44`), teslim kaydı yerel. Kargo ücreti ve eşiği bilgisayarın kendi ayarı (`OrderDeck.Core/Settings/AppSettings.cs:50`, `:261-274`).

**Amaç**
1. Her bilgisayar bütün bilgisayarların **bitmiş** yayınlarını ve etiketlerini görür. Toplamlar, raporlar, müşteri kartı ve ödeme tutarı aynı veriden çıkar.
2. Yayın bittikten sonra her bilgisayar o yayının etiketlerinde şunları yapabilir: iptal ve iptali geri alma, kodun yayındaki fiyatını düzeltme, ilk baskı ve yeniden baskı, yedek ekleme ve onaylama, kargo dosyasına bağlama ve kargo kararı, ödeme isteme.
3. Canlı yayın onu açan bilgisayarda bugünkü gibi yerel ve çevrimdışı çalışır. Başka bilgisayar canlı yayına dokunamaz; yalnız "PC-A'da canlı" başlığını görür.
4. Para değişmezleri bilgisayarlar arasında korunur: bir yayında bir kodun tek fiyatı (#508 F1–F3); kişi×kapsam başına tek eşzamanlı istek ve tek geçerli düşüm; kargo eşiği tek ayardan.
5. Karışık sürümde ve sunucu geri alma penceresinde sessiz ayrışma olmaz: işlem ya doğru çalışır ya da görünür biçimde reddedilir.

**Kapsam dışı**
- Başka bilgisayarın canlı yayınını düzenlemek ya da etiketlerini yayın sürerken görmek (sahip kararı Y1).
- Çekilişler. Açılıştaki `CancelAllOrphaned` (`OrderDeck.App/AppHost.cs:676`) yerel kalır.
- Dekont (Payment) satırlarının bilgisayarlar arası inişi. Bu, banka spec'inin W3'ü (`2026-10-10-banka-havale-onay-design.md` §8).
- PaymentJob durum makinesinin senkronu. Yerel kalır; bilgisayarlar arası koordinasyonu sunucudaki istek kaydı sağlar (§6.4).
- Panel ve Shopper istemci değişikliği. Sözleşme aynı; yeni alanları yok sayarlar. Panel `GET sessions` toplamının `PrintedAt`'e bakmaması ve Shopper süzgeçleri ayrı iş.
- Kargo dosyasından elle etiket çıkarma arayüzü. Bağ çözme yalnız kurallarla olur (§7.2).
- Yayın başlığı ve notu düzenleme (bugün de yolu yok).

## 2. Kavramlar

- **Kopya kimliği (`ReplicaId`):** yerel veritabanına bağlı GUID (`_meta.ReplicaId`, göç 049). `AppSettings.InstallationId` (`AppSettings.cs:124`) kullanılmaz: o ayar dosyasına bağlı; sahiplik ise canlı kuyruğu tutan veritabanı dosyasını izlemeli. Yedekten geri yüklemede yenilenir (§8.7).
- **Yayın hâlleri** (sunucu satırına göre). Mod On'da her hâlde fiyat, iptal ve yedek birimlerinin tek yazıcısı vardır (DM1). Kargo bağı tablonun dışındadır: mod On'da her hâlde, canlı oturum dahil, yalnız sunucu komutu yazar; hiçbir push yazamaz (K4).

  | Hâl | Sunucu | Birim yazıcısı | Başka bilgisayarda |
  |---|---|---|---|
  | Canlı | `EndedAt IS NULL` | yalnız sahip kopya (push) | yalnız başlık: "PC-A'da canlı" |
  | Devrediliyor | `EndedAt` dolu, `ReleasedAt IS NULL` | yalnız sahip kopya (push) | başlık: "PC-A'da yayın bitti, N etiket gönderilmeyi bekliyor"; devralınamaz |
  | Devredilmiş | `ReleasedAt` dolu | yalnız sunucu komutu (sahip dahil herkes) | tam satırlar, akıştan |

  Bilinçli istisnalar: devredilmiş yayına push'tan **ekleme** ve **baskı doldurma** (K3) kabul edilir; force-end ile kapanmış yayında sahibin push'u K2'ye göre yargılanır (K5 istisnası, §4.2). Mod Off'ta tablo geçerli değildir: her bilgisayar kendi yayınlarını bugünkü gibi push'la yazar.

  Force-end yalnız **canlı** hâldeki oturuma uygulanır. Devrediliyor hâlindeki oturumun etiketleri sahipte bekliyor demektir; başka bilgisayar onu devralamaz (§4.6, §5.7).

- **Sahip:** yayını açan kopya (`StreamSessions.OwnerReplicaId`). NULL, yayının eski sürümde açıldığı anlamına gelir (miras).
- **Devir (release):** sunucunun yayını sahipten alması. İki yolla olur. (a) Sahibin `Release=true` taşıyan kapanış gönderimi; bu yalnız yayının etiket kuyruğu boşken gönderilir (§5.2). (b) Zorla bitirme: canlı oturumda `force-end` komutu (§4.6), sahibi hiç dönmeyecek oturumda Burak'ın `sales-sync force-end` CLI'ı (§4.10). İkisi de `ForceEndedBy` yazar. Devir tek işlemde `ReleasedAt` yazar ve yayının bütün siparişlerine `SessionReleasedAt` basar; siparişler değişiklik akışına o anda girer.
- **Yerel otorite (`StreamSession.Authority`):** `local` ise satırı bu bilgisayar yazar. `server` ise yalnız komut yanıtı ve akış yazar; yerel yazım tetikleyiciyle ABORT edilir. Kendi yayını ancak **mod On'dayken** (`SalesSyncState.Mode = 1`) devir sunucudan **görülünce** `server` olur: ya kapanış yanıtında ya da akışta `ReleasedAt` gelince. O ana kadar yereldir (§5.2). Mod Off'ta yalnız `ReleasedAt` saklanır, `Authority` hep `local` kalır; mod On'a geçişte §8.4 adım 3 çevirir. Yerelde hâlâ açık (aktif) kendi yayını force-end görse de operatör bitirene kadar `local` kalır (§5.4).
- **Komut:** devredilmiş yayının etiketini ya da kargo dosyasını değiştiren istek (§4.6). Sunucuda doğrulanır, tek işlemde uygulanır, `CommandId` ile idempotenttir. Beklenen satır sürümlerini taşır (iyimser eşzamanlılık). Sonuç yanıtla bu bilgisayara, akışla öbürlerine iner.
- **Birim ve sunucu damgası:** birlikte yazılan alanlar. Birimler:
  - fiyat {Price, PriceCorrectedAt, PaidPrice}
  - iptal {CancelledAt, CancelReason}
  - yedek {IsTentativeBackup}
  - kargo bağı {ShipmentId}
  - baskı {PrintedAt, PrintBatchId, PrintSeq}

  Komutun değiştirdiği birime sunucu saatiyle `PriceSetAt`, `CancelSetAt`, `BackupSetAt` ya da `ShipmentSetAt` basılır. `uncancel` de iptal birimini değiştirir; `CancelSetAt` basar. Hiçbir yol damgayı silmez. Damga "bu birime komut dokundu" demektir, birimin güncel değerini söylemez; güncel değere bakan kural (K3) damgayla birlikte değeri de okur. Damgalı birimi bayat push değiştiremez (§4.2 K2). Tek istisna, satırın güncel sürümünü gördüğünü kanıtlayan v2 push'udur (`baseVersion = Version`): o istemci komutun sonucunu görmüş ve yeni bir karar vermiştir. İstemci saati hiçbir çakışma kararında kullanılmaz.
- **Oluşum alanları:** SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, AddedAt, IsShippingFee, IsBackupPromoted, ProductId, ProductVariantId, ParentOrderId. Yalnız eklemede yazılırlar; her biçimde ve her kipte. CustomerId'yi yalnız sunucudaki birleştirici taşır.
- **Mod kapısı (`Licenses.SalesSyncMode`):** lisans başına Off ya da On.
  - Off: bugünkü davranış. Ws ek olarak yeni alanları gönderir, satış ve ödeme geçmişini doldurur, ödeme kayıtlarını kirasız yazar ve Y5'i yerel yolda uygular (§7.4).
  - On: akış, komutlar, kargonun sunucuya geçmesi, lisans geneli kargo ayarı ve ödeme kirası birlikte açılır.
  - Yalnız CLI açar; koşullar §4.10'da. Açıkken eski sürümün yazımı sessizce değil görünür biçimde reddedilir.
- **PersonKey:** sunucunun kişi anahtarı. `a`, sahip satırın asıl kaydıdır (`CustomerIdResolver.cs:49`). `a`'nın `GroupId`'si boş değilse anahtar `g:{GroupId}` (ham değer, ordinal karşılaştırma), boşsa `c:{a hex}`. Sahip GUID'siz bir yerel kimlikse (projeksiyonu yok, §6.4) `r:{ham Id}`. Bu, yereldeki `CustomerGroup.PersonKey` ile aynı sınıflamadır (`OrderDeck.Core/Customers/CustomerGroup.cs:21-22`; önekler orada `grp:`/`id:`). Ortak bakiye §2'deki `S(a)` ile aynı kişiyi gösterir. SQL karşılaştırması harf ve sondaki boşluk farkını yutabildiği için indeks ve kilit anahtarın SHA-256 özetiyle (`PersonKeyHash binary(32)`) çalışır; metin yalnız bellekte ordinal yeniden denetim içindir (§3.1).
- **PersonKey kirası:** kişi×kapsam başına, bilgisayarlar arası ödeme isteği kirası (`payment-requests/begin|finish`, §6.4). Yerel `PaymentPersonGate`'in sunucu ikizidir. Anahtar PersonKey'dir; canlı kira (d) ve önceki istek (e) aramalarında ayrıca üye kümesi kesişimi denetlenir ve istemcinin üye kümesi sunucudaki grubu kapsamıyorsa istek reddedilir (`members-stale`). Böylece grup senkronu gecikse bile iki bilgisayar aynı kişiyi bulur.
- **Ön gönderim:** sunucu toplamıyla karşılaştırmadan ya da sunucuda satır seçen bir komuttan önce bu bilgisayarın ilgili gönderim kuyruklarının (bekleyen komutlar, müşteri, yayın ve etiket) sınırlı sürede boşaltılması. Deseni `SyncFlushService.cs:81-97`. Zaman aşımı tek başına engel değildir; karar sunucu karşılaştırmasınındır (§6.4).
- **Bekleyen komut:** çevrimdışı kuyruğa yalnız iki komut girer: devredilmiş yayından kalan kuyruk etiketinin iptali ve ilk baskısı (§5.5). Yerel okuyucular bunları uygulanmış sayar (örtü).
- **Ws:** bu spec'in WPF değişikliklerini (W1, W2 ve W3'ün **hepsini**) taşıyan ilk Velopack sürümü (öneri v0.9.11, yerel şema 49). Sunucudaki karşılığı `SalesSync:MinClientVersion`; Ws yayımlanınca Burak yazar (§8.1). Boşsa mod açılamaz ve mod On'da komut ve kira uçları 409 `client-update-required` döner (fail-closed). İkinci kilit: `hello`'daki `featureLevel` yalnız W3'ü içeren derlemede 3'tür; mod kapısı her kopyada 3 ister (§4.10-2).
- **Görülen kip (`SeenMode`):** Ws'nin her v2 isteği o anda uyguladığı kipi (0/1) taşır; sunucu bunu `WpfReplicas.SeenMode`'a yazar. Mod geçişlerinde iki yönde kullanılır: açılış penceresinde Off kurallarıyla verilmiş kargo kararlarının kaybolmaması (§8.4) ve eski sürüme dönüşten önce PC'nin Off geçişini bitirdiğinin kanıtı (§8.6).
  - İstemci 0'ı ancak Off turu tamamen bitince bildirir: yerel `Mode = 0` ∧ `PendingSalesCommand` boş (§8.5). Aksi hâlde 1 bildirir.
  - Sunucu `SeenMode = 0`'a yalnız **açılış penceresindeki kopya** için Off kurallarını uygular (§4.0). Yedekten geri yüklenmiş ya da mod açıldıktan sonra doğmuş kopyanın 0'ı On kurallarıyla yargılanır: geri yüklenen veritabanının `SalesSyncState`'i yedekten gelir ve Off görünebilir (§8.7).

## 3. Veri modeli

### 3.1 Sunucu (EF göçleri; yalnız ekleyici; `Program.cs:864-872` açılışta uygular)

| Tablo | Değişiklik | PR | Gerekçe |
|---|---|---|---|
| `StreamSessions` | `OwnerReplicaId uniqueidentifier NULL`, `OwnerMachineName nvarchar(128) NULL`, `ForceEndedBy nvarchar(64) NULL` (`cli` ya da ReplicaId; yalnız zorla bitirme yazar), `ReleasedAt datetimeoffset NULL`, `UnsentLabelCount int NULL` | S1 | Sahiplik ve devir (§2). `UnsentLabelCount`, sahibin `Release = false` kapanış gönderiminin bildirdiği gönderilmemiş etiket sayısıdır (§4.1); devrediliyor çipi ve CLI onu gösterir. |
| `StreamSessions` | `ChangeSeq rowversion` + `(LicenseId, ChangeSeq)`; filtreli `(LicenseId)` WHERE `[ReleasedAt] IS NULL` | S1 | Akış; canlı ve devrediliyor listesi (§4.5). |
| `Orders` | `ParentOrderId`, `ShipmentId` (uniqueidentifier NULL), `PrintBatchId nvarchar(40)`, `PrintSeq int`, `PriceCorrectedAt datetimeoffset`, `PaidPrice decimal(18,2)`, `OriginReplicaId uniqueidentifier` | S1 | Yerelde kalan 048 alanları ve yedek bağı (§1). |
| `Orders` | `SessionReleasedAt datetimeoffset NULL` | S1 | Akış süzgeci; devirde ve eklemede dolar (DM3). |
| `Orders` | `PriceSetAt`, `CancelSetAt`, `BackupSetAt`, `ShipmentSetAt` (datetimeoffset NULL, yalnız komut basar, sunucu saati) | S1 | K2: komut kararını hiçbir push geri alamaz. S1'de kolon ve kural, S2'de yazıcı. |
| `Orders` | `TextScrubbedAt datetimeoffset NULL` | S1 | KVKK (§9). |
| `Orders` | `ChangeSeq rowversion` + `(LicenseId, ChangeSeq)`; filtreli `(LicenseId, ShipmentId)` WHERE `[ShipmentId] IS NOT NULL` | S1 | Akış; dosya türetme. |
| `Orders` | `SyncVersion` (`LicenseDbContext.cs:488`) değişmez, telde `Version` adıyla açılır | S1 | Komutların beklenen sürümü. |
| `Shipments` | `ChangeSeq rowversion` + indeks; `Version int` jeton; `AmountDerivedAt datetimeoffset NULL`; `CommandedAt datetimeoffset NULL`; `OriginReplicaId uniqueidentifier NULL` | S1 | `AmountDerivedAt` doluysa tutar bağlı siparişlerden türetilir (DM7). `OriginReplicaId` mod Off'ta dosyanın tek yazıcısıdır: v2 `shipments/sync`, `sales/backfill` ve dosya açan `attach-shipment` boşsa yazar; `disable --apply` Shipped olmayan dosyada onu bağlı etiketlerin sahibine göre yeniden yazar (§4.10). Mod kapısı (§4.10-6) ve `disable` turu (§8.5) bunu okur. |
| `Licenses` | `SalesSyncMode tinyint NOT NULL DEFAULT 0`, `SalesSyncModeChangedAt` | S1 | Mod kapısı. S1 okur, CLI S3'te gelir. |
| `SalesLegacyTraces` (yeni) | `LicenseId` PK (FK, cascade), `LastLegacySalesPushAt datetimeoffset NOT NULL` | S1 | Protokolsüz istek izi (iki kipte yazılır, §4.0). Licenses satırında değil: oraya yapılan her UPDATE `Licenses.RowVersion`'ı (eşzamanlılık jetonu) ilerletir ve License'ı okuyup kaydeden bugünkü yollar (WPF payment-account, panel shopper-code) eski sürümün saatlik iziyle yarışıp 500 alırdı. |
| `LicenseShippingSettings` (yeni) | `LicenseId` PK, `FreeShippingThreshold decimal(18,2) NULL`, `ShippingFee decimal(18,2) NULL`, `Version int` jeton, `UpdatedAt`, `UpdatedBy nvarchar(64)` (`cli` ya da ReplicaId) | S2 | Sahip kararı Y4: tek, lisans geneli ayar (§6.2). |
| `WpfReplicas` (yeni) | PK `(LicenseId, ReplicaId)`, `MachineName`, `HardwareFingerprint` (ilk yazan kazanır), `HardwareFingerprintConflictAt datetimeoffset NULL` (aynı ReplicaId farklı parmak iziyle hello gönderdiğinde bir kez yazılır, hiç silinmez; §8.7), `ClientVersion nvarchar(32)`, `Protocol tinyint`, `FeatureLevel tinyint`, `SeenMode tinyint NULL`, `SeenModeAt`, `SeenModeOnAt` (kopyanın 1 bildirdiği ilk an), `PreviousReplicaId`, `FirstSeenAt`, `LastSeenAt`, `BackfillCompletedAt`, `PaymentHistoryCompletedAt`, `BackfillDivergedLocal int`, `BackfillDivergedServer int` (§4.3), `ReportedThreshold`/`ReportedFee decimal NULL`; indeks `(LicenseId, LastSeenAt)` | S1 | Kalp atışı (sahip sessizliği, §4.6), mod kapısı (§4.10), kopya veritabanı tespiti (§8.7), kip geçişi kanıtı ve açılış penceresi (§4.0, §8.4, §8.6). PK lisans başına: lisans değiştiren PC yeni lisansta yeni satır açar, eski lisanstaki sessizlik denetimi bozulmaz. |
| `SalesCommandLogs` (yeni) | `CommandId` PK, `LicenseId`, `ReplicaId`, `Kind nvarchar(32)`, `RequestHash binary(32)`, `Status`, `ResultJson`, `CreatedAt` | S2 | İdempotency ve denetim izi. `ResultJson` yalnız Id, sürüm ve tutar taşır; ad ya da mesaj metni **taşımaz** (§9). 90 gün. |
| `PaymentRequestRecords` (yeni) | `Id` PK (istemcinin `recordId`'si), `LicenseId`, `PersonKey nvarchar(80)` (`Latin1_General_100_BIN2`), `PersonKeyHash binary(32)`, `ScopeKey nvarchar(128)`, `OwnerWpfCustomerId nvarchar(64)` (GUID'siz sahip için ham Id), `ReplicaId`, `MachineName`, `Kind tinyint {Lease, Record}`, `State tinyint {Sending, Delivered, Unconfirmed, Abandoned}`, `ProductTotal`, `TotalBeforeBalance`, `NetAmount`, `AppliedAmount`, `FullyFromBalance`, `ShippingSettingsVersion`, `RequestHash binary(32)`, `StartedAt`, `LeaseUntil`, `FinishedAt`, `Version int` jeton | S3 | Kira ve bilgisayarlar arası teslim kaydı (§6.4). `Record`: kirasız, sonradan yazılan kayıt (ödeme geçmişi geri doldurması ve mod Off, §6.4). |
| `PaymentRequestRecords` | Filtreli **tekil** `(LicenseId, PersonKeyHash, ScopeKey)` WHERE `[State] = 0` (Sending); `(LicenseId, PersonKeyHash, ScopeKey, StartedAt)` | S3 | DM6: kişi×kapsamda tek canlı kira. Yarışın hakemi indeks; özet üzerinde olduğu için harman ve sondaki boşluk karşılaştırmayı bozmaz. Tekil ihlal 409 `in-progress-elsewhere`'e çevrilir. |
| `PaymentRequestMembers` (yeni) | `(RecordId, WpfCustomerId)` PK, `LicenseId`, `ScopeKey`; indeks `(LicenseId, WpfCustomerId, ScopeKey)` | S3 | Üye kesişimi denetimi (§6.4-d, e). `WpfCustomerId` ham Id'dir (GUID'siz üyeler dahil). |
| `PaymentRequestRecordOrders` (yeni) | `(RecordId, OrderId)` PK, `LicenseId` | S3 | İsteğin kapsadığı siparişler. "Bu istek bu etiketi içeriyor mu" sorusu (`Covers`) uzak kayıtta saat karşılaştırması yerine bununla cevaplanır (§6.4). |

Göç notları
- `rowversion` eklemek tablonun her satırını yazar. Prod SQL Server Express'te `ONLINE` indeks işlemi yok (ortak bakiye §3.1). S1'den önce Burak prod'da Orders, StreamSessions ve Shipments satır sayılarını salt okunur bir sorguyla alır; süre `restore-verify` kopyasında ölçülür.
- **Ölçüt (iki ayrı sınır).** (1) `Migrate()` komutları varsayılan 30 sn `CommandTimeout` ile koşar (`Program.cs:121`'deki `UseSqlServer`'da da bağlantı dizesinde de ayar yok); Orders'a `rowversion` ekleyen `ALTER` tek bir komuttur ve 30 sn'yi aşarsa zaman aşımına düşer, göç geri sarılır, imaj geri alınır ve S1 hiç deploy olamaz. (2) Göçün toplamı, soğuk açılış dahil, deploy'un 60 sn'lik `/ready` yoklamasına sığmalı; aşarsa geri alma betiği konteyneri göç ortasında durdurur ve SQL Server yarım DDL'i geri sararken Orders şema kilidi altında kalır (geri alma süresince kesinti; veri kaybı yok). Bu yüzden ölçülen en uzun tek komut ≈ 20 sn'nin, göçün toplamı ≈ 40 sn'nin altında olmalı. Aşılırsa `rowversion` ve indeksler ayrı bir göçe ayrılır ve o göç yayın penceresi dışında, bakım deploy'unda çıkar. Komut zaman aşımını yükseltmek çözüm değil: (2)'deki riski büyütür.
- Geri doldurma göçte yapılmaz; onu `sales-release-repair` işi yapar (§4.9).
- `Order.cs:87-89`'daki "rowversion değil, Postgres planı yüzünden" notu: müşteri projeksiyonu aynı bağımlılığı zaten aldı (`LicenseDbContext.cs:704-711`). Postgres göçü ikisini birlikte çözer (§12).

### 3.2 Yerel SQLite — `049_sales_sync.sql` (şema 49)

Kurallar
- Göç yalnız ekleyicidir. Tetikleyiciler yalnız yerleşik SQL kullanır (`045_customer_unit_stamps.sql:61-70` ve 048 kuralı).
- Önceki sürüm kolonları adla okuduğu için şema 49'da çalışır. Prova: `Migration049RehearsalTests`, `Migration045RehearsalTests` deseninde.
- **Mod Off'ta 049'un ABORT tetikleyicilerinin hiçbiri etkili olmaz:** `Authority` hep `local` kalır (devir görülse de; §2, §5.2), `SalesSyncState.Mode` 0'dır. Davranıştaki farklar kuyruğa alma tetikleyicisi ve Y5'in yerel yolu (§7.4).

Kolonlar ve tablolar
- `_meta.ReplicaId TEXT`. Göç `lower(hex(randomblob(16)))` ile tohumlar.
- `StreamSession`:
  - `Origin TEXT NOT NULL DEFAULT 'local'` (`local` ya da `remote`; `remote` = akıştan indi)
  - `Authority TEXT NOT NULL DEFAULT 'local'` (`local` ya da `server`)
  - `OwnerMachineName TEXT`, `ReleasedAt INTEGER`
- `Label.ServerVersion INTEGER` ve `Shipment.ServerVersion INTEGER`: sunucunun `Version`'ı, komutun beklenen sürümü ve v2 push'unun `baseVersion`'ı.
- `Shipment.OriginReplicaId TEXT`: sunucudaki karşılığının aynası; yerelde açılan dosyada bu kopya.
- `RemoteLiveSession(Id TEXT PK, Title, StartedAt, EndedAt, OwnerMachineName, OwnerLastSeenAt, State)`: başka bilgisayarın canlı ya da devrediliyor yayını. Bu satırlar `StreamSession`'a **hiç girmez**. Böylece `GetActive` (`SessionRepository.cs:67-74`, koşulu "`EndedAt IS NULL` olan herhangi bir satır") ve eski sürüme dönüşte `StartStream` ile açılıştaki kurtarma ekranı (`StartupFlow.cs:102-118`) yanılmaz.
- `SalesSyncState(Id=1, Mode, ModeSeenAt, ShippingVersion, FreeShippingThreshold, ShippingFee, ScrubOrderText, FirstCatchUpAt, AggregatesDerivedAt, ShipmentsDerivedAt, PaymentHistorySentAt)`.
- `PendingPaymentRecord(RecordId TEXT PK, Kind TEXT, PayloadJson, CreatedAt, Attempts, LastError)`: gönderilemeyen `finish` ve mod Off kayıtları (§6.4). İmleç `SyncCursor('payment-history-out', LicenseKey)`.
- `PendingSalesCommand(CommandId TEXT PK, Kind TEXT, PayloadJson, CreatedAt, Attempts, LastError)` ve `PendingSalesCommandLabel(LabelId, CommandId, PK(LabelId, CommandId))`. `Kind` yalnız `cancel` ya da `mark-printed` olabilir (§5.5).
- `SalesFeedFailure(Kind, ItemId, Attempts, LastError, SkippedAt)`: `CustomerFeedFailure`'ın ikizi (U10).
- `SalesConflictLog(Id, At, LabelId, Kind, Reason)`: yalnız Id taşır, kişisel veri taşımaz.
- İmleçler `SyncCursor('sales-changes-in', LicenseKey)` ve `SyncCursor('sales-backfill-out', LicenseKey)`. 038 şeması değişmez; satır yoksa 0 sayılır.
- `Customer`: şema değişmez. `TotalAmount` ve `TotalLabelsPrinted` artık etiketlerden türetilmiş değer anlamına gelir (§6.1).

Tetikleyiciler. Hepsinde `NOT EXISTS (SELECT 1 FROM SyncApplyGuard)` koşulu var; akış uygulaması bu yüzden onları susturur (`OrderDeck.Core/Storage/SyncApplyScope.cs:29-75`).

| Ad | Olay | Koşul | Etki |
|---|---|---|---|
| `TR_Label_Enqueue` | AFTER UPDATE OF Price, CancelledAt, CancelReason, PrintedAt, PrintBatchId, PrintSeq, IsTentativeBackup, PriceCorrectedAt, PaidPrice, ShipmentId, ParentLabelId | değer değişti ∧ `NEW.Revision = OLD.Revision` | `SyncedAt = NULL`, `Revision + 1`; CancelledAt ya da IsTentativeBackup değiştiyse ayrıca `StockSyncedAt = NULL` |
| `TR_Label_ServerAuthority_U` | BEFORE UPDATE OF birim ve oluşum kolonları (**CustomerId hariç**) | oturumun `Authority = 'server'` | `RAISE(ABORT, 'sales-server-authority')` |
| `TR_Label_ServerAuthority_I` | BEFORE INSERT | oturumun `Authority = 'server'` | ABORT |
| `TR_Label_ShipmentLink` | BEFORE UPDATE OF ShipmentId | `SalesSyncState.Mode = 1` | ABORT `'sales-shipment-server-owned'` |
| `TR_Shipment_ServerOwned_I` ve `_U` | BEFORE INSERT ya da UPDATE OF Status, HeldAt, ShippedAt, CumulativeAmount | `Mode = 1` | ABORT |
| `TR_Shipment_Enqueue` | AFTER UPDATE OF aynı kolonlar | `Mode = 0` ∧ Revision değişmedi | kuyruğa alma |
| `TR_StreamSession_ServerAuthority` | BEFORE UPDATE OF EndedAt, Title, Platforms, Notes | `OLD.Authority = 'server'` | ABORT |
| `TR_StreamSession_Enqueue` | AFTER UPDATE OF aynı kolonlar | Revision değişmedi | kuyruğa alma |

Notlar
- **Kuyruğa alma tetikleyicisi** müşteri tarafındaki 045 kararının etiketteki karşılığıdır (`045_customer_unit_stamps.sql:11-16`): Label'a yazan çok sayıda yol var ve birini unutmak, o değişikliğin hiçbir yere gitmemesi demek. Bugünkü elle `SyncedAt=NULL` yazımları kalır; Revision'ı kendileri artırdıkları için tetikleyici onlarda etkisizdir, çift artış olmaz. Kuyruğa almayan tek yol `ShipmentRepository.AttachLabel` (`ShipmentRepository.cs:181-187`); bu yol artık kuyruğa girer.
- **ABORT tetikleyicileri** unutulmuş bir yazma yolunu sessiz ayrışma yerine görünür hataya çevirir.
- **Canlı bilgisayarın yolları kırılmaz.**
  - Kendi canlı oturumu `Authority = 'local'`.
  - Mod On'da canlı bilgisayar kargo dosyasına yazmaz. `CorrectPrices` dosya bloğunu (`LabelService.cs:414-421`) ve `Cancel`/`Uncancel`'ın Y5 dosya bloğunu (§7.4) atlar, dekontun kargo adımı komuttur (§5.1, §5.3).
  - CustomerId tetikleyicilerin dışında tutulur. Yerel yeniden anahtarlama (`CustomerSyncRepository.cs:477-499`) her durumda yazabilir.
- **Şema koruma testi:** Label, Shipment ve StreamSession'a yazan her SQL bir listede tutulur. Liste dışı bir yazım testte yakalanır (`CustomerSyncRepositoryTests.cs:650` deseni).

### 3.3 Değişmezler (DM)

- **DM1.** Mod On'da bir yayının her hâlinde fiyat, iptal ve yedek birimlerinin tek yazıcısı vardır (§2 tablosu). Kargo bağının yazıcısı her hâlde, canlı oturum dahil, sunucu komutudur (K4). İstisnalar yazılıdır: devredilmiş yayına ekleme, K3 baskı doldurması ve force-end sonrası sahibin K2 ile yargılanan push'u.
- **DM2.** Damgalı birim yalnız komutla değişir.
- **DM3.** Devredilmiş yayının siparişi akıştan kaçmaz. `SessionReleasedAt` ya devir anında ya da eklemede dolar; onarım işi kalanları kapatır.
- **DM4.** Oluşum alanları eklemeden sonra değişmez.
- **DM5.** Bir yayında bir kodun kargoya verilmemiş, iptal edilmemiş, kargo ücreti olmayan etiketlerinin tek fiyatı vardır (#508 F1). Devredilmiş yayında bunu sunucu denetler.
- **DM6.** Kişi×kapsamda aynı anda en çok bir `Sending` kira olur. Mod On'da bir müşteri satırı×kapsamda yeni yazımla en çok bir geri alınmamış düşüm oluşur.
- **DM7.** `AmountDerivedAt` dolu ve Shipped olmayan dosyanın tutarı, bağlı, iptal edilmemiş ve geçici yedek olmayan siparişlerin fiyat toplamıdır. İptal bağı çözmez; tutardan düşürür. Shipped dosyanın tutarı kargoya verildiği anda donar.
- **DM8.** Akış öğesini uygulamak yerel gönderim kuyruğuna dokunmaz: `SyncedAt` ve `Revision` değişmez. F05'in karşılaştır-ve-yaz onayı (`LabelRepository.cs:386-391`) bozulmaz, yankı oluşmaz.

## 4. Sunucu

### 4.0 Ortak

- Bütün uçlar bugünkü desende `Bearer-Customer` ve lisans sahipliği denetimiyle çalışır (`LicensesSessionsSyncController.cs:68-71`).
- v2 isteği `ReplicaId`, `Protocol = 2` ve `SeenMode` taşır. Sunucu her v2 isteğinde `WpfReplicas.LastSeenAt`'i ilerletir (kalp atışı) ve `SeenMode`'u yazar; 1 geldiğinde `SeenModeOnAt` boşsa ya da `SalesSyncModeChangedAt`'ten eskiyse onu da yazar. Kopya satırı lisans başınadır (§3.1).
- **Açılış penceresindeki kopya.** Mod On'da `SeenMode = 0` bildiren v2 isteği yalnız şu dört koşul birlikte tutarsa Off kurallarıyla yargılanır (K4 ve K5-ii istisnaları, §7.1):
  - kopyanın `WpfReplicas` satırı `SalesSyncModeChangedAt`'ten önce var (`FirstSeenAt` daha eski)
  - `PreviousReplicaId` boş, yani kopya yedekten geri yüklenmemiş (§8.7)
  - aynı ReplicaId'ye ikinci donanım parmak izi yok (`HardwareFingerprintConflictAt` boş, §8.7)
  - `SeenModeOnAt` boş ya da `SalesSyncModeChangedAt`'ten eski: kopya bu açılıştan beri modu görmemiş

  Koşul tutmazsa istek `SeenMode = 1` gibi yargılanır ve sayı günlüğe yazılır. Gerekçe: pencere, kuyruğunda modu görmeden verilmiş **güncel** kararlar olan kopya içindir. Geri yüklenmiş veritabanının kuyruğu ise yedek anındaki bayat değerlerdir; onları Off kurallarıyla almak devredilmiş yayında sonradan verilmiş kararları geri alırdı.
- İstemci sürümü `User-Agent`'tan okunur. Ayrıştırıcı `LicensesController.cs:126-135`'ten ortak bir `ClientVersion` yardımcısına taşınır; karşılaştırma `System.Version` ile yapılır. Ortak bakiye §7.2 aynı yardımcıyı kullanacak; bu PR önce çıktığı için yardımcıyı bu PR getirir.
- Protokolsüz satış isteği (≤0.9.10) **iki kipte de** lisansın `SalesLegacyTraces.LastLegacySalesPushAt` izini yazar (saatte en çok bir kez); bu yalnız bir zaman damgasıdır, davranışı değiştirmez. Mod kapısının "7 gün eski sürüm izi yok" koşulu (§4.10-3) ancak böyle ölçülebilir. Günlük uyarısı yalnız mod On'da düşer.
- İş kuralları ortak kodda durur (§4.11). Elle tutulan bir "sunucu ikizi" yazılmaz.

### 4.1 `POST sessions/sync` (`LicensesSessionsSyncController.cs:64-152`) — S1

v2 öğesinde ek alanlar: `Release` (bool), `MachineName` ve `UnsentLabelCount` (bitmiş oturumda, `Release = false` iken yayının gönderilmemiş etiket sayısı). Sunucu sayıyı devredilmemiş oturumda `UnsentLabelCount`'a yazar.
- **Ekleme:** v2'de `OwnerReplicaId` çağıran kopya olur, `OwnerMachineName` yazılır. v1'de ikisi de NULL kalır.
- **`EndedAt` tekdüze (her biçimde):** dolu değer NULL'a ya da başka bir değere dönmez (`??=`). Bugün `:92` koşulsuz eziyor.
- Title, Platforms ve Notes bugünkü gibi yazılır. Devredilmiş oturumda yok sayılır.
- **Devir.** Koşul: `EndedAt` dolu ∧ `ReleasedAt IS NULL` ∧ şunlardan biri:
  - v2: çağıran sahip ∧ `Release = true`
  - v1 ya da `OwnerReplicaId IS NULL`: `EndedAt` dolu gelmesi yeter. Bugünkü sıra korunur; mod On'da bu iz olarak raporlanır.

  Etki tek işlemde (`BeginTransactionAsync`): önce `ReleasedAt = UtcNow`, sonra `ExecuteUpdate(Orders WHERE LicenseId = @l AND SessionId = @id AND SessionReleasedAt IS NULL → SessionReleasedAt = @r)`. Sıra önemli: siparişlerin rowversion'ı oturumunkinden büyük olur.
- v2'de sahip olmayan kopyanın `EndedAt`'i yok sayılır (`ignored`). Yayını kapatmanın tek başka yolu `force-end` (§4.6).
- **Yanıt:** `SyncedSessionDto` + `OwnerReplicaId`, `ReleasedAt`, `Protocol = 2`. Eski istemci yanıtı zaten atıyor (`SessionOrderSyncService.cs:100`).
- "Yayın başladı" bildirimi (`LicensesSessionsSyncController.cs:110-112`) değişmez.

### 4.2 `POST orders/sync` (`LicensesSessionsSyncController.cs:202-446`) — S1

v2 ek alanları: `Format = 2`, `baseVersion` (satırın istemcideki `ServerVersion`'ı; yeni satırda boş), ParentOrderId, PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice. ShipmentId de gönderilir; okunduğu durumlar K4'te.

**Ekleme dalı (`LicensesSessionsSyncController.cs:295-333`):** bugünkü gibi; ek olarak yeni alanlar ve `OriginReplicaId` yazılır.
- `SessionReleasedAt` oturumdan kopyalanır (DM3). Devirden sonra gelen ekleme hemen akışa girer. Bu durum force-end sonrasındaki gerçek satışları ve devredilmiş ebeveyne eklenen yedeği kapsar.
- v2'de başka kopyanın **canlı** oturumuna ekleme satır başına reddedilir: `rejected: session-owned-elsewhere`.
- KVKK: `ScrubOrderTextOnPurge` açıksa ve asıl projeksiyonun `PurgedAt`'i doluysa DisplayName ve MessageText boş yazılır, `TextScrubbedAt` basılır (§9). Silmeden sonra gelen geç ekleme metni geri getirmez.

**Güncelleme dalı (`LicensesSessionsSyncController.cs:271-294`) — birim kuralı.** K1–K3 S1'den itibaren her biçimde ve iki kipte geçerli; K4–K7'nin kip ve koşulu satırında yazılı:

| # | Birim | Kural |
|---|---|---|
| K1 | Oluşum alanları | Güncellemede hiç yazılmaz. `LicensesSessionsSyncController.cs:275-277` (SessionId, MessageText, Code) ve `:285-286` (ProductId, ProductVariantId) kalkar; eski istemcinin ProductId'yi NULL'laması **mod beklenmeden** kapanır. ParentOrderId yalnız boşsa doldurulur. |
| K2 | Fiyat, iptal, yedek | İlgili `*SetAt` NULL ise yazılır; bu bugünkü son-gelen-kazanır davranışıdır. Doluysa yalnız v2 push'unun `baseVersion`'ı sunucudaki `Version`'a eşitse yazılır (istemci komutun sonucunu görüp yeni karar verdi; damga kalır). Aksi hâlde yazılmaz ve yanıtta `ignored[{id, unit}]` ile `current` döner. v1 damgalı birimi hiç yazamaz. Yedek tek yönlüdür: IsTentativeBackup yalnız 1'den 0'a gider. |
| K3 | Baskı | Sunucuda PrintedAt NULL ise üç alan birlikte alınır; ancak sipariş **şu anda komutla iptal edilmiş** durumdaysa (`CancelledAt` dolu ∧ `CancelSetAt` dolu) alınmaz ve v2'ye `ignoredUnits: [print]`, reason `cancelled-elsewhere` döner; istemci "kâğıdı imha et" uyarısı gösterir. Bu, `mark-printed`'ın "iptal edilmemiş" koşuluyla aynı sonuçtur. Komutla iptal edilip komutla geri alınmış siparişin (`CancelSetAt` dolu, `CancelledAt` NULL) baskısı alınır. Aynı push'ta "basıldı, sonra iptal edildi" satırında damga yoktur; PrintedAt bugünkü gibi yazılır. Sunucuda PrintedAt doluysa yalnız boş PrintBatchId ve PrintSeq doldurulur. PrintedAt hiçbir yoldan silinmez (`048_label_print_batch.sql:8-9`: yeniden baskı dokunmaz). |
| K4 | Kargo bağı | Mod Off: v2'de `ShipmentSetAt` NULL ise ya da `baseVersion = Version` ise (K2'deki istisnanın aynısı) push'taki değer (boşaltma dahil) yazılır; aksi hâlde `ignored` + `current`. v1 dokunmaz. Mod On: push'tan alınmaz, birim her oturum hâlinde sunucunundur. Tek istisna açılış penceresindeki kopyadır (§4.0): onun v2 push'u, `ShipmentSetAt` NULL ve sunucudaki değer boşsa doldurur (§8.4). |
| K5 | Devredilmiş oturum, mod On | v2 ve v1 push'undaki K2 değişiklikleri reddedilir: v2'ye `rejected[{id, reason: session-released, current}]` döner, v1 için yalnız iz yazılır. K3 doldurma ve ekleme kabul edilir. **İstisnalar** (push reddedilmez, yalnız K2 ile yargılanır: komutun damgaladığı birim kazanır ve `ignored` döner, damgasız birim push'tan alınır): (i) oturum zorla bitirildiyse (`ForceEndedBy` dolu) sahibin v2 push'u; (ii) açılış penceresindeki kopyanın (§4.0) v2 push'u (§8.4). |
| K6 | Kargoya verilmiş dosya, mod On | Bağlı olduğu dosya Shipped olan siparişin **fiyat** değişikliği reddedilir: `rejected: shipment-shipped`. İptal ve iptali geri alma iki yolda da §7.2 tablosuna göre işler. |
| K7 | Türetilmiş dosya, mod On | Kabul edilen fiyat, iptal, yedek ya da **kargo bağı** değişikliği (K4 istisnası, `sales/backfill` bağı ve komutlar dahil), aynı SaveChanges'te ilgili dosyaların tutarını yeniden türetir: bağ değiştiyse eski ve yeni dosya. Yalnız `AmountDerivedAt` dolu ve Shipped olmayan dosyada. Fiyat, iptal ve yedek değişikliği bağı değiştirmez (§7.2). |

- `SyncVersion++` bugünkü gibi her işlenen satırda artar (`LicensesSessionsSyncController.cs:293`). Defter kilidi gerekçesi aynen geçerli (`Order.cs:70-91`). Canlı oturum siparişleri akışta olmadığı için yankı trafiği doğmaz.
- **Stok defteri (`LicensesSessionsSyncController.cs:340-367`)** ham paketten değil, birleştirilmiş varlık durumundan beslenir. Bugün paket alanları doğrudan deftere giriyor (`LicensesSessionsSyncController.cs:355-364`). Reddedilen ya da yok sayılan birim deftere bayat bir durum taşımaz.
- Bildirimler (`LicensesSessionsSyncController.cs:395-431`) değişmez; yalnız ekleme dalında tetiklenirler.
- **Yanıt:**
  - `Format = 2` için öğe başına `{id, version, shipmentId, sessionReleasedAt, status: applied|ignored|rejected, ignoredUnits[], reason, current?}`. `current`, akış öğesi biçimindeki sunucu satırıdır; `ignored` ve `rejected`'da her zaman dolar.
  - v1 için bugünkü `SyncedOrderDto` listesi (`LicensesSessionsSyncController.cs:433-445`).

### 4.3 `POST sales/backfill` — S1

Ws'nin ilk açılışında geçmişi tamamlar (§8.2). Gövde `{sessions[], orders[], shipments[{id, labelIds[]}]}`, 200'lük parti.
- **Yalnız boşu doldurur.** Sunucuda dolu bir alan ya da damgalı bir birim asla yazılmaz.
- **Oturum:** `OwnerReplicaId` boşsa çağıran yazılır. Sunucuda `EndedAt` NULL, yerelde doluysa yazılır; `sessions/sync` gibi yalnız hâlâ boşsa (ilişkisel sağlayıcıda kaydetmeden sonra aynı işlemde koşullu UPDATE, §4.1 tekdüzelik): okumadan sonra commit olan bir force-end'in `EndedAt`'i ezilmez (§10.1-11). Devir yalnız öğe `Release = true` taşıyorsa uygulanır; istemci bunu §5.2'deki kuyruk boşluğu kuralıyla hesaplar. Kaçmış kapanışlar böyle kapanır; sunucuda sonsuza kadar açık kalan oturum sorunu budur (`PanelProductsController.cs:636-648`).
- **Sipariş:** satır yoksa eklenir; oturum denetimi aynıdır (`LicensesSessionsSyncController.cs:233-247`). Yeni alanlar (ParentOrderId, PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice, OriginReplicaId, ShipmentId) yalnız boşsa doldurulur. Eklemede KVKK kuralı §4.2 ile aynıdır.
- **Başka lisansın Id'si (karar, 2026-10-14).** Lisans değiştiren PC'nin yerel geçmişinde eski lisansın oturum ve etiketleri durur; lisans değişimi temizliği onlara dokunmaz (§5.4) ve `sales-backfill-out` imleci lisans anahtarına bağlı olduğu için Ws o geçmişi yeni lisansa da gönderir. StreamSessions ve Orders'ın birincil anahtarı yalnız Id'dir. Bu yüzden:
  - Id'si sunucuda başka bir lisansa ait oturum ya da sipariş yazılmaz; satır başına `rejected: foreign-license` döner (oturum sonucunda `reason`). O oturuma bağlı yeni siparişler de, oturum istekte olsun ya da olmasın, aynı nedenle satır başına reddedilir. Parti düşmez; öbür satırlar yazılır, öbür lisansın satırları değişmez.
  - Hiçbir lisansta olmayan oturuma bağlı sipariş ise orders/sync'teki gibi (F01) partinin tamamını 400 `unknown-session` ile düşürür.
  - İstemci `foreign-license`'ı son durum sayar: satırı yeniden göndermez, imleç ilerler, sayı günlüğe yazılır. §8.2-5'in "`applied` bekle" koşulu yalnız 4. adımda kuyruğa alınan satırlar içindir; bu satırlar o kümeye girmez.
- **Mevcut siparişte birim ayrışması.** Satır sunucuda varsa sunucu yazmaz, ama damgasız birimleri (fiyat, iptal, yedek, baskı; zaman damgaları saniyeye yuvarlanarak) öğedekiyle karşılaştırır. Farklı olanlar yanıtta `diverged[{id, units[], localWins}]` olarak döner. Nedeni F05 öncesinin kayıp güncellemeleridir: push uçuştayken yapılan iptal, baskı ya da fiyat değişikliği yerelde kaldı ama sunucuya hiç gitmedi ve satır senkronize göründü (`032_sync_ack_revision.sql:1-8`, 2026-09-10). 032'den sonra bu satırları yeniden kuyruğa alan bir göç yok. Bunlar düzeltilmezse mod On'un ilk turu yereldeki doğru değeri sunucunun eski değeriyle ezer (§8.4-3, §8.7).
  - `localWins = true` ancak şu koşullarda: lisans mod Off, kopyanın `PreviousReplicaId`'si boş (yedekten geri yüklenmemiş), aynı ReplicaId'ye ikinci parmak izi yok (`HardwareFingerprintConflictAt` boş), birim damgasız ve push onu yazabiliyor. Baskı biriminde bu, sunucu satırının basılmamış ve şu anda komutla iptal edilmemiş olması demektir (K3 basılmış satırın baskısını değiştirmez, komutla iptal edilmiş satıra baskı almaz); aksi hâlde yeniden kuyruğa alınan satır hiç `applied` olmaz ve `backfillCompleted` takılırdı (§8.2-5). Gerekçe: mod Off'ta ve bu spec'ten önce bir etiketi yalnız onun bilgisayarı yazar; sunucuda başka yazıcı yok (sunucu kodunda Orders birimlerini yazan tek yer `LicensesSessionsSyncController.cs:278-284`). Özgün veritabanında yerel değer bu yüzden ya sunucuyla aynı ya da ondan yenidir.
  - Geri yüklenmiş ya da kopyalanmış veritabanında yerel değer bayat olabilir: `localWins = false`, sunucu kazanır (§8.7).
  - Sunucu iki sayıyı kopya satırına ekler (`BackfillDivergedLocal`, `BackfillDivergedServer`); `sales-audit` ve `enable` kuru koşusu gösterir. Ne yapılacağı §8.2'de.
- **Dosya bağları:** `shipments[].labelIds` ile, `Orders.ShipmentId` boşsa ve `ShipmentSetAt` boşsa yazılır. Bugün her bağ yalnız etiketin kendi bilgisayarında kurulabildiği için çakışma beklenmez. Dosyanın `OriginReplicaId`'si boşsa çağıran yazılır. Mod On'da yazılan bağ K7 ile dosyayı türetir.
- **Bildirim yok:** push, WhatsApp `OrderReceived` etiket kuralı ve shopper push üretilmez (D1'in Replay deseni). Testle sabitlenir (§10.2).
- Stok defteri eklemede bugünkü gibi çalışır (CatalogAware).
- Moddan bağımsızdır ve idempotenttir.

### 4.4 `GET sales/changes?afterSeq&take≤500` — S1

Desen `LicensesWpfCustomersPullController.cs:204-270`.
- **Ufuk:** `MIN_ACTIVE_ROWVERSION()` sayfa başına bir kez okunur (`:217-219`). `afterSeq < 0 ∨ afterSeq ≥ ufuk` ise akış baştan verilir ve `cursorReset` döner (`:221`). Boş sayfada `next = afterSeq` (`:268`).
- **Kaynaklar.** Her biri `ChangeSeq ∈ (afterSeq, ufuk)`, ChangeSeq sırasıyla ve en çok `take` kayıt:
  - StreamSessions WHERE `ReleasedAt IS NOT NULL`
  - Orders WHERE `SessionReleasedAt IS NOT NULL`. SessionId NULL siparişler akışa girmez: yerelde SessionId NOT NULL FK; sayıları denetim raporunda görünür.
  - Shipments (iki kipte). İstemci dosya öğesini yalnız mod On'da ve `disable` turunda uygular (§8.5). O turun, tuttuğu dosyaların sunucudaki son hâlini (başka bilgisayarın komutları, `disable --apply`'ın yeniden yazdığı `OriginReplicaId`) görmesi bunu gerektirir.

  Üç liste birleştirilip sıralanır, ilk `take` öğe döner; `next` son öğenin seq'idir.
- **Yan liste (imleci etkilemez):** sayfadaki siparişlerin oturum satırları (güncel hâliyle) ve müşteri taslakları `{Id (asıl kayıt), Platform, Username, DisplayName, PurgedAt}`. CustomerId'si GUID olmayan siparişin projeksiyonu yoktur (WPF projeksiyon gönderiminde atlanır, `WpfCustomerProjectionSyncService.cs:195-199`; sunucu ham değeri saklar, `LicensesSessionsSyncController.cs:258-268`); onun taslağı siparişin kendi Platform, Username ve DisplayName'inden kurulur, Id ham değerdir.
  - rowversion yalnız satırın son hâlini gösterdiği için oturum, siparişlerinden sonra gelebilir. Yan liste FK sırasını (`002_pivot_to_labels.sql:25-26`) sayfa içinde sağlar.
  - Eksik müşteri öğeyi zehirlemez. 2026-08-14 dersi: tek satır bütün akışı tutmamalı.
- **Öğe:** sipariş satırının tamamı, `Version`, ShipmentId, yeni alanlar ve bilgi amaçlı birim damgaları.
- **KVKK:** asıl projeksiyonu PurgedAt dolu müşterinin siparişinde okuma anında `DisplayName = null`, `MessageText = ""` (§9).
- Mod Off'ta da çalışır; istemci bu modda uygulamaz. Tek istisna `disable`'dan sonraki son uygulama turudur (§8.5).

### 4.5 `GET sales/state` ve `POST sales/hello` — S1

- **`GET sales/state`** her turda (30 sn) çağrılır ve kalp atışı sayılır. Yanıt:
  - `mode`, `modeChangedAt`, `minClientVersion`
  - `liveSessions: [{id, title, startedAt, endedAt, ownerMachineName, ownerLastSeenAt, state: live|handing-over, unsentLabelCount}]` (`ReleasedAt` NULL olanlar)
  - `shipping: {version, freeShippingThreshold, fee}` (iki kipte; istemci mod On'da kullanır, `disable` turunda yerele kopyalar, §8.5)
  - `scrubOrderTextOnPurge` (§9)
  - `forceEndSilenceMinutes` (S2): sunucunun `force-end` ve Y8 devralma kararındaki sahip sessizliği eşiği (`SalesSync:ForceEndSilenceMinutes`, varsayılan 15). §5.7'deki "≥ 15 dk sessiz" satırları ve onay metni bu değeri kullanır; istemci 15'i sabit yazmaz, yoksa ayar değişince çip ile sunucu ayrışır.
  - `legacyMachines: [{machineName, appVersion, lastSeenAt}]`

  `ownerLastSeenAt`, sahip kopyanın `WpfReplicas.LastSeenAt` değeridir. StreamSessions satırına yazılmaz; böylece her kalp atışında oturumun rowversion'ı ve akış ilerlemez.
- **`POST sales/hello`:** `{replicaId, previousReplicaId?, machineName, hardwareFingerprint, protocol, featureLevel, seenMode, reportedShipping: {threshold, fee}, backfillCompleted?, paymentHistoryCompleted?}`.

### 4.6 `POST sales/commands` — S2

Zarf: `{commandId, replicaId, kind, payload, expected: [{orderId, version}]}`. `expected` en çok 500 öğe taşır. Kümeyi sunucunun kurduğu ve `expected`'in onun tamamını taşıması gereken türlerde (`attach-shipment`'ın aday kümesi, `set-code-price`'ın planı) tavan 10.000'dir: 500'lük tavan, kümesi onu aşan müşteriyi ya da kodu kalıcı bir 400/409 döngüsüne kilitlerdi. Sunucunun kümesi bunu da aşarsa 409 `too-many-candidates {count, max}` döner; kalıcıdır, satır taşımaz, yeniden denenmez.

İşleme sırası:
1. Lisans sahipliği denetlenir. Mod Off ise 409 `mode-off`. `MinClientVersion` boşsa ya da istemci sürümü ondan eskiyse 409 `client-update-required` (fail-closed).
2. **Yeniden oynatma:** `SalesCommandLogs`'ta CommandId varsa ve RequestHash aynıysa, kayıtlı durum ve **güncel** satırlar döner. Hash farklıysa 409 `content-conflict` (A11 deseni). İki istisna: yazmayan `reprint`'in sonucu (`reprintIds`, `correctionIds`) kayıttan değil güncel satırlardan yeniden kurulur ve yeniden oynatma günlüğe yazmaz (arada iptal edilen etiket basılacaklar listesinde dönmez, §5.6); `attach-shipment`'ın havuzu her yanıtta güncel dosyalardan hesaplanır (aşağıdaki tablo).
3. **Görünürlük:** fiyat, iptal ya da yedek birimine dokunulan her siparişin oturumu devredilmiş olmalı. Değilse 409 `session-live {machine, ownerLastSeenAt}`. Kargo bağı bu denetime girmez, çünkü mod On'da her oturum hâlinde sunucunundur (DM1, K4): `attach-shipment`'ın aday kümesi çağıranın görebildiği siparişlerle (devredilmiş oturumlar ve çağıranın kendi oturumları) sınırlanır; `shipment-decision`'ın iptalli bağı çözmesi canlı oturumdaki siparişe de yazar. Canlı sahibin yerel aynası bağ değişikliğini devirde akıştan alır.
4. **Beklenen sürüm:** `expected` ile güncel `Version` uyuşmazsa 409 `stale {current}` döner; hiçbir şey yazılmaz.
5. **Uygulama** tek SaveChanges'te; gerekiyorsa işlem kapsamlı `sp_getapplock` ile:
   - alan yazımları, ilgili `*SetAt = UtcNow`, `SyncVersion++`
   - stok defteri: birleştirilmiş durumdan, yalnız ProductId dolu siparişlerde (`StockLedgerWriter`)
   - K7 dosya türetmesi
   - komut günlüğü satırı
6. Yarışta `DbUpdateConcurrencyException` olursa işlem tümüyle geri alınır ve 409 `stale` döner (`LicensesSessionsSyncController.cs:369-393` deseni).
7. **Yanıt:** `{status, orders[], shipments[], sessions[], result}`, akış öğesi biçiminde. İstemci yanıtı akışla aynı uygulama fonksiyonuyla yazar.

| Tür | Yük | Doğrulama | Etki | Yerel ayna |
|---|---|---|---|---|
| `cancel` | items, reason | zaten iptal olan atlanır | `CancelledAt` (sunucu saati), `CancelReason`, `CancelSetAt`; bağ **kalır**; Shipped olmayan dosya türetilir (tutar ve havuz düşer, §7.2); defter | `LabelService.cs:125-162` |
| `uncancel` | items | iptal edilmemiş olan atlanır | iptal alanları NULL; `CancelSetAt` basılır (silinmez, §2); bağ kalır; Shipped olmayan dosya türetilir (tutar eski hâline döner); defter | `LabelService.cs:166-198` |
| `set-code-price` | sessionId, code (null ise selectedIds), alsoSelectedIds, newPrice ≥ 0, paidFromBalanceIds | Planı sunucu kendisi kurar: oturum×kod (`SameCode`) ∪ alsoSelected, ardından `Classify` (Shipped dosyadaki etiket değişmez). Plan kümesi `expected`'den farklıysa 409 `plan-stale` | Düzeltilebilir ∧ fiyatı farklı olanlarda `Price = new`. `PriceCorrectedAt = now` yalnız basılmış satırda. `PaidPrice = eski fiyat` yalnız NULL ∧ (ShipmentId dolu ∨ id ∈ paidFromBalanceIds). `PriceSetAt` basılır, dosya türetilir | `CorrectPrices` `LabelService.cs:376-426`, `UpdatePriceForCorrection` `LabelRepository.cs:226-244`, `PlanCodeCorrection` `LabelService.cs:311-335` |
| `confirm-backup` | backupOrderId, newPrice? | `IsTentativeBackup = 1` değilse etkisiz | fiyat (verildiyse), `IsTentativeBackup = 0`, `BackupSetAt` (fiyat değiştiyse `PriceSetAt` de); defter | `ConfirmBackup` `LabelService.cs:520-553` |
| `add-backup` | newOrderId, parentOrderId, müşteri {id, platform, username, displayName}, messageText, price?, code? | ebeveyn devredilmiş oturumda; müşteri asıl kayda çözülür | ebeveynin oturumuna IsTentativeBackup = 1, IsBackupPromoted = 1, ParentOrderId, `OriginReplicaId` = çağıran; `SessionReleasedAt` kopyalanır; ProductId ve varyant **boş** (yerel `AddBackup` bugün de `Add`'i ürünsüz çağırıyor, `LabelService.cs:473-480`; iki yol stok defterinde ayrışmasın diye aynı) | `AddBackup` `LabelService.cs:453-481` |
| `mark-printed` | batchId, items [{orderId, seq}] | yalnız PrintedAt NULL ∧ iptal edilmemiş; ilk yazan kazanır | PrintedAt (sunucu saati), PrintBatchId, PrintSeq. Sonuç: `marked[]`, `alreadyPrinted[]`, `cancelled[]` | `MarkPrintedAndRecord` `LabelService.cs:230-274` (R7-04, `LabelService.cs:252`) |
| `reprint` | orderIds | yazma yok | güncel satırlar ve `correctionIds` (yeniden oynatmada da güncel satırlardan) | `PrepareReprint` `LabelService.cs:432-438` |
| `attach-shipment` | customerId, proposedShipmentId, expected (istemcinin gördüğü bağlanmamış küme) | `sp_getapplock('ship:{lisans}:{asıl müşteri}')`. Adaylar: asıl müşterinin ShipmentId NULL, iptal edilmemiş, geçici yedek olmayan, görünür ve **kargo bağı bilinen** (`OriginReplicaId` dolu) siparişleri. `OriginReplicaId`'si boş sipariş, hiçbir Ws geri doldurmasının üstlenmediği satırdır (Ws'ye hiç güncellenmemiş, emekli PC'nin geçmişi): sunucuda bağı yok ama kargolanmış olabilir; aday olsaydı gönderilmiş ürün yeni dosyanın tutarına ve eşiğe girerdi. Aday kümesi ≠ `expected` ise 409 `stale` | En yeni **türetilmiş** (`AmountDerivedAt` dolu) Pending/Held dosya seçilir; yoksa `proposedShipmentId` ile açılır (`AmountDerivedAt = now`, `OriginReplicaId` = çağıran). Siparişler bağlanır (`ShipmentSetAt`), tutar türetilir. Yanıt havuzu da döner: kişinin bütün Pending/Held dosyalarının toplamı (`--accept-shipment` ile kabul edilmiş türetilmemiş dosya saklanan tutarıyla, §4.10). Havuz commit'ten sonra okunan güncel dosyalardan hesaplanır, yanıtın `shipments[]`'iyle aynı satırlardan: hedef dışındaki açık dosyayı ship kilidini almayan bir yazıcı (`cancel`, sahibin push'u; K7) eşzamanlı türetebilir, komut anında okunan tutar hiç var olmamış bir havuz olurdu | `GetOrCreateOpenShipment` `ShipmentService.cs:50-65`, `AttachLabels` `:73-102`, `GetUnattachedByCustomer` `LabelRepository.cs:639-654` |
| `shipment-decision` | shipmentId, decision, expectedVersion | dosya Shipped ise 409 | `Decide` aynası. Hedef Pending/Held ise kişinin öbür açık dosyaları da aynı karara uyar (U12). Kargoya verilen her **türetilmiş** dosyada bağlı ve **iptal edilmiş** siparişlerin bağı çözülür (`ShipmentId = NULL`, `ShipmentSetAt`): ürün kolide değil, tutar onlarsız donar (§7.2). Kabul edilmiş (türetilmemiş) dosyada bağ kalır ve tutar saklanan değeriyle donar: saklanan tutar iptalle düşmediği için o siparişi içerir; bağı çözülseydi sipariş geri alınınca sonraki dosyaya girer, aynı tutar iki dosyada sayılırdı (§4.10-6). `ShipmentStatusChanged` etiket kuralı tetiklenir (`LicensesShipmentsSyncController.cs:153-154` ile eşlik) | `ApplyDecision` `ShipmentService.cs:177-207` |
| `force-end` | sessionId, confirm = true | oturum **canlı** olmalı (`EndedAt IS NULL`); devrediliyor hâlinde 409 `handing-over {unsentLabelCount}`. Sahibin `LastSeenAt`'i ≤ şimdi − 15 dk olmalı (`SalesSync:ForceEndSilenceMinutes`, Y7); değilse 409 `owner-active {lastSeenAt}` | `EndedAt = now`, `ForceEndedBy` = çağıran, devir (§4.1) | — |

Ayrı bir "dosyadan çıkar" (detach) komutu yok. İptal ve iptali geri alma bağa dokunmaz; tutarı türetme değiştirir. Bağ yalnız dosya kargoya verilirken, o anda iptal edilmiş siparişlerden çözülür (§7.2).

### 4.7 Ödeme uçları — S3

- `POST payment-requests/begin`, `POST payment-requests/{id}/finish`, `GET payment-requests?scopeKey&memberIds`. Ayrıntı §6.4'te.
- `begin` mod Off'ta 409 `mode-off` döner; kira denetimi yapmadan başarı dönmez. `disable`'dan sonra Off turunu henüz koşmamış bilgisayarın önbelleğinde uzak etiketler durur; etkisiz bir başarı, Off'a geçmiş öbür bilgisayar aynı etiketleri kendi tablosundan isterken kümülatif çift isteği (R1) yeniden açardı. `finish`, liste ve `backfill` iki kipte çalışır: mod On'da başlamış kiranın sonucu `disable`'dan sonra da yazılabilmeli.
- `POST payment-requests/backfill`: kirasız kayıt (`Kind = Record`) ekler; `recordId` ile idempotent, aynı Id'ye farklı içerik 409 `content-conflict`. Moddan bağımsızdır. Ws'nin ödeme geçmişi geri doldurması ve mod Off'taki her istek bunu kullanır (§6.4).
- `customer-balance/apply` (`LicensesCustomerBalanceApplyController.cs:224`): mod On'a özgü kapsam kuralı (§6.6).
- `preview` (`:74`) ve `apply`: mod On'da istemci `MinClientVersion`'dan eskiyse 409 `client-update-required` (§6.5).

### 4.8 `GET/PUT shipping-settings` — S2

- PUT gövdesi `{expectedVersion, freeShippingThreshold, fee}`. Kabul edilirse `Version + 1`, `UpdatedBy` = çağıran ReplicaId. Sürüm eskiyse 409 `stale`. WPF ayar ekranı bunu yalnız mod On'da çağırır.
- Doğrulama `ShippingSettings.IsEnabled` kuralıyla aynı (`AppSettings.cs:272-273`): ya ikisi de boş (kapalı) ya ikisi de > 0.
- Mod Off'ta değeri Burak CLI ile yazar: `sales-sync shipping set --license <id> --threshold <t> --fee <f> [--apply]` (kuru koşu varsayılan, `UpdatedBy = 'cli'`). Bu mod açılmadan önceki hazırlıktır. İstemciler değeri mod On'da kullanır; `disable` turunda yerel ayara kopyalar (§6.2, §8.5).

### 4.9 Hangfire işleri

- **`sales-release-repair`:** açılışta bir kez kuyruğa alınır, ayrıca günde bir koşar (04:50 UTC, yayın penceresi dışında). Emsal: `identity-key-repair` (`Program.cs:1018-1024`).
  - (a) `EndedAt` dolu ∧ `ReleasedAt` NULL ∧ `OwnerReplicaId IS NULL` olan miras oturumlar devredilir.
  - (b) Oturumu devredilmiş ama `SessionReleasedAt`'i NULL kalmış siparişler (eski imajın geri alma penceresinde eklediği satırlar) 5 bin'lik partilerle damgalanır.
  - (c) Mod On'da: `AmountDerivedAt` dolu ve **Shipped olmayan** bir dosyanın tutarı türetilmiş değerden farklıysa yeniden türetilir ve günlüğe yazılır. Shipped dosyanın donmuş tutarına dokunulmaz (§7.2); bunu bir test sabitler.
  - (d) `ReleasedAt` dolu ∧ `EndedAt` NULL olan oturumlarda `EndedAt = ReleasedAt`. S1 ve sonrası bu hâli yazmaz; yalnız S1 öncesi imaja dönüşte kalır: eski kod v0.9.10'un bayat gönderimiyle devredilmiş oturumun `EndedAt`'ini NULL'lar, istemci o oturumu bir daha göndermez. Asıl kapanış anı kaybolmuştur; devir anı onu izleyen ilk sunucu anıdır. `sessions/sync` de devredilmiş oturumun boş `EndedAt`'ini dolu gönderimle doldurur (§4.1'in tekdüzeliği: yalnız boşsa).
  - İdempotent. Sayılar günlüğe yazılır; ad yazılmaz.
  - v2 oturumlarını bu iş **devretmez**. Devrediliyor hâlindeki bir v2 oturumunu devretmek "etiket kuyruğu boşalmadan devir yok" kuralını bozardı. Geri alma penceresinde kaybolan v2 kapanışını istemci kendisi yeniden gönderir (§5.2).
- **`sales-command-log-retention`:** günde bir; 90 günden eski kayıtları siler.
- Kira süresi için ayrı iş yok. `begin`, süresi dolmuş `Sending` kaydını aynı işlemde `Unconfirmed` yapar (§6.4).

### 4.10 Mod yönetimi ve denetim CLI'ları — S1 (`sales-audit`) / S3 (`sales-sync`)

- `sales-sync status|enable|disable --license <id> [--apply]`, `sales-sync force-end --session <id> [--apply]` ve `sales-sync shipping set` (§4.8). Desen `Program.cs:38-55`; varsayılan kuru koşudur. `force-end` CLI'ı, sahibi hiç kalmamış ya da hiç dönmeyecek canlı ve **devrediliyor** oturumlar içindir; ikincisini arayüzden bitirmenin yolu yoktur (§4.6). Kuru koşu sahibin son sinyalini ve `UnsentLabelCount`'u gösterir; Burak'ın onayıyla koşulur ve `ForceEndedBy = 'cli'` yazar. Canlı oturumda `EndedAt`'i devir anıyla aynı yazar (`force-end` komutu gibi); devrediliyor oturumda `EndedAt`'e dokunmaz, o sahibin kapanış anıdır. Sahip sonradan dönerse eklemeleri ve damgasız değişiklikleri K5 istisnası (i) ile alınır. Geri yüklenmiş kopyanın istisnada sahip sayılması (§8.7) oturumun bitirildiği andaki hâline göre yargılanır ve hâl bu iki alandan okunur: `EndedAt = ReleasedAt` ise oturum canlıyken bitirildi (başka makinedeki kopya sahip ≥ `ForceEndSilenceMinutes` sustuysa sahip sayılır, Y7), farklıysa devrediliyordu (başka makinedeki kopya hiç sahip sayılmaz, Y8). Ayrı bir kolon gerekmez: `EndedAt` tekdüzedir (§4.1), `ReleasedAt` hiç değişmez. `status` her kopya için sürümü, `featureLevel`'ı, `SeenMode`'u, son görülme zamanını ve geri doldurma ayrışma sayılarını gösterir.
- `sales-sync adopt-orders --license <id> --orders <id,…> --replica <id> [--apply]`: kargo bağı bilinmeyen siparişi (`OriginReplicaId` boş, §4.6 `attach-shipment`) Burak gerçekten gönderilmediğini doğruladıktan sonra aday yapar; `OriginReplicaId`'yi verilen kopya olarak yazar. Varsayılan kuru koşu.
- `sales-audit --license <id>`: salt okunur rapor. İçeriği:
  - 24 saatten eski canlı ya da devrediliyor oturumlar
  - SessionId NULL siparişler
  - projeksiyonu olmayan CustomerId'ler; ayrıca CustomerId'si GUID olmayan sipariş sayısı (kural §6.4: toplamda ham Id ile eşlenir)
  - `SessionReleasedAt`'i eksik siparişler
  - kopyaların bildirdiği farklı kargo ayarları
  - tek fiyat ihlalleri (DM5)
  - kapsam başına birden çok geri alınmamış düşüm
  - aynı ReplicaId'ye iki parmak izi (`HardwareFingerprintConflictAt` dolu kopyalar)
  - `OriginReplicaId`'si boş ya da bağsız (tutarı > 0, bağlı siparişi yok) Shipped olmayan dosyalar
  - kargo bağı bilinmeyen siparişler (`OriginReplicaId` boş): sayı ve tutar; bunlar `attach-shipment` adayı olmaz (§4.6)
  - kopya başına geri doldurma birim ayrışması: yerel kazanan ve sunucu kazanan sayısı (§4.3)
  - force-end sonrası sahipten kabul edilen birim değişikliği sayısı (K5 istisnası)
  - açılış penceresi koşulunu tutmadığı için On kurallarıyla yargılanan `SeenMode = 0` istekleri (§4.0)

  Yalnız sayı ve Id basar.

**`enable` ön koşulları.** Kuru koşu her birini sayı olarak basar; biri tutmazsa `--apply` reddeder.
1. `SalesSync:MinClientVersion` dolu. Son 14 günde görülmüş ve devre dışı bırakılmamış her etkinleştirmenin `AppVersion`'ı ≥ bu değer (`Activation.cs:16-20`). AppVersion yoksa eski sayılır.
2. Son 14 günde görülmüş her `WpfReplicas` satırında `Protocol = 2`, `FeatureLevel = 3`, `BackfillCompletedAt` ve `PaymentHistoryCompletedAt` dolu. Aynı ReplicaId'ye bağlı iki farklı donanım parmak izi yok: bu satırların hiçbirinde `HardwareFingerprintConflictAt` dolu değil (§8.7).
3. Son 7 günde `LastLegacySalesPushAt` yok (iz iki kipte yazılır, §4.0).
4. `LicenseShippingSettings` satırı var ve `UpdatedBy` dolu (§4.8, §6.2).
5. `sales-audit`'te devredilmemiş ve 24 saatten eski oturum 0; `SessionReleasedAt`'i eksik sipariş 0.
6. **Kargo bağları tam.** Bugün sunucuda etiket–dosya bağı yok (`shipments/sync` etiket taşımıyor, `LicensesShipmentsSyncController.cs:43-50`); bağları yalnız Ws geri doldurması getirir. Bu yüzden 14 gün penceresinden bağımsız olarak: Shipped olmayan her dosyanın `OriginReplicaId`'si dolu ve o kopyanın `BackfillCompletedAt`'i dolu. Ayrıca her dosya için fark `saklanan − türetilmiş` hesaplanır ve ikiye ayrılır:
   - **açıklanan (Y5):** 0 ≤ fark ≤ bağlı ve iptal edilmiş siparişlerin fiyat toplamı
   - **açıklanamayan:** geri kalan her durum, ayrıca tutarı > 0 olup hiç bağlı siparişi olmayan dosya

   Açıklanamayan dosya sayısı 0 değilse `--apply` reddeder. Burak, dosyayı açan PC'de baktıktan sonra tek tek `--accept-shipment <id>` ile kabul edebilir. **Kabulün anlamı:** "saklanan tutar doğru, bağlar eksik". Kabul edilen dosya türetilmez (`AmountDerivedAt` NULL kalır, saklanan tutar korunur); K7 ve onarım işi ona dokunmaz; `attach-shipment` onu seçmez, kişiye yeni dosya açar; havuz ve `shipment-decision` onu saklanan tutarıyla sayar. Eksik bağların siparişleri tipik olarak `OriginReplicaId`'sizdir ve aday olmaz (§4.6); aynı tutar iki dosyada sayılmaz. Kabul edilmeseydi `--apply` dosyayı eksik bağlardan türetir, tutar 0'a iner ve etiketler sonraki dekontta yeni dosyaya girerdi.
7. Rapor (koşul değil, bilgi): açıklanan farkı olan dosya sayısı, toplam fark ve eşiği aşan ya da altına inen dosya sayısı; kargo bağı bilinmeyen sipariş sayısı ve tutarı; kopya başına geri doldurma ayrışması. Ws ilk açılışta kendi açık dosyalarını zaten türettiği için (§8.2) açıklanan fark çoğunlukla 0'dır; Y5'in geçmişe dönük etkisi her PC'de Ws'nin ilk açılışında görülür (§7.4).

**`enable --apply`:** `SalesSyncMode = 1`, `SalesSyncModeChangedAt` yazılır. Ardından Shipped olmayan, kabul edilmemiş dosyalar partilerle türetilir (`AmountDerivedAt = now`). Verilmiş kararlar değiştirilmez. Kopyalar modu ilk turlarında görür; o ana kadarki kargo gönderimleri kaybolmaz (§8.4).

**`disable`:** acil kapatma anahtarıdır. Sunucu modu 0 yapar; damgalar kalır ve bayat push'a karşı komut kararını korumaya devam eder (K2). Aynı işlemde Shipped olmayan her dosyanın `OriginReplicaId`'si, mod Off'taki tek yazıcısı olacak kopyaya göre yeniden yazılır:
- Bağlı her siparişin sahibi: oturumun `OwnerReplicaId`'si, boşsa siparişin `OriginReplicaId`'si; geri yükleme zinciri (`PreviousReplicaId`) en yeni kopyaya çözülür (§8.7). Sahibi bilinmeyen sipariş sayılmaz.
- Bütün bağlı siparişler tek kopyanınsa dosya o kopyanın olur. Gerekçe: Off'ta dosyayı yönetebilecek bilgisayar, etiketlerin yerelde durduğu bilgisayardır. Y2'ye göre yayın hep aynı PC'den açılıyor, yayın sonrası işler ise başka PC'de yapılıyor; mod On'da PC-B'nin PC-A etiketleri için açtığı dosya bu yüzden olağan durumdur. Dosyayı açanın tutulması, etiketleri bağlı kalan PC-A'yı dosyasız, dosyayı tutan PC-B'yi etiketsiz bırakırdı (§15-38).
- Bağlı sipariş yoksa ya da hiçbirinin sahibi bilinmiyorsa değer değişmez.
- İki kopyanın siparişlerini taşıyan **karışık** dosya, bağlı tutarı büyük olan kopyaya verilir (eşitlikte küçük ReplicaId). Kuru koşu karışık dosyaları Id ve kopya başına tutarla listeler; Y2 altında yalnız iki PC'den yayın açılmışsa oluşur. Öbür kopyanın bu dosyaya bağlı etiketleri riskin 15. maddesindeki durumdadır.

Değişiklik dosyaların ChangeSeq'ini ilerletir; her istemci yeni değeri `disable` turunun son akışında alır (§4.4, §8.5). İstemci tarafı §8.5'te.

### 4.11 Ortak kurallar — `OrderDeck.Shared/Sales/LabelRules.cs`

Proje hem sunucuya hem Core'a zaten bağlı (`OrderDeck.LicenseServer.csproj:44`, `OrderDeck.Core.csproj:20`). Saf kural parçaları buraya taşınır; Core'daki çağıranlar bu koda yönlenir:
- `SameCode(a, b)`: `LabelRepository.cs:321-322`'deki tanım aynen; OrdinalIgnoreCase, kırpma yok. Sorgu kodunu çağıran kırpar (`LabelService.cs:293`). `CodeKey` (`:337`).
- `Classify(facts, shipmentStatusOf)` (`LabelService.cs:339-355`). Girdisi en küçük bir kayıttır: `LabelFacts(Id, Code, Price, Printed, Cancelled, Tentative, ShippingFee, ShipmentId)`. Shared, Core'un `Label` tipini bilmez.
- `CodePrice(source)` (`:296-303`): en çok tekrarlanan fiyat; eşitlikte en son yazılanın fiyatı.
- `Contribution(facts)`: basılmış ∧ iptal edilmemiş ∧ geçici yedek değil → (1, Price). Bugün beş yerde elle tekrarlanan kuralın tek tanımı (`:137`, `:179`, `:255`, `:392`, `:546`).
- `PaidPriceOnCorrection` (`LabelRepository.cs:232`).
- `OrderUnitMerge`: K1–K7 karar tablosu, saf fonksiyon.

Bir JSON test vektörü dosyası iki test projesinde birden koşar. Kod eşleşmesi SQL harmanıyla değil bellekte yapılır; harman Türkçe harfleri katlamaz.

## 5. WPF

### 5.1 Canlı yol (bugünkü gibi)

Şu işler yerelde ve çevrimdışı çalışmaya devam eder:
- sohbetten etiket yazma (`MainShellViewModel.cs:1758`, `:1863`) ve baskı (`:2117-2146`)
- canlı oturumun kuyruğundan çıkarma ve kuyruğu temizleme (`:2001`, `:2021`)
- FİYAT kutusu (`ConfirmCodePrice`, `:621-647`, `CorrectPrices`'a gider)
- canlı oturumda yedek ekleme ve onaylama
- yayını bitirme (`EndStream`, `:1642-1745`)

Mod On'daki farklar:
- (a) `CorrectPrices`, kargo dosyası bloğunu (`LabelService.cs:414-421`) atlar; `Cancel` ve `Uncancel` da mod Off'taki Y5 dosya bloğunu (§7.4) atlar. Dosya tutarını sunucu türetir (K7). Bu yolu D3'ün Shipment ABORT tetikleyicisi kırıyordu. `Classify` yerel aynadaki Shipped durumunu okumaya devam eder; sunucu son sözü K6 ile söyler.
- (b) Önceki yayınlardan kalan kuyruk etiketleri yönlendiriciden geçer (§5.3, §5.5).
- (c) Baskıdan hemen önce satırlar veritabanından yeniden okunur (§5.6).

### 5.2 Gönderim ve devir (`SessionOrderSyncService`)

- **v2 gövde:** ReplicaId, Protocol, SeenMode ve Format = 2 alanları; her etikette `baseVersion = ServerVersion`. `CatalogAware: true` (`SessionOrderSyncService.cs:148`) aynen kalır.
- **Gönderim kapısı.** `SyncOnceAsync` dört yerden eşzamanlı çağrılabiliyor: 60 sn servisi (`SessionOrderSyncHostedService.cs:20`), kapanış (`SyncFlushService.cs:90-94`), satış turu ve ödeme ön gönderimi. Bu yüzden tek bir `SemaphoreSlim` ile sıraya alınır.
- **Kapanış sırası.** Bugün önce oturumlar, sonra etiketler gidiyor (`SessionOrderSyncService.cs:68-70`). Bitmiş ve `Authority = 'local'` olan kendi oturumu için:
  - `Release = NOT EXISTS (Label WHERE SessionId = @id AND SyncedAt IS NULL)`, devir kapısı altında hesaplanır. `Release = false` ise öğe gönderilmemiş etiket sayısını (`UnsentLabelCount`) taşır; başka bilgisayarın çipi ve CLI bunu gösterir (§4.1, §5.7).
  - Oturum satırı, devir sunucudan görülene kadar `MarkSynced` **edilmez**. Kuyrukta kalır ve her turda yeniden gider. Böylece geri alma penceresinde kapanış kaybolmaz (hakem 2'nin D2 karşı örneği 2).
  - Etiketler gittikten sonra oturum aynı turda bir kez daha gönderilir.
  - Bekletilen (`Release = false`) oturumun yeniden gönderimi "gönderilen kayıt" sayılmaz. Kapanıştaki boşaltma döngüsü (`SyncFlushService.cs:90-94`, tur bir şey gönderdikçe yineliyor) bu yüzden boşuna dönmez.
  - Yanıtta `ReleasedAt` gelirse tek bir SyncApplyScope işleminde `ReleasedAt` ve `MarkSynced` yazılır; `Authority = 'server'` **yalnız `SalesSyncState.Mode = 1` iken** yazılır. Mod Off'ta oturum `local` kalır ve bugünkü gibi düzenlenir; mod On'a geçişte §8.4 adım 3 onu çevirir.
  - Yanıtta `Protocol = 2` yoksa sunucu eski sürümdür; bugünkü gibi `MarkSynced` yapılır.
- **Devir kapısı.** Süreç içi ve oturum başına bir kilit. Tur, "kuyruk boş mu" okumasından kapanış yanıtının uygulanmasına kadar kilidi tutar. Yönlendiricinin yerel yolu, kendi bitmiş ama devredilmemiş oturumuna yazmadan önce kilidi 0 bekleme ile almaya çalışır. Alamazsa operatöre "Yayın sunucuya devrediliyor; birkaç saniye sonra tekrar dene." der. Kilit olmasaydı "boş" okuması ile devir arasında yapılan yerel düzenleme sunucuda K5 ile reddedilirdi.
- **Yanıtın işlenmesi.** Bugün yanıt atılıyor (`SessionOrderSyncService.cs:148`).
  - `applied`: `MarkSynced(id, now, revision)` karşılaştır-ve-yaz (F05, `LabelRepository.cs:386-391`) ve `ServerVersion`. Mod On'da sunucudaki ShipmentId farklıysa guard altında yazılır.
  - `ignored`: kayıt `SalesConflictLog`'a düşer. Yok sayılan birimler `current`'tan guard altında yazılır, toplamlar yeniden türetilir, `MarkSynced` yapılır; hepsi aynı F05 koşuluyla (satır gönderimden sonra yeniden değiştiyse dokunulmaz, yeni değişiklik yine gider). Akışın uygulanmadığı mod Off'ta da yerel değer böylece sunucuyla ayrışık kalmaz. Baskı birimi `cancelled-elsewhere` ile yok sayıldıysa (K3) operatöre "kâğıdı imha et" denir.
  - `rejected`: `current` guard altında zorla yazılır, toplamlar yeniden türetilir, `MarkSynced` yapılır. Operatöre liste gösterilir: "N değişiklik uygulanamadı: yayın sunucuya devredilmişti" ya da "…: kargoya verilmiş dosya".
  - Mod Off'ta `ignored` ya da `rejected` uygulaması bir etiketin fiyatını, iptalini, yedeğini ya da kargo bağını değiştirirse aynı işlemde Y5'in yerel dosya bloğu (§7.4) da çalışır: etiket yerel ve Shipped olmayan bir dosyaya bağlıysa tutar farkı kadar düzeltilir; bağ değiştiyse eski dosyadan düşülür, yenisine eklenir. Aksi hâlde etiket değişir, dosya tutarı değişmez ve sapma dosya kargolanana kadar kalırdı.
- **Geri doldurma** (§8.2) kendi imleciyle yürür ve `SyncedAt`/`Revision`'a dokunmaz.

### 5.3 Düzenleme yönlendiricisi (`ILabelEditor`)

Karar tek yerde verilir. Mod Off'ta her zaman bugünkü `LabelService`/`ShipmentService` kullanılır. Mod On'da hedef etiketin oturumunun `Authority`'si `local` ise yerel servis, `server` ise `SalesCommandClient` kullanılır. Tek ara durum: mod On'un ilk yetişmesi bitmeden (`FirstCatchUpAt` boş) bitmiş, `local` ve yerelde `ReleasedAt`'i bilinmeyen kendi oturumunda düzenleme kapalıdır ("Senkron açılıyor; birkaç dakika sonra tekrar dene"). O oturum sunucuda devredilmiş olabilir (onarım işi, §4.9-a); yerel yazım push'ta K5 ile reddedilir ve değişiklik kaybolurdu (§8.4). Karışık seçim (müşteri kartı, kalıntı kuyruk) bölünür. Mod On'da kargo işlemleri her zaman komuttur, çünkü dosya sunucunundur.

| Çağıran | Yer | İşlem |
|---|---|---|
| Kuyruktan çıkar | `MainShellViewModel.cs:2001` | cancel (kalıntı etiket → komut ya da bekleyen komut) |
| Kuyruğu temizle | `:2021` | cancel |
| Kuyruk baskısı | `:2146` | mark-printed (kalıntı etiket) |
| Yedek seçimi | `BeginAddBackup` `:1911-1916`, `TryAssignChatAsBackup` `:1937` | add-backup |
| Basılanlar toplu iptal | `MainShellViewModel.PrintedLabels.cs:310` | cancel |
| Yeniden bas | `MainShellViewModel.PrintedLabels.cs:226` | reprint |
| Müşteri kartı | `CustomerDetailViewModel.cs:330`, `:367` | cancel / uncancel |
| Yedek çekmecesi | `BackupTransferDrawer.xaml.cs:96` | confirm-backup |
| Fiyatı düzelt | `PriceCorrectionViewModel.cs:401-402` | set-code-price |
| FİYAT kutusu | `MainShellViewModel.cs:621-647` | yalnız canlı oturum → yerel (değişmez) |
| Dekont kargo adımı | `DekontEkleViewModel.cs:421-475` | attach-shipment, shipment-decision |
| Eşik çekmecesi kararı | `DekontEkleViewModel.cs:485-491` | shipment-decision |

409'ların ele alınışı:
- `stale` ve `plan-stale`: yanıttaki taze satırlar uygulanır.
  - cancel, uncancel ve mark-printed'da niyet idempotent olduğu için taze sürümlerle bir kez kendiliğinden yeniden denenir.
  - Fiyat, yedek ve kargoda sessiz yeniden deneme yoktur: "Bu arada PC-X'te değişti; plan yenilendi." denir ve plan yeniden gösterilir.
- `session-live`: "Bu yayın PC-A'da canlı (son sinyal SS:DD)".
- `owner-active {lastSeenAt}` (force-end): "PC-A hâlâ sinyal veriyor (son SS:DD); yayın buradan bitirilemez."
- `handing-over {unsentLabelCount}` (force-end): "PC-A'da yayın bitti ama N etiket henüz gönderilmedi. PC-A açılıp internete bağlanınca yayın buradan düzenlenebilir olur."
- `client-update-required`: "Bu işlem için uygulamayı güncelle." Velopack güncelleme sorusu açılır.
- `too-many-candidates {count, max}` (`attach-shipment`, `set-code-price`): kümenin etiket sayısı tek komutun tavanını aşıyor. Yeniden denenmez; `SalesConflictLog`'a yazılır. Metin: "Bu işlem çok fazla etiketi kapsıyor (N); destek ile iletişime geç."
- `content-conflict`: aynı CommandId'ye farklı içerik, yani istemci hatası. Yeniden denenmez; `SalesConflictLog`'a yazılır, bekleyen komutsa kuyruktan düşer ve örtüsü kalkar. Metin: "İşlem kaydedilemedi (iç hata); tekrar dene."
- `mode-off` (komut ucu ve `begin`): istemci modu yanlış biliyor demektir. Hemen `sales/state` okunur ve Off geçişi (§8.5) başlar; ödeme isteği Off turu bittikten sonra yerel toplamla yeniden kurulur. Metin: "Senkron kapatıldı; ekran yenileniyor."
- 404: düzenleme kapalı, "Sunucu güncelleniyor; biraz sonra tekrar dene."

### 5.4 Akış uygulaması (`SalesChangesPullService` + `SalesFeedRepository.ApplyItem`)

**Servis**
- `AppHost`'ta kaydedilir (`AppHost.cs:523`, `:535`, `:561` yanına). Genel başlatma döngüsü kayıtlı bütün `IHostedService`'leri başlatır (`OrderDeck.App/Startup/WpfStartupEnvironment.cs:189-197`, CLAUDE.md'deki PR #89 kuralının bugünkü karşılığı); DI testi kaydı ve başlatılmayı birlikte sabitler (§10.3).
- 30 sn'de bir; açılışta hemen.
- Tur sırası:
  1. `sales/state` çağrılır; mod ve başlıklar okunur.
  2. Ön gönderim: müşteri, ardından oturum ve etiket.
  3. Müşteri akışı turu bitmiş olmalı (`CustomerChangesPullService.cs:125`, `_tourGate`). Satış akışının kendi tur kilidi var.
  4. Sayfalar çekilir; tur başına en çok 10 sayfa (hız sınırı 100 istek/dk/IP, müşteri akışıyla paylaşılıyor).
  5. İmleç her sayfada kaydedilir; aynı SQLite dosyasında, lisansa bağlı.
  6. Dispatcher'a `SalesChanged(labelIds, sessionIds, shipmentIds)` olayı gider.
- `CustomerChangesPullService`'ten alınanlar:
  - tur kilidi (`CustomerChangesPullService.cs:125`)
  - akıştan önce gönderim (`TryPushAsync`, `CustomerChangesPullService.cs:473`)
  - `cursorReset`'te geri sarma (`CustomerChangesPullService.cs:290`)
  - zehirli öğe kuralı U10: 5 tur; ortam hataları sayılmaz
  - lisans değişince temizlik: `Origin = 'remote'` satırlar ve `RemoteLiveSession` silinir, etkilenen müşterilerin toplamları yeniden türetilir; yerel satırlara dokunulmaz
  - her `await`'te `ConfigureAwait(false)`

**`ApplyItem`.** Öğe başına bir SyncApplyScope açılır. Kilit sırası U17: önce `CustomerBusySet`, sonra SQLite.
- **Oturum:**
  - Yerelde yoksa `Origin = 'remote'`, `Authority = 'server'` ile eklenir.
  - Yerelde varsa (kendi oturumu) ve yerelde bitmişse: `ReleasedAt` ve (mod On'da) `Authority = 'server'` yazılır.
  - Yerelde varsa ve hâlâ **aktifse** (force-end; PC-A aslında yayında): `EndedAt` yazılmaz, `Authority` `local` kalır, yalnız `ReleasedAt` saklanır ve `SessionEndedRemotely` olayı bandı açar (§5.7). Sohbetten etiket yazma, FİYAT kutusu ve baskı kesilmez; sunucu bu eklemeleri ve damgasız birim değişikliklerini kabul eder (K5 istisnası). Operatör yayını bitirince normal kapanış koşar; sunucuda oturum zaten devredilmiş olduğu için yanıt `ReleasedAt`'i döner ve oturum o anda `server` olur.
  - `RemoteLiveSession` satırı silinir.
- **Sipariş:**
  - Önce yan listedeki oturum ve müşteri sağlanır.
  - Müşteri yerelde yoksa ve (Platform, Username) ile başka Id'li bir eşleşme varsa öğe "bekliyor" olur: yönlendirmeyi müşteri akışı getirecektir, deneme sayılmaz. Eşleşme yoksa müşteri akışının sunucudan ekleme yolu taslakla kullanılır (`CustomerSyncRepository.cs:535-574`): 0/0 toplam, mezar taşı boşaltması (`:570`). Kalıcı park yok; etiket inişi silinmiş bir kimliği diriltmez.
  - Yerelde satır var ve `SyncedAt IS NULL` ise uygulama **durur** (U5): gönderilmemiş yerel değişiklik var. Tur önce gönderir; yine bekliyorsa öğe sonraki tura kalır, deneme sayılmaz.
  - Değilse bütün birimler, ShipmentId ve `ServerVersion` yazılır. Oluşum alanlarına ve CustomerId'ye dokunulmaz; bunlar yalnız eklemede, `CustomerIdSql.Resolve` ile yazılır.
  - İlk yetişmede (`FirstCatchUpAt` boş) kendi `Origin = 'local'` satırında sunucudaki birim yereldekinden farklıysa yazmadan önce `SalesConflictLog`'a `catchup-overwrite` olarak düşer; tur sonunda sayı varsa bir kez uyarı gösterilir: "N etikette sunucudaki değer bu bilgisayardakinden farklıydı; sunucunun değeri alındı." Geri doldurmadaki onarımdan (§8.2) sonra bu sayı yalnız geri yüklenmiş veritabanında 0'dan büyük olmalıdır.
  - Yeni satırda `SyncedAt = now`. `StockSyncedAt` boşsa `SyncedAt`'in değeriyle doldurulur. NULL kalırsa aynı etiket stoktan ikinci kez düşer (`LabelRepository.cs:699-717`).
  - Etkilenen müşterilerin (eski ve yeni CustomerId) toplamları aynı işlemde yeniden türetilir (§6.1).
- **Dosya:** mod On'da ve `disable` turunda (§8.5) yerel aynaya upsert edilir (`OriginReplicaId` dahil). Mod Off'un normal turlarında akış zaten çekilmez.
- **KVKK:** yerel müşteri silinmişse DisplayName ve MessageText boşaltılmış olarak yazılır. Bu, inen satır içindir ve ayardan bağımsızdır; mevcut yerel satırların boşaltılması ayara bağlıdır (§9).

### 5.5 Kalıntı kuyruk ve bekleyen komutlar

- `GetUnprintedOutsideSession` (`LabelRepository.cs:96-107`) yalnız `Origin = 'local'` oturumlara bakar. Başka bilgisayarın basılmamış kuyruğu bu bilgisayara teklif edilmez (`MainShellViewModel.cs:1631-1639`).
- Bu bilgisayarın devredilmiş oturumlarından kalan etiketler kuyruğa eklenebilir. Bu etiketlerin iptali ve ilk baskısı komutla yapılır. Hakem 1'in D2 ölümcül bulgusu tam bu yoldu: yerel iptal sunucudan reddedilip geri dönüyordu.
- **Çevrimdışı:** iki işlem `PendingSalesCommand`'a yazılır (sabit CommandId, idempotent). Örtü kuralı: bekleyen iptal ya da basım örtüsündeki etiketi `GetUnprintedBySession` (`LabelRepository.cs:71-87`) ve `GetUnprintedOutsideSession` dışarıda bırakır. Aynı kâğıt ikinci kez teklif edilmez; D3'teki bekleyen baskı açığı (çift kâğıt) böyle kapanır. Müşteri toplamı onaydan sonra değişir.
- **Gönderim:** her turda, akıştan önce; ayrıca ödeme isteğinin ön gönderiminde ilk sırada (§6.4). Sonuç akış öğesi gibi uygulanır ve örtü kalkar.
- **Mod Off'a dönüşte** gönderilemezler (komut ucu 409 `mode-off`). Bekleyen komutlar yalnız bu bilgisayarın kendi oturumlarındaki kalıntı etiketlere ait olduğu ve o oturumlar `local`'e döndüğü için yerel yazıma çevrilir: `cancel` → `LabelService.Cancel`, `mark-printed` → aynı batch Id ve sırayla `MarkPrintedAndRecord`. Basılmış kâğıt bu yüzden yeniden teklif edilmez (§8.5).
  - Sunucu `cancelled` ya da `alreadyPrinted` dönerse operatöre bildirim gider: "kâğıdı imha et".
  - 409 `stale` olursa satırlar tazelenir ve bir kez yeniden denenir.
  - Kalıcı ret `SalesConflictLog`'a yazılır ve uyarı gösterilir.

### 5.6 Baskı anında yeniden okuma

`PrintCoreAsync` (`MainShellViewModel.cs:2117-2146`) bugün kâğıdı ekrandaki kopyadan basıyor (`:2120`; `LabelViewModel.cs:13-16`). Yeni sıra:
1. Satırlar kimlikle veritabanından yeniden okunur (`GetByIds`) ve bekleyen örtü uygulanır. İptal edilmiş ya da basılmış satır düşer; kâğıda güncel fiyat basılır (kopya `Replace` ile tazelenir).
2. Mod On'da, çevrimiçiyken ve sette `Authority = 'server'` satır varsa önce en çok 3 sn'lik bir akış turu koşar, sonra 1. adım tekrarlanır.
3. Kâğıt basılır.
4. Yerel satırlar `MarkPrintedAndRecord` ile, sunucu satırları `mark-printed` (ya da bekleyen komut) ile işaretlenir.
5. Sunucu `cancelled[]` dönerse: "Şu etiketler başka bilgisayarda iptal edilmişti; kâğıtları imha et." Metin deseni `MainShellViewModel.PrintedLabels.cs:321-324`.

**Yeniden baskı** (`MainShellViewModel.PrintedLabels.cs:223-246`): çevrimiçiyken `reprint` komutu güncel sunucu satırlarını döner ve onlar basılır. Çevrimdışıyken önbellekten basılır, kâğıda değil ekrana "son eşitleme SS:DD" notu düşer.

### 5.7 Arayüz durumları

| Durum | Nerede | Metin |
|---|---|---|
| Başka bilgisayarda canlı | üst çubuk çipi (`RemoteLiveSession`) | "PC-A'da canlı · son sinyal 21:14" |
| Başka bilgisayarda devrediliyor | çip; "Yayını bitir…" eylemi **yok** (sahip kararı Y8) | "PC-A'da yayın bitti, N etiket gönderilmeyi bekliyor · son sinyal SS:DD". Sahip ≥ 15 dk sessizse ek satır: "PC-A açılıp internete bağlanınca yayın buradan düzenlenebilir olur." Etiketler sahipte beklediği için devralma eksik tutarla ödeme isteği ve koli üretirdi; sunucu da eksik olduğu için `stale-total` bunu yakalayamazdı (§4.6, M2). Sahip hiç dönmeyecekse kapatma Burak'ın CLI'ıyladır (§4.10). |
| Canlı yayının sahibi ≥ 15 dk sessiz | çip ve "Yayını bitir…" eylemi | Onay: "PC-A 17 dakikadır sessiz. Yayın gerçekten bitti mi? Bitirirsen yayın buradan düzenlenebilir olur. PC-A aslında çalışıyorsa, bağlantısı gelince gönderdiği yeni etiketler ve değişiklikler de işlenir; aynı etikette burada yaptığın değişiklik önceliklidir." |
| Kendi yayını uzaktan bitirildi (yayın hâlâ açık) | MainShell bandı | "Bu yayın PC-B'den bitirildi olarak işaretlendi (SS:DD). Yayına devam edebilirsin; bitirdiğinde değişiklikler eşitlenir. PC-B'nin aynı etikette yaptığı değişiklik önceliklidir." |
| Kendi yayını devrediliyor | durum satırı | "Yayın sunucuya devrediliyor…" |
| Devredilmiş yayın, çevrimdışı | düğmeler pasif, araç ipucu | "Bu değişiklik için sunucu bağlantısı gerekli" |
| Komut ucu yok (404) | araç ipucu | "Sunucu güncelleniyor; biraz sonra tekrar dene" |
| Diğer 409'lar | satır içi | §5.3 listesi |
| 409 stale | satır içi | "Bu arada PC-X'te değişti; plan yenilendi." |
| Reddedilen push | uyarı listesi | §5.2 |
| Akış | durum satırı | "Satış: güncel ✓ (SS:DD)", "yetişiyor", "N öğe bekliyor", "M öğe atlandı", "Yayın X devredilemedi: N etiket gönderilemiyor". Dikkat sayacına (`SyncOutboxRepository.cs:44-50`) SalesFeedFailure ve devredilemeyen oturum eklenir. |
| Mod Off | — | bugünkü gibi (`disable`'dan sonra uzak satır kalmaz, §8.5) |

- Rapor ve geçmiş ekranları başka bilgisayarın yayınını makine adıyla gösterir (`OwnerMachineName`).
- Faz 0 yetişme denetimi (`MainShellViewModel.cs:1610-1617`) mod On'da satış akışının ilk yetişmesini de bekler.

### 5.8 Çevrimdışı davranış (sahip kararı Y3)

| İşlem | Çevrimdışı |
|---|---|
| Canlı yayındaki her iş | çalışır (bugünkü gibi) |
| Kendi bitmiş ama devredilmemiş yayını | yerel düzenleme çalışır; devir bağlantı gelince olur |
| Kalıntı kuyruk etiketinin iptali ya da ilk baskısı | bekleyen komut (§5.5) |
| Devredilmiş yayında fiyat, iptal (kart ya da Basılanlar), yedek, kargo | düğmeler pasif |
| Dekont kaydı | çalışır. Kargo adımı atlanır ve uyarı gösterilir. Bir sonraki dekont ya da bağlama bütün bağlanmamış etiketleri alır; bugünkü "bir sonraki dekontta tekrar denenir" sözleşmesi bu (`DekontEkleViewModel.cs:467-473`) |
| Ödeme isteği | GUID'li üyesi olan kişide engellenir; bugün de önizleme yüzünden engelleniyor (`PaymentRequestService.cs:1062-1066`). Mod On'da kapsamına uzak etiket giren kişide de engellenir (kira zorunlu, §6.5). Yalnız kendi etiketleri olan GUID'siz kişide bugünkü gibi yerel; kayıt bağlantı gelince yazılır |
| Yeniden baskı | önbellekten, notla |

### 5.9 `GetActive` ve okuyucular

- `SessionRepository.GetActive` (`SessionRepository.cs:67-74`) koşuluna `AND Authority = 'local'` eklenir. Bu bir savunma katmanı: uzak oturum hiçbir zaman `EndedAt` NULL olarak inmez, `RemoteLiveSession` de ayrı tablo.
- `GetAllEnded` ve `GetLatestEnded` (`SessionRepository.cs:86-109`) uzak oturumları kendiliğinden görür. Dekont eşleştirmesindeki "son yayın" (`DekontEkleViewModel.cs:315`) artık bütün bilgisayarların en son devredilmiş yayınıdır.
- Raporlar (`GetSessionTotals` `LabelRepository.cs:393-417`, `GetPeriodAccountRows` `:567-605`) bütün bilgisayarların satışlarını içerir. Fatura ve e-Arşiv dökümü tek bilgisayardan alınır (Y6, §11).

### 5.10 Toplamlar (WPF tarafı)

- Müşteri toplamları W1 ile etiketlerden türetilir (`CustomerAggregateSql`); yerel olay, akış uygulaması ve yeniden anahtarlama aynı fonksiyonu çağırır (§6.1).
- Ödeme tutarı W3 ile sayaçtan değil etiketlerden okunur (`GetPrintedTotal`) ve sunucu toplamıyla doğrulanır (§6.3, §6.4).
- Kargo eşiği ve ücreti mod On'da lisans geneli ayardan okunur (§6.2).
- Kargo dosyası tutarı mod Off'ta Ws'nin yerel yolunda iptal ve iptali geri almayla düşer ve geri gelir (Y5, §7.4); mod On'da sunucu türetir.

## 6. Toplamlar ve ödeme

### 6.1 Müşteri toplamları

- `Customer.TotalAmount` ve `TotalLabelsPrinted` fark sayacı olmaktan çıkar. Yeni `CustomerAggregateSql.Recompute(ids, write)` bunları `LabelRules.Contribution` üzerinden etiketlerden türetir.
- Çağrı yerleri:
  - `LabelService.cs:160`, `:195`, `:272`, `:413`, `:547`. Bugün `RecordPrintedLabels` (`CustomerService.cs:150-154`) → `IncrementLabelStats` (`CustomerRepository.cs:311-321`).
  - `CustomerSyncRepository.AddAggregates` (`CustomerSyncRepository.cs:504-523`)
  - akış uygulaması (§5.4)
- `LastSeenAt` yalnız yerel olaylarla ilerler; bunun için ayrı bir SQL ifadesi kullanılır.
- Aynı satırı iki kez uygulamak sıfır fark üretir; bugünkü fark sayaçlarında bu idempotentlik yok.
- Tam yeniden hesap iki kez yapılır: Ws ilk açılışta bir kez (yerel etiketlerden, mod Off) ve mod On'un ilk tam yetişmesinden sonra bir kez. Değişen satır sayısı ve toplam fark günlüğe yazılır; ad yazılmaz.
- **Para yolları sayacı okumaz.** Yeni `LabelRepository.GetPrintedTotal(memberIds)`, `GetSessionPrintedTotal`'ın (`LabelRepository.cs:514-524`) oturum süzgeçsiz hâlidir. Şu yerler onu okur: `CustomerSearchViewModel.cs:338` (bugün Σ `TotalAmount`), `StreamReportViewModel.cs:271`, `CorrectedPaymentRequests.cs:412`.

### 6.2 Lisans geneli kargo ayarı (Y4)

- Tek kaynak `LicenseShippingSettings` (§3.1, §4.8).
- Mod On'da yeni `ShippingSettingsProvider` sunucu değerini döner (yerel önbellek: `SalesSyncState`). Mod Off'ta `AppSettings.Shipping` döner.
- `ComputeShipping` (`PaymentRequestService.cs:1640-1663`) bugün `AppSettings`'i alıp `settings.Shipping`'i okuyor. İmzası `ShippingSettings` alacak biçimde değişir; değeri her çağıran `ShippingSettingsProvider`'dan alır. Böylece yerel ayarın yanlışlıkla okunduğu bir yol derlenmez.
- Okuyucular:
  - `ComputeShipping`'in çağıranları: `OpenWhatsApp` (`PaymentRequestService.cs:208`), istek akışı (`:480`) ve #508 planı (`CorrectedPaymentRequests.cs:228`, bugün `_settings()` yerel AppSettings)
  - `EvaluateAfterPayment` (`ShipmentService.cs:132`)
  - `PaymentMatcherService.Match` (`PaymentMatcherService.cs:59`)
  - dekont ekranındaki eşik gösterimi (`DekontEkleViewModel.cs:282`)
  - ayar ekranı (`SettingsViewModel.cs:456-457`, `:589-590`). Mod On'da kaydet PUT yapar ve bağlantı ister; mod Off'ta yerel ayarı yazar.
- `begin` isteği `shippingSettingsVersion` ve `recipientPays` taşır. Sürüm eskiyse 409 `settings-stale` döner; istemci önbelleği yeniler, tutarı yeniden hesaplar ve bir kez daha dener. Sürüm doğruysa sunucu ayrıca `totalBeforeBalance = productTotal + kargo` eşitliğini lisans ayarıyla denetler (alıcı ödemeli ya da eşik üstü ya da ayar kapalı ise kargo 0); tutmazsa 409 `settings-stale`. Yerel ayarla hesaplanmış bir tutar doğru sürüm numarasıyla geçemez. Aynı kişiye iki bilgisayarın farklı kargo tutarı istemesi böyle kapanır.
- **Geçiş:** Ws, `hello`'da kendi yerel değerini bildirir. `sales-sync status` farklı değerleri gösterir. Burak tek değeri `sales-sync shipping set` ile yazar (§4.8). Bu, modu açmanın koşuludur (§4.10-4).
- **`disable`'da** sunucu değeri her PC'nin yerel `AppSettings.Shipping`'ine yazılır (§8.5). PC'ler Off'a döndüklerinde farklı yerel değerlerle farklı kargo tutarı istemez.

### 6.3 Tutarın kaynağı

| Kapsam | Ekrandaki toplam | Ödeme isteğinde |
|---|---|---|
| `session:{kendi canlı yayını}` | yerel | yerel (otorite canlı bilgisayar); ön gönderimden sonra sunucu doğrular |
| `session:{devredilmiş}` | önbellek | önbellek; sunucu doğrular |
| `cumulative` | önbellek (devredilmiş yayınlar + kendi yayınları) | önbellek; sunucu doğrular. Başka bilgisayarda canlı ya da devredilmemiş satış varsa istek engellenir |

Kapsam anahtarları değişmez: `session:{id}` ve `cumulative` (`CustomerSearchViewModel.cs:291-292`, `StreamReportViewModel.cs:254-255`, `CorrectedPaymentRequests.cs:130-132`).

### 6.4 PersonKey kirası (S3 + W3)

Kira, `RequestPaymentAsync` (`PaymentRequestService.cs:286-363`) içinde şu noktaya girer: kişi kiralandıktan (`EnterPersonAsync`, `:314`) ve tutar okunduktan (`:336`) sonra, bakiye adımından (`:501-517`) önce.

**İstemci tarafı** (mod On; mod Off için aşağıdaki "Ödeme geçmişi")
1. **Ön gönderim:** önce bekleyen komutlar (§5.5), sonra müşteri, oturum ve etiket kuyrukları; en çok 5 sn. Zaman aşımı ya da ilgisiz bir kuyruktaki takılma tek başına isteği engellemez: eksik gönderim kişinin toplamını etkiliyorsa sunucu karşılaştırması (f) yakalar. Tek doğrudan engel bekleyen komuttur. Bu kişinin bu kapsamdaki bir etiketine ait bekleyen komut (örtü) gönderilemediyse istek engellenir: "Bekleyen baskı ya da iptal onayı gönderilemedi; bağlantı gelince tekrar dene." Yerel toplam onaylanmamış baskıyı saymadığı (§5.5) ve sunucu da onu bilmediği için iki toplam eşit çıkar; engel olmasa kâğıdı basılmış ürün istenmezdi.
2. Tutar yeniden okunur; ön gönderim sırasında basılmış etiketler de girer. Toplama giren siparişlerin Id'leri (`orderIds`) birlikte toplanır.
3. `POST payment-requests/begin` gönderilir: `{recordId, replicaId, ownerId, memberIds, scopeKey, orderIds, productTotal, totalBeforeBalance, recipientPays, shippingSettingsVersion, confirmedPriorIds}`. `memberIds` kişinin **bütün** üyeleridir (GUID'siz olanlar dahil, ham Id), çünkü yerel tutar da bütün üyelerden okunuyor (`CustomerSearchViewModel.cs:337`, `StreamReportViewModel.cs:272`). `PaymentRequestService.cs:1121`'deki GUID süzgeci yalnız bakiye uçlarında kalır.

**Sunucu tarafı.** Tek işlem içinde, `sp_getapplock('payreq:{lisans}:{PersonKeyHash}:{kapsam}')` ile. Ayrıca her üye için `payreq-m:{lisans}:{üye}:{kapsam}` kilidi sıralı alınır (sıra ölü kilitlenmeyi önler). Böylece iki bilgisayar grup senkronu gecikmesi yüzünden farklı PersonKey hesaplasa bile, ortak üyeleri varsa aynı anda geçemez.
- a. **Yeniden oynatma:** aynı `recordId` varsa ve `RequestHash` aynıysa kayıtlı sonuç döner; farklıysa 409 `content-conflict`. Yanıtı kaybolan `begin` böylece güvenle yinelenir.
- b. PersonKey hesaplanır; GUID'li üyeler asıl kayda çözülür, GUID'siz üyeler ham Id ile kalır. Sunucu PersonKey'in grubundaki asıl üyeleri de bulur. İstemcinin `memberIds`'i bunları kapsamıyorsa 409 `members-stale {members}`.
- c. Süresi dolmuş `Sending` kayıtları `Unconfirmed` yapılır. Aynı kopyanın aynı kişi×kapsamdaki başka `Sending` kaydı da `Unconfirmed` yapılır: süreç çöküp yeniden açılmıştır, mesaj gitmiş olabilir.
- d. Başka kopyanın `Sending` kaydı varsa (PersonKey'den ya da üye kümesi kesişiminden, aynı kapsamda) 409 `in-progress-elsewhere {machine, since}`.
- e. `confirmedPriorIds` dışında bir `Delivered` ya da `Unconfirmed` kayıt varsa (yine PersonKey'den **ya da üye kümesi kesişiminden**) 409 `already-requested {priors: [{id, machine, netAmount, at, unconfirmed}]}`.
- f. **Sunucu toplamı:** üyelerin siparişlerinden `Contribution` verenlerin kümesi ve toplamı; `session:{id}` için o yayın, `cumulative` için bütün yayınlar. GUID'li üye asıl kayda, GUID'siz üye ham `CustomerId`'ye eşlenir (sunucu GUID olmayan değeri aynen saklıyor, `LicensesSessionsSyncController.cs:258-268`). Küme `orderIds`'ten ya da toplam istemcininkinden farklıysa 409 `stale-total {serverTotal, liveElsewhere}`. Küme karşılaştırması, toplamı eşit iki farklı kümeyi de (biri iptal, biri yeni etiket) yakalar. `liveElsewhere`, farkı başka kopyanın devredilmemiş yayınındaki siparişler açıklıyorsa doludur.
- g. Kargo ayarı sürümü eskiyse ya da tutar lisans ayarıyla tutmuyorsa 409 `settings-stale` (§6.2).
- h. `Sending` kaydı, üyeleri ve `orderIds` (`PaymentRequestRecordOrders`) yazılır; `LeaseUntil = now + 120 sn`. Eşzamanlı ikinci kaydı filtreli tekil indeks reddeder; tekil ihlal 500 değil 409 `in-progress-elsewhere` olur.

**409'ların istemcideki karşılığı**
- `in-progress-elsewhere` → mevcut `PaymentInProgress` sonucu (`PaymentRequestService.cs:61`), metin "PC-A'da bu kişiye ödeme isteniyor".
- `already-requested` → mevcut `AlreadyRequested` sorusu (sahip kararı 2, #506), makine adıyla. Onay belirteci uzak kayıt Id'lerini de kapsar.
- `members-stale` → bir müşteri akışı turu, kişi yeniden kurulur, bir kez yeniden denenir. Sürerse istek engellenir: "Müşteri grupları eşitleniyor; biraz sonra tekrar dene."
- `stale-total` → bir akış turu ve bir kez yeniden deneme. Fark sürerse istek engellenir: "Bu bilgisayar güncel değil (sunucu X ₺, burada Y ₺)". `liveElsewhere` doluysa metin: "Bu kişinin PC-A'da süren yayında etiketi var; istek yayın bitip devredilince yapılabilir."
- `settings-stale` → önbellek yenilenir, bir kez yeniden denenir.
- `mode-off` → mesaj gitmez. Off turu (§8.5) koşulur; uzak etiketler silindikten sonra istek yerel toplamla ve mod Off kuralıyla (kirasız kayıt) yeniden kurulur (§4.7, §5.3).
- `content-conflict` → istemci hatası; günlüğe yazılır, istek engellenir.

**Kapsam uzlaştırması.** `ReconcilePersonScopeAsync`'in adayları bugün yerel `person.Members`'tan geliyor (`PaymentRequestService.cs:1121`). Mod On'da bunlar `begin` yanıtındaki sunucu üyeleriyle birleştirilir. Grup senkronu gecikmiş bir bilgisayar, başka üyeye yazılmış düşümü böyle görür ve kendi çapasından ikinci düşüm yazmaz. §6.6 kuralı çapa düzeyinde kalır; bu katman onu kişi düzeyinde tamamlar.

**Akışın sonu.** Her sonuçta `finish` çağrılır: `{delivered | unconfirmed | abandoned, netAmount, appliedAmount, fullyFromBalance}`. Bağlandığı yerler bugünkü `TryBeginSend`, `MarkDelivered` ve `AbandonSend` noktaları (`PaymentRequestService.cs:541`, `:553-562`). `finish` gönderilemezse `PendingPaymentRecord`'da bekletilir ve yeniden denenir.

**Teslim kaydı olmadan dolan kira.** Kayıt "gitmiş olabilir" anlamında `Unconfirmed` olur ve öbür bilgisayarın sorusunda görünür (hakem 2 aşısı). Bunun yerel karşılığı "SendAttempts > Deliveries" mantığıdır.

**Ödeme geçmişi (mod Off'ta da).** Kayıtlar yalnız mod On'da yazılsaydı, Ws ile açılış arasındaki istekler (en az 7 gün) ve öncekiler sunucuda hiç görünmezdi. Mod On'da başka bilgisayar aynı kapsamı "zaten istendi" sorusu çıkmadan yeniden isterdi; rozetler ve #508'in "ödendi, fark" planı da yanlış çıkardı.
- Ws'nin ilk açılışında (§8.2) yerel `PaymentJob`'lardan, "zaten istendi" sorusuna giren her iş için (`PersonScopePlan.PromptJobs`, miras işler dahil) bir `Record` kaydı `payment-requests/backfill`'e gider. Kayıt şunları taşır: kapsam, sahip, üyeler, `PromptNet`, düşüm, `FullyFromBalance`, `Unconfirmed` (= `HasUnconfirmedSend`), zaman ve kapsadığı siparişler. Kapsanan küme bugünkü `Covers` kuralıyla yerelde hesaplanır (`PrintedAt ≤ PromptAt`; ikisi de aynı bilgisayarın saati). Kayıt Id'si iş Id'si ile teslim sayısından türetilir, yineleme zararsızdır. Bitince `hello {paymentHistoryCompleted: true}` gider.
- Mod Off'ta her teslim ya da belirsiz gönderimden sonra aynı uca kirasız bir kayıt kuyruğa alınır (`PendingPaymentRecord`). Gönderim isteği bekletmez; bugünkü çevrimdışı davranış değişmez.
- Mod kapısı her kopyada `PaymentHistoryCompletedAt` ister (§4.10-2). Mod On'un ilk turu bu kuyruğu da boşaltır (§8.4).

**Teslim kayıtlarının okunması.** `GET payment-requests?scopeKey&memberIds` yanıtı yerel işlerle birleştirilir. Bu kopyanın (ve `PreviousReplicaId` zincirinin) kayıtları yerel işle zaten temsil edilir; birleştirmeye yalnız başka kopyaların kayıtları girer. Sıralama sunucu saatiyle (`FinishedAt`) yapılır; henüz gönderilmemiş yerel kayıt en yeni sayılır. "Bu istek bu etiketi içeriyor mu" sorusu (`Covers`; bugün isteğin `PromptAt`'i etiketin `PrintedAt`'iyle karşılaştırılıyor) uzak kayıtta `PaymentRequestRecordOrders` üyeliğiyle cevaplanır. İki bilgisayarın saati hiç karşılaştırılmaz. Okuyanlar: `CorrectedPaymentRequests.Badges` (`CorrectedPaymentRequests.cs:303`) ve `BalancePaidLabels` (`:274`) (Delivered ∧ FullyFromBalance), "zaten istendi" sorusu ve #508'in "ödendi, fark" planı (`:168`). Cloud API'nin idempotency anahtarı her çağrıda yeni (`PaymentRequestService.cs:1572-1583`) ve wa.me yolunda sunucu hiç yok. Bu yüzden sıralı ikinci isteği yalnız bu soru durdurabilir; artık bilgisayarlar arasında da durdurur.

**Gecikme.** Canlı yayın sırasındaki ödeme isteğine bir sunucu çağrısı (`begin`) ve çoğu zaman boş geçen bir ön gönderim eklenir. Bugün de aynı akışta `/scope`, önizleme ve apply çağrıları var.

### 6.5 Kümülatif çift istek penceresi (hakem 1, D3 ölümcül bulgusu)

D3'te R1 sürümü uzak etiketleri toplama katıyordu ama kirayı R2'ye bırakıyordu. Bu yüzden iki bilgisayar aynı kişiden kümülatif **tam** tutarı isteyebilirdi. Bu tasarımda bu pencere yok:
1. Akış uygulaması, uzak etiketler, kira ve `stale-total` aynı mod kapısıyla, birlikte açılır ve birlikte kapanır. Mod Off'ta Ws'nin yerel tablosunda uzak etiket yoktur: mod hiç açılmadıysa hiç inmemiştir, `disable`'dan sonra ise ilk Off turu onları siler (§8.5). Kümülatif toplam bugünkü gibi yalnız bu bilgisayarın etiketlerinden çıkar.
2. Mod On'da kira zorunludur ve **fail-closed** çalışır: `begin`'e ulaşılamazsa GUID'li üyesi olan ya da kapsamına uzak etiket giren kişide mesaj gitmez. S3 imajı otomatik geri alınıp kira ucu 404 dönse de durum aynıdır. Akıştan GUID'siz Id'li müşteri de inebilir (taslak sipariş satırından, §4.4); kural bu yüzden GUID'e değil uzak etikete de bakar ve uzak etiketi olan hiçbir kişi kirayı atlayamaz. Yalnız kendi etiketleri olan GUID'siz kişide bugünkü gibi yerel devam edilir (çevrimdışı canlı yayın, Y3).
3. Mod On'da eski sürümün `preview` ve `apply` çağrıları 409 `client-update-required` alır (§4.7). GUID'li kişide eski bilgisayarın mesajı engellenir (`PaymentRequestService.cs:1062-1066`).

### 6.6 Kapsam başına tek geçerli düşüm (S3)

- **Koşul:** `apply` (`LicensesCustomerBalanceApplyController.cs:224`) çağrısında mod On ∧ SaleScope dolu ∧ anahtar yeni. `legacy:` öneki ayırt edici değildir: miras işin replay'i asıl kapsamla gider (`SaleScopeOf`, `PaymentRequestService.cs:1396-1397`), kural ona da uygulanır.
- **Kural:** `sp_getapplock('bal:{lisans}:{asıl kayıt}:{kapsam}')` alınır; sonra `/scope` ucuyla aynı yüklem çalıştırılır (`LicensesCustomerBalanceApplyController.cs:143-147`: aynı asıl kayıt, aynı kapsam, `purchase-deduction`, geri alınmamış). Böyle bir düşüm varsa 409 `scope-has-deduction {transactionId, appliedAmount, productTotal}`.
- **İstemci:** düşümü `AdoptRemoteResult` (`PaymentRequestService.cs:1196`) ile benimser ve revizyon adımına döner. **Miras işte** (`IsLegacyScope`) benimseme yapılmaz: düşüm büyük olasılıkla başka bir yerel işindir ve iki iş aynı düşümü sahiplenirdi. İş #506'nın birleştirme sorusuna (`ReconcilePersonScopeAsync`'in çok izli sonucu) düşer.
- **Kapanan yol:** bayat bir B revizyonundan KA ile KB2 iki ayrı düşüm olarak kalıyordu. Bugün `/scope` yalnız `Created` ve anahtarsız işte soruluyor (`PaymentRequestService.cs:938`) ve `(LicenseId, WpfCustomerId, SaleScope)` indeksi tekil değil (`LicenseDbContext.cs:887-890`).
- **Ayrı tablo yok.** D3'ün `ActiveSaleScopes` tablosu, sunucu imajı geri alınınca bayat aktiflik satırı bırakıyordu (hakem 2 ölümcül bulgusu). Bu kural defterin kendisini okur: geri alma penceresinde yazılan düşümler sonradan yine görünür, bayat durum kalmaz.
- **Satır (çapa) düzeyinde kalır.** Farklı üyelerdeki izler #506'nın birleştirme sorusuna düşer (`ReconcilePersonScopeAsync`, `PaymentRequestService.cs:1117`). Grup senkronu gecikmesinde başka üyedeki izin görülmesini kira sağlar: `members-stale`, (e) adımının üye kesişimi ve sunucu üyeleriyle yapılan uzlaştırma (§6.4). Ortak bakiye M10 ile uyumludur. Ortak bakiye ekibi aynı kişinin başka satırındaki çapayı da 409 yapmayı #506'nın meşru çok izli akışını bozduğu için reddetmişti (ortak bakiye §13 sonuç notu).
- Mod Off'ta kural yok; bugünkü davranış.

### 6.7 #506 ve ortak bakiye ile etkileşim

- **#506:** kişi×kapsam işi yerelde tutulur (`PaymentJobRepository.cs:370`, `FindOrCreateForPerson`). Kira onun sunucu ikizidir. Sahip kararı 2 ("tekrar gönderilsin mi?") ve 4 (birleştir) soruları yerel işten ve sunucu kayıtlarından birlikte kurulur.
- **Ortak bakiye:**
  - Havuz kipi düşülen tutarı değiştirir. Kira ve §6.6 kuralı ondan bağımsızdır.
  - Ortak bakiye S2'nin yeniden yazdığı apply (`DrawAsync`), §6.6 kuralını **çapa** düzeyinde korumalıdır.
  - `ClientVersion` yardımcısı ortaktır.
  - Havuz kipinin `memberIds`'i ile kiranınki aynı kaynaktan gelir.
  - PersonKey ortak bakiyenin `S(a)` kişisidir. Ortak bakiye S1, `GroupId` kolonunu BIN2 harmanına alır. O gelene kadar bu spec ham `GroupId`'yi sunucuda bellekte ordinal karşılaştırır; ortak bakiye §4.13'ün yöntemi budur ve BIN2'den sonra da doğru kalır.

### 6.8 Sayısal örnek: iki bilgisayar, bir kişi

Lisans geneli ayar: eşik 500 ₺, kargo ücreti 60 ₺. Kişi `ornek_kisi`: T (TikTok, GUID'li sahip, telefonlu) ve I (Instagram), aynı GroupId. PC-A yayın bilgisayarı, PC-B ofis bilgisayarı.

| Zaman | Olay | PC-A kümülatif | PC-B kümülatif | Sunucu (kişi) |
|---|---|---|---|---|
| Dün | Yayın S1, PC-A: T K1 150 ₺ ve I K2 200 ₺ basıldı; S1 devredildi | 350 | 350 | 350 |
| Bugün 21:10 | S2 canlı, PC-A: T K3 120 ₺ basıldı ve gönderildi; T K4 80 ₺ kuyrukta | 470 | 350 (S2 görünmez) | 470 |
| 21:12 | PC-B kümülatif "Ödeme iste": `begin` → `stale-total {470, liveElsewhere}` | — | engellendi, mesaj yok | — |
| 23:40 | PC-A yayını bitirir, K4 basılır; etiketler 23:40:30'da gider, devir 23:41'de | 550 | en geç 23:41:30'da 550 | 550 |
| Ertesi gün 10:00:05 / :06 | PC-A ve PC-B aynı anda kümülatif ister | PC-A: `Sending` (kira 120 sn) | PC-B: `in-progress-elsewhere {PC-A}` | — |
| 10:00:20 | PC-A: 550 ≥ 500 → ücretsiz kargo, 550 ₺ teslim edildi | — | — | Delivered 550 |
| 10:03 | PC-B yeniden dener → `already-requested`: "PC-A'dan 550 ₺ istendi (10:00); tekrar gönderilsin mi?" | — | soru | — |

Bugünkü davranışta 10:00'da iki bilgisayar da mesaj gönderirdi: PC-A 550 ₺, PC-B yalnız kendi gördüğü kısım. Müşteri iki ayrı istek alırdı. Yeni davranışta PC-A'nın süreci teslimden önce çökerse kira 10:02:05'te dolar. PC-B'nin bir sonraki denemesi kaydı `Unconfirmed` yapar ve şunu sorar: "PC-A'dan 550 ₺ istenmiş olabilir (sonucu bilinmiyor)". Canlı bilgisayar kendi canlı yayını için (`session:S2`) yayın sırasında da isteyebilir; ön gönderimden sonra sunucu toplamı yerelle tutar.

## 7. Kargo

### 7.1 Sahiplik

- Mod On'da dosya ve etiket–dosya bağı yalnız sunucuda yazılır. Yerel `Shipment` tablosu salt okunur bir aynadır; tetikleyici ABORT eder.
- Dekont ekranı `attach-shipment` ve `shipment-decision` komutlarını kullanır. Eşik, `EvaluateAfterPayment` (`ShipmentService.cs:121-153`) mantığıyla, yanıttaki sunucu havuzu üzerinden değerlendirilir.
- Havuz, asıl müşteri satırının bütün Pending/Held dosyalarıdır. Bugünkü gibi satır bazlıdır (`ShipmentRepository.cs:71-94`); kişi (grup) bazına çıkarılmaz.
- Mod On'da `ShipmentSyncService` (`ShipmentSyncService.cs:128-163`) gönderim yapmaz. `/since` çekmesi zaten uygulanmıyordu; o da kalkar. Dosyalar akışla iner.
- Sunucu mod On'da `shipments/sync`'i yok sayar. Tek istisna, açılış penceresindeki kopyanın (§4.0) v2 gönderimidir (§8.4). Bu durumda dosya Shipped değilse ve `CommandedAt` boşsa karar alanları (Status, HeldAt, ShippedAt) alınır (Shipped'e geçişte iptalli bağlar §7.2'deki gibi çözülür), yeni dosya eklenir. Tutar push'tan değil bağlardan türetilir; bağlar sonradan `orders/sync` ile gelirse K7 dosyayı yeniden türetir (kargo gönderimi 30 sn'de, etiket gönderimi 60 sn'de bir koşuyor, `ShipmentSyncHostedService.cs:19`, `SessionOrderSyncHostedService.cs:20`; dosya çoğu zaman bağlarından önce gelir).
  - **Pencerede kargoya verilen dosya.** Shipped'e geçişte dosya o anki (çoğu zaman eksik) bağlarla türetilip donar ve K7 Shipped dosyayı atlar. Karar ise yerelde, gönderilmeyi bekleyen bağlarla verilmişti; yerelde kargolanmış dosyaya bağ kurulamaz (`ShipmentService.cs:79`), yani aynı kopyanın sonradan K4 istisnasıyla kurduğu bağ kararın dayandığı bağdır. Bu yüzden K4 pencere bağının hedefi pencerede kargolanmış bir dosyaysa (Shipped, türetilmiş, `CommandedAt` boş, `OriginReplicaId` çağıran; türetilmiş bir dosyayı komutsuz kargolayan tek yol budur) `orders/sync` dosyayı o bağlarla bir kez daha türetir. Komutla kargolanmış dosyaya ve kabul edilmiş dosyaya dokunulmaz; onarım işi Shipped dosyayı yine atlar. Sayı günlüğe yazılır (§10.5).
  - Shipped'e geçişte iptalli bağ yalnız türetilmiş dosyada çözülür (§7.2); kabul edilmiş dosya saklanan tutarıyla ve bağlarıyla donar.
  - **Kalan yarış (bilerek).** Pencerenin kargo gönderimi ile bağları arasında başka bilgisayarın `attach-shipment`'ı aynı etiketleri açık bir dosyaya bağlarsa pencerenin K4 bağı yok sayılır (`shipment-server-owned`, `current`): kopya sunucunun bağını alır, kayıt `SalesConflictLog`'a düşer; kolide giden ürün o açık dosyada sayılır. Pencere mod açılışından sonraki ilk turla sınırlıdır. Shipped'i bağlar gelene kadar ertelemek seçilmedi: sunucu kopyanın hangi bağları göndereceğini bilemez, kargo kuyruğu boşalmaz ve ilk tur (§8.4-1) takılırdı.
- **Shipped tek yönlüdür (S1, her kipte, v1 dahil).** Sunucuda Shipped olan dosyaya `shipments/sync` Status, CumulativeAmount, HeldAt ve ShippedAt yazmaz; CustomerId kanonikleştirmesi bugünkü gibi sürer. Bugün bu uç bütün alanları koşulsuz eziyor (`LicensesShipmentsSyncController.cs:121-126`); bayat bir yerel ayna kargolanmış dosyayı Held'e geri çevirip yeni etiketi ona bağlatabilirdi. Yerelde de Shipped son durumdur (`ShipmentService.cs:79`, `:183`); meşru bir geri dönüş yolu yok. v2 yanıtı dosya başına güncel satırı döner; Ws onu guard altında yerel aynaya yazar. Eski istemci yanıtı atar (`ShipmentSyncService.cs:108`); sunucu yine de doğru kalır.

### 7.2 Sahip kararı Y5: bağlandıktan sonra iptal edilen etiket

İptal **bağı çözmez**; tutarı ve havuzu türetme düşürür (DM7). Bağın kalması iki nedenle şart:
- Kodda `ShipmentId`, "dekontla ödendi"nin tek işaretidir: `CorrectedPaymentRequests.cs:187-193` (`attached`), Basılanlar'daki "Ödendi" rozeti (`PrintedLabelsViewModel.cs:360-363`) ve `PaidPrice`'ın saklanma koşulu (`LabelRepository.cs:232`). Bağ çözülseydi yanlışlıkla iptal edilip geri alınan etiket "ödenmedi" görünür, sonraki fiyat düzeltmesi `PaidPrice` yazmaz ve #508 planı iade yerine yeniden istek önerirdi.
- Geri alma etiketi kendiliğinden aynı dosyaya geri katar; dosya Shipped olduysa aynı tutar iki dosyada sayılmaz.

| Olay | Dosya durumu | Etki |
|---|---|---|
| İptal (komut ya da kabul edilen push) | Pending, Held, RecipientPays | Bağ kalır; tutar ve havuz etiketin fiyatı kadar düşer |
| İptal | Shipped | Bağ kalır, tutar donmuştur (karar verilmiş; iade ayrı iş) |
| İptali geri alma | Pending, Held, RecipientPays | Bağ kalır; tutar ve havuz eski hâline döner, "Ödendi" rozeti yerinde |
| İptali geri alma | Shipped | Bağ kalır, tutar donmuştur. Etiket iptalden önce koliye girmişti |
| Dosya kargoya verilir (`shipment-decision`) | Pending/Held → Shipped | Türetilmiş dosyada o anda iptal edilmiş bağlı siparişlerin bağı çözülür: ürün kolide değil, tutar onlarsız donar. Sonradan geri alınırsa etiket bağlanmamış olur ve sonraki dosyaya girer. Kabul edilmiş (türetilmemiş, §4.10-6) dosyada bağ kalır: saklanan tutarı iptalle düşmediği için o siparişi içerir, bağ çözülseydi aynı tutar iki dosyada sayılırdı |
| `set-code-price` | açık | Tutar yeniden türetilir; bugünkü `CorrectPrices` farkının (`LabelService.cs:414-421`) karşılığı |
| `confirm-backup` | — | Geçici yedek hiç bağlanmamıştı (`GetUnattachedByCustomer` süzgeci, `LabelRepository.cs:639-654`); değişiklik yok |

**Sayısal örnek** (tek satırlı müşteri `ornek_alici`, eşik 500 ₺, kargo ücreti 60 ₺)
1. S1'de K1 300 ₺ basıldı; 360 ₺ istendi; 360 ₺'lik dekont geldi. `attach-shipment` ile F1 = 300. Havuz 300 < 500; operatör "Beklet" der, F1 Held olur.
2. S2'de K5 250 ₺ basıldı; dekont geldi. `attach-shipment` ile F1 = 550 ≥ 500. Çekmece "ücretsiz kargo" teklif eder; operatör kararı sonraya bırakır.
3. Müşteri K5'i istemez ve PC-B K5'i iptal eder.
   - **Bugün:** F1 550'de kalır. Kargo yanlışlıkla ücretsiz görünür, panelde de 550 yazar.
   - **Yeni:** K5 F1'e bağlı kalır, F1 = 300 olur. Havuz 300, eşiğe 200 ₺ kalır. Eşiğin altına inildiği için "ücretsiz kargo kazandın" mesajı (`PaymentRequestService.cs:1457`, tutarı havuzdan okur) artık teklif edilmez.
4. İptal yanlışlıkla yapılmıştı; PC-B geri alır. F1 yeniden 550, havuz 550, K5 Basılanlar'da yine "Ödendi". Yeni dekont beklenmez.
5. F1 iptalden önce Shipped olsaydı F1 550'de donar ve K5 bağlı kalırdı; geri alma da hiçbir şeyi değiştirmezdi. F1, K5 iptalliyken kargoya verilseydi K5'in bağı o anda çözülür ve F1 300'de donardı; K5 sonradan geri alınırsa bir sonraki dosyaya girer.

### 7.3 Karar

`shipment-decision`, `ApplyDecision` (`ShipmentService.cs:177-207`) mantığıyla çalışır. Hedef Pending ya da Held ise kişinin öbür açık dosyaları da aynı karara uyar (U12). Kargoya verilen her dosyada iptalli bağlar çözülür (§7.2). `ShipmentStatusChanged` etiket kuralı sunucuda tetiklenir.

### 7.4 Geçiş

- **Mod Off'ta Y5 Ws'nin yerel yolunda uygulanır (W1).** O dönemde her dosyanın tek yazıcısı tek bir bilgisayardır (`OriginReplicaId`; `disable`'dan sonra bağlı etiketlerin sahibi, §4.10) ve Off'ta uzak etiket yoktur (§6.5-1). İki yazıcının farklı tutar üretmesi bu yüzden söz konusu değildir. `LabelService.Cancel` ve `Uncancel`, `CorrectPrices`'ın dosya bloğuyla aynı biçimde aynı yazma paketinde çalışır. Etiket bir dosyaya bağlıysa, geçici yedek değilse ve dosya Shipped değilse dosya tutarından fiyatı düşülür ya da geri eklenir; tutar `AttachLabels`'ın kattığı fiyatla aynıdır (`ShipmentService.cs:73-102`). Bağ değişmez. Shipment tetikleyicisi değişikliği kuyruğa alır ve `shipments/sync` gönderir. Push yanıtındaki `current`'ın uygulanması da aynı bloktan geçer (§5.2). Mod On'da bu blok atlanır, sunucu türetir (§5.1-a).
- **Fark tabanı: Ws ilk açılışta yerel dosyaları bir kez türetir (§8.2).** Y5 farkla çalıştığı için tabanın doğru olması gerekir. Ws öncesinde (≤0.9.10) iptal edilmiş bağlı etiket açık dosyanın tutarında hâlâ durur (`LabelService.cs:125-162` dosyaya dokunmuyor). Türetme olmasaydı böyle bir etiketin Ws'de iptali geri alınınca fiyatı ikinci kez eklenirdi (F = 400, K5 150 iptal, F 400'de kalır; Ws'de geri alma → 550, gerçek 400; 500 eşiğinde yanlış ücretsiz kargo) ve mod hiç açılmayacak tek PC'li lisansta sapma kargolamaya kadar kalırdı. Ws öncesinde dosyaya yalnız yerel etiket bağlanabildiği için türetme kesindir: Shipped olmayan her yerel dosyanın tutarı = bağlı, iptal edilmemiş, geçici yedek olmayan etiketlerin fiyat toplamı (yerel DM7). Fark ve dosya sayısı günlüğe yazılır (ad yok); `SalesSyncState.ShipmentsDerivedAt` yazılır. Shipment tetikleyicisi değişikliği gönderir. Y5'in geçmişe dönük etkisi (sahip kararı) her PC'de bu anda görülür.
- Mod Off'ta Y5 dosyayı her seferinde yeniden türetmez, farkla çalışır. Gerekçe: `disable`'dan sonra karışık bir dosya (§4.10) başka bilgisayarın etiketlerini de taşır; yerelde olmayan etiketleri türetme düşürürdü.
- **Bağlı dosya yerelde yoksa** (mod Off'ta `disable` turunun sildiği başka bilgisayarın dosyası): Y5 bloğu ve `CorrectPrices`'ın dosya bloğu bu dosyayı atlar, istisna atmaz. Bugün `CorrectPrices` bulunamayan dosyada `PriceCorrectionConflictException` atıp paketi geri sarıyor (`LabelService.cs:414-421`); o etiketin iptali, geri alınması ve aynı kodun fiyat düzeltmesi kilitlenirdi. `Classify` (`:339-355`) bulunamayan dosyayı bugün düzeltilebilir sayıyor; yeni sınıf "başka bilgisayarın dosyasında": durumu bilinmediği için (Shipped olabilir) fiyatı düzeltilmez, sonuçta ayrıca listelenir.
- Mod Off'ta "kargoya verilirken iptalli bağı çöz" kuralı uygulanmaz: o kural türetilmiş tutara dayanır. Ws'nin ilk türetmesinden sonra Off'ta da tutar iptalli etiketi içermez. Tek sonucu nadir sıradır (iptal, kargo, geri alma): geri alınan etiket kargolanmış dosyaya bağlı kalır ve sonraki dosyaya girmez; bugünkü davranış budur.
- Eski sürüm (≤0.9.10) bilgisayarda bugünkü davranış sürer; Ws'ye güncellenince ilk açılış türetir, yeni iptaller düşer.
- Mod açılırken Shipped olmayan dosyalar türetilir. Fark raporu `enable` kuru koşusunda gösterilir; açıklanamayan fark açılışı engeller (§4.10-6, -7). Verilmiş kararlar değiştirilmez.

## 8. Geçiş

### 8.1 Sıra

| Adım | Ne | Koşul ve doğrulama |
|---|---|---|
| 0 | Spec onayı. Burak prod'da Orders, StreamSessions ve Shipments satır sayısını salt okunur sorguyla alır; `rowversion` ekleme süresi `restore-verify` kopyasında ölçülür. | Prod'a yazım yok. En uzun tek komut ≈ 20 sn'nin (30 sn `CommandTimeout`), toplam ≈ 40 sn'nin (60 sn `/ready`) altında değilse göç bölünür (§3.1 ölçütü). |
| 1 | **S1** merge ve otomatik deploy | Yayın penceresi dışında (Paz/Pzt/Çar/Per 20:00–01:00 TR değil). EndedAt tekdüze, K1 (ProductId düzeltmesi), K2 (damga yok, son-gelen-kazanır), K3, v1 kapanışında devir. Onarım işi eski bitmiş oturumları devreder. Doğrulama gerçek etkiden: dolu `Orders.SessionReleasedAt` sayısı, `sales/changes` ilk sayfası. |
| 2 | **S2** merge ve deploy | Komutlar mod Off'ta 409 `mode-off`. Davranış değişmez. |
| 3 | **S3** merge ve deploy | Kira ucu ve kapsam kuralı yalnız mod On'da etkili; `payment-requests/backfill` her kipte açık. CLI'lar gelir. |
| 4 | **W1 → W2 → W3** sırayla master'a | **Sürüm kuralı:** W1'in merge'ünden W3'ün merge'üne kadar master'dan plansız Velopack sürümü çıkmaz; acil düzeltme sahadaki sürümün etiket dalından çıkar. Bilinçli bir ara sürüm (asgari değer S1 + W1, §13) çıkarılabilir. Asıl kilit `featureLevel`'dır: 3 yalnız W3'ü içeren derlemede bildirilir. Yarım bir derleme sahaya inse bile mod kapısını geçemez (§4.10-2). |
| 5 | Ws Velopack sürümü; Burak `SalesSync__MinClientVersion`'ı compose ortamına Ws sürümüyle yazar (sunucudaki `.env`, ortak bakiye §7.2'deki karşılığıyla aynı yol); PC'ler sırayla güncellenir | Her PC'nin ilk açılışı §8.2. Mod Off'ta davranış bugünküyle aynı, artı Y5'in yerel yolu (§7.4, §8.5). Değer boşken mod açılamaz (§4.10-1). |
| 6 | `sales-sync status` (kuru); Burak `sales-sync shipping set` ile lisans geneli kargo ayarını yazar | Kopyaların bildirdiği değerler yan yana gösterilir (§6.2). |
| 7 | 7 gün boyunca eski sürüm izi yok → `sales-sync enable --license <id>` kuru → rapor sahibe → `--apply` | Yayın penceresi dışında, Burak'ın onayıyla. Açıklanamayan kargo farkı varsa önce o dosyalar çözülür (§4.10-6). |
| 8 | Her PC'nin mod On'daki ilk turu (§8.4) | Durum satırı "yetişiyor", ardından "güncel ✓". `sales-sync status`'ta her kopyanın `SeenMode`'u 1. |
| 9 | Saha kontrolleri (§10.4) | Gerçek etkiden. |
| 10 | **S4** daraltma | Mod On'dan ≥ 2 hafta sonra. |

### 8.2 Ws'nin ilk açılışı (mod Off)

1. Göç 049 koşar ve ReplicaId üretilir. `hello` gönderilir; yerel kargo ayarı, `featureLevel` ve `seenMode = 0` bildirilir.
2. Normal giden kuyruklar boşaltılır.
3. Geri doldurma `sales-backfill-out` imleciyle (rowid) yapılır: önce bütün yerel oturumlar, sonra etiketler, sonra dosya bağları (dosyanın `OriginReplicaId`'si bu kopya olarak); `sales/backfill`'e, 200'lük partilerle. `SyncedAt` ve `Revision`'a dokunulmaz (F05). Sunucu yalnız boşu doldurur ve bildirim üretmez (§4.3). Kaçmış kapanışlar bu adımda kapanır.
4. **Birim ayrışmasının onarımı.** Yanıttaki `diverged` satırlarından `localWins = true` olanlar normal kuyruğa yeniden alınır (`SyncedAt = NULL`, `Revision + 1`, tek işlemde) ve olağan v2 push'uyla gider. Mod Off'ta damga olmadığı için K2 son-gelen-kazanır ile yerel değeri yazar; stok defteri birleştirilmiş durumdan beslendiği için kaybolmuş iptalin stok iadesi de bu anda olur (`032_sync_ack_revision.sql:8`). `localWins = false` olanlar yalnız `SalesConflictLog`'a `backfill-diverged` olarak yazılır; sunucunun değeri mod On'un ilk turunda iner (§8.7). Sayılar günlüğe yazılır.
5. `hello {backfillCompleted: true}` ancak 4. adımda kuyruğa alınan satırların hepsi `applied` yanıtı aldıktan sonra gönderilir. Mod kapısı bu bayrağı her kopyada istediği için (§4.10-2) mod, F05 öncesi kayıp güncellemeler sunucuya geçmeden açılamaz.
6. Ödeme geçmişi `payment-history-out` imleciyle `payment-requests/backfill`'e gönderilir (§6.4); bitince `hello {paymentHistoryCompleted: true}`.
7. Müşteri toplamları yerel etiketlerden bir kez türetilir; değişen satır sayısı günlüğe yazılır.
8. Shipped olmayan yerel dosyaların tutarı bağlardan bir kez türetilir (§7.4, Y5 fark tabanı); `ShipmentsDerivedAt` yazılır. Tetikleyici değişen dosyaları `shipments/sync` ile gönderir.

Geri doldurma idempotenttir. Yarıda kesilmesi, yeniden denenmesi ya da `cursorReset` zararsızdır.

### 8.3 Mod kapısı (mühendislik kararı M1)

Koşullar §4.10'da. Kapı neden dinamik değil? Dinamik kapı her istekte "bütün PC'ler Ws+ mı" diye bakardı. Eski bir PC'nin tek bir kalp atışı lisansı yayın ortasında Off'a düşürürdü. O zaman devredilmiş yayınlar düzenlenemez olur, kargo yerel otoriteye geri döner ve kira kalkar; bunun güvenli bir dönüş tarafı yok. Ortak bakiye §7.2 de aynı gerekçeyle statik kapı seçti. Eski PC riskini anahtar değil, çalışma anı korumaları kapatır:

| Mod On'da eski sürüm (≤0.9.10) | Sonuç |
|---|---|
| Yeni etiket ekleme ve baskı doldurma | Kabul edilir (gerçek satış). |
| Devredilmiş yayında fiyat, iptal ya da yedek değişikliği | Yok sayılır; `LastLegacySalesPushAt` yazılır, uyarı düşer (K5). Damgalı birimler her durumda korunur (K2). |
| `shipments/sync` | Yok sayılır, iz yazılır. (Açılış penceresindeki Ws'nin v2 gönderimi bu satıra girmez; §4.0, §7.1, §8.4.) Shipped dosya her kipte tek yönlüdür (§7.1). |
| Önizleme ve apply | 409 `client-update-required`. GUID'li kişide mesaj engellenir. |
| Başkalarının değişikliklerini görmek | Göremez. Güncellenene kadar bayat görünüm; para yolları yukarıdaki satırlarla korunur. |

### 8.4 Mod On'daki ilk tur (her PC)

Sunucuda `enable --apply` ile her PC'nin modu ilk görmesi arasında 30 sn'ye kadar bir pencere var. O pencerede verilen kargo kararı ve kurulan bağ Off kurallarıyla kuyruğa girer. Bu yüzden istemci modu **ancak kuyruklarını boşalttıktan sonra** yerelde açar ve o ana kadar her isteğinde `SeenMode = 0` bildirir. Sunucu da açılış penceresindeki kopyanın (§4.0) `SeenMode = 0` gönderimlerini Off kurallarıyla alır: kargoda §7.1, etiket bağında K4 istisnası, birimlerde K5 istisnası (ii). Damgalı birim ve `CommandedAt`'li dosya her durumda korunur.

1. `sales/state` modu 1 gösterir. Bütün giden kuyruklar boşaltılır (`SeenMode = 0` ile): bekleyen ödeme kayıtları, müşteri, oturum ve etiket, kargo, dekont. Biri boşalmazsa yerel mod 0 kalır ve bir sonraki turda yeniden denenir. Durum satırı "Senkron açılıyor; bekleyen gönderim var" der.
2. Kargo ayarı önbelleğe alınır. Tek işlemde, SQLite yazma kilidi altında:
   - Kargo kuyruğu ve canlı olmayan oturumların etiket kuyruğu **yeniden** denetlenir. Boş değilse (ör. 1. adım gönderirken kaydedilen bir dekontun kargo yazımı) işlem hiçbir şey yazmaz, yerel mod 0 kalır ve bir sonraki turda 1. adımdan devam edilir. Canlı oturumun etiketleri bu denetime girmez: iki kipte aynı yargılanırlar.
   - `SalesSyncState.Mode = 1` yazılır; bundan sonraki istekler `SeenMode = 1` taşır. Bu andan sonra `TR_Shipment_ServerOwned_*` yerel kargo yazımını ABORT ettiği için gönderilmemiş kargo kararı oluşamaz.
   - Yerelde `ReleasedAt`'i dolu olan kendi bitmiş oturumları `Authority = 'server'` olur. Böylece ilk yetişme birkaç tur sürse de (tur başına 10 sayfa) bu oturumlarda yerel yol seçilmez; yerel yazım push'ta K5 ile reddedilip kaybolmaz. `ReleasedAt`'i yerelde bilinmeyen kendi bitmiş oturumları (onarım işinin devrettiği miras oturumlar, §4.9-a) `FirstCatchUpAt` yazılana kadar düzenlemeye kapalıdır (§5.3).
3. Akış 0'dan çekilir. Kalan kendi bitmiş oturumları devredilmiş olarak gelir ve `Authority = 'server'` olur. Uzak yayınlar eklenir. Yerel `Shipment` tablosu sunucu satırlarıyla değiştirilir (guard altında). 2. adımın kilit altındaki denetimi ve ondan sonra ABORT eden tetikleyici yüzünden burada `SyncedAt IS NULL` yerel dosya oluşamaz; bunu bir test sabitler (§10.3). Önceki taslaktaki "dur ve 1. adımdan yeniden dene" kuralı kalktı: mod 1 yazıldıktan sonra kargo gönderimi kapalı ve sunucu kopyanın 0'ını artık pencere saymıyor (§4.0), yani o dosya hiç boşalmaz ve ilk yetişme hiç bitmezdi.
4. Ufka ulaşılınca (boş sayfa ve bekleyen öğe yok) toplamlar tek işlemde türetilir; `FirstCatchUpAt` yazılır.

O anda canlı bir yayın varsa yerel kalır ve normal yoldan devreder.

### 8.5 Karışık sürüm ve kip geçişleri

| Durum | Davranış |
|---|---|
| Mod Off, Ws ile eski sürüm yan yana | Bütün bilgisayarlar bugünkü gibi. Ws ek olarak yeni alanları gönderir, kapanışı kuyruk boşalınca devreder (`Authority` yine `local` kalır), AttachLabel'dan sonra etiketi kuyruğa alır (tetikleyici), toplamları türetir, Y5'i yerel yolda uygular (§7.4) ve ödeme kayıtlarını kirasız yazar (§6.4). Sunucu: K1, K3, tekdüze EndedAt, protokolsüz istek izi; damga yok. |
| Mod On, bütün PC'ler Ws | Tam tasarım. |
| Mod On, eski PC döner | §8.3 tablosu. Admin'e uyarı. Mod kendiliğinden kapanmaz. |
| On → Off (`disable`) | Aşağıda. |
| Off → On (yeniden) | §8.4 baştan. |

**On → Off (`disable`).** Sunucu modu 0 yapar, damgalar kalır ve Shipped olmayan dosyaların `OriginReplicaId`'si bağlı etiketlerin sahibine göre yeniden yazılır (§4.10). Her Ws, `sales/state`'te ya da bir `mode-off` yanıtında Off'u görünce şu turu koşar. Tur tamamen bitene kadar, yani yerel `Mode = 0` ∧ `PendingSalesCommand` boş olana kadar `SeenMode = 1` bildirir (§2); bu yüzden sürüm düşürmeden önce turun bittiği sunucudan görülür (§8.6). Açılışta `Mode = 0` ∧ `PendingSalesCommand` dolu bulunursa (süreç 2. ile 3. adım arasında kapanmış) 3. adım yeniden koşar. Mod On dönemini hiç görmemiş Ws (o dönem kapalıydı) da `sales/state`'teki `modeChangedAt`'i yerelde son gördüğünden yeni bulursa 1. ve 2. adımı koşar: dönem içinde başka bilgisayarın onun etiketleriyle açtığı dosya ona geçmiş olabilir (§4.10).
1. Akış son kez boş sayfaya kadar uygulanır (akış Off'ta da çalışır, §4.4). Bu son tur **dosya öğelerini de** uygular: tutulacak dosyaların sunucudaki son hâli (son mod On turundan sonra başka bilgisayarın verdiği komutlar, yeniden yazılan `OriginReplicaId`) yerel aynaya iner. Aksi hâlde örneğin PC-A kapalıyken PC-B'nin kargoladığı F, PC-A'da Held kalır, yerel otoriteye bu hâliyle döner, yeni etiket ona bağlanır ve `shipments/sync` sunucudaki Shipped dosyayı Held'e çevirirdi (sunucunun Shipped'i tek yönlü tutması bunu ayrıca kapatır, §7.1). Kendi satırlarının `ServerVersion`'ı güncellenir; sonraki yerel düzenleme damgalı birimde bile `baseVersion` eşitliğiyle kabul edilir (K2, K4).
2. Tek SyncApplyScope işleminde:
   - `Mode = 0` olur, kendi oturumları `Authority = 'local'` olur.
   - `Origin = 'remote'` oturum ve etiketler ile `RemoteLiveSession` silinir; lisans değişimi temizliğinin aynısıdır (§5.4). Müşteri toplamları yeniden türetilir. Off'ta uzak etiket kalmaz; kira olmadan kümülatif çift tam istek penceresi (R1) yeniden açılmaz ve bayat önbellekten tutar istenmez (§6.5-1).
   - Yerel `Shipment` tablosunda, 1. adımda tazelenmiş `OriginReplicaId`'si bu kopya olmayan ve Shipped olmayan satırlar silinir; kalanlar yerel otoriteye döner. Sunucu bu değeri bağlı etiketlerin sahibine göre yazdığı için (§4.10) dosya, etiketleri yerelde duran bilgisayarda kalır: mod On'da PC-B'nin PC-A etiketleri için açtığı dosya PC-A'ya geçer, PC-A'nın sonraki dekontu onu bulur ve eşik doğru havuzdan hesaplanır. Her açık dosyanın tek yazıcısı vardır; `shipments/sync`'in son-gelen-kazanır'ı iki bilgisayarın tutarını birbirine ezdiremez. Yalnız karışık dosyada (§4.10) öbür bilgisayarın etiketleri yerelde olmayan bir dosyaya bağlı kalır (risk 15).
   - Lisans geneli kargo ayarı yerel `AppSettings.Shipping`'e yazılır (§6.2).
3. Bekleyen komutlar, guard dışında ve normal yerel yazım olarak çevrilir (§5.5): kuyruğa girer ve push'la gider. Basılmış kâğıt yeniden teklif edilmez. Örtü, yerel yazım işlenene kadar kalkmaz.
4. Bundan sonra bugünkü davranış. Push'a dönen `ignored` yalnız gerçekten bayat gönderimde olur ve istemci `current`'ı uygular (§5.2).

### 8.6 Geri dönüş kuralları

- **S1 otomatik geri alınırsa** (`/ready`): imaj S1 öncesine döner, göçler kalır. O sırada v2 konuşan istemci yok, çünkü Ws S3'ten sonra çıkar. Pencerede eski kod kaçan devir damgaları ve, v0.9.10'un bayat oturum gönderimiyle, devredilmiş yayının boş `EndedAt`'ini bırakabilir; S1 geri gelince onarım işi ikisini de kapatır (§4.9 b ve d).
- **S2 ya da S3 otomatik geri alınırsa:** bir önceki imaj birim damgalarına zaten uyar (K2 S1'de). Komut ve kira uçları 404 döner:
  - istemcide devredilmiş yayın düzenlemesi kapanır
  - ödeme isteği GUID'li kişide engellenir (fail-closed, §6.5)
  - canlı yayın sürer
  - kapanışlar istemcinin yeniden göndermesiyle devreder (§5.2); onarım işi kalanları kapatır (§4.9)
- **YASAK — mod On bir lisans varken S1 öncesi imaja elle sabitlemek.** Eski kod damgalara uymaz: bayat push komut kararını geri alır, ProductId yine NULL'lanır, devir yazılmaz. Tespit: `SELECT COUNT(*) FROM Licenses WHERE SalesSyncMode = 1` > 0. `deploy/README.md`'ye ortak bakiye §7.4 biçiminde bir uyarı eklenir. Etiketin kaynağında `git cat-file -e <sha>:OrderDeck.Shared/Sales/LabelRules.cs` başarısızsa o imaj S1 öncesidir.
- **WPF'in ≤0.9.10'a dönmesi (Velopack, istisnai destek eylemi).** Eski ikili 049'un tetikleyicilerini bilmez ve `Mode = 1` ya da `Authority = 'server'` durumundan çıkamaz: `TR_Shipment_ServerOwned_*` ve `TR_Label_ShipmentLink` dekontun kargo adımını ABORT eder (dekont ekranı hatayı sessizce yutar, `DekontEkleViewModel.cs:467-473`); kendi geçmiş yayınlarında iptal ve düzeltme de düşer.
  - **Ön koşul:** lisans Off yapılmış (§8.5) **ve** o PC Off turunu bitirmiş olmalı. Kanıt, `sales-sync status`'ta o kopyanın `SeenMode = 0`'ı (§2).
  - **Ws hiç açılamıyorsa** (dönüşün en olası nedeni) tur koşamaz. Destek için `deploy/README.md`'ye, eski sürüm kurulmadan önce yerel veritabanında koşulacak bağımsız bir sıfırlama betiği yazılır. Betik §8.5 adım 2'nin SQL karşılığıdır: `UPDATE SalesSyncState SET Mode = 0`; `UPDATE StreamSession SET Authority = 'local' WHERE Origin = 'local'`; uzak etiket, oturum ve `RemoteLiveSession` silme. Dosyalarda sunucunun kuralı (§4.10) yerel veriyle uygulanır, uzak etiketler silinmeden **önce**: Shipped olmayan dosya, bağlı etiketlerinin hepsi `Origin = 'local'` oturumlardaysa kalır, hiçbiri değilse silinir; karışık dosya varsa betik durur ve Burak'a listeler. Bekleyen komut varsa da betik durur ve listeler.
    - Betik yolunda akış yok; yerel ayna son mod On turundaki hâlinde kalır. Kargolanmış dosyanın yeniden açılmasını sunucunun Shipped'i tek yönlü tutması önler (§7.1); kalan bayatlık (tutar, Held/Pending) eski ikilinin `shipments/sync`'iyle sunucudaki değerin üzerine yazılabilir. Burak dönüşten sonra o kopyanın açık dosyalarını `sales-audit` ile kontrol eder. Bu yol yalnız Ws hiç açılamadığında kullanılan istisnai destek eylemidir.
  - 049 kalır. Eski kod `RemoteLiveSession`'ı görmez; uzak oturum zaten kalmaz. Eski `GetActive`, `StartStream` ve kurtarma ekranı bu yüzden etkilenmez.
  - Sürüm notu: Off turu uzak etiketleri sildiği için eski kodun "Önceki yayınlardan basılmamış etiket var" sorusu (`LabelRepository.cs:96-107`) yalnız bu bilgisayarın kuyruğunu teklif eder.
  - `Migration049RehearsalTests` ayrıca `Mode = 1` ve `Authority = 'server'` durumundaki bir veritabanını önceki sürümün SQL'iyle açar: sıfırlama betiğinden önce ABORT'un görünür olduğunu, sonra dekont kargo adımının ve iptalin çalıştığını sabitler.

### 8.7 Kopya veritabanı ve yedekten geri yükleme

- `RestoreService.RestoreAsync` (`OrderDeck.App/Services/RestoreService.cs:44`) geri yüklemeden sonra yeni bir ReplicaId üretir. Bir sonraki `hello` eski kimliği `previousReplicaId` olarak taşır.
- Geri yükleme bütün veritabanı dosyasını değiştirir (`RestoreService.cs:44-80`); `SalesSyncState` de yedekten gelir. Mod açılmadan önce alınmış yedekte `Mode = 0`'dır. Bu yüzden:
  - İstemci geri yüklemeden sonra `hello` ve `sales/state` ile modu tazelemeden hiçbir gönderim yapmaz.
  - Sunucu geri yüklenmiş kopyayı (`PreviousReplicaId` dolu) hiçbir zaman açılış penceresi saymaz (§4.0). Yedeğin kuyruğundaki bayat değerler (ör. yedekten sonra Off'ta verilmiş bir iptali geri alan "basılmış, iptalsiz" satır, ya da sonradan verilmiş bir kargo kararını geri alan dosya satırı) böylece On kurallarıyla yargılanır: devredilmiş oturumda K5 reddeder ve `current` yerel satırı düzeltir; `shipments/sync` yok sayılır.
- Sunucu, önceki kopyanın devredilmemiş oturumlarının sahipliğini yeni kopyaya geçirir. Koşul: yeni kopyanın ilk `hello`'sundan sonra önceki kopyadan hiç istek gelmemiş olmalı. Geçiş günlüğe yazılır.
  - Ek koşul (Y8, §11): yeni kopyanın donanım parmak izi öncekinden farklıysa ya da bilinmiyorsa geri yükleme başka bir makinededir ve Y8 geçerlidir. Devrediliyor oturum devralınmaz; sahip dönmeyecekse CLI `force-end` (§4.10). Canlı oturum yalnız önceki kopya ≥ `SalesSync:ForceEndSilenceMinutes` susmuşsa devralınır (Y7). Aynı parmak izinde (aynı makinede geri yükleme) kopya hemen devralır (sahip kararı 2026-10-14, §11).
  - **Uygulama (S2).** Üç çağıran (`sessions/sync`, `orders/sync`, `sales/backfill`) tek yoldan yargılar: `SalesReplicaService.CanInheritAsync(…, TakeoverPhase, …)`. İlk koşul (önceki kopya yeni kopyanın ilk görüldüğü andan beri susuyor, zincir boyunca) ve Y8 ek koşulu birliktedir; hâl push'tan **önceki** sunucu satırından okunur (gönderimin kendi `EndedAt`'i canlı oturumu devrediliyor göstermez). S1'in `SilentPredecessorsAsync`'i bunun içine katıldı. Ek kurallar:
    - **Sahipteki klon izi:** "aynı makine" için iki parmak izi de bilinmeli ve eşit olmalı, **iki kopyada da** `HardwareFingerprintConflictAt` boş olmalı. Önceki kopyada klon izi varsa yayının hangi makinede açıldığı bilinemez; belirsizlikte kopya başka makine sayılır (Y8 korunur).
    - **Sahip olmayanın kapanışı:** v2'de sahip olmayan (devralamayan) kopyanın `EndedAt`'i ve `Release = true`'su yazılmaz, yanıtta `ignored: session-owned-elsewhere` döner; `EndedAt` sunucuda zaten dolu olsa da. Devralamayan geri yüklenmiş kopya "devredildi" sanıp oturumu yerelde `server` yapmaz.
    - **Zorla bitirilmiş oturum (K5 istisnası i):** geri yüklenmiş kopyanın sahip sayılması oturumun bitirildiği andaki hâline göredir (`EndedAt = ReleasedAt` canlı, farklı ise devrediliyor; §4.10).
  - W1 bu kural sunucuda olmadan açılmaz (§13).
- Disk klonu (aynı ReplicaId, farklı donanım parmak izi): `hello` ilk parmak izini korur, `HardwareFingerprintConflictAt`'i bir kez yazar ve hiç silmez; admin uyarısı düşer. Hangi makinenin özgün olduğu bilinemediği için o andan sonra kopya açılış penceresine girmez (§4.0) ve geri doldurmada yerel kazanmaz (§4.3); `sales-audit` sayar ve mod açılamaz (§4.10-2).
- Geri yüklenen eski veritabanındaki satırlar akış imleci geri sarıldığı için sunucunun son hâlini yeniden indirir. `SyncedAt` dolu satırlarda sunucu kazanır. D1'in "eski yedek iptali diriltir" açığı burada yok, çünkü geçmiş hiçbir zaman yeniden oynatılmaz; geri doldurma yalnız boşu doldurur ve ayrışma onarımı (§4.3, §8.2-4) geri yüklenmiş kopyada çalışmaz.
- Kalan risk: yedek anında `SyncedAt` NULL olan bir satır devredilmemiş bir yayına aitse, gönderildiğinde son-gelen-kazanır ile yazılır. Bugünkü riskle aynı sınıftadır, testle sabitlenir (§10.3).

## 9. KVKK

- **Akışta, okuma anında (S1, her zaman):** asıl projeksiyonu silinmiş müşterinin siparişi `DisplayName = null`, `MessageText = ""` ile iner. Bu hiçbir şeyi silmez; yalnız metnin yeni bilgisayarlara yayılmasını önler.
- **Kalıcı boşaltma tek bir ayara bağlıdır:** `SalesSync:ScrubOrderTextOnPurge`, **varsayılan kapalı**. Kapalıyken bugünkü politika sürer: yerel silme yalnız Customer satırını boşaltıyor (`CustomerRepository.cs:1496-1530`), Label satırlarına dokunmuyor; sunucu da Orders metnini saklıyor. Geri dönüşsüz bir silme olduğu için açılması sahip kararıdır (§11). Açılırsa yalnız ayar değişir; aşağıdaki kodun hepsi S1 ve W2 ile gelir ve ayara bakar.
- **Ayar açıkken sunucuda:** projeksiyonu silen tek yol `ShopperPurgeService` (`ShopperPurgeService.cs:171-221`, `MarkPurged` `:221`). Aynı işlemde o asıl kayda bağlı Orders satırlarında `DisplayName = null`, `MessageText = ""` ve `TextScrubbedAt = now` yazılır. ChangeSeq ilerlediği için silme bütün PC'lere iner. Silmeden sonra gelen ekleme (çevrimdışı PC'nin geç push'u ya da geri doldurma) da metni boş yazar (§4.2, §4.3).
  - Username, Code ve Price mali kayıt olarak kalır. Yerel politikayla aynı (`CustomerRepository.cs:1496-1500`).
- **Ayar açıkken yerelde:** `sales/state` ayarı bildirir (`SalesSyncState.ScrubOrderText`). Müşteri akışından gelen silme, o kişinin bütün yerel Label satırlarında (kendi ve uzak) DisplayName ve MessageText'i guard altında boşaltır. Satır, CustomerId, kod ve fiyat kalır. `TwoPcConvergenceTests.cs:572-598` sözleşmesi ("satış kaydı silmeden etkilenmez") korunur.
- Akıştan inen müşteri taslağı mezar taşı boşaltmasından geçer (`CustomerSyncRepository.cs:570`). Etiket inişi silinmiş bir kimliği diriltmez.
- `SalesCommandLogs`, `PaymentRequestRecords`, `SalesConflictLog` ve günlükler kişisel metin saklamaz; yalnız Id, tutar ve makine adı tutarlar.
- Yayılım: etiket metni artık daha çok bilgisayarda duruyor (§12). Silinmiş müşterinin metni yeni bilgisayara hiç inmez; daha önce inmiş metin ancak ayar açıkken silinir.

## 10. Doğrulama

### 10.1 Testcontainers (gerçek SQL Server, `SqlServerContainerFixture`)

InMemory'nin eşzamanlılık semantiği yok; bu yüzden bu testler gerçek SQL Server ister.
1. Üç tabloda `MIN_ACTIVE_ROWVERSION` ufku: commit olmamış, daha küçük seq'li bir yazım atlanmaz, sonradan iner.
2. Devir siparişleri aynı işlemde damgalar; siparişlerin seq'i oturumunkinden büyüktür; devirden sonraki ekleme `SessionReleasedAt`'i kopyalar ve akışa girer.
3. Onarım işi: geri alma penceresindeki sipariş (eski kod yolu taklit edilir) ve miras oturum devredilir; ikinci koşu etkisizdir; devrediliyor hâlindeki v2 oturumuna dokunulmaz.
4. Aynı koda eşzamanlı iki `set-code-price`: biri 409 `plan-stale`, DM5 korunur.
5. Komut ile sahibin geç push'u aynı satırda: jeton biri için 409 üretir. Komuttan sonra gelen push damgalı birimi değiştiremez (`ignored`).
6. Aynı müşteriye eşzamanlı iki `attach-shipment`: tek dosya açılır (applock).
7. Aynı siparişe eşzamanlı `cancel` ve `attach-shipment`: tutar DM7'yi sağlar.
8. İki kopyadan aynı kişi×kapsama eşzamanlı `begin`: biri 409 (filtreli tekil indeks). Üye kesişimi yolu da test edilir: farklı GroupId, ortak üye.
9. Farklı anahtarlı iki apply aynı kapsamda: biri 409 `scope-has-deduction`.
10. Bayat B revizyonu (KA + KB2 senaryosu) tek geçerli düşümde kalır.
11. `force-end` ile sahibin kapanışı eşzamanlı: tek devir, EndedAt tekdüze. Aynısı sahibin `sales/backfill` kapanışıyla.
12. Göç Express sürümünde ve `ONLINE` olmadan koşar; süre ölçülür.
13. Mod On'da protokolsüz push komut sonucunu geri alamaz; iz yazılır.
14. v1 güncellemesi ProductId'yi NULL'lamaz ve stoğu geri sarmaz (defter birleştirilmiş durumdan beslenir).
15. Kira süresi dolup `finish` gelmezse kayıt `Unconfirmed` olur ve öbür kopyanın `begin`'inde `already-requested` olarak döner.
16. `begin` yeniden oynatma: aynı `recordId` ve içerik kayıtlı sonucu döner; aynı kopyanın eski `Sending`'i `Unconfirmed` olur; tekil ihlal 409'a çevrilir. PersonKey'leri yalnız harf ya da sondaki boşlukla ayrılan iki kişi birbirini engellemez (özet indeksi).
17. `members-stale`: sunucu grubunun üyesini içermeyen `memberIds` reddedilir; (e) adımı başka üyedeki teslim kaydını üye kesişimiyle bulur.
18. `stale-total` küme karşılaştırması: toplamı eşit ama kümesi farklı istek reddedilir; GUID'siz üyenin siparişi ham `CustomerId` ile sayılır.
19. Kargoya verme iptalli bağları çözer; `uncancel` bağı ve "ödendi" bilgisini korur; onarım işinin (c) adımı Shipped dosyaya dokunmaz.
20. K2 `baseVersion`: güncel sürümü gören v2 push'u damgalı birimi yazar, bayat olan yazamaz. K5 istisnaları: force-end sonrası sahibin ve `SeenMode = 0`'ın push'u K2 ile yargılanır.
21. Mod On'da açılış penceresindeki kopyanın `shipments/sync` kararı alınır, tutar türetilir; `CommandedAt`'li dosyaya dokunulmaz. Dosya bağlarından önce gelirse, sonra gelen K4 bağı dosyayı yeniden türetir (K7); pencerede kargoya verilmiş dosyayı da (§7.1), komutla kargolanmış dosyanın donmuş tutarına dokunmadan. `PreviousReplicaId`'li ya da modu bir kez görmüş kopyanın `SeenMode = 0`'ı On kurallarıyla yargılanır (§4.0).
22. KVKK ayarı açıkken silmeden sonra gelen geç ekleme metni boş yazar; kapalıyken metin kalır, akışta yine maskelenir.
23. K3: komutla iptal edilip komutla geri alınmış siparişe push'la gelen baskı kabul edilir; şu anda komutla iptal edilmiş siparişe gelen baskı `cancelled-elsewhere` ile yok sayılır.
24. `shipments/sync` sunucuda Shipped olan dosyayı her kipte ve v1'de Held'e, Pending'e ya da başka tutara çeviremez; v2 yanıtı güncel satırı döner.
25. `disable --apply`: PC-B'nin açtığı ve yalnız PC-A etiketleri taşıyan açık dosyanın `OriginReplicaId`'si PC-A olur; karışık dosya büyük paya gider ve kuru koşuda listelenir.
26. `sales/backfill`: mevcut satırda damgasız birim ayrışması `diverged` döner, sunucu yazmaz; `localWins` mod Off'ta özgün kopyada `true`, `PreviousReplicaId`'li kopyada `false`. Başka lisansta var olan oturum ve sipariş Id'leri 500 değil satır başına `foreign-license` alır, öbür lisansın satırları değişmez.
27. `attach-shipment` `OriginReplicaId`'si boş siparişi aday saymaz; `--accept-shipment` ile kabul edilen dosyayı seçmez, türetmez.
28. `force-end` devrediliyor hâlindeki oturumda 409 `handing-over`; `begin` mod Off'ta 409 `mode-off`.

### 10.2 InMemory ve birim

- Ortak kural vektörleri (JSON) hem sunucu hem istemci test projesinde koşar: `SameCode`, `Classify`, `CodePrice`, `Contribution`, `PaidPriceOnCorrection`, K1–K7.
- Komut doğrulamaları ve 409 sınıfları. CLI `enable` koşulları ve kuru rapor: açıklanan ve açıklanamayan kargo farkı, `OriginReplicaId`'siz açık dosya, `--accept-shipment`, boş `MinClientVersion`, `featureLevel < 3`.
- `payment-requests/backfill` idempotent; aynı Id'ye farklı içerik 409.
- **Bildirim bastırma:** `sales/backfill` ile eklenen basılmış sipariş push, WhatsApp `OrderReceived` kuralı ve shopper push üretmez. Komutlar bildirim üretmez; tek istisna `shipment-decision`'ın etiket kuralı.
- Akış sayfası kurulumu: yan liste, KVKK boşaltması, `cursorReset`.

### 10.3 WPF

- **`TwoPcSalesConvergenceTests` + `FakeSalesServer`.** Desen: `TwoPcConvergenceTests` ve `FakeCustomerServer`; sıra permütasyonlarıyla koşar. Senaryolar:
  - A yayın yapar, bitirir, B görür
  - B iptal eder; A bunu görür, kâğıt çıkmaz
  - B iptal eder, ardından A'nın geç push'u gelir (`ignored`)
  - force-end, A aslında canlı: A sohbetten etiket yazmaya devam eder; A'nın damgasız değişiklikleri kabul edilir, B'nin damgaladıkları kazanır; A bitirince oturum `server` olur
  - kalıntı kuyruk etiketinin çevrimdışı iptali ve ilk baskısı (bekleyen komut, tek kâğıt)
  - yedekten dönüş
  - `cursorReset`
  - On → Off → On geçişleri. Off'tan sonra: uzak satır yok, yalnız kendi açık dosyaları, bekleyen komutlar yerel yazıma çevrilmiş (kâğıt yeniden teklif edilmez), `ignored`'daki `current` uygulanmış, kargo ayarı iki PC'de aynı
  - açılış penceresi: modu henüz görmemiş PC'nin kargo kararı kaybolmaz; 1. adım ile 2. adım arasında kaydedilen dekont modu 0'da tutar, ilk yetişme takılmaz; ilk yetişme sürerken kendi devredilmiş oturumunda yerel yol seçilmez
  - On → Off, PC-B'nin açtığı PC-A etiketli dosya: Off'ta PC-A dosyayı tutar, sonraki dekontta eşik doğru havuzdan hesaplanır; PC-B dosyayı tutmaz
  - On → Off, PC-A kapalıyken PC-B dosyayı kargolamış: Off turu dosyayı Shipped olarak alır, yeni etiket ona bağlanmaz
  - Off turu 2. ile 3. adım arasında kesilir: açılışta 3. adım yeniden koşar, o ana kadar `SeenMode = 1` bildirilir
  - geri doldurma ayrışması: F05 öncesi kaybolmuş iptal ve baskı Ws ilk açılışta sunucuya geçer; mod On'un ilk turu onları geri almaz. Geri yüklenmiş veritabanında sunucu kazanır
  - yedekten dönüş, mod açıldıktan sonra: yedeğin kuyruğundaki bayat satır devredilmiş oturumda iptali geri getirmez
  - Ws ilk açılış dosya türetmesi: Ws öncesi iptal edilmiş bağlı etiketin Ws'de geri alınması tutarı bir kez ekler
  - mod Off'ta `ignored` ya da `rejected` ile değişen fiyat dosya tutarına yansır; bağlı dosyası yerelde olmayan etiketin iptali ve fiyat düzeltmesi istisna atmaz
  - mod Off'ta devredilmiş kendi yayınında iptal, fiyat düzeltme ve kalıntı baskı bugünkü yerel yoldan çalışır
  - iptal → geri al: dosya tutarı, havuz ve "Ödendi" rozeti eski hâline döner (mod On komut yolu ve mod Off Y5 yolu)
  - kira: in-progress, already-requested, Unconfirmed, members-stale
  - ödeme geçmişi: PC-A'nın açılıştan önceki isteği mod On'da PC-B'nin sorusunda ve rozetlerinde görünür
  - ön gönderim: bekleyen komut isteği engeller, ilgisiz takılı kuyruk engellemez
  - miras iş `scope-has-deduction` aldığında benimsemez, birleştirme sorusuna düşer
- Tetikleyici şema testi: Label, Shipment ve StreamSession'a yazan her SQL listede. ABORT'un canlı yolları kırmadığı (CorrectPrices, AttachLabel mod Off'ta) ayrıca test edilir.
- `Migration049RehearsalTests`: önceki sürüm şema 49'da yayın açar, etiket yazar, basar ve iptal eder. Ayrıca `Mode = 1` durumundaki veritabanı ve sıfırlama betiği (§8.6).
- Aynı akış öğesini iki kez uygulamak toplamda sıfır fark üretir. `StockSyncedAt` dolu kalır; stok iki kez düşmez.
- Mod On'un ilk turu: 2. adımdan sonra `SyncedAt IS NULL` yerel dosya oluşamaz (ABORT ve kilit altındaki denetim).
- Devir kapısı: "boşaldı" okuması ile kapanış arasında yerel düzenleme reddedilir; kapanış kuyruk boşalmadan `Release = true` göndermez.
- Baskıdan önce yeniden okuma: başka bilgisayarda iptal edilen kalıntı etiket basılmaz.
- DI testi: hosted service hem kayıtlı hem başlatılıyor (`WpfStartupEnvironment.cs:189-197`).
- Testlerde sabit kimlik bilgisi yazılmaz; `TestPhone` gibi üreteçler kullanılır (CLAUDE.md).

### 10.4 Saha kontrolleri (gerçek etkiden; deploy doğrulama dersi)

1. PC-A'da yayın, bitir: PC-B'de ≤ 60 sn içinde görünür. DB'de `ReleasedAt`, `SessionReleasedAt` ve sayılar.
2. PC-B'de iptal ve kod fiyatı: PC-A'da ≤ 30 sn. DB'de `CancelSetAt`, `PriceSetAt`, `SalesCommandLogs`.
3. PC-A'nın bayat push'u PC-B'nin iptalini geri almaz (`ignored` kaydı).
4. PC-B'de dekont, bağla, eşik; ardından iptal: dosya tutarı düşer; geri alınca eski hâline döner ve "Ödendi" rozeti kalır (§7.2 örneği).
5. Aynı kişiye iki bilgisayardan aynı anda istek: biri `in-progress-elsewhere`, ardından `already-requested` sorusu. `PaymentRequestRecords` satırları.
6. Stok: aynı etiket bir kez düşer (panel bakiyesi).
7. Son 3 yayının toplamı her PC'de panelle aynı.
8. Kalıntı kuyruk etiketi çevrimdışı iptal edilir, bağlantı gelince sunucuda iptal olur.
9. Mod açılmadan önce PC-A'dan ödeme istenen bir kişi, açılıştan sonra PC-B'de "zaten istendi" sorusuyla gelir. `PaymentRequestRecords`'ta `Kind = Record` satırı.

### 10.5 Gözlem

Günlük sayılar: devir süresi (kapanıştan devre), K5 ve K6 retleri, K5 istisnasıyla alınan birimler, `ignored` sayısı, `in-progress-elsewhere`, `members-stale` ve `stale-total` sayıları, onarım işinin düzelttiği satırlar, `SeenMode = 0` ile alınan kargo gönderimleri ve açılış penceresi koşulunu tutmadığı için On kurallarıyla yargılananlar, geri doldurma ayrışması (yerel kazanan, sunucu kazanan), ilk yetişmedeki `catchup-overwrite`, Shipped dosyaya gelip yok sayılan `shipments/sync` satırları, protokolsüz istek izi. Ad ya da metin yazılmaz.

## 11. Sahip kararları

**Sahip kararları (2026-10-13, kesin)**

| # | Soru | Karar | Tasarımdaki karşılığı |
|---|---|---|---|
| Y1 | Başka PC bir yayını canlıyken mi, bittikten sonra mı değiştirebilsin? | Bittikten sonra. Canlı yayın başka PC'de salt okunur. | Yayın hâlleri (§2), `session-live` (§4.6), RemoteLiveSession yalnız başlık (§3.2, §5.7) |
| Y2 | Yayınlar hep aynı PC'den mi açılıyor? | Evet, aynı PC'den. | Sadeleştirme: canlı yayının etiketleri hiç inmez; force-end ve eşzamanlı canlı yayın nadir durumlar. Doğruluk korunur: sahiplik yayın başına, her PC yayın açabilir (§2). |
| Y3 | Yayın sonrası düzenleme internet istesin mi? | Kabul. Canlı PC çevrimdışı çalışmayı sürdürür. | §5.8; kalıntı kuyruk için bekleyen komut (§5.5) |
| Y4 | Kargo ücreti ve eşik tek bir lisans geneli ayar mı olsun? | Evet. | `LicenseShippingSettings` (§3.1, §4.8, §6.2) |
| Y5 | Kargo dosyasına bağlandıktan sonra iptal edilen etiket tutardan ve eşikten düşsün mü? | Evet. | §7.2 (mod On, sunucu türetir) ve §7.4 (mod Off, Ws'nin yerel yolu). İptal bağı çözmez, geri alma tutarı ve "ödendi" bilgisini geri getirir. |
| Y6 | Fatura ve e-Arşiv dökümü tek PC'den mi alınıyor? | Evet, tek PC'den. | Raporlar bütün PC'lerin satışlarını içerir (§5.9). Teknik kilit eklenmez (§12). |
| Y7 | Sahip PC yayın ortasında susarsa başka PC onaylı "yayını bitir" yapabilsin mi? | Evet, ≥ 15 dk sessizlikten sonra. | `force-end` (§4.6), çip ve onay (§5.7). Yalnız canlı yayında: bitmiş ama etiketleri gönderilmemiş yayını başka PC devralamaz (Y8); sahip hiç dönmeyecekse CLI (§4.10, §15-43) |
| Y8 | Yayın bitmiş ama yayın PC'si etiketlerini göndermeden kapanmışsa (devrediliyor) başka PC o yayını devralabilsin mi? | Hayır. Yayın bitti ama yayın bilgisayarı etiketlerini göndermeden kapandıysa öbür bilgisayar o yayını yayın bilgisayarı açılana kadar düzenleyemez; 15 dk devralma yalnız canlı yayın ortasında çöken bilgisayar için. | `force-end` yalnız canlı oturumda; devrediliyor hâlinde 409 `handing-over {unsentLabelCount}` (§4.6). Çipte "Yayını bitir…" eylemi yok, yalnız bekleyen etiket sayısı ve son sinyal (§5.7). Onarım işi v2 oturumunu devretmez (§4.9). Sahip hiç dönmeyecekse Burak'ın `sales-sync force-end` CLI'ı, sayı görülerek (§4.10, risk 4). Başka makineye geri yüklenen kopya da devrediliyor oturumu devralmaz; devrediliyorken CLI ile zorla bitirilmiş oturumda da sahip sayılmaz (§8.7, §4.10, S2; aynı makinede hemen devralır, §11 kararı). Gerekçe: devralma eksik etiketle ödeme isteği ve koli üretirdi; sunucu da eksik olduğu için `stale-total` yakalayamazdı (M2, §15-43) |
| Y9 | Yayın sırasında etiketler sunucuya ne sıklıkla gitsin? (2026-10-14) | Birkaç saniyede bir. "İnternet yoksa yayında yok, satış da yok." | Canlı (aktif) oturum varken etiket gönderim aralığı ≈ 5 sn (bugün 60 sn, `SessionOrderSyncHostedService.cs:20`); aktif oturum yokken 60 sn kalır. Gönderilecek etiket yoksa istek atılmaz. Bilgisayar yayın ortasında kapanırsa sunucuda eksik kalan en çok son birkaç saniyedir. W1'de. |

Önceki sahip kararları aynen geçerli: #508 F1–F3 (kod başına tek fiyat), #506 karar 2 (tekrar göndermeden önce sor) ve karar 4 (birleştir), ortak bakiye K0–K6.

**Mühendislik kararları**

| # | Karar | Gerekçe |
|---|---|---|
| M1 | Lisans başına statik mod kapısı (CLI). Açılış ön koşulları: `MinClientVersion` dolu, bütün aktif PC'ler Ws+ ve `featureLevel = 3`, geri doldurma ve ödeme geçmişi tamam, kargo bağları tam, 7 gün protokolsüz iz yok. Çalışma anında eski sürüme karşı korumalar (§8.3). | Dinamik kapının güvenli dönüş tarafı yok (§8.3). |
| M2 | Kapanış ancak yayının etiket kuyruğu boşken devreder. Oturum satırı devir görülene kadar kuyrukta kalır. | D3'teki "kapanış etiketlerden önce" açığı; geri alma penceresinde kayıp kapanış. |
| M3 | Birim damgaları sunucu saatiyle ve yalnız komutla basılır; push damgalı birimi değiştiremez (K2, S1'den itibaren). | İstemci saati kararda yok. Off, geri alma ve eski sürüm pencerelerinde komut kararı korunur. |
| M4 | Oluşum alanları yalnız eklemede yazılır; defter birleştirilmiş durumdan beslenir (S1, iki kipte). | ProductId'nin NULL'lanması moddan bağımsız kapanır. |
| M5 | Başka PC'nin canlı yayını `StreamSession`'a girmez (`RemoteLiveSession`). | Eski ikiliye dönüşte `GetActive` ve kurtarma ekranı yanılmaz. |
| M6 | ABORT tetikleyicisinin koşulu `Authority = 'server'` (Label) ya da `Mode = 1` (Shipment). Mod On'da canlı PC kargoya yazmaz. | Unutulmuş yol görünür hataya döner; canlı yollar kırılmaz. |
| M7 | Kuyruğa alma tetikleyiciyle. | "Bir yolu unutma" riski; `AttachLabel` bugün kuyruğa almıyor. |
| M8 | Kapsam başına tek düşüm kuralı ayrı tablo olmadan, defter üzerinde, çapa satırı düzeyinde. | Geri almaya dayanıklı; #506 ve ortak bakiye M10 ile uyumlu. |
| M9 | Kira fail-closed: mod On'da `begin` yoksa GUID'li kişide mesaj gitmez. | R1 benzeri pencereyi geri alma anında da kapatır. |
| M10 | Bilgisayarlar arası kira PersonKey ile, ayrıca üye kesişimiyle. | Grup senkronu gecikse de aynı kişi bulunur. |
| M11 | Sunucu toplamı farklıysa istek engellenir (uyarıyla devam yok); karşılaştırmadan önce ön gönderim. | Eksik ya da fazla tutar istenmez. |
| M12 | Y5 iki kipte: mod On'da sunucu türetir, mod Off'ta Ws'nin yerel yolu fark olarak uygular; farkın tabanı Ws ilk açılışında bir kez türetilir. İptal bağı çözmez. | Mod Off'ta her dosyanın tek yazıcısı vardır (§7.4). Bağ "dekontla ödendi"nin tek işareti (§7.2). Taban türetilmeseydi Ws öncesi iptal geri alınınca fiyat iki kez eklenirdi. |
| M13 | Ayrı "dosyadan çıkar" komutu yok. Bağ yalnız dosya kargoya verilirken iptalli siparişlerden çözülür. | Arayüz eklenmeden Y5 karşılanır; kolide olmayan ürün sonraki dosyaya girer. |
| M14 | K2'de `baseVersion` eşitliği: güncel sürümü gören v2 push'u damgalı birimi yazabilir. | Off'a dönüşte ve force-end sonrasında bilinçli yeni karar kilitlenmez; bayat push yine geri alamaz. |
| M15 | `disable` turu son akışı dosyalarla birlikte uygular, uzak satırları ve Off'ta başka PC'nin olacak açık dosyaları siler, bekleyen komutları yerel yazıma çevirir, kargo ayarını yerele kopyalar. | Off'ta kira yok; uzak satır kalırsa R1 penceresi geri açılırdı. Bayat dosya aynası yerel otoriteye dönmemeli (§15-39). |
| M16 | Her v2 isteği `SeenMode` taşır; modu görmemiş kopyanın gönderimi Off kurallarıyla alınır. | Açılış penceresinde verilen karar sessizce kaybolmaz; sürüm düşürmenin ön koşulu ölçülebilir olur. |
| M17 | Ödeme kayıtları mod Off'ta da (kirasız) yazılır, geçmiş geri doldurulur; uzak `Covers` sipariş kümesiyle. | Açılıştan önceki istekler mod On'da görünmezdi; iki bilgisayarın saati karşılaştırılmaz. |
| M18 | Force-end, aktif sahipte yerel yayını kesmez. | Yayın ortasında sohbetten etiket yazılamaz hâle gelmez; damgalı birimde komut kazanır. |
| M19 | `disable`'da açık dosyanın Off'taki sahibi, dosyayı açan değil bağlı etiketlerin sahibidir; sunucu yazar, son akış taşır. Shipped dosya sunucuda her kipte tek yönlüdür. | Y2 altında mod On'da dosyaları çoğunlukla yayın PC'si olmayan PC açar; Off'ta dosyayı yalnız etiketlerin durduğu PC yönetebilir (§4.10, §8.5). |
| M20 | Ws geri doldurması F05 öncesi kayıp güncellemeleri bulur; özgün kopyada mod Off'ta yerel kazanır, geri yüklenmiş kopyada sunucu. | Mod On'un ilk turu yereldeki doğru iptal ve baskıyı sessizce geri almasın; eski yedek de geçmişi diriltmesin (§4.3, §8.2). |
| M21 | Açılış penceresi istemcinin bildirdiği `SeenMode`'a değil kopya geçmişine bağlı (§4.0). | Geri yüklenen veritabanı yedekteki kipi bildirir; bayat kuyruk Off kurallarıyla alınmasın. |
| M22 | Kargo bağı bilinmeyen sipariş (`OriginReplicaId` boş) `attach-shipment` adayı olmaz. | Emekli PC'nin kargolanmış geçmişi yeni dosyanın tutarına ve eşiğe girmesin (§4.6). |

**Açık kalanlar**
- **KVKK metin boşaltması — KARAR (2026-10-13, Burak): silinmesin.** Silmede sunucudaki ve bütün PC'lerdeki etiket metinleri (takma ad, yorum) kalıcı olarak boşaltılmaz; bugünkü politika sürer (`SalesSync:ScrubOrderTextOnPurge = false`). Sahip "evet" derse yalnız ayar değişir: sunucu ve yerel boşaltma aynı ayara bakar (§9). Akıştaki okuma anı maskelemesi her durumda kalır; silinmiş müşterinin metni yeni bilgisayara inmez.
- Eski, sahibi hiç kalmamış açık oturumların toplu kapatılması: `sales-sync force-end` CLI'ı var. Her kapatma sahibin onayıyla, `sales-audit` listesinden yapılır.
- **Aynı makinede geri yüklenen kopyanın devralması — KARAR (2026-10-14, Burak): hemen devralır.** §8.7'nin S1'deki devralması, önceki kopya geri yüklenen kopyanın ilk görüldüğü andan beri hiç istek göndermediyse canlı ve devrediliyor oturumların sahipliğini o kopyanın ilk isteğinde geçiriyordu (S1'in `SalesReplicaService.SilentPredecessorsAsync`'i; S2'de `CanInheritAsync`'e katıldı). Sessizlik süresi ve parmak izi aranmıyordu. Bu yüzden yayın PC'si yalnız kapalıyken (Y8'in tam durumu) ya da iki kalp atışı arasındayken (≈ 30 sn) başka bir PC'ye geri yüklenen eski yedek yayını devralıp kendi eksik kuyruğuyla devredebilir; yayın PC'si açılınca kapanışı yok sayılır, canlı yayındaysa yeni eklemeleri `session-owned-elsewhere` alır.
  - **Başka makine: Y8 karara bağlıyor.** Farklı ya da bilinmeyen parmak izinde geri yüklenen kopya "öbür bilgisayar"dır. Devrediliyor oturum devralınmaz; sahip dönmeyecekse CLI `force-end`. Canlı oturum yalnız önceki kopya ≥ 15 dk (`SalesSync:ForceEndSilenceMinutes`) susmuşsa devralınır (Y7; Y8: "15 dk devralma yalnız canlı yayın ortasında çöken bilgisayar için").
  - **Aynı makine: hemen devralır (karar).** Aynı parmak izinde önceki veritabanı dosyası artık yoktur, yayın bilgisayarı yedekten açılmıştır; bilgisayar kullanılmaya aynen devam eder, yalnız kimliği (ReplicaId) yeni kopyaya geçer. Önceki kopya hiç dönmeyeceği için beklemek oturumu CLI'a bırakırdı. Kalan risk, yedekten sonra yazılmış etiketlerin kaybıdır (§8.7 "kalan risk" ile aynı sınıf). Eski veritabanı bir şekilde geri gelirse, devralınmış oturumlar için gönderimleri `session-owned-elsewhere` alır; o kopya yeni yayınlar için kullanılabilir.
  - **Uygulama.** S1'de v2 istemci olmadığı için kural hiçbir isteği etkilemiyordu. Y8 kuralı `ForceEndSilenceMinutes` ile birlikte S2'de sunucuya yazıldı: üç çağıran (`sessions/sync`, `orders/sync`, `sales/backfill`) aynı yoldan, `SalesReplicaService.CanInheritAsync` (ek kurallar §8.7'de: sahipteki klon izi, sahip olmayanın `Release`'i, zorla bitirilmiş oturumun hâli). S1'in testi (`Geri_yuklenmis_kopya_sessiz_onceki_kopyanin_oturumunu_devralir`) canlı oturumu, önceki kopya > 15 dk sessizken devralır; o durum Y8 kuralında da geçerli kalır. Devrediliyor ve < 15 dk durumlarının testleri S2'de. W1 bu kural sunucuda olmadan açılmaz (§13).

## 12. Riskler ve bilerek yapılmayanlar

**Riskler**
1. **`rowversion` ekleme süresi.** Orders tablosunun tamamı yeniden yazılır. Önlem: ölçüm, gerekirse ayrı bakım göçü (§3.1). Postgres planında rowversion'ın karşılığı yok; müşteri akışı bu borcu zaten taşıyor, Postgres göçü ikisini birlikte çözer.
2. **Kurallar iki tarafta çalışıyor.** Önlem: `OrderDeck.Shared` ortak kodu ve ortak test vektörleri (§4.11).
3. **Bağlantı bağımlılığı.** Yayın sonrası düzenleme internet ister (Y3 ile kabul).
4. **Devir takılabilir.** Bir etiket kalıcı olarak gönderilemiyorsa (ör. `unknown-session` 400, `LicensesSessionsSyncController.cs:233-247`) yayın devredilmez. Durum satırı bunu gösterir, `sales-audit` sayar; başka PC'nin çipi gönderilmemiş etiket sayısını gösterir. Çözüm: hatalı satırın düzeltilmesi ya da, gönderilemeyen sayı görülerek, Burak'ın `sales-sync force-end` CLI'ı. Arayüzdeki force-end yalnız canlı yayına uygulanır (§4.6).
5. **Yanlış force-end.** Ağ bölünmesinde sahip yalnız çevrimdışıyken yayını başka PC bitirebilir. Sahip yayına devam eder (M18); bağlantı gelince yeni etiketleri ve damgasız değişiklikleri kabul edilir. Aynı etiketi PC-B de komutla değiştirdiyse PC-B'ninki kazanır ve sahibe `ignored` listesi gösterilir. PC-B kodun fiyatını değiştirdikten sonra sahip aynı koda eski FİYAT'la yeni etiket yazarsa tek fiyat ihlali (DM5) olabilir; `sales-audit` sayar ve bir sonraki `set-code-price` onu da kapsar.
6. **Toplamlar görünür biçimde değişir.** Türetilmiş toplamlar kaymış sayaçlardan farklı çıkabilir. Mod açılınca dosya tutarları türetilir; Y5 açık dosyalarda geçmişe dönük etki eder. Sayılar raporlanır, verilmiş kararlar değiştirilmez.
7. **Raporlar bütün PC'leri içerir.** Fatura dökümü iki PC'den alınırsa aynı satış iki kez faturalanır. Y6 ile operasyonel kural; teknik kilit yok.
8. **Kopya veritabanı.** Önlem: parmak izi denetimi ve mod kapısı (§8.7).
9. **Hız sınırı.** Aynı NAT arkasındaki PC'ler 100 istek/dk/IP bütçesini paylaşır. İlk yetişme birkaç tur sürebilir; tur başına sayfa sınırı var.
10. **Eski PC dönerse.** Bayat görüntü; para yolu korunur (§8.3). Kesin çözüm PC'yi güncellemek.
11. **KVKK yayılımı.** Mesaj metni daha çok bilgisayarda duruyor. Silinmiş müşterinin metni yeni bilgisayara inmez; daha önce inmiş metnin silinmesi sahip kararına bağlı ayardadır (§9, §11).
12. **Kişi anahtarı ayrışması.** Grup senkronu gecikmesinde üye kesişimi ve `members-stale` yakalar. Grubu sunucunun da henüz bilmediği, hiç kesişmeyen üye kümelerinde #506'nın birleştirme sorusu kalır.
13. **Bildirim boşluğu.** Basılmadan gönderilip sonra basılan etiketin bildirim üretmemesi (`LicensesSessionsSyncController.cs:324-332`) aynen sürer; komutlar da üretmez.
14. **İş büyüklüğü.** En riskli PR W2'dir (yönlendirici ve akış). W3, `PaymentRequestService`'in yalnız girişine ve çıkışına sarılır; durum makinesi (`PaymentJobRepository`) değişmez.
15. **Acil kapatma sonrası karışık dosya.** `disable`'da açık dosya bağlı etiketlerin sahibine geçer (§4.10); Y2 altında bu, dosyayı mod On'da hangi PC açmış olursa olsun yayın PC'sidir. Yalnız iki PC'nin etiketlerini birlikte taşıyan karışık dosyada küçük payın sahibi PC'nin etiketleri yerelinde olmayan bir dosyaya bağlı kalır: o PC'de bu etiketlerin iptali dosya tutarına yansımaz, fiyatları düzeltilemez (§7.4) ve yeni dekontta yeni dosyaya girmezler. Karışık dosya ancak iki PC'den yayın açılmışsa oluşur; `disable` kuru koşusu onları listeler, Burak isterse `--apply`'dan önce kararlarını (kargola, alıcı ödesin) verdirebilir. Mod yeniden açılınca dosyalar türetilir.

**Bilerek yapılmayanlar**
- Başka PC'nin canlı yayınını düzenlemek ya da canlıyken izlemek (Y1).
- D1'in istemci saatli birim damgaları, `SessionCodePrices` tablosu ve uzak kod fiyatının canlı PC'ye otomatik uygulanması. Kâğıt ile kayıt ayrışıyordu.
- D3'ün `ActiveSaleScopes` tablosu (§6.6).
- D3'ün R1/R2 iki aşamalı WPF sürümü; tek mod kapısı onun yerini alır.
- Dinamik mod kapısı ve modun kendiliğinden kapanması.
- Elle "dosyadan çıkar" arayüzü (M13).
- `add-backup`'ın ebeveynden ProductId kopyalaması: yerel `AddBackup` bugün ürünsüz ekliyor, komut da öyle (§4.6). Stoğa düşen yedek ayrı bir ürün kararıdır.
- Çekilişler, PaymentJob senkronu, dekont inişi.
- Panel ve Shopper istemci değişikliği.
- Fatura dökümüne teknik kilit (Y6).
- Kişi (grup) bazında kargo havuzu; bugün olduğu gibi müşteri satırı bazında kalır.

## 13. PR planı

Her PR ayrı açılır; bir sonraki ancak öncekinin master'a girmesinden sonra açılır. Yığılmış PR yok (yığılmış PR tuzağı). Merge ve deploy yayın penceresine denk gelmez. Commit'lerde Co-Authored-By satırı bulunur.

| PR | Repo | İçerik | Efor (gün) |
|---|---|---|---|
| **S1** `feat(sales-sync): sahiplik, devir, birim kuralı ve satış akışı` | LiveDeck (sunucu) | Göç (§3.1, S1 satırları; `ONLINE` yok; `Shipments.OriginReplicaId`, `WpfReplicas` lisans başına PK ve `SeenMode`). `sessions/sync` (tekdüze EndedAt, devir, `Release`). `orders/sync` K1–K7 (S1'de K2 damgasız ama `baseVersion` okunur, K3 iptal kuralı, K4 mod Off, K6 yalnız fiyat), birleştirilmiş durumdan defter, v2 yanıtı (`ignored`'da `current`), eklemede KVKK ayarı. `sales/backfill` (bildirimsiz, dosya kaynağı). `sales/changes` (yan liste, GUID'siz taslak, KVKK, cursorReset). `sales/state` ve `hello` (`featureLevel`, `seenMode`, `unsentLabelCount`). Açılış penceresi kopya kuralı (§4.0, `SeenModeOnAt`). Geri doldurmada birim ayrışması (`diverged`, `localWins`). K7'de kargo bağı. Akışta dosyalar iki kipte. `shipments/sync`'te Shipped tek yönlü, v2 yanıtı güncel satır (§7.1). Protokolsüz istek izi iki kipte. `sales-release-repair` işi (c: Shipped hariç). `sales-audit`. `ClientVersion` yardımcısı. `OrderDeck.Shared/Sales/LabelRules.cs` ve vektörler. `deploy/README` uyarısı. §8.7 devralması yalnız "önceki kopya sustu" koşuluyla; Y8 kuralı S2'de (§11). Testcontainers 1–3, 12, 14, 20, 22–24, 26. | 6,5 |
| **S2** `feat(sales-sync): kapanmış yayın komutları ve sunucuya ait kargo` | LiveDeck (sunucu) | `SalesCommandLogs`. On komut türü (§4.6): görünürlük, beklenen sürüm, applock, damgalar, defter, dosya türetme (DM7, Y5: iptal bağı çözmez), kargoya verirken iptalli bağın çözülmesi. `shipment-decision` etiket kuralı. `force-end` (yalnız canlı oturum, `ForceEndedBy`; K5 istisnasının kendisi S1'in `orders/sync`'inde). `attach-shipment` adayında kargo bağı bilinen sipariş ve türetilmiş dosya; `shipment-decision`'da bağın görünürlük dışı olması. `shipping-settings` ve `sales-sync shipping set`. Mod On'da `shipments/sync` iz kuralı ve açılış penceresi istisnası. §8.7 devralmasına Y8 kuralı (§11): farklı ya da bilinmeyen parmak izinde devrediliyor oturum devralınmaz, canlı oturum yalnız `ForceEndSilenceMinutes` sessizlikten sonra; aynı makine yarısı sahip kararına göre. `sessions/sync`, `orders/sync` ve `sales/backfill` aynı yoldan. Testcontainers 4–7, 11, 13, 19, 21, 27, 28 (force-end). | 5,5 |
| **S3** `feat(payment-request): bilgisayarlar arası kira ve kapsam başına tek düşüm` | LiveDeck (sunucu) | `PaymentRequestRecords` (özet anahtar), `PaymentRequestMembers`, `PaymentRequestRecordOrders`. `begin` (yeniden oynatma, `members-stale`, üye kesişimi c ve e, küme karşılaştırması, kargo tutarı denetimi), `finish`, liste ve `backfill` uçları. Apply kapsam kuralı (applock, defter yüklemi, miras replay dahil). Mod On'da ve boş `MinClientVersion`'da `client-update-required`; mod Off'ta `begin` 409 `mode-off`. `sales-sync` CLI'ları (açıklanamayan kargo farkı, `--accept-shipment` ve anlamı, `disable`'da dosya sahipliğinin yeniden yazılması ve karışık dosya listesi, devrediliyor oturum için `force-end`, `adopt-orders`). Testcontainers 8–10, 15–18, 25, 28 (`begin`). | 4,5 |
| **W1** `feat(sales-sync): 049, v2 gönderim, devir ve geri doldurma` | LiveDeck (WPF) | Göç 049 (kolonlar, tablolar, tetikleyiciler). Canlı oturumda ≈ 5 sn gönderim aralığı (Y9). ReplicaId ve `hello`. v2 DTO'ları (`baseVersion`, `SeenMode`). Gönderim kapısı, kapanış sırası ve devir kapısı (mod Off'ta `Authority` yazılmaz). Yanıt işleme (applied/ignored+current/rejected; `current` uygulamasında Y5 dosya bloğu). `sales-backfill-out` ve birim ayrışmasının onarımı (§8.2-4, -5). Türetilmiş toplamlar (`CustomerAggregateSql`). Y5'in mod Off yerel yolu (`Cancel`/`Uncancel` dosya bloğu), ilk açılış dosya türetmesi, yerelde olmayan dosyanın atlanması ve `Classify`'ın yeni sınıfı. Kapanışta `UnsentLabelCount`. `GetActive` ve `GetUnprintedOutsideSession` süzgeçleri. Baskıdan önce yeniden okuma. Şema ve prova testleri (sıfırlama betiği dahil). **Ön koşul:** §8.7 devralmasının Y8 kuralı sunucuda (S2); yoksa W1 açılmaz (geri yüklenen kopya ilk v2 istemcisiyle devralmaya başlar). | 5,5 |
| **W2** `feat(sales-sync): akış uygulaması ve kapanmış yayında düzenleme` | LiveDeck (WPF) | `SalesChangesPullService` (kayıt ve başlatma). `ApplyItem` (taslak müşteri, U5, StockSyncedAt, force-end'de aktif yayın yerel kalır). `RemoteLiveSession` ve çip. `ILabelEditor` (mod Off'ta hep yerel) ve `SalesCommandClient`. `PendingSalesCommand` ve örtü. Kargo aynası ve dekont adımı. Force-end arayüzü. Arayüz durumları ve 409 eşlemeleri (§5.3, §5.7). Kip geçişleri: açılış penceresi (§8.4: kilit altında yeniden denetim, yerelde devri bilinen oturumların hemen `server` olması, ilk yetişmede `catchup-overwrite` sayımı), `disable` turu (§8.5: dosya öğeleri dahil son akış, `SeenMode`'un tur bitince 0 olması, yarıda kalan 3. adımın yeniden koşması). Geri yüklemeden sonra gönderimden önce kip tazeleme (§8.7). Devrediliyor çipi (`UnsentLabelCount`, force-end yok). KVKK yerel boşaltma (ayara bağlı). `TwoPcSalesConvergenceTests`. | 9 |
| **W3** `feat(payment-request): kira, sunucu toplamı ve lisans geneli kargo ayarı` | LiveDeck (WPF) | Ön gönderim (bekleyen komut önce), `begin`/`finish` sarmalı ve 409 eşlemeleri (`members-stale` dahil). Ödeme geçmişi geri doldurması ve mod Off kayıtları (`PendingPaymentRecord`). `GetPrintedTotal`. Uzak kayıtlar soruda, rozetlerde ve #508 planında (sipariş kümesiyle `Covers`). Uzlaştırmada sunucu üyeleri. `begin`'in `mode-off`'unda Off turu ve yeniden kurma. `scope-has-deduction` benimsemesi (miras işte değil). `ShippingSettingsProvider`, `ComputeShipping(ShippingSettings)` ve ayar ekranı. `featureLevel = 3`. Testler. | 4,5 |
| — | Velopack Ws, `MinClientVersion`, PC'ler sırayla, mod açılışı, saha kontrolleri | | 2 |
| **S4** `chore(sales-sync): daraltma` | LiveDeck (sunucu) | Mod On'dan ≥ 2 hafta sonra. Mod On'da protokolsüz sipariş güncellemesi sessizce yok sayılmak yerine 409 `client-update-required` alır. Ölü kod (`ShipmentRepository.GetByStatus`/`GetLabelIds`, `LabelService.AddShippingFee`, `RemoveBackup`, `LabelRepository.MarkPrinted`) ayrı bir karar olarak listelenir. | 1 |
| **D-ortak-bakiye** `docs(balance): satış senkronu bağımlılıkları` | LiveDeck (docs) | Ortak bakiye spec'ine: §4.2 `DrawAsync`'e `scope-has-deduction` kuralı (çapa düzeyi, §6.6); §12 S2'den "UA ayrıştırıcının ortak yardımcıya taşınması" adımının çıkarılması (yardımcıyı bu spec'in S1'i getiriyor); §7.1'e "bu spec'in S3'ünden sonra" sırası. S3 ile aynı hafta açılır. | 0,5 |
| **D-banka** `docs(bank): satış senkronu dayanakları` | LiveDeck (docs) | Aşağıdaki (a)–(c) önerilerinin banka spec'ine yansıtılması. Ortak bakiyeden sonra. | 0,5 |
| — | OrderDeck-Mobile, OrderDeck-Shopper | Değişiklik yok. | 0 |

**Toplam ≈ 39,5 iş günü** (iki docs PR'ı dahil; üçüncü inceleme ≈ 2,5 gün ekledi). Kazanan tasarımların tahmini 23–23,5 gündü. Fark aşılardan ve ikinci incelemeden geliyor: lisans geneli kargo ayarı, Y5 kuralı (iki kipte), `add-backup`, bekleyen komut ve örtü, kira kayıtları ve Unconfirmed, ödeme geçmişi geri doldurması, KVKK ayarı, onarım işi, baskıdan önce yeniden okuma, kip geçişleri (açılış penceresi ve `disable` turu) ve force-end arayüzü. Asgari ilk değer S1 + W1'dir (≈ 12 gün): ProductId düzeltmesi, tekdüze EndedAt, kaçmış kapanışların kapanması, geçmişin sunucuda tamamlanması ve Y5'in yerel yolu. Görünürlük ve değiştirebilme mod On ile gelir.

**Diğer iki işe göre sıra ve bağımlılıklar** (sahip sırası: önce bu senkron, sonra ortak bakiye, sonra banka havalesi)

1. **Bu spec:** S1 → S2 → S3 (+ D-ortak-bakiye) → W1 → W2 → W3 → Ws → mod On → S4.
2. **Ortak bakiye** (`2026-10-11-ortak-bakiye-design.md`), bu spec'in S3'ü master'a girdikten sonra başlar. Aşağıdaki zorunluluklar bugün yalnız bu belgede duruyor; D-ortak-bakiye docs PR'ı onları ortak bakiye spec'ine taşır.
   - Aynı denetleyiciye dokunuyorlar (`LicensesCustomerBalanceApplyController`): ortak bakiye S1 scope, transactions ve reverse'i, S2 apply'ı yeniden yazıyor.
   - Ortak bakiye S2'nin `DrawAsync`'i §6.6 kuralını çapa düzeyinde korumalıdır.
   - S1'in `ClientVersion` yardımcısı kullanılır; `PersonBalance:MinClientVersion` ve `SalesSync:MinClientVersion` aynı karşılaştırmayı paylaşır. Ortak bakiye §12 S2'deki "UA ayrıştırıcının ortak yardımcıya taşınması" adımı bu yüzden düşer.
   - Wh ⊇ Ws olmalı: havuz kipinin kapısı bütün PC'leri Wh+ ister; böylece hepsi satış senkronunu da taşır.
   - Bu spec ortak bakiyeye bağımlı değildir. PersonKey, BIN2 harmanı gelmeden de bellekte ordinal hesaplanır (§6.7).
3. **Banka havalesi** (`2026-10-10-banka-havale-onay-design.md`), ortak bakiyeden sonra gelir (onun K6'sı havuza bağlı). Bu spec bankaya üç dayanak sağlar. Bunlar öneridir; banka spec'ine yansıtılması D-banka docs PR'ını ister:
   - (a) Banka §9 "karışık sürüm" kuralı bütün PC'lerin Ws+ olmasını da istemelidir. Mod On'un koşulu bunu zaten sağlar.
   - (b) Banka W2'nin beklenti üretim noktası, kendi kuyruğu yerine bu spec'in `payment-requests/{id}/finish(delivered)` kaydından sunucuda türetilebilir. Bu kayıt sahip satırını (`OwnerWpfCustomerId`), kapsamı ve net tutarı zaten taşıyor; banka'nın `(WpfCustomerId, ScopeKey)` anahtarına doğrudan eşlenir.
   - (c) Havale onayındaki kargo yönergesi, S2'nin sunucu kargo servisiyle sunucuda uygulanabilir. Banka §8 W3'teki "yönerge sunucudan gelir" bu servise dayanır. Lisans geneli eşik (§6.2) de sunucuda okunabilir.

## 14. Tasarım incelemesi — bulgu eşlemesi

Kaynak: 2026-10-13 tasarım karşılaştırması (üç aday, iki hakem). "H1" hakem 1, "H2" hakem 2. D1 = "PR-3 desenini genişletme", D2 = "yayın sahipliği + komutlar", D3 = "kapanan yayın sunucuya devredilir". Her bulgu kod kesitinde doğrulandı.

| # | Kaynak | Önem | Konu | Karar (bölüm) |
|---|---|---|---|---|
| 1 | H1 aşı (D2) | Önemli | Lisans başına sert mod kapısı; D3'ün uyarı bandı onun yanında | İşlendi §2, §4.10, §8.3, M1. Uyarı bandı `legacyMachines` olarak §4.5 ve §5.7'de. |
| 2 | H1 aşı (D2) | Önemli | Ortak iş kuralları `OrderDeck.Shared`'da | İşlendi §4.11, §10.2 |
| 3 | H1 aşı (D2) | Önemli | Kapanış yalnız etiketler gönderildikten sonra | İşlendi §4.1 (`Release`), §5.2, M2 |
| 4 | H1 aşı (D2) | Önemli | Force-end sahibin son sinyaline (15 dk) bağlı; başlıkta "PC-X'te canlı (son sinyal)" | İşlendi §4.5, §4.6, §5.7, Y7 |
| 5 | H1 aşı (D2+D3) | Önemli | PersonKey `g:`/`c:` + üye kümesi kesişimi | İşlendi §2, §6.4-d ve -e, M10 |
| 6 | H1 aşı (D1) | Önemli | Tetikleyiciyle kuyruğa alma; `AttachLabel` kuyruğa almıyor | İşlendi §3.2 (`TR_Label_Enqueue`), §5.1, M7 |
| 7 | H1 aşı (D1) | Önemli | Baskıdan hemen önce yeniden okuma | İşlendi §5.6 |
| 8 | H1 aşı (D1), H2 aşı | Önemli | Oluşum alanları yalnız eklemede; ProductId NULL'lanması mod beklenmeden | İşlendi §4.2 K1, defter birleştirilmiş durumdan, M4; test §10.1-14 |
| 9 | H1 aşı (D1) | Önemli | TwoPcSalesConvergence + FakeSalesServer, ortak vektörler, bildirim bastırma testi | İşlendi §10.2, §10.3 |
| 10 | H1 aşı (D1), H2 aşı | Önemli | KVKK: Orders metinleri boşaltılır, `TextScrubbedAt`, silme bütün PC'lere iner; yerel boşaltma | İşlendi §9. Sahip onayı açık madde (§11) |
| 11 | H1 ölümcül (D1) | Ölümcül | Uzak canlı yayın `StreamSession`'a iniyor; eski ikiliye dönüşte `GetActive` yanılıyor | İşlendi §3.2 (`RemoteLiveSession`), §5.9, §8.6, M5 |
| 12 | H1 ölümcül (D1), H2 ölümcül (D1) | Ölümcül | İstemci saatli son-yazan-kazanır; uzak kod fiyatı canlı PC'ye uygulanıyor; canlıdaki uzak iptal kâğıda çıkıyor | Elendi: sunucu saatli damga (K2, M3), canlı yayın başka PC'de salt okunur (Y1), §12 bilerek yapılmayanlar |
| 13 | H1 ölümcül (D2) | Ölümcül | Canlı kuyruk yolları yerelde; kalıntı etiketin iptali sunucudan reddedilip geri dönüyor | İşlendi §5.3 (yönlendirici, `MainShellViewModel.cs:2001`, `:2021`, `:2146`), §5.5 (komut ve çevrimdışı bekleyen komut) |
| 14 | H1 ölümcül (D3), H2 ölümcül (D3) | Ölümcül | R1'de uzak etiket kümülatif toplama giriyor, kira R2'ye kalıyor: çift tam istek | İşlendi §6.5 (tek mod kapısı, fail-closed kira, eski sürüme `client-update-required`), M9 |
| 15 | H1 ölümcül (D2, D3) | Ölümcül | Sunucu toplamıyla karşılaştırmadan önce sahibin gönderilmemiş etiketleri gönderilmeli | İşlendi §2 (ön gönderim), §6.4 adım 1–2, M11 |
| 16 | H2 aşı (D3) | Önemli | Akış sayfasında müşteri taslakları; kalıcı park yok | İşlendi §4.4 (yan liste), §5.4 |
| 17 | H2 aşı (D3) | Önemli | Eklemede `SessionReleasedAt` kopyası ve tekrarlı onarım işi | İşlendi §4.2 ekleme dalı, §4.9 |
| 18 | H2 aşı (D3) | Önemli | Kapanmış yayına `add-backup`; `BeginAddBackup` yönlendiriciye | İşlendi §4.6, §5.3 |
| 19 | H2 aşı (D3) | Önemli | Teslim kaydı olmadan dolan kira "Unconfirmed / gitmiş olabilir" | İşlendi §6.4, §10.1-15 |
| 20 | H2 aşı (D3) | Önemli | Guard dışı yazımı ABORT eden tetikleyici; CorrectPrices ve AttachLabel hariç | İşlendi §3.2 (koşullar), §5.1, M6 |
| 21 | H2 aşı (D3) | Önemli | Komutla değişen birime sunucu saatiyle `*SetAt`; Off ve geri alma penceresindeki push geri alamasın | İşlendi §3.1, §4.2 K2 (S1'de), M3 |
| 22 | H2 aşı (D1) | Önemli | Geri doldurmada push, WhatsApp kuralı ve shopper bildirimi bastırılır | İşlendi §4.3, §10.2 |
| 23 | H2 ölümcül (D2) | Ölümcül | Devredilmiş yayına sonradan eklenen sipariş akışa girmiyor (force-end sonrası, Basılanlar'dan yedek) | İşlendi §4.2 (eklemede kopya), §4.6 (`add-backup` kopyalar), §4.9-b |
| 24 | H2 ölümcül (D2) | Ölümcül | Sunucu geri alma penceresinde biten yayın hiç devredilmiyor | İşlendi §5.2 (oturum devir görülene kadar kuyrukta), §4.9 (tekrarlı onarım), §8.6 |
| 25 | H2 ölümcül (D3) | Ölümcül | Kapanış etiketlerden önce gidiyor: kod fiyatı geç eklemeyi kapsamıyor, eksik tutar | İşlendi M2, §4.1, §5.2 |
| 26 | H2 ölümcül (D3) | Ölümcül | Shipment ABORT tetikleyicisi canlı PC'nin `CorrectPrices` dosya yazımını kırıyor | İşlendi §5.1-a (mod On'da dosya bloğu atlanır), K7, §3.2 |
| 27 | H2 ölümcül (D3) | Ölümcül | `ActiveSaleScopes` geri almada bayat kalıyor | İşlendi §6.6 (ayrı tablo yok, defter + applock), M8 |
| 28 | H2 ölümcül (D1) | Ölümcül | Replay damgasız birimi eziyor; eski yedek iptali diriltiyor | Elendi: geçmiş yeniden oynatılmaz, geri doldurma yalnız boşu doldurur (§4.3, §8.7) |
| 29 | H2, D2 karşı örnek 3 | Önemli | Müşteri eksikse etiket kalıcı park | İşlendi (birleşik: 16) |
| 30 | H2, D2 karşı örnek 4 | Önemli | Kirayı tutan PC çökerse "daha önce istendi" görülmüyor | İşlendi (birleşik: 19) |
| 31 | H2, D3 karşı örnek 5 | Önemli | Devralmada sinyal yaşı şartı yok | İşlendi §4.6 `force-end` (15 dk, `owner-active`) |
| 32 | H2, D3 karşı örnek 6 | Önemli | Çevrimdışı bekleyen baskı yerel PrintedAt yazmıyor: çift kâğıt | İşlendi §5.5 (örtü kuyruk okuyucularından düşer), §10.3 |
| 33 | H1, D2 notu | Küçük | İptal edilen etiket kargo tutarında kalıyor (bugünkü hata korunuyordu) | Sahip kararı Y5 ile değişti: §7.2, M12, M13 |
| 34 | H1, D3 notu | Küçük | ProductId'nin yalnız eklemede yazılacağı K4'te var, riskte "eklenmedi" deniyor | İşlendi (birleşik: 8); tek ifade K1 |
| 35 | H2, D2 karşı örnek 6 | Küçük | Değer geç geliyor; yayın sonrası düzenleme bağlantı istiyor | Y3 ile kabul; asgari değer S1 + W1 (§13) |
| 36 | H1 ve H2 sahip soruları | — | Y1–Y7 soruları | §11 tablosu. "Eski sürüm varken tamamen kapalı mı?" sorusu M1 ile, "toplam farklıysa engel mi?" sorusu M11 ile yanıtlandı; KVKK sorusu açık madde. |

**Sonuç.** İki hakemin bütün aşıları (H1: 10, H2: 9; örtüşenler birleşik) ve bütün ölümcül bulguları (H1: 5, H2: 8) işlendi. Eşdeğer ama önerilenden farklı çözülenler:
- 14: D3 için önerilen "R1'de kümülatif isteği kapat" yerine kira, akışla aynı kapı altında ve fail-closed.
- 21: damga kuralı S2'de değil S1'de geliyor; böylece her geri alma, damgaya uyan bir imaja döner.
- 24: onarım işi v2 oturumlarını devretmiyor; devri istemcinin yeniden gönderimi tamamlıyor. Böylece "kuyruk boşalmadan devir yok" kuralı onarım işinde de korunuyor.

## 15. Spec incelemesi (2026-10-13) — bulgu tablosu

Kaynak: bu belgenin üç mercekle incelenmesi: para ve toplamlar, kod ve iç tutarlılık, eksiklik ve karar. Her bulgu kod kesitinde (a126ff26) ve belgede doğrulandı; hiçbiri gerçek dışı çıkmadı. Aynı sorunu farklı merceklerden bulanlar birleşik işlendi.

| # | Mercek | Önem | Bulgu | İşlem |
|---|---|---|---|---|
| 1 | para-toplam | Kritik | İptal bağı çözüyordu. Kodda `ShipmentId` "dekontla ödendi"nin tek işareti (`CorrectedPaymentRequests.cs:187-193`, `PrintedLabelsViewModel.cs:360-363`, `LabelRepository.cs:232`): yanlış iptal edilip geri alınan etiket ödenmemiş görünüyor, Shipped dosyada tutar iki kez sayılıyordu | İşlendi. İptal ve geri alma bağa dokunmaz, tutarı türetme düşürür (DM7, K7, §4.6, §7.2, M12, M13). Bağ yalnız dosya kargoya verilirken iptalli siparişlerden çözülür. Önerilen "`CancelSetAt < ShippedAt`" karşılaştırması yerine bu yol seçildi: push'tan gelen iptalde damga yok ve istemci saati kararda kullanılmıyor. Test §10.1-19, §10.3, saha §10.4-4 |
| 2 | para-toplam | Kritik | Ödeme kayıtları yalnız mod On'da yazılıyordu; açılıştan önceki istekler başka PC'de görünmüyor, aynı kapsam yeniden isteniyordu | İşlendi. `payment-requests/backfill`, Ws ilk açılışında ödeme geçmişi, mod Off'ta kirasız kayıt, mod kapısında `PaymentHistoryCompletedAt` (§4.7, §6.4, §8.2, §4.10-2, M17). Uzak `Covers`, önerilen `CoveredPrintedAt` yerine sipariş kümesiyle (`PaymentRequestRecordOrders`): iki bilgisayarın saati hiç karşılaştırılmaz. Saha §10.4-9 |
| 3 | para-toplam | Önemli | `disable` uzak etiketleri önbellekte bırakıyordu: kirasız Off'ta R1 çift tam istek penceresi geri açılıyor, her PC bütün açık dosyaları tutup `shipments/sync`'te birbirini eziyordu | İşlendi. Off turu uzak satırları siler, toplamları türetir, yalnız kendi açık dosyalarını tutar (`Shipments.OriginReplicaId`) (§8.5, §6.5-1, M15). Test §10.3 |
| 4 | para-toplam | Önemli | Force-end sonrası sahibin iptal ve fiyat değişiklikleri toptan reddedilip yeni etiketleri kabul ediliyordu (DM5'i spec'in kuralı üretiyordu); `GetActive` null dönüp sohbetten etiket yazılamıyordu | İşlendi. K5 istisnası (i): sahibin push'u yalnız K2 ile yargılanır (komutun damgaladığı birim kazanır). Aktif yayın yerelde açık kalır (§4.2, §5.4, §5.7, M18, risk 5). PC-B için ayrı "geç gelen değişiklikler" listesi yapılmadı: değişiklikler akışla iner, sayısı `sales-audit` ve gözlemde (§4.10, §10.5) |
| 5 | para-toplam | Önemli | Sunucuda etiket–dosya bağı yalnız Ws geri doldurmasıyla geliyor; 14 günden uzun kapalı PC'nin açık dosyaları açılışta 0'a türetiliyordu | İşlendi. Mod kapısı koşulu 6: her açık dosyada `OriginReplicaId` ve o kopyanın geri doldurması, 14 gün sınırı olmadan. Fark açıklanan (Y5) ve açıklanamayan diye ayrıldı; açıklanamayan > 0 ise `--apply` reddeder, `--accept-shipment` ile tek tek kabul (§4.10) |
| 6 | para-toplam | Önemli | Grup senkronu gecikince ikinci PC başka çapadan ikinci düşüm yazabiliyordu; (d) üye kesişimiyle aranmıyordu | İşlendi. `members-stale`, (e) adımında üye kesişimi, uzlaştırmada sunucu üyeleri (§2, §6.4, §6.6). Test §10.1-17 |
| 7 | para-toplam | Küçük | `ComputeShipping` yerel ayarı okuyabiliyordu; `begin` tutarı ayarla denetlemiyordu | İşlendi. `ComputeShipping(ShippingSettings)`, okuyuculara `OpenWhatsApp` ve `CorrectedPaymentRequests.cs:228`; `begin` kargo tutarını lisans ayarıyla denetler (§6.2) |
| 8 | para-toplam | Küçük | Onarım işinin (c) adımı ve K7 Shipped dosyanın donmuş tutarını düzeltiyordu | İşlendi §4.9-c, K7; test §10.1-19 |
| 9 | para-toplam | Küçük | Ön gönderim bekleyen komutları kapsamıyordu; ön gönderimin zaman aşımı tek başına isteği engelliyordu | İşlendi. Önce bekleyen komutlar; zaman aşımı engel değil, karar `stale-total`'ın; bu kişinin bekleyen komutu kalırsa açık engel (§2, §5.5, §6.4-1). Test §10.3 |
| 10 | para-toplam | Küçük | `begin` yalnız GUID'li üyeleri taşıyordu; GUID'siz üyesi olan kişide `stale-total` kalıcıydı, akışta taslağı yoktu | İşlendi. `memberIds` bütün üyeler, sunucu ham `CustomerId` ile eşler; GUID'siz taslak sipariş satırından (§4.4, §6.4). §6.5-2'deki "her müşterinin GUID'i var" iddiası düzeltildi: kira uzak etiketi olan her kişide zorunlu. `sales-audit` sayar. Mod kapısına koşul eklenmedi: açık kural var |
| 11 | kod-tutarlılığı | Kritik | §5.2 devri görünce kipe bakmadan `Authority = 'server'` yazıyordu; mod Off boyunca bitmiş yayınlar kilitleniyordu | İşlendi. `Authority = 'server'` yalnız mod On'da; mod Off'ta yalnız `ReleasedAt`; yönlendirici mod Off'ta hep yerel (§2, §3.2, §5.2, §5.3, §5.4). Test §10.3 |
| 12 | kod-tutarlılığı | Önemli | `disable` uzak satırları bırakıyordu (3 ile aynı) | Birleşik: 3 |
| 13 | kod-tutarlılığı | Önemli | ≤0.9.10'a dönüş, PC'nin Off turunu bitirmiş olmasına sessizce dayanıyordu; eski ikili `Mode = 1`'de dekont kargo adımını sessizce kaybediyordu | İşlendi. Ön koşul `SeenMode = 0` (`sales-sync status`'ta kopya başına), `deploy/README`'de bağımsız sıfırlama betiği, prova testi (§2, §8.6, §10.3) |
| 14 | kod-tutarlılığı | Önemli | `enable --apply` ile ilk tur arasındaki pencerede verilen kargo kararı ve bağ sessizce geri dönüyordu | İşlendi, önerilen (a) yolu: her v2 isteği `SeenMode` taşır, sunucu modu görmemiş kopyanın gönderimini Off kurallarıyla alır (§7.1, K4, K5-ii). İstemci modu kuyruk boşalınca açar; §8.4-3 gönderilmemiş dosya varken durur (§8.4, M16). Test §10.1-21, §10.3 |
| 15 | kod-tutarlılığı | Önemli | Shipped dosyada iptal push'ta reddediliyor (K6), komutta kabul ediliyordu; K3 komutla iptal edilmiş siparişe baskı dolduruyordu | İşlendi. K6 yalnız fiyat; iptal iki yolda §7.2'ye göre. K3, `CancelSetAt` doluysa baskı almaz ve "kâğıdı imha et" der (`mark-printed` ile aynı). Koşul birleşik durumdaki iptale değil komut damgasına bağlandı: aynı push'ta "basıldı, sonra iptal edildi" satırının PrintedAt'i bugünkü gibi kalır. **Düzeltildi §15-40:** damga `uncancel`'da da basıldığı için koşul artık damga ∧ güncel iptal |
| 16 | kod-tutarlılığı | Küçük | Mod Off'ta lisans geneli kargo ayarını yazacak yol yoktu | İşlendi. `sales-sync shipping set` CLI'ı (kuru koşu varsayılan), `UpdatedBy` (§4.8, §4.10-4, §6.2, §8.1-6) |
| 17 | kod-tutarlılığı | Küçük | `add-backup` ProductId'yi ebeveynden kopyalıyor, yerel `AddBackup` kopyalamıyordu | İşlendi: komut da ürünsüz ekler (bugünkü davranış). Kopyalama ayrı ürün kararı olarak "bilerek yapılmayanlar"da (§4.6, §12) |
| 18 | kod-tutarlılığı | Küçük | `legacy:` öneki miras replay'i ayırmıyordu; miras iş 409'da yanlış düşümü benimseyebilirdi | İşlendi §6.6: önek koşuldan çıktı, kural miras replay'e de uygulanır; miras iş benimsemez, #506'nın birleştirme sorusuna düşer. Test §10.3 |
| 19 | kod-tutarlılığı | Küçük | `LastLegacySalesPushAt` yalnız mod On'da yazıldığı için "7 gün iz yok" koşulu ölçülemiyordu | İşlendi: iz iki kipte, uyarı yalnız On'da (§3.1, §4.0, §4.10-3) |
| 20 | kod-tutarlılığı | Küçük | `PersonKey` harmanı belirsizdi; indeks harf ve sondaki boşluk farkını yutuyordu | İşlendi. İndeks ve kilit `PersonKeyHash binary(32)` üzerinde, metin BIN2 ve bellekte ordinal; tekil ihlal 409 (§2, §3.1, §6.4). Test §10.1-16 |
| 21 | kod-tutarlılığı | Küçük | `begin` idempotent değildi; yinelenen ya da çökme sonrası `begin` 500'e düşüyordu | İşlendi §6.4-a, -c, -h. Test §10.1-16 |
| 22 | kod-tutarlılığı | Küçük | Yerel metin boşaltması koşulsuzdu; silmeden sonra gelen geç ekleme metni geri yazıyordu | İşlendi. Yerel boşaltma da aynı ayara bağlı (`sales/state`), eklemede ve geri doldurmada boşaltma (§4.2, §4.3, §4.5, §9). Test §10.1-22 |
| 23 | kod-tutarlılığı | Küçük | `WpfReplicas` PK yalnız `ReplicaId`; lisans değiştiren PC eski lisanstaki sessizlik denetimini bozuyordu | İşlendi: PK `(LicenseId, ReplicaId)` (§3.1, §4.0) |
| 24 | kod-tutarlılığı | Küçük | Üç atıf kayması: 048:28, `:938`'in dosyası, hosted service başlatma iddiası | İşlendi §1, §6.6, §5.4, §10.3 |
| 25 | eksiklik-karar | Kritik | Kendi yayınının `server` olması moda bağlı değildi (11 ile aynı) | Birleşik: 11 |
| 26 | eksiklik-karar | Önemli | `disable` R1'i açıyor; Off'ta `ignored` kalıcı ayrışma bırakıyor; düşen bekleyen baskı çift kâğıt doğuruyordu | Birleşik: 3. Ek olarak `ignored`'da `current` uygulanır (§5.2); K2'nin `baseVersion` eşitliği Off'taki bilinçli yeni kararı kilitlemez (M14); bekleyen komutlar yerel yazıma çevrilir (§5.5, §8.5) |
| 27 | eksiklik-karar | Önemli | Sürüm düşürme Off turuna dayanıyordu (13 ile aynı) | Birleşik: 13 |
| 28 | eksiklik-karar | Önemli | Y4 geçişi uygulanamıyordu; `disable`'dan sonra PC'ler farklı yerel değerlere dönüyordu | Birleşik: 16. Ek olarak `disable` turu sunucu değerini yerel `AppSettings.Shipping`'e yazar (§6.2, §8.5). Açılışta kopya gerekmez: mod On'da sağlayıcı sunucu değerini okur |
| 29 | eksiklik-karar | Önemli | `MinClientVersion`'ın yayın adımı ve boş davranışı tanımsızdı; yarım bir hotfix derlemesi kapıyı geçebilirdi | İşlendi. Compose ortamı adımı, boşsa açılış reddedilir ve uçlar fail-closed, `featureLevel = 3` yalnız W3 derlemesinde, plansız ara sürüm kuralı (§2, §4.6-1, §4.10-1, -2, §8.1-4, -5) |
| 30 | eksiklik-karar | Önemli | Geri dönüşsüz KVKK boşaltması sahip onayı olmadan varsayılan açıktı | İşlendi. Varsayılan kapalı (bugünkü politika); sunucu ve yerel aynı ayara bakar; sahip kararı açık madde (§9, §11) |
| 31 | eksiklik-karar | Küçük | Y5 mod On'a yanlış gerekçeyle erteleniyordu | İşlendi. Y5 mod Off'ta Ws'nin yerel yolunda da (§7.4, M12); "kapsam dışı" ve "bilerek yapılmayanlar"dan çıkarıldı |
| 32 | eksiklik-karar | Küçük | K6/K7/§7.2 tutarsızlığı; Shipped'de geri alma çift sayım; K7'nin kip koşulu | Birleşik: 1, 8, 15 |
| 33 | eksiklik-karar | Küçük | Force-end onay metni kaybı küçük gösteriyordu | Birleşik: 4. K5 istisnası sahibin force-end'den önce gönderemediği düzenlemeleri de kabul ediyor; onay metni gerçek davranışa göre yeniden yazıldı (§5.7) |
| 34 | eksiklik-karar | Küçük | `client-update-required`, `content-conflict`, `owner-active` istemcide eşlenmemişti; Off'taki metin yanıltıcıydı | İşlendi §5.3, §5.7 |
| 35 | eksiklik-karar | Küçük | PersonKey harmanı (20 ile aynı) | Birleşik: 20 |
| 36 | eksiklik-karar | Küçük | Ortak bakiyeye yüklenen zorunluluklar yalnız bu belgede duruyordu | İşlendi §13: D-ortak-bakiye docs PR'ı ve bağımlılık notu. Ortak bakiye spec'i bu dalda değiştirilmedi |
| 37 | eksiklik-karar | Küçük | DM1 metindeki istisnalarla çelişiyordu; §4.10 başlığı `sales-audit`'i S3'e koyuyordu | İşlendi §2, §3.3, §4.10 |

**Sonuç.** 37 bulgunun hiçbiri reddedilmedi; 8'i başka bir bulguyla birleşik işlendi. Önerilenden farklı çözülenler:
- 1: saat karşılaştırması yerine "kargoya verirken iptalli bağı çöz".
- 2: `CoveredPrintedAt` yerine sipariş kümesi.
- 4: PC-B'ye ayrı liste yerine akış, `sales-audit` ve gözlem.
- 10: mod kapısı koşulu yerine açık eşleme kuralı ve rapor.
- 14: önerilen iki yoldan (a).
- 17: tercih edilen yol bugünkü davranış (ürünsüz).

Bu turun sonunda sahip kararı gerektiren tek açık madde KVKK metin boşaltmasıydı (§11).

### Üçüncü inceleme (2026-10-13, 20 bulgu)

Kaynak: §1–§14'ün sahip yanıtları (Y1–Y7) ışığında yeniden incelenmesi. Her bulgu kod kesitinde (a126ff26) ve belgede doğrulandı; hiçbiri gerçek dışı çıkmadı. Numaralar ilk tablonun devamıdır.

| # | Önem | Bulgu | İşlem |
|---|---|---|---|
| 38 | Önemli | `disable` açık dosyanın Off'taki sahibini `OriginReplicaId`'ye (açan PC) göre seçiyordu. Y2 altında mod On'da PC-B, PC-A'nın etiketleri için dosya açar; Off'ta PC-A dosyayı siler ama etiketleri bağlı kalır (`GetUnattachedByCustomer` onları almaz), PC-B etiketsiz dosyayı tutar. PC-A'nın sonraki dekontu yeni dosya açar (`ShipmentService.cs:50-65`), havuz eksik sayılır, ilk ürün hiç gönderilmez. Risk 15'teki "bugünkü davranış" iddiası yanlıştı: bugün bir PC'nin etiketi başka PC'nin dosyasına hiç bağlanamıyor | İşlendi. `disable --apply` Shipped olmayan dosyanın `OriginReplicaId`'sini bağlı etiketlerin sahibine göre yeniden yazar (oturum sahibi, yoksa siparişin kaynağı; geri yükleme zinciri çözülür). Off turu bu değeri son akışla alır (§3.1, §4.10, §8.5, M19). Sıfırlama betiği aynı kuralı yerel veriyle uygular (§8.6). Risk 15 yeniden yazıldı. Önerilen "karışık dosyada tur dursun" yerine karışık dosya bağlı tutarı büyük olan kopyaya verilir ve kuru koşuda listelenir: acil kapatma turu takılmamalı, Y2 altında karışık dosya ancak iki PC'den yayın açılırsa oluşur. Test §10.1-25, §10.3 |
| 39 | Önemli | `disable` turunun son akışında dosya yoktu (§4.4 yalnız mod On); tutulan dosyanın bayat aynası yerel otoriteye dönüyordu. `shipments/sync` alanları koşulsuz ezdiği için (`LicensesShipmentsSyncController.cs:121-126`) kargolanmış dosya Held'e dönüyor, yeni etiket ona bağlanıyordu | İşlendi: (a) ve (b)'nin ilk yarısı. Akış dosyaları iki kipte verir, istemci `disable` turunda da uygular (§4.4, §5.4, §8.5-1). Sunucuda Shipped her kipte ve v1'de tek yönlü; v2 yanıtı güncel satırı döner (§7.1, M19). Yerelde de Shipped son durumdur (`ShipmentService.cs:79`, `:183`). "`CommandedAt`'li dosyada karar alanları yalnız komutla" kısmı alınmadı: Off'ta dosyanın sahibi PC kararı yeniden kendisi verir (Held → Shipped) ve bu kural onu kilitlerdi; bayat taban (a) ile tazelenir. Test §10.1-24, §10.3 |
| 40 | Önemli | K3 baskıyı `CancelSetAt` doluysa reddediyordu; `uncancel` da damga bastığı için komutla iptal edilip geri alınmış etiketin baskısı hiç alınmıyordu. İstemci `current`'ı uygulayıp PrintedAt'i siliyor, "kâğıdı imha et" diyor, etiket kuyruğa dönüyor; döngü hiç bitmiyor ve etiket hiçbir toplama girmiyordu | İşlendi. K3 koşulu `CancelledAt` dolu ∧ `CancelSetAt` dolu, yani şu anda komutla iptal. `uncancel` damgayı basar, hiçbir yol silmez; damga birimin değerini söylemez (§2, §4.2, §4.6). Aynı push'taki "basıldı, sonra iptal" satırı etkilenmez. §15-15'in gerekçesi buna göre düzeltildi. Test §10.1-23 |
| 41 | Kritik | Mod On'un ilk turu F05 öncesinin kayıp güncellemelerini sunucunun eski değeriyle eziyordu: iptal, baskı ya da fiyat yerelde var, sunucuda yok (`032_sync_ack_revision.sql:1-8`, 2026-09-10; 032'den sonra bu satırları yeniden kuyruğa alan göç yok). İptal edilmiş ürün yeniden borç görünüyor, basılmış etiket kuyruğa dönüyor; yerel değer ezildiği için geri dönüşü yok | İşlendi. `sales/backfill` mevcut satırda damgasız birimleri karşılaştırır, yazmaz, `diverged` döner (§4.3). Mod Off'ta, geri yüklenmemiş ve kopyalanmamış kopyada yerel kazanır: satırlar normal kuyruğa yeniden alınır ve v2 push'uyla gider. `backfillCompleted` ancak bunlar uygulanınca gönderilir; mod kapısı onu istediği için mod onarım bitmeden açılamaz (§8.2-4, -5, M20). Geri yüklenmiş kopyada sunucu kazanır. Sayılar `sales-audit`, `status` ve `enable` raporunda; ilk turda kendi satırının ezilmesi `catchup-overwrite` olarak sayılır ve uyarılır (§5.4). Önerilen "sayı 0 değilse `--apply` reddetsin ya da satırlar tek tek kabul edilsin" yerine koşullu otomatik onarım seçildi. Bu spec'ten önce bir etiketi yalnız kendi PC'si yazıyor (sunucuda Orders birimlerini yazan tek yer `LicensesSessionsSyncController.cs:278-284`); özgün veritabanında yerel değer bu yüzden her zaman yenidir. Elle kabul Burak'a karar veremeyeceği bir liste bırakırdı. Test §10.1-26, §10.3 |
| 42 | Önemli | K3 ve `uncancel` damgası (40 ile aynı; kanıt: force-end'de ve eski sürümde de aynı döngü) | Birleşik: 40 |
| 43 | Önemli | `force-end` devrediliyor hâlinde de çalışıyordu. O hâl tanım gereği etiketlerin sahipte beklediği hâldir: devralan PC eksik veriyle ödeme isteği ve koli üretiyor, sunucu da eksik olduğu için `stale-total` yakalamıyordu. M2'nin ve §4.9'un "kuyruk boşalmadan devir yok" ilkesi bozuluyordu | İşlendi. `force-end` yalnız `EndedAt IS NULL`; devrediliyor hâlinde 409 `handing-over {unsentLabelCount}`. Sahibin `Release = false` kapanışı gönderilmemiş sayıyı taşır, çip gösterir. Sahip hiç dönmeyecekse Burak'ın CLI'ı sayı görülerek kapatır (§2, §3.1, §4.1, §4.6, §4.10, §5.3, §5.7, Y7, risk 4). `EndedByReplicaId` yerine `ForceEndedBy` (`cli` ya da ReplicaId): CLI ile kapanan oturumda da K5 istisnası (i) geçerli. Sahip kararı (2026-10-13): devralma yok, 15 dk devralma yalnız canlı yayın ortasında çöken bilgisayar için (Y8, §11) |
| 44 | Önemli | Sunucuda bağı bilinmeyen siparişler (Ws'ye hiç güncellenmemiş, emekli PC'nin kargolanmış geçmişi) mod On'da `attach-shipment` adayıydı: gönderilmiş ürün yeni dosyanın tutarına ve eşiğe giriyordu. `expected` aynı satırları içerdiği için 409 da çıkmıyordu | İşlendi. `OriginReplicaId`'si boş sipariş aday değildir; `add-backup` `OriginReplicaId` yazar. Sayı ve tutar `sales-audit`'te ve `enable` raporunda; gerçekten gönderilmemiş olanlar `sales-sync adopt-orders` ile aday yapılır (§4.6, §4.10, M22). Alternatif (koşul 6'yı Shipped dosyalara genişletmek) sunucuya bağı getirmediği için seçilmedi. Test §10.1-27 |
| 45 | Önemli | Açılış penceresi istemcinin bildirdiği `SeenMode`'a dayanıyordu. Mod açılmadan önce alınmış bir yedek geri yüklenince (`RestoreService.cs:44-80` bütün dosyayı değiştirir, `SalesSyncState` de yedekten gelir) bayat kuyruk `SeenMode = 0` ile Off kurallarıyla alınıyor; Off'ta verilmiş iptal ve kargo kararı geri dönüyordu | İşlendi. Sunucu pencereyi kopya geçmişine bağlar: satır mod değişiminden önce var, `PreviousReplicaId` boş, ikinci parmak izi yok, `SeenModeOnAt` bu açılıştan eski ya da boş (§2, §3.1, §4.0, K4, K5, §7.1, M21). İstemci geri yüklemeden sonra kipi tazelemeden göndermez (§8.7). Test §10.1-21, §10.3 |
| 46 | Önemli | Mod Off'ta Y5 fark olarak çalışıyordu ama Ws öncesinde iptal edilmiş bağlı etiket açık dosyanın tutarında duruyordu (`LabelService.cs:125-162`); Ws'de iptal geri alınınca fiyat ikinci kez ekleniyordu. Ayrıca `ignored`/`rejected`'daki `current` etiketin fiyatını değiştiriyor, dosyaya dokunmuyordu | İşlendi. Ws ilk açılışta Shipped olmayan yerel dosyaları bağlardan bir kez türetir ve farkı günlüğe yazar; o anda yalnız yerel bağ olduğu için türetme kesindir (§3.2, §7.4, §8.2-8, M12). `current` uygulaması Y5 bloğundan geçer (§5.2). Önerilen "Off'ta Y5 her seferinde türetsin" alınmadı: `disable` sonrası karışık dosya başka PC'nin etiketlerini taşır, yerel türetme onları düşürürdü. `enable` raporundaki açıklanan farkın bu yüzden çoğunlukla 0 olacağı yazıldı (§4.10-7). Test §10.3 |
| 47 | Küçük | İlk yetişme birkaç tur sürerken kendi devredilmiş oturumları `local` kalıyordu; yerel yazım `SeenMode = 1` ile K5'e çarpıp kayboluyordu | İşlendi. 2. adım yerelde `ReleasedAt`'i bilinen kendi oturumlarını hemen `server` yapar; bilinmeyenler `FirstCatchUpAt`'e kadar düzenlemeye kapalıdır (§5.3, §8.4-2). Test §10.3 |
| 48 | Küçük | 3. adım gönderilmemiş yerel dosyada durup "1. adımdan yeniden dene" diyordu. Mod 1'de kargo gönderimi kapalı ve sunucu yok saydığı için ilk yetişme hiç bitmiyordu | İşlendi. 2. adım SQLite yazma kilidi altında kargo kuyruğunu ve canlı olmayan oturumların etiket kuyruğunu yeniden denetler; boş değilse mod 0 kalır. Ondan sonra tetikleyici yerel kargo yazımını ABORT ettiği için 3. adımdaki durum oluşamaz; o kural kalktı ve bir test bunu sabitler (§8.4, §10.3). Önerilen "3. adımda modu 0'a döndür" alınmadı: kopya 1 bildirdikten sonra sunucu onun 0'ını artık pencere saymaz (§4.0) |
| 49 | Küçük | K4 istisnasıyla gelen bağ dosyayı türetmiyordu. Açılış penceresinde dosya bağlarından önce gelince (kargo 30 sn, etiket 60 sn; `ShipmentSyncHostedService.cs:19`, `SessionOrderSyncHostedService.cs:20`) tutar onarım işine kadar eksik kalıyordu | İşlendi. K7'ye kargo bağı eklendi: K4 istisnası, `sales/backfill` bağı ve komutlar eski ve yeni dosyayı türetir (§4.2, §4.3, §7.1). Test §10.1-21 |
| 50 | Küçük | Ws öncesi iptal, Ws'de geri alma (46 ile aynı) | Birleşik: 46 |
| 51 | Küçük | K4'te K2'nin `baseVersion` istisnası yoktu. `disable` sonrası komutla bağı çözülmüş etiketin yerel bağlanması `ignored` alıyor, `AttachLabels`'ın eklediği tutar kalıyor (`ShipmentService.cs:73-102`), dosya her dekontta şişiyordu | İşlendi. K4 mod Off'ta `baseVersion = Version` ile de yazar; `current` uygulaması Y5 bloğundan geçer (§4.2, §5.2, §8.5-1) |
| 52 | Küçük | `SeenMode = 0`, bekleyen komutların yerel yazıma çevrilmesinden önce bildirilebiliyordu. Süreç arada kapanırsa destek "tur bitti" sanıp eski sürümü kuruyor, çift kâğıt doğuyordu | İşlendi. 0 ancak `Mode = 0` ∧ `PendingSalesCommand` boşken bildirilir; açılışta yarım kalmış 3. adım yeniden koşar (§2, §8.5). Test §10.3 |
| 53 | Küçük | `--accept-shipment`'ın ne yaptığı yazılı değildi; `--apply` kabul edilen dosyayı eksik bağlardan türetip tutarı 0'a indiriyordu | İşlendi. Kabul "saklanan tutar doğru" demektir: dosya türetilmez, K7 ve onarım dokunmaz, `attach-shipment` onu seçmez, havuz saklanan tutarı sayar. Eksik bağların siparişleri 44 gereği aday olmaz; aynı tutar iki dosyada sayılmaz (§4.6, §4.10). Test §10.1-27 |
| 54 | Küçük | `begin`'in mod Off'taki davranışı tanımsızdı; Off turunu koşmamış PC uzak etiketlerle istek kurup R1 penceresini açabiliyordu | İşlendi. `begin` Off'ta 409 `mode-off`; istemci Off turunu koşar ve isteği yeniden kurar. `finish`, liste ve `backfill` iki kipte çalışır (§4.7, §5.3, §6.4). Test §10.1-28 |
| 55 | Küçük | K7 bağ değişikliğinde türetmiyordu (49 ile aynı; kanıt `sales/backfill` bağını da kapsıyor) | Birleşik: 49 |
| 56 | Küçük | Kargoya verirken iptalli bağın çözülmesi canlı oturumdaki siparişe dokununca görünürlük kuralı 409 `session-live` veriyordu; canlı PC kendi kolisini kapatamıyordu. DM1 bu yazımı istisna olarak listelemiyordu | İşlendi. Kargo bağı mod On'da her oturum hâlinde sunucunundur (push yazamaz, K4). Görünürlük denetimi yalnız fiyat, iptal ve yedeğe uygulanır; DM1 ve §2 buna göre yazıldı (§2, §3.3, §4.6-3). Önerilen "başka PC'nin canlı siparişinde 409 ya da erteleme" gerekmedi: bağ birimini canlı sahip zaten yazamıyor |
| 57 | Küçük | `disable`'dan sonra bağlı dosyası yerelde olmayan etiketin iptali, geri alınması ve fiyat düzeltmesi `CorrectPrices`'ın `s is null` istisnasına çarpıyordu (`LabelService.cs:414-421`); `Classify` bulunamayan dosyayı düzeltilebilir sayıyordu (`:339-355`) | İşlendi. Mod Off'ta yerelde olmayan dosya atlanır, istisna atılmaz; `Classify` onu "başka bilgisayarın dosyasında" sınıfına koyar ve fiyatını düzeltmez (§7.4). 38'in düzeltmesinden sonra bu durum yalnız karışık dosyada kalır. Test §10.3 |

**Sonuç.** 20 bulgunun hiçbiri reddedilmedi; 3'ü başka bir bulguyla birleşik işlendi (42 → 40, 50 → 46, 55 → 49). Önerilenden farklı ya da kısmen alınanlar:
- 38: karışık dosyada turu durdurmak yerine büyük paya verme ve listeleme.
- 39: `CommandedAt` kilidi alınmadı; Off'taki sahip kararı kilitlenmesin.
- 41: elle kabul yerine koşullu otomatik onarım (yerel kazanır yalnız özgün kopyada ve mod Off'ta).
- 44: koşul 6'nın genişletilmesi seçilmedi.
- 46: Off'ta her seferinde türetme yerine bir kez taban türetme.
- 48: 3. adımda moda geri dönüş yerine 2. adımda kilit altında denetim.
- 56: 409 ya da erteleme yerine bağın her hâlde sunucuya ait olduğunun yazılması.

Sahip kararı bekleyen madde kalmadı (§11): KVKK metin boşaltması yapılmaz; aynı makinede geri yüklenen kopya hemen devralır (2026-10-14). Devrediliyor yayının başka bilgisayarca devralınması sahip kararıyla kapandı: devralma yok (Y8, 2026-10-13). Geri yüklenen kopya başka bir makinedeyse aynı karar geçerlidir; S1'in §8.7 devralması bunu henüz uygulamaz, kural S2'de sunucuya yazılır ve W1 ondan önce açılmaz (§8.7, §13).
