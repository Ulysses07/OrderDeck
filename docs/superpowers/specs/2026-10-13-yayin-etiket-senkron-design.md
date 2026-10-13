# Yayın ve Etiket Senkronu (Çoklu Bilgisayar, Faz 2) — Tasarım

**Tarih:** 2026-10-13 · **Durum:** sahip kararları alındı (§11), tasarım incelemesinin bütün aşıları ve ölümcül bulguları işlendi (§14), uygulama planı bekliyor · **İlgili:** `2026-10-05-coklu-bilgisayar-senkron-design.md` (dal `docs/coklu-bilgisayar-senkron`; bu belge onun "Faz 2" satırının ve kargo kısmının yerini alır), `2026-10-11-ortak-bakiye-design.md`, `2026-10-10-banka-havale-onay-design.md`, PR #506 (ödeme isteği kişi anahtarı), PR #508 (Basılanlar, kod başına tek fiyat)

Kaynaklar: LiveDeck `origin/master` a126ff26. Kod ağacı 04f2b3d1 ile aynı, fark yalnız web sürüm bump'ı; #506 ve #508 dahil. Satır numaraları bu kesite göredir. Sahada v0.9.10 (yerel şema 48). Üç aday tasarım iki hakemle karşılaştırıldı. Hakem 1 "kapanan yayın sunucuya devredilir" (D3, 8,2) tasarımını, hakem 2 "yayın sahipliği + sunucuda doğrulanan komutlar" (D2, 7,5) tasarımını seçti. İkisi aynı mimarinin iki varyantı: canlı yayın sahibinde yerel, bitince sunucuya geçer, sonrası komutla değişir. Bu belge ikisini birleştirir. D1 (çok yazıcılı, istemci saatli birim damgası) elendi. İki hakemin bütün aşıları ve ölümcül bulguları §14'te tek tek eşlendi.

## 1. Amaç ve kapsam

**Sorun (bugün)**
- Yayın ve etiket yalnız gönderiliyor, başka bilgisayara inmiyor (`OrderDeck.App/Services/Sync/SessionOrderSyncService.cs:15-17` "Push-only"). Sunucuda Orders ve StreamSessions için çekme ucu yok.
- Sunucu sipariş satırını bütünüyle eziyor, son gelen kazanıyor (`OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs:271-294`; oturumda `:89-96`). Bayat bir tam satır başka bilgisayarın iptalini geri getirir ve stok defterini de geri sarar. Bayat oturum gönderimi `EndedAt`'i NULL'layıp yayını yeniden "açar" (`:92`).
- Katalog bilmeyen eski istemcinin güncellemesi `ProductId`'yi NULL'luyor (`:285-286`).
- `ParentLabelId`, `ShipmentId`, `PrintBatchId`, `PrintSeq`, `PriceCorrectedAt` ve `PaidPrice` yalnız yerelde duruyor (`OrderDeck.Core/Storage/Migrations/048_label_print_batch.sql:28`). Başka bilgisayar yedek ilişkisini, "dekontla ödendi" bilgisini ve kâğıt grubunu bilemez.
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
- Mod Off'ta (geçiş dönemi) sahip kararı Y5'in yerel yolda uygulanması (§7.4).

## 2. Kavramlar

- **Kopya kimliği (`ReplicaId`):** yerel veritabanına bağlı GUID (`_meta.ReplicaId`, göç 049). `AppSettings.InstallationId` (`AppSettings.cs:124`) kullanılmaz: o ayar dosyasına bağlı; sahiplik ise canlı kuyruğu tutan veritabanı dosyasını izlemeli. Yedekten geri yüklemede yenilenir (§8.7).
- **Yayın hâlleri** (sunucu satırına göre). Her hâlin **tek** yazıcısı vardır (DM1):

  | Hâl | Sunucu | Yazıcı | Başka bilgisayarda |
  |---|---|---|---|
  | Canlı | `EndedAt IS NULL` | yalnız sahip kopya (push) | yalnız başlık: "PC-A'da canlı" |
  | Devrediliyor | `EndedAt` dolu, `ReleasedAt IS NULL` | yalnız sahip kopya (push) | başlık: "PC-A'da yayın bitti, etiketler gönderiliyor" |
  | Devredilmiş | `ReleasedAt` dolu | yalnız sunucu komutu (sahip dahil herkes) | tam satırlar, akıştan |

- **Sahip:** yayını açan kopya (`StreamSessions.OwnerReplicaId`). NULL, yayının eski sürümde açıldığı anlamına gelir (miras).
- **Devir (release):** sunucunun yayını sahipten alması. İki yolla olur. (a) Sahibin `Release=true` taşıyan kapanış gönderimi; bu yalnız yayının etiket kuyruğu boşken gönderilir (§5.2). (b) `force-end` komutu (§4.6). Devir tek işlemde `ReleasedAt` yazar ve yayının bütün siparişlerine `SessionReleasedAt` basar; siparişler değişiklik akışına o anda girer.
- **Yerel otorite (`StreamSession.Authority`):** `local` ise satırı bu bilgisayar yazar. `server` ise yalnız komut yanıtı ve akış yazar; yerel yazım tetikleyiciyle ABORT edilir. Kendi yayını ancak devir sunucudan **görülünce** `server` olur: ya kapanış yanıtında ya da akışta `ReleasedAt` gelince. O ana kadar yereldir (§5.2).
- **Komut:** devredilmiş yayının etiketini ya da kargo dosyasını değiştiren istek (§4.6). Sunucuda doğrulanır, tek işlemde uygulanır, `CommandId` ile idempotenttir. Beklenen satır sürümlerini taşır (iyimser eşzamanlılık). Sonuç yanıtla bu bilgisayara, akışla öbürlerine iner.
- **Birim ve sunucu damgası:** birlikte yazılan alanlar. Birimler:
  - fiyat {Price, PriceCorrectedAt, PaidPrice}
  - iptal {CancelledAt, CancelReason}
  - yedek {IsTentativeBackup}
  - kargo bağı {ShipmentId}
  - baskı {PrintedAt, PrintBatchId, PrintSeq}

  Komutun değiştirdiği birime sunucu saatiyle `PriceSetAt`, `CancelSetAt`, `BackupSetAt` ya da `ShipmentSetAt` basılır. Damgalı birimi hiçbir push değiştiremez (§4.2 K2). İstemci saati hiçbir çakışma kararında kullanılmaz.
- **Oluşum alanları:** SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, AddedAt, IsShippingFee, IsBackupPromoted, ProductId, ProductVariantId, ParentOrderId. Yalnız eklemede yazılırlar; her biçimde ve her kipte. CustomerId'yi yalnız sunucudaki birleştirici taşır.
- **Mod kapısı (`Licenses.SalesSyncMode`):** lisans başına Off ya da On.
  - Off: bugünkü davranış. Ws yalnız yeni alanları gönderir ve geçmişi doldurur.
  - On: akış, komutlar, kargonun sunucuya geçmesi, lisans geneli kargo ayarı ve ödeme kirası birlikte açılır.
  - Yalnız CLI açar; koşullar §4.10'da. Açıkken eski sürümün yazımı sessizce değil görünür biçimde reddedilir.
- **PersonKey:** sunucunun kişi anahtarı. `a`, sahip satırın asıl kaydıdır (`CustomerIdResolver.cs:49`). `a`'nın `GroupId`'si boş değilse anahtar `g:{GroupId}` (ham değer, ordinal karşılaştırma), boşsa `c:{a hex}`. Bu, yereldeki `CustomerGroup.PersonKey` ile aynı sınıflamadır (`OrderDeck.Core/Customers/CustomerGroup.cs:21-22`; önekler orada `grp:`/`id:`). Ortak bakiye §2'deki `S(a)` ile aynı kişiyi gösterir.
- **PersonKey kirası:** kişi×kapsam başına, bilgisayarlar arası ödeme isteği kirası (`payment-requests/begin|finish`, §6.4). Yerel `PaymentPersonGate`'in sunucu ikizidir. Anahtar PersonKey'dir; ayrıca üye kümesi kesişimi de denetlenir. Böylece grup senkronu gecikse bile iki bilgisayar aynı kişiyi bulur.
- **Ön gönderim:** sunucu toplamıyla karşılaştırmadan ya da sunucuda satır seçen bir komuttan önce bu bilgisayarın ilgili gönderim kuyruklarının (müşteri, yayın ve etiket) sınırlı sürede boşaltılması. Deseni `SyncFlushService.cs:81-97`.
- **Bekleyen komut:** çevrimdışı kuyruğa yalnız iki komut girer: devredilmiş yayından kalan kuyruk etiketinin iptali ve ilk baskısı (§5.5). Yerel okuyucular bunları uygulanmış sayar (örtü).
- **Ws:** bu spec'in WPF değişikliklerini taşıyan ilk Velopack sürümü (öneri v0.9.11, yerel şema 49). Sunucudaki karşılığı `SalesSync:MinClientVersion`.

## 3. Veri modeli

### 3.1 Sunucu (EF göçleri; yalnız ekleyici; `Program.cs:864-872` açılışta uygular)

| Tablo | Değişiklik | PR | Gerekçe |
|---|---|---|---|
| `StreamSessions` | `OwnerReplicaId uniqueidentifier NULL`, `OwnerMachineName nvarchar(128) NULL`, `EndedByReplicaId uniqueidentifier NULL`, `ReleasedAt datetimeoffset NULL` | S1 | Sahiplik ve devir (§2). |
| `StreamSessions` | `ChangeSeq rowversion` + `(LicenseId, ChangeSeq)`; filtreli `(LicenseId)` WHERE `[ReleasedAt] IS NULL` | S1 | Akış; canlı ve devrediliyor listesi (§4.5). |
| `Orders` | `ParentOrderId`, `ShipmentId` (uniqueidentifier NULL), `PrintBatchId nvarchar(40)`, `PrintSeq int`, `PriceCorrectedAt datetimeoffset`, `PaidPrice decimal(18,2)`, `OriginReplicaId uniqueidentifier` | S1 | Yerelde kalan 048 alanları ve yedek bağı (§1). |
| `Orders` | `SessionReleasedAt datetimeoffset NULL` | S1 | Akış süzgeci; devirde ve eklemede dolar (DM3). |
| `Orders` | `PriceSetAt`, `CancelSetAt`, `BackupSetAt`, `ShipmentSetAt` (datetimeoffset NULL, yalnız komut basar, sunucu saati) | S1 | K2: komut kararını hiçbir push geri alamaz. S1'de kolon ve kural, S2'de yazıcı. |
| `Orders` | `TextScrubbedAt datetimeoffset NULL` | S1 | KVKK (§9). |
| `Orders` | `ChangeSeq rowversion` + `(LicenseId, ChangeSeq)`; filtreli `(LicenseId, ShipmentId)` WHERE `[ShipmentId] IS NOT NULL` | S1 | Akış; dosya türetme. |
| `Orders` | `SyncVersion` (`LicenseDbContext.cs:488`) değişmez, telde `Version` adıyla açılır | S1 | Komutların beklenen sürümü. |
| `Shipments` | `ChangeSeq rowversion` + indeks; `Version int` jeton; `AmountDerivedAt datetimeoffset NULL`; `CommandedAt datetimeoffset NULL` | S1 | `AmountDerivedAt` doluysa tutar bağlı siparişlerden türetilir (DM7). |
| `Licenses` | `SalesSyncMode tinyint NOT NULL DEFAULT 0`, `SalesSyncModeChangedAt`, `LastLegacySalesPushAt` | S1 | Mod kapısı; protokolsüz istek izi. S1 okur, CLI S3'te gelir. |
| `LicenseShippingSettings` (yeni) | `LicenseId` PK, `FreeShippingThreshold decimal(18,2) NULL`, `ShippingFee decimal(18,2) NULL`, `Version int` jeton, `UpdatedAt`, `UpdatedByReplicaId` | S2 | Sahip kararı Y4: tek, lisans geneli ayar (§6.2). |
| `WpfReplicas` (yeni) | `ReplicaId` PK, `LicenseId`, `MachineName`, `HardwareFingerprint`, `ClientVersion nvarchar(32)`, `Protocol tinyint`, `PreviousReplicaId`, `FirstSeenAt`, `LastSeenAt`, `BackfillCompletedAt`, `ReportedThreshold`/`ReportedFee decimal NULL`; indeks `(LicenseId, LastSeenAt)` | S1 | Kalp atışı (sahip sessizliği, §4.6), mod kapısı (§4.10), kopya veritabanı tespiti (§8.7). |
| `SalesCommandLogs` (yeni) | `CommandId` PK, `LicenseId`, `ReplicaId`, `Kind nvarchar(32)`, `RequestHash binary(32)`, `Status`, `ResultJson`, `CreatedAt` | S2 | İdempotency ve denetim izi. `ResultJson` yalnız Id, sürüm ve tutar taşır; ad ya da mesaj metni **taşımaz** (§9). 90 gün. |
| `PaymentRequestRecords` (yeni) | `Id` PK (istemcinin `recordId`'si), `LicenseId`, `PersonKey nvarchar(80)`, `ScopeKey nvarchar(128)`, `OwnerWpfCustomerId`, `ReplicaId`, `MachineName`, `State tinyint {Sending, Delivered, Unconfirmed, Abandoned}`, `ProductTotal`, `TotalBeforeBalance`, `NetAmount`, `AppliedAmount`, `FullyFromBalance`, `ShippingSettingsVersion`, `StartedAt`, `LeaseUntil`, `FinishedAt`, `Version int` jeton | S3 | Kira ve bilgisayarlar arası teslim kaydı (§6.4). |
| `PaymentRequestRecords` | Filtreli **tekil** `(LicenseId, PersonKey, ScopeKey)` WHERE `[State] = 0` (Sending); `(LicenseId, PersonKey, ScopeKey, StartedAt)` | S3 | DM6: kişi×kapsamda tek canlı kira. Yarışın hakemi indeks. |
| `PaymentRequestMembers` (yeni) | `(RecordId, WpfCustomerId)` PK, `LicenseId`, `ScopeKey`; indeks `(LicenseId, WpfCustomerId, ScopeKey)` | S3 | Üye kesişimi denetimi (§6.4-c). |

Göç notları
- `rowversion` eklemek tablonun her satırını yazar. Prod SQL Server Express'te `ONLINE` indeks işlemi yok (ortak bakiye §3.1). S1'den önce Burak prod'da Orders, StreamSessions ve Shipments satır sayılarını salt okunur bir sorguyla alır; süre `restore-verify` kopyasında ölçülür. `/ready` 60 sn sınırına yakınsa `rowversion` ve indeksler ayrı bir göçe ayrılır ve o göç yayın penceresi dışında, bakım deploy'unda çıkar.
- Geri doldurma göçte yapılmaz; onu `sales-release-repair` işi yapar (§4.9).
- `Order.cs:87-89`'daki "rowversion değil, Postgres planı yüzünden" notu: müşteri projeksiyonu aynı bağımlılığı zaten aldı (`LicenseDbContext.cs:704-711`). Postgres göçü ikisini birlikte çözer (§12).

### 3.2 Yerel SQLite — `049_sales_sync.sql` (şema 49)

Kurallar
- Göç yalnız ekleyicidir. Tetikleyiciler yalnız yerleşik SQL kullanır (`045_customer_unit_stamps.sql:61-70` ve 048 kuralı).
- Önceki sürüm kolonları adla okuduğu için şema 49'da çalışır. Prova: `Migration049RehearsalTests`, `Migration045RehearsalTests` deseninde.
- **Mod Off'ta 049'un ABORT tetikleyicilerinin hiçbiri etkili olmaz:** `Authority` hep `local` kalır, `SalesSyncState.Mode` 0'dır. Davranıştaki tek fark kuyruğa alma tetikleyicisidir.

Kolonlar ve tablolar
- `_meta.ReplicaId TEXT`. Göç `lower(hex(randomblob(16)))` ile tohumlar.
- `StreamSession`:
  - `Origin TEXT NOT NULL DEFAULT 'local'` (`local` ya da `remote`; `remote` = akıştan indi)
  - `Authority TEXT NOT NULL DEFAULT 'local'` (`local` ya da `server`)
  - `OwnerMachineName TEXT`, `ReleasedAt INTEGER`
- `Label.ServerVersion INTEGER` ve `Shipment.ServerVersion INTEGER`: sunucunun `Version`'ı, komutun beklenen sürümü.
- `RemoteLiveSession(Id TEXT PK, Title, StartedAt, EndedAt, OwnerMachineName, OwnerLastSeenAt, State)`: başka bilgisayarın canlı ya da devrediliyor yayını. Bu satırlar `StreamSession`'a **hiç girmez**. Böylece `GetActive` (`SessionRepository.cs:67-74`, koşulu "`EndedAt IS NULL` olan herhangi bir satır") ve eski sürüme dönüşte `StartStream` ile açılıştaki kurtarma ekranı (`StartupFlow.cs:102-118`) yanılmaz.
- `SalesSyncState(Id=1, Mode, ModeSeenAt, ShippingVersion, FreeShippingThreshold, ShippingFee, FirstCatchUpAt, AggregatesDerivedAt)`.
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
  - Mod On'da canlı bilgisayar kargo dosyasına yazmaz. `CorrectPrices` dosya bloğunu (`LabelService.cs:414-421`) atlar, dekontun kargo adımı komuttur (§5.1, §5.3).
  - CustomerId tetikleyicilerin dışında tutulur. Yerel yeniden anahtarlama (`CustomerSyncRepository.cs:477-499`) her durumda yazabilir.
- **Şema koruma testi:** Label, Shipment ve StreamSession'a yazan her SQL bir listede tutulur. Liste dışı bir yazım testte yakalanır (`CustomerSyncRepositoryTests.cs:650` deseni).

### 3.3 Değişmezler (DM)

- **DM1.** Bir yayının her hâlinin tek yazıcısı vardır (§2 tablosu).
- **DM2.** Damgalı birim yalnız komutla değişir.
- **DM3.** Devredilmiş yayının siparişi akıştan kaçmaz. `SessionReleasedAt` ya devir anında ya da eklemede dolar; onarım işi kalanları kapatır.
- **DM4.** Oluşum alanları eklemeden sonra değişmez.
- **DM5.** Bir yayında bir kodun kargoya verilmemiş, iptal edilmemiş, kargo ücreti olmayan etiketlerinin tek fiyatı vardır (#508 F1). Devredilmiş yayında bunu sunucu denetler.
- **DM6.** Kişi×kapsamda aynı anda en çok bir `Sending` kira olur. Mod On'da bir müşteri satırı×kapsamda yeni yazımla en çok bir geri alınmamış düşüm oluşur.
- **DM7.** `AmountDerivedAt` dolu ve Shipped olmayan dosyanın tutarı, bağlı, iptal edilmemiş ve geçici yedek olmayan siparişlerin fiyat toplamıdır.
- **DM8.** Akış öğesini uygulamak yerel gönderim kuyruğuna dokunmaz: `SyncedAt` ve `Revision` değişmez. F05'in karşılaştır-ve-yaz onayı (`LabelRepository.cs:386-391`) bozulmaz, yankı oluşmaz.

## 4. Sunucu

### 4.0 Ortak

- Bütün uçlar bugünkü desende `Bearer-Customer` ve lisans sahipliği denetimiyle çalışır (`LicensesSessionsSyncController.cs:68-71`).
- v2 isteği `ReplicaId` ve `Protocol = 2` taşır. Sunucu her v2 isteğinde `WpfReplicas.LastSeenAt`'i ilerletir; bu kalp atışıdır.
- İstemci sürümü `User-Agent`'tan okunur. Ayrıştırıcı `LicensesController.cs:126-135`'ten ortak bir `ClientVersion` yardımcısına taşınır; karşılaştırma `System.Version` ile yapılır. Ortak bakiye §7.2 aynı yardımcıyı kullanacak; bu PR önce çıktığı için yardımcıyı bu PR getirir.
- Protokolsüz istek (≤0.9.10) mod On'da `LastLegacySalesPushAt` yazar ve günlüğe uyarı düşer.
- İş kuralları ortak kodda durur (§4.11). Elle tutulan bir "sunucu ikizi" yazılmaz.

### 4.1 `POST sessions/sync` (`LicensesSessionsSyncController.cs:64-152`) — S1

v2 öğesinde ek alanlar: `Release` (bool) ve `MachineName`.
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

v2 ek alanları: `Format = 2`, ParentOrderId, PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice. ShipmentId de gönderilir ama yalnız mod Off'ta okunur.

**Ekleme dalı (`LicensesSessionsSyncController.cs:295-333`):** bugünkü gibi; ek olarak yeni alanlar ve `OriginReplicaId` yazılır.
- `SessionReleasedAt` oturumdan kopyalanır (DM3). Devirden sonra gelen ekleme hemen akışa girer. Bu durum force-end sonrasındaki gerçek satışları ve devredilmiş ebeveyne eklenen yedeği kapsar.
- v2'de başka kopyanın **canlı** oturumuna ekleme satır başına reddedilir: `rejected: session-owned-elsewhere`.

**Güncelleme dalı (`LicensesSessionsSyncController.cs:271-294`) — birim kuralı.** S1'den itibaren her biçimde ve iki kipte geçerli:

| # | Birim | Kural |
|---|---|---|
| K1 | Oluşum alanları | Güncellemede hiç yazılmaz. `LicensesSessionsSyncController.cs:275-277` (SessionId, MessageText, Code) ve `:285-286` (ProductId, ProductVariantId) kalkar; eski istemcinin ProductId'yi NULL'laması **mod beklenmeden** kapanır. ParentOrderId yalnız boşsa doldurulur. |
| K2 | Fiyat, iptal, yedek | İlgili `*SetAt` NULL ise yazılır; bu bugünkü son-gelen-kazanır davranışıdır. Doluysa yazılmaz ve yanıtta `ignored[{id, unit}]` döner. Yedek tek yönlüdür: IsTentativeBackup yalnız 1'den 0'a gider. |
| K3 | Baskı | Sunucuda PrintedAt NULL ise üç alan birlikte alınır. Doluysa yalnız boş PrintBatchId ve PrintSeq doldurulur. PrintedAt hiçbir yoldan silinmez (`048_label_print_batch.sql:8-9`: yeniden baskı dokunmaz). |
| K4 | Kargo bağı | Mod Off: `ShipmentSetAt` NULL ve sunucudaki değer boşsa push'tan doldurulur. Mod On: push'tan hiç alınmaz, birim sunucunundur. |
| K5 | Devredilmiş oturum, mod On | v2 ve v1 push'undaki K2 değişiklikleri reddedilir: v2'ye `rejected[{id, reason: session-released, current}]` döner, v1 için yalnız iz yazılır. K3 doldurma ve ekleme kabul edilir. |
| K6 | Kargoya verilmiş dosya, mod On | Bağlı olduğu dosya Shipped olan siparişin fiyat ve iptal değişikliği reddedilir: `rejected: shipment-shipped`. |
| K7 | Türetilmiş dosya | Kabul edilen fiyat, iptal ya da yedek değişikliği, aynı SaveChanges'te bağlı dosyanın tutarını yeniden türetir. İptal, Shipped olmayan dosyada bağı da çözer (§7.2). |

- `SyncVersion++` bugünkü gibi her işlenen satırda artar (`LicensesSessionsSyncController.cs:293`). Defter kilidi gerekçesi aynen geçerli (`Order.cs:70-91`). Canlı oturum siparişleri akışta olmadığı için yankı trafiği doğmaz.
- **Stok defteri (`LicensesSessionsSyncController.cs:340-367`)** ham paketten değil, birleştirilmiş varlık durumundan beslenir. Bugün paket alanları doğrudan deftere giriyor (`LicensesSessionsSyncController.cs:355-364`). Reddedilen ya da yok sayılan birim deftere bayat bir durum taşımaz.
- Bildirimler (`LicensesSessionsSyncController.cs:395-431`) değişmez; yalnız ekleme dalında tetiklenirler.
- **Yanıt:**
  - `Format = 2` için öğe başına `{id, version, shipmentId, sessionReleasedAt, status: applied|ignored|rejected, ignoredUnits[], reason, current?}`. `current`, akış öğesi biçimindeki sunucu satırıdır.
  - v1 için bugünkü `SyncedOrderDto` listesi (`LicensesSessionsSyncController.cs:433-445`).

### 4.3 `POST sales/backfill` — S1

Ws'nin ilk açılışında geçmişi tamamlar (§8.2). Gövde `{sessions[], orders[], shipments[{id, labelIds[]}]}`, 200'lük parti.
- **Yalnız boşu doldurur.** Sunucuda dolu bir alan ya da damgalı bir birim asla yazılmaz.
- **Oturum:** `OwnerReplicaId` boşsa çağıran yazılır. Sunucuda `EndedAt` NULL, yerelde doluysa yazılır. Devir yalnız öğe `Release = true` taşıyorsa uygulanır; istemci bunu §5.2'deki kuyruk boşluğu kuralıyla hesaplar. Kaçmış kapanışlar böyle kapanır; sunucuda sonsuza kadar açık kalan oturum sorunu budur (`PanelProductsController.cs:636-648`).
- **Sipariş:** satır yoksa eklenir; oturum denetimi aynıdır (`LicensesSessionsSyncController.cs:233-247`). Yeni alanlar (ParentOrderId, PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice, OriginReplicaId, ShipmentId) yalnız boşsa doldurulur.
- **Dosya bağları:** `shipments[].labelIds` ile, `Orders.ShipmentId` boşsa yazılır. Bugün her bağ yalnız etiketin kendi bilgisayarında kurulabildiği için çakışma beklenmez.
- **Bildirim yok:** push, WhatsApp `OrderReceived` etiket kuralı ve shopper push üretilmez (D1'in Replay deseni). Testle sabitlenir (§10.2).
- Stok defteri eklemede bugünkü gibi çalışır (CatalogAware).
- Moddan bağımsızdır ve idempotenttir.

### 4.4 `GET sales/changes?afterSeq&take≤500` — S1

Desen `LicensesWpfCustomersPullController.cs:204-270`.
- **Ufuk:** `MIN_ACTIVE_ROWVERSION()` sayfa başına bir kez okunur (`:217-219`). `afterSeq < 0 ∨ afterSeq ≥ ufuk` ise akış baştan verilir ve `cursorReset` döner (`:221`). Boş sayfada `next = afterSeq` (`:268`).
- **Kaynaklar.** Her biri `ChangeSeq ∈ (afterSeq, ufuk)`, ChangeSeq sırasıyla ve en çok `take` kayıt:
  - StreamSessions WHERE `ReleasedAt IS NOT NULL`
  - Orders WHERE `SessionReleasedAt IS NOT NULL`. SessionId NULL siparişler akışa girmez: yerelde SessionId NOT NULL FK; sayıları denetim raporunda görünür.
  - Shipments (yalnız mod On)

  Üç liste birleştirilip sıralanır, ilk `take` öğe döner; `next` son öğenin seq'idir.
- **Yan liste (imleci etkilemez):** sayfadaki siparişlerin oturum satırları (güncel hâliyle) ve müşteri taslakları `{Id (asıl kayıt), Platform, Username, DisplayName, PurgedAt}`.
  - rowversion yalnız satırın son hâlini gösterdiği için oturum, siparişlerinden sonra gelebilir. Yan liste FK sırasını (`002_pivot_to_labels.sql:25-26`) sayfa içinde sağlar.
  - Eksik müşteri öğeyi zehirlemez. 2026-08-14 dersi: tek satır bütün akışı tutmamalı.
- **Öğe:** sipariş satırının tamamı, `Version`, ShipmentId, yeni alanlar ve bilgi amaçlı birim damgaları.
- **KVKK:** asıl projeksiyonu PurgedAt dolu müşterinin siparişinde okuma anında `DisplayName = null`, `MessageText = ""` (§9).
- Mod Off'ta da çalışır; istemci bu modda uygulamaz.

### 4.5 `GET sales/state` ve `POST sales/hello` — S1

- **`GET sales/state`** her turda (30 sn) çağrılır ve kalp atışı sayılır. Yanıt:
  - `mode`, `modeChangedAt`, `minClientVersion`
  - `liveSessions: [{id, title, startedAt, endedAt, ownerMachineName, ownerLastSeenAt, state: live|handing-over}]` (`ReleasedAt` NULL olanlar)
  - `shipping: {version, freeShippingThreshold, fee}` (mod On)
  - `legacyMachines: [{machineName, appVersion, lastSeenAt}]`

  `ownerLastSeenAt`, sahip kopyanın `WpfReplicas.LastSeenAt` değeridir. StreamSessions satırına yazılmaz; böylece her kalp atışında oturumun rowversion'ı ve akış ilerlemez.
- **`POST sales/hello`:** `{replicaId, previousReplicaId?, machineName, hardwareFingerprint, protocol, reportedShipping: {threshold, fee}, backfillCompleted?}`.

### 4.6 `POST sales/commands` — S2

Zarf: `{commandId, replicaId, kind, payload, expected: [{orderId, version}]}`.

İşleme sırası:
1. Lisans sahipliği denetlenir. Mod Off ise 409 `mode-off`. İstemci sürümü `MinClientVersion`'dan eskiyse 409 `client-update-required`.
2. **Yeniden oynatma:** `SalesCommandLogs`'ta CommandId varsa ve RequestHash aynıysa, kayıtlı durum ve **güncel** satırlar döner. Hash farklıysa 409 `content-conflict` (A11 deseni).
3. **Görünürlük:** dokunulan her siparişin oturumu devredilmiş olmalı. Değilse 409 `session-live {machine, ownerLastSeenAt}`. `attach-shipment`'ta aday kümesi, çağıranın görebildiği siparişlerle sınırlanır: devredilmiş oturumlar ve çağıranın kendi oturumları.
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
| `cancel` | items, reason | zaten iptal olan atlanır | `CancelledAt` (sunucu saati), `CancelReason`, `CancelSetAt`; Shipped olmayan dosyada bağ çözülür (`ShipmentId = NULL`, `ShipmentSetAt`) ve dosya türetilir; defter | `LabelService.cs:125-162` |
| `uncancel` | items | iptal edilmemiş olan atlanır | iptal alanları NULL; dosyası Shipped ise bağ çözülür (§7.2); defter | `LabelService.cs:166-198` |
| `set-code-price` | sessionId, code (null ise selectedIds), alsoSelectedIds, newPrice ≥ 0, paidFromBalanceIds | Planı sunucu kendisi kurar: oturum×kod (`SameCode`) ∪ alsoSelected, ardından `Classify` (Shipped dosyadaki etiket değişmez). Plan kümesi `expected`'den farklıysa 409 `plan-stale` | Düzeltilebilir ∧ fiyatı farklı olanlarda `Price = new`. `PriceCorrectedAt = now` yalnız basılmış satırda. `PaidPrice = eski fiyat` yalnız NULL ∧ (ShipmentId dolu ∨ id ∈ paidFromBalanceIds). `PriceSetAt` basılır, dosya türetilir | `CorrectPrices` `LabelService.cs:376-426`, `UpdatePriceForCorrection` `LabelRepository.cs:226-244`, `PlanCodeCorrection` `LabelService.cs:311-335` |
| `confirm-backup` | backupOrderId, newPrice? | `IsTentativeBackup = 1` değilse etkisiz | fiyat (verildiyse), `IsTentativeBackup = 0`, `BackupSetAt` (fiyat değiştiyse `PriceSetAt` de); defter | `ConfirmBackup` `LabelService.cs:520-553` |
| `add-backup` | newOrderId, parentOrderId, müşteri {id, platform, username, displayName}, messageText, price?, code? | ebeveyn devredilmiş oturumda; müşteri asıl kayda çözülür | ebeveynin oturumuna IsTentativeBackup = 1, IsBackupPromoted = 1, ParentOrderId; `SessionReleasedAt` kopyalanır; ProductId ve varyant ebeveynden | `AddBackup` `LabelService.cs:453-481` |
| `mark-printed` | batchId, items [{orderId, seq}] | yalnız PrintedAt NULL ∧ iptal edilmemiş; ilk yazan kazanır | PrintedAt (sunucu saati), PrintBatchId, PrintSeq. Sonuç: `marked[]`, `alreadyPrinted[]`, `cancelled[]` | `MarkPrintedAndRecord` `LabelService.cs:230-274` (R7-04, `LabelService.cs:252`) |
| `reprint` | orderIds | yazma yok | güncel satırlar ve `correctionIds` | `PrepareReprint` `LabelService.cs:432-438` |
| `attach-shipment` | customerId, proposedShipmentId, expected (istemcinin gördüğü bağlanmamış küme) | `sp_getapplock('ship:{lisans}:{asıl müşteri}')`. Adaylar: asıl müşterinin ShipmentId NULL, iptal edilmemiş, geçici yedek olmayan ve görünür siparişleri. Aday kümesi ≠ `expected` ise 409 `stale` | En yeni Pending/Held dosya seçilir; yoksa `proposedShipmentId` ile açılır (`AmountDerivedAt = now`). Siparişler bağlanır (`ShipmentSetAt`), tutar türetilir. Yanıt havuzu da döner: kişinin bütün Pending/Held dosyalarının toplamı | `GetOrCreateOpenShipment` `ShipmentService.cs:50-65`, `AttachLabels` `:73-102`, `GetUnattachedByCustomer` `LabelRepository.cs:639-654` |
| `shipment-decision` | shipmentId, decision, expectedVersion | dosya Shipped ise 409 | `Decide` aynası. Hedef Pending/Held ise kişinin öbür açık dosyaları da aynı karara uyar (U12). `ShipmentStatusChanged` etiket kuralı tetiklenir (`LicensesShipmentsSyncController.cs:153-154` ile eşlik) | `ApplyDecision` `ShipmentService.cs:177-207` |
| `force-end` | sessionId, confirm = true | oturum devredilmemiş olmalı; sahibin `LastSeenAt`'i ≤ şimdi − 15 dk olmalı (`SalesSync:ForceEndSilenceMinutes`, Y7). Değilse 409 `owner-active {lastSeenAt}` | `EndedAt ??= now`, `EndedByReplicaId`, devir (§4.1) | — |

Ayrı bir "dosyadan çıkar" (detach) komutu yok. Bağ çözme, `cancel`, `uncancel` ve push'tan kabul edilen iptalin sunucu etkisidir (§7.2).

### 4.7 Ödeme uçları — S3

- `POST payment-requests/begin`, `POST payment-requests/{id}/finish`, `GET payment-requests?scopeKey&memberIds`. Ayrıntı §6.4'te.
- `customer-balance/apply` (`LicensesCustomerBalanceApplyController.cs:224`): mod On'a özgü kapsam kuralı (§6.6).
- `preview` (`:74`) ve `apply`: mod On'da istemci `MinClientVersion`'dan eskiyse 409 `client-update-required` (§6.5).

### 4.8 `GET/PUT shipping-settings` — S2

- PUT gövdesi `{expectedVersion, freeShippingThreshold, fee}`. Kabul edilirse `Version + 1`. Sürüm eskiyse 409 `stale`.
- Doğrulama `ShippingSettings.IsEnabled` kuralıyla aynı (`AppSettings.cs:272-273`): ya ikisi de boş (kapalı) ya ikisi de > 0.
- Mod Off'ta da yazılabilir; bu mod açılmadan önceki hazırlıktır. İstemciler değeri yalnız mod On'da okur (§6.2).

### 4.9 Hangfire işleri

- **`sales-release-repair`:** açılışta bir kez kuyruğa alınır, ayrıca günde bir koşar (04:50 UTC, yayın penceresi dışında). Emsal: `identity-key-repair` (`Program.cs:1018-1024`).
  - (a) `EndedAt` dolu ∧ `ReleasedAt` NULL ∧ `OwnerReplicaId IS NULL` olan miras oturumlar devredilir.
  - (b) Oturumu devredilmiş ama `SessionReleasedAt`'i NULL kalmış siparişler (eski imajın geri alma penceresinde eklediği satırlar) 5 bin'lik partilerle damgalanır.
  - (c) Mod On'da: `AmountDerivedAt` dolu bir dosyanın tutarı türetilmiş değerden farklıysa yeniden türetilir ve günlüğe yazılır.
  - İdempotent. Sayılar günlüğe yazılır; ad yazılmaz.
  - v2 oturumlarını bu iş **devretmez**. Devrediliyor hâlindeki bir v2 oturumunu devretmek "etiket kuyruğu boşalmadan devir yok" kuralını bozardı. Geri alma penceresinde kaybolan v2 kapanışını istemci kendisi yeniden gönderir (§5.2).
- **`sales-command-log-retention`:** günde bir; 90 günden eski kayıtları siler.
- Kira süresi için ayrı iş yok. `begin`, süresi dolmuş `Sending` kaydını aynı işlemde `Unconfirmed` yapar (§6.4).

### 4.10 Mod yönetimi ve denetim CLI'ları — S3

- `sales-sync status|enable|disable --license <id> [--apply]` ve `sales-sync force-end --session <id> [--apply]`. Desen `Program.cs:38-55`; varsayılan kuru koşudur. `force-end` CLI'ı, sahip kopyası hiç kalmamış eski açık oturumlar içindir ve Burak'ın onayıyla koşulur.
- `sales-audit --license <id>`: salt okunur rapor. İçeriği:
  - 24 saatten eski canlı ya da devrediliyor oturumlar
  - SessionId NULL siparişler
  - projeksiyonu olmayan CustomerId'ler
  - `SessionReleasedAt`'i eksik siparişler
  - kopyaların bildirdiği farklı kargo ayarları
  - tek fiyat ihlalleri (DM5)
  - kapsam başına birden çok geri alınmamış düşüm
  - aynı ReplicaId'ye iki parmak izi

  Yalnız sayı ve Id basar.

**`enable` ön koşulları.** Kuru koşu her birini sayı olarak basar; biri tutmazsa `--apply` reddeder.
1. Son 14 günde görülmüş ve devre dışı bırakılmamış her etkinleştirmenin `AppVersion`'ı ≥ `SalesSync:MinClientVersion` (`Activation.cs:16-20`). AppVersion yoksa eski sayılır.
2. Son 14 günde görülmüş her `WpfReplicas` satırında `Protocol = 2` ve `BackfillCompletedAt` dolu. Aynı ReplicaId'ye bağlı iki farklı donanım parmak izi yok.
3. Son 7 günde `LastLegacySalesPushAt` yok.
4. `LicenseShippingSettings` satırı var; değeri Burak seçmiş (§6.2).
5. `sales-audit`'te devredilmemiş ve 24 saatten eski oturum 0; `SessionReleasedAt`'i eksik sipariş 0.
6. Rapor (koşul değil, bilgi): Shipped olmayan dosyalardan türetilmiş tutarı saklanandan farklı olanların sayısı, toplam farkı ve eşiği aşan ya da altına inen dosya sayısı. Sahip kararı Y5'in açılış günündeki etkisi budur (§7.4).

**`enable --apply`:** `SalesSyncMode = 1`, `SalesSyncModeChangedAt` yazılır. Ardından Shipped olmayan dosyalar partilerle türetilir (`AmountDerivedAt = now`). Verilmiş kararlar değiştirilmez.

**`disable`:** acil kapatma anahtarıdır (§8.5). Damgalar komutla değişmiş birimleri korumaya devam eder.

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
- (a) `CorrectPrices`, kargo dosyası bloğunu (`LabelService.cs:414-421`) atlar; dosya tutarını sunucu türetir (K7). Bu yolu D3'ün Shipment ABORT tetikleyicisi kırıyordu. `Classify` yerel aynadaki Shipped durumunu okumaya devam eder; sunucu son sözü K6 ile söyler.
- (b) Önceki yayınlardan kalan kuyruk etiketleri yönlendiriciden geçer (§5.3, §5.5).
- (c) Baskıdan hemen önce satırlar veritabanından yeniden okunur (§5.6).

### 5.2 Gönderim ve devir (`SessionOrderSyncService`)

- **v2 gövde:** ReplicaId, Protocol ve Format = 2 alanları. `CatalogAware: true` (`SessionOrderSyncService.cs:148`) aynen kalır.
- **Gönderim kapısı.** `SyncOnceAsync` dört yerden eşzamanlı çağrılabiliyor: 60 sn servisi (`SessionOrderSyncHostedService.cs:20`), kapanış (`SyncFlushService.cs:90-94`), satış turu ve ödeme ön gönderimi. Bu yüzden tek bir `SemaphoreSlim` ile sıraya alınır.
- **Kapanış sırası.** Bugün önce oturumlar, sonra etiketler gidiyor (`SessionOrderSyncService.cs:68-70`). Bitmiş ve `Authority = 'local'` olan kendi oturumu için:
  - `Release = NOT EXISTS (Label WHERE SessionId = @id AND SyncedAt IS NULL)`, devir kapısı altında hesaplanır.
  - Oturum satırı, devir sunucudan görülene kadar `MarkSynced` **edilmez**. Kuyrukta kalır ve her turda yeniden gider. Böylece geri alma penceresinde kapanış kaybolmaz (hakem 2'nin D2 karşı örneği 2).
  - Etiketler gittikten sonra oturum aynı turda bir kez daha gönderilir.
  - Bekletilen (`Release = false`) oturumun yeniden gönderimi "gönderilen kayıt" sayılmaz. Kapanıştaki boşaltma döngüsü (`SyncFlushService.cs:90-94`, tur bir şey gönderdikçe yineliyor) bu yüzden boşuna dönmez.
  - Yanıtta `ReleasedAt` gelirse tek bir SyncApplyScope işleminde `Authority = 'server'`, `ReleasedAt` ve `MarkSynced` yazılır.
  - Yanıtta `Protocol = 2` yoksa sunucu eski sürümdür; bugünkü gibi `MarkSynced` yapılır.
- **Devir kapısı.** Süreç içi ve oturum başına bir kilit. Tur, "kuyruk boş mu" okumasından kapanış yanıtının uygulanmasına kadar kilidi tutar. Yönlendiricinin yerel yolu, kendi bitmiş ama devredilmemiş oturumuna yazmadan önce kilidi 0 bekleme ile almaya çalışır. Alamazsa operatöre "Yayın sunucuya devrediliyor; birkaç saniye sonra tekrar dene." der. Kilit olmasaydı "boş" okuması ile devir arasında yapılan yerel düzenleme sunucuda K5 ile reddedilirdi.
- **Yanıtın işlenmesi.** Bugün yanıt atılıyor (`SessionOrderSyncService.cs:148`).
  - `applied`: `MarkSynced(id, now, revision)` karşılaştır-ve-yaz (F05, `LabelRepository.cs:386-391`) ve `ServerVersion`. Mod On'da sunucudaki ShipmentId farklıysa guard altında yazılır.
  - `ignored`: kayıt `SalesConflictLog`'a düşer, `MarkSynced` yapılır. Gerçek değer akışla gelir.
  - `rejected`: `current` guard altında zorla yazılır, toplamlar yeniden türetilir, `MarkSynced` yapılır. Operatöre liste gösterilir: "N değişiklik uygulanamadı: yayın PC-B'den bitirildi" ya da "…: kargoya verilmiş dosya".
- **Geri doldurma** (§8.2) kendi imleciyle yürür ve `SyncedAt`/`Revision`'a dokunmaz.

### 5.3 Düzenleme yönlendiricisi (`ILabelEditor`)

Karar tek yerde verilir: hedef etiketin oturumunun `Authority`'si `local` ise bugünkü `LabelService`/`ShipmentService`, `server` ise `SalesCommandClient` kullanılır. Karışık seçim (müşteri kartı, kalıntı kuyruk) bölünür. Mod On'da kargo işlemleri her zaman komuttur, çünkü dosya sunucunundur.

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
- `mode-off` ve 404: düzenleme kapalı, "Sunucu güncelleniyor; biraz sonra tekrar dene."

### 5.4 Akış uygulaması (`SalesChangesPullService` + `SalesFeedRepository.ApplyItem`)

**Servis**
- `AppHost`'ta kaydedilir (`AppHost.cs:523`, `:535`, `:561` yanına) ve başlatma döngüsüne de eklenir (`OrderDeck.App/Startup/WpfStartupEnvironment.cs:189-200`). CLAUDE.md kuralı: kayıt tek başına servisi başlatmaz.
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
  - Yerelde varsa (kendi oturumu): `EndedAt` boşsa yazılır (force-end durumu); `Authority = 'server'` ve `ReleasedAt` yazılır. Aktif oturumsa `SessionEndedRemotely` olayı çıkar.
  - `RemoteLiveSession` satırı silinir.
- **Sipariş:**
  - Önce yan listedeki oturum ve müşteri sağlanır.
  - Müşteri yerelde yoksa ve (Platform, Username) ile başka Id'li bir eşleşme varsa öğe "bekliyor" olur: yönlendirmeyi müşteri akışı getirecektir, deneme sayılmaz. Eşleşme yoksa müşteri akışının sunucudan ekleme yolu taslakla kullanılır (`CustomerSyncRepository.cs:535-574`): 0/0 toplam, mezar taşı boşaltması (`:570`). Kalıcı park yok; etiket inişi silinmiş bir kimliği diriltmez.
  - Yerelde satır var ve `SyncedAt IS NULL` ise uygulama **durur** (U5): gönderilmemiş yerel değişiklik var. Tur önce gönderir; yine bekliyorsa öğe sonraki tura kalır, deneme sayılmaz.
  - Değilse bütün birimler, ShipmentId ve `ServerVersion` yazılır. Oluşum alanlarına ve CustomerId'ye dokunulmaz; bunlar yalnız eklemede, `CustomerIdSql.Resolve` ile yazılır.
  - Yeni satırda `SyncedAt = now`. `StockSyncedAt` boşsa `SyncedAt`'in değeriyle doldurulur. NULL kalırsa aynı etiket stoktan ikinci kez düşer (`LabelRepository.cs:699-717`).
  - Etkilenen müşterilerin (eski ve yeni CustomerId) toplamları aynı işlemde yeniden türetilir (§6.1).
- **Dosya:** mod On'da yerel aynaya upsert edilir.
- **KVKK:** yerel müşteri silinmişse DisplayName ve MessageText boşaltılmış olarak yazılır.

### 5.5 Kalıntı kuyruk ve bekleyen komutlar

- `GetUnprintedOutsideSession` (`LabelRepository.cs:96-107`) yalnız `Origin = 'local'` oturumlara bakar. Başka bilgisayarın basılmamış kuyruğu bu bilgisayara teklif edilmez (`MainShellViewModel.cs:1631-1639`).
- Bu bilgisayarın devredilmiş oturumlarından kalan etiketler kuyruğa eklenebilir. Bu etiketlerin iptali ve ilk baskısı komutla yapılır. Hakem 1'in D2 ölümcül bulgusu tam bu yoldu: yerel iptal sunucudan reddedilip geri dönüyordu.
- **Çevrimdışı:** iki işlem `PendingSalesCommand`'a yazılır (sabit CommandId, idempotent). Örtü kuralı: bekleyen iptal ya da basım örtüsündeki etiketi `GetUnprintedBySession` (`LabelRepository.cs:71-87`) ve `GetUnprintedOutsideSession` dışarıda bırakır. Aynı kâğıt ikinci kez teklif edilmez; D3'teki bekleyen baskı açığı (çift kâğıt) böyle kapanır. Müşteri toplamı onaydan sonra değişir.
- **Gönderim:** her turda, akıştan önce. Sonuç akış öğesi gibi uygulanır ve örtü kalkar.
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
| Başka bilgisayarda devrediliyor | çip | "PC-A'da yayın bitti, etiketler gönderiliyor" |
| Sahip ≥ 15 dk sessiz | çip ve "Yayını bitir…" eylemi | Onay: "PC-A 17 dakikadır sessiz. Yayın gerçekten bitti mi? Bitirirsen PC-A'nın bundan sonraki fiyat ve iptal değişiklikleri uygulanmaz; yeni etiketleri yine kaydedilir." |
| Kendi yayını uzaktan bitirildi | MainShell bandı | "Bu yayın PC-B'den bitirildi (SS:DD). Kuyruktaki etiketler basılabilir; yeni etiket için yeni yayın başlat." |
| Kendi yayını devrediliyor | durum satırı | "Yayın sunucuya devrediliyor…" |
| Devredilmiş yayın, çevrimdışı | düğmeler pasif, araç ipucu | "Bu değişiklik için sunucu bağlantısı gerekli" |
| Komut ucu yok (404) | araç ipucu | "Sunucu güncelleniyor; biraz sonra tekrar dene" |
| 409 stale | satır içi | "Bu arada PC-X'te değişti; plan yenilendi." |
| Reddedilen push | uyarı listesi | §5.2 |
| Akış | durum satırı | "Satış: güncel ✓ (SS:DD)", "yetişiyor", "N öğe bekliyor", "M öğe atlandı", "Yayın X devredilemedi: N etiket gönderilemiyor". Dikkat sayacına (`SyncOutboxRepository.cs:44-50`) SalesFeedFailure ve devredilemeyen oturum eklenir. |
| Mod Off | — | bugünkü gibi |

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
| Ödeme isteği | GUID'li sahibi olan kişide engellenir; bugün de önizleme yüzünden engelleniyor (`PaymentRequestService.cs:1062-1066`). GUID'siz kişide bugünkü gibi yerel |
| Yeniden baskı | önbellekten, notla |

### 5.9 `GetActive` ve okuyucular

- `SessionRepository.GetActive` (`SessionRepository.cs:67-74`) koşuluna `AND Authority = 'local'` eklenir. Bu bir savunma katmanı: uzak oturum hiçbir zaman `EndedAt` NULL olarak inmez, `RemoteLiveSession` de ayrı tablo.
- `GetAllEnded` ve `GetLatestEnded` (`SessionRepository.cs:86-109`) uzak oturumları kendiliğinden görür. Dekont eşleştirmesindeki "son yayın" (`DekontEkleViewModel.cs:315`) artık bütün bilgisayarların en son devredilmiş yayınıdır.
- Raporlar (`GetSessionTotals` `LabelRepository.cs:393-417`, `GetPeriodAccountRows` `:567-605`) bütün bilgisayarların satışlarını içerir. Fatura ve e-Arşiv dökümü tek bilgisayardan alınır (Y6, §11).

### 5.10 Toplamlar (WPF tarafı)

- Müşteri toplamları W1 ile etiketlerden türetilir (`CustomerAggregateSql`); yerel olay, akış uygulaması ve yeniden anahtarlama aynı fonksiyonu çağırır (§6.1).
- Ödeme tutarı W3 ile sayaçtan değil etiketlerden okunur (`GetPrintedTotal`) ve sunucu toplamıyla doğrulanır (§6.3, §6.4).
- Kargo eşiği ve ücreti mod On'da lisans geneli ayardan okunur (§6.2).

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
- Okuyucular:
  - `ComputeShipping` (`PaymentRequestService.cs:1640-1663`)
  - `EvaluateAfterPayment` (`ShipmentService.cs:132`)
  - `PaymentMatcherService.Match` (`PaymentMatcherService.cs:59`)
  - dekont ekranındaki eşik gösterimi (`DekontEkleViewModel.cs:282`)
  - ayar ekranı (`SettingsViewModel.cs:456-457`, `:589-590`). Mod On'da kaydet PUT yapar ve bağlantı ister.
- `begin` isteği `shippingSettingsVersion` taşır. Sürüm eskiyse 409 `settings-stale` döner; istemci önbelleği yeniler, tutarı yeniden hesaplar ve bir kez daha dener. Aynı kişiye iki bilgisayarın farklı kargo tutarı istemesi böyle kapanır.
- **Geçiş:** Ws, `hello`'da kendi yerel değerini bildirir. `sales-sync status` farklı değerleri gösterir. Burak tek değeri seçer (WPF ayar ekranından PUT). Bu, modu açmanın koşuludur (§4.10-4).

### 6.3 Tutarın kaynağı

| Kapsam | Ekrandaki toplam | Ödeme isteğinde |
|---|---|---|
| `session:{kendi canlı yayını}` | yerel | yerel (otorite canlı bilgisayar); ön gönderimden sonra sunucu doğrular |
| `session:{devredilmiş}` | önbellek | önbellek; sunucu doğrular |
| `cumulative` | önbellek (devredilmiş yayınlar + kendi yayınları) | önbellek; sunucu doğrular. Başka bilgisayarda canlı ya da devredilmemiş satış varsa istek engellenir |

Kapsam anahtarları değişmez: `session:{id}` ve `cumulative` (`CustomerSearchViewModel.cs:291-292`, `StreamReportViewModel.cs:254-255`, `CorrectedPaymentRequests.cs:130-132`).

### 6.4 PersonKey kirası (S3 + W3)

Kira, `RequestPaymentAsync` (`PaymentRequestService.cs:286-363`) içinde şu noktaya girer: kişi kiralandıktan (`EnterPersonAsync`, `:314`) ve tutar okunduktan (`:336`) sonra, bakiye adımından (`:501-517`) önce.

**İstemci tarafı**
1. **Ön gönderim:** müşteri, oturum ve etiket kuyrukları, en çok 5 sn. Başarısız olursa ve kişinin GUID'li sahibi varsa sonuç `BalanceUncertain`; mesaj gitmez.
2. Tutar yeniden okunur; ön gönderim sırasında basılmış etiketler de girer.
3. `POST payment-requests/begin` gönderilir: `{recordId, replicaId, ownerId, memberIds, scopeKey, productTotal, totalBeforeBalance, shippingSettingsVersion, confirmedPriorIds}`. `memberIds` GUID'li üyelerdir, süzgeç `PaymentRequestService.cs:1121`'deki gibi.

**Sunucu tarafı.** Tek işlem içinde, `sp_getapplock('payreq:{lisans}:{personKey}:{kapsam}')` ile. Ayrıca her asıl üye için `payreq-m:{lisans}:{üye}:{kapsam}` kilidi sıralı alınır (sıra ölü kilitlenmeyi önler). Böylece iki bilgisayar grup senkronu gecikmesi yüzünden farklı PersonKey hesaplasa bile, ortak üyeleri varsa aynı anda geçemez.
- a. PersonKey hesaplanır ve üyeler asıl kayda çözülür.
- b. Süresi dolmuş `Sending` kayıtları `Unconfirmed` yapılır.
- c. Başka kopyanın `Sending` kaydı varsa (PersonKey'den ya da üye kümesi kesişiminden, aynı kapsamda) 409 `in-progress-elsewhere {machine, since}`.
- d. `confirmedPriorIds` dışında bir `Delivered` ya da `Unconfirmed` kayıt varsa 409 `already-requested {priors: [{id, machine, netAmount, at, unconfirmed}]}`.
- e. **Sunucu toplamı:** üyelerin siparişlerinin `Contribution` toplamı; `session:{id}` için o yayın, `cumulative` için bütün yayınlar. İstemcininkinden farklıysa 409 `stale-total {serverTotal, liveElsewhere}`. `liveElsewhere`, farkı başka kopyanın devredilmemiş yayınındaki siparişler açıklıyorsa doludur.
- f. Kargo ayarı sürümü eskiyse 409 `settings-stale`.
- g. `Sending` kaydı ve üyeleri yazılır; `LeaseUntil = now + 120 sn`. Eşzamanlı ikinci kaydı filtreli tekil indeks reddeder.

**409'ların istemcideki karşılığı**
- `in-progress-elsewhere` → mevcut `PaymentInProgress` sonucu (`PaymentRequestService.cs:61`), metin "PC-A'da bu kişiye ödeme isteniyor".
- `already-requested` → mevcut `AlreadyRequested` sorusu (sahip kararı 2, #506), makine adıyla. Onay belirteci uzak kayıt Id'lerini de kapsar.
- `stale-total` → bir akış turu ve bir kez yeniden deneme. Fark sürerse istek engellenir: "Bu bilgisayar güncel değil (sunucu X ₺, burada Y ₺)". `liveElsewhere` doluysa metin: "Bu kişinin PC-A'da süren yayında etiketi var; istek yayın bitip devredilince yapılabilir."
- `settings-stale` → önbellek yenilenir, bir kez yeniden denenir.

**Akışın sonu.** Her sonuçta `finish` çağrılır: `{delivered | unconfirmed | abandoned, netAmount, appliedAmount, fullyFromBalance}`. Bağlandığı yerler bugünkü `TryBeginSend`, `MarkDelivered` ve `AbandonSend` noktaları (`PaymentRequestService.cs:541`, `:553-562`). `finish` gönderilemezse yerelde bekletilir ve yeniden denenir.

**Teslim kaydı olmadan dolan kira.** Kayıt "gitmiş olabilir" anlamında `Unconfirmed` olur ve öbür bilgisayarın sorusunda görünür (hakem 2 aşısı). Bunun yerel karşılığı "SendAttempts > Deliveries" mantığıdır.

**Teslim kayıtlarının okunması.** `GET payment-requests?scopeKey&memberIds` yanıtı yerel işlerle birleştirilir. Okuyanlar: `CorrectedPaymentRequests.Badges` (`CorrectedPaymentRequests.cs:303`) ve `BalancePaidLabels` (`:274`) (Delivered ∧ FullyFromBalance), "zaten istendi" sorusu ve #508'in "ödendi, fark" planı (`:168`). Cloud API'nin idempotency anahtarı her çağrıda yeni (`PaymentRequestService.cs:1572-1583`) ve wa.me yolunda sunucu hiç yok. Bu yüzden sıralı ikinci isteği yalnız bu soru durdurabilir; artık bilgisayarlar arasında da durdurur.

**Gecikme.** Canlı yayın sırasındaki ödeme isteğine bir sunucu çağrısı (`begin`) ve çoğu zaman boş geçen bir ön gönderim eklenir. Bugün de aynı akışta `/scope`, önizleme ve apply çağrıları var.

### 6.5 Kümülatif çift istek penceresi (hakem 1, D3 ölümcül bulgusu)

D3'te R1 sürümü uzak etiketleri toplama katıyordu ama kirayı R2'ye bırakıyordu. Bu yüzden iki bilgisayar aynı kişiden kümülatif **tam** tutarı isteyebilirdi. Bu tasarımda bu pencere yok:
1. Akış uygulaması, uzak etiketler, kira ve `stale-total` aynı mod kapısıyla, birlikte açılır. Mod Off'ta Ws'nin yerel tablosunda uzak etiket yoktur; kümülatif toplam bugünkü gibi yalnız bu bilgisayarın etiketlerinden çıkar.
2. Mod On'da kira zorunludur ve **fail-closed** çalışır: `begin`'e ulaşılamazsa GUID'li kişide mesaj gitmez. S3 imajı otomatik geri alınıp kira ucu 404 dönse de durum aynıdır. Akıştan inen her müşterinin GUID'i var; bu yüzden uzak etiketi olan hiçbir kişi kirayı atlayamaz.
3. Mod On'da eski sürümün `preview` ve `apply` çağrıları 409 `client-update-required` alır (§4.7). GUID'li kişide eski bilgisayarın mesajı engellenir (`PaymentRequestService.cs:1062-1066`).

### 6.6 Kapsam başına tek geçerli düşüm (S3)

- **Koşul:** `apply` (`LicensesCustomerBalanceApplyController.cs:224`) çağrısında mod On ∧ SaleScope dolu ∧ `legacy:` ile başlamıyor ∧ anahtar yeni.
- **Kural:** `sp_getapplock('bal:{lisans}:{asıl kayıt}:{kapsam}')` alınır; sonra `/scope` ucuyla aynı yüklem çalıştırılır (`:143-147`: aynı asıl kayıt, aynı kapsam, `purchase-deduction`, geri alınmamış). Böyle bir düşüm varsa 409 `scope-has-deduction {transactionId, appliedAmount, productTotal}`.
- **İstemci:** düşümü `AdoptRemoteResult` (`PaymentRequestService.cs:1196`) ile benimser ve revizyon adımına döner.
- **Kapanan yol:** bayat bir B revizyonundan KA ile KB2 iki ayrı düşüm olarak kalıyordu. Bugün `/scope` yalnız `Created` ve anahtarsız işte soruluyor (`:938`) ve `(LicenseId, WpfCustomerId, SaleScope)` indeksi tekil değil (`LicenseDbContext.cs:887-890`).
- **Ayrı tablo yok.** D3'ün `ActiveSaleScopes` tablosu, sunucu imajı geri alınınca bayat aktiflik satırı bırakıyordu (hakem 2 ölümcül bulgusu). Bu kural defterin kendisini okur: geri alma penceresinde yazılan düşümler sonradan yine görünür, bayat durum kalmaz.
- **Satır (çapa) düzeyinde kalır.** Farklı üyelerdeki izler #506'nın birleştirme sorusuna düşer (`ReconcilePersonScopeAsync`, `PaymentRequestService.cs:1117`). Ortak bakiye M10 ile uyumludur. Ortak bakiye ekibi aynı kişinin başka satırındaki çapayı da 409 yapmayı #506'nın meşru çok izli akışını bozduğu için reddetmişti (ortak bakiye §13 sonuç notu).
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

### 7.2 Sahip kararı Y5: bağlandıktan sonra iptal edilen etiket

Tutar türetilir (DM7). Bağ şöyle değişir:

| Olay | Dosya durumu | Etki |
|---|---|---|
| İptal (komut ya da kabul edilen push) | Pending, Held, RecipientPays | Bağ çözülür (`ShipmentId = NULL`, `ShipmentSetAt`); tutar ve havuz düşer |
| İptal | Shipped | Bağ kalır, tutar donar (karar verilmiş; iade ayrı iş) |
| İptali geri alma | — (bağ çözülmüştü) | Etiket bağlanmamış durumdadır; sonraki dekont ya da bağlama onu alır |
| İptali geri alma | Shipped (bağ kalmıştı) | Bağ çözülür; etiket bağlanmamış olur, yeni dosyaya girer |
| `set-code-price` | açık | Tutar yeniden türetilir; bugünkü `CorrectPrices` farkının (`LabelService.cs:414-421`) karşılığı |
| `confirm-backup` | — | Geçici yedek hiç bağlanmamıştı (`GetUnattachedByCustomer` süzgeci, `LabelRepository.cs:639-654`); değişiklik yok |

**Sayısal örnek** (tek satırlı müşteri `ornek_alici`, eşik 500 ₺, kargo ücreti 60 ₺)
1. S1'de K1 300 ₺ basıldı; 360 ₺ istendi; 360 ₺'lik dekont geldi. `attach-shipment` ile F1 = 300. Havuz 300 < 500; operatör "Beklet" der, F1 Held olur.
2. S2'de K5 250 ₺ basıldı; dekont geldi. `attach-shipment` ile F1 = 550 ≥ 500. Çekmece "ücretsiz kargo" teklif eder; operatör kararı sonraya bırakır.
3. Müşteri K5'i istemez ve PC-B K5'i iptal eder.
   - **Bugün:** F1 550'de kalır. Kargo yanlışlıkla ücretsiz görünür, panelde de 550 yazar.
   - **Yeni:** K5'in bağı çözülür ve F1 = 300 olur. Havuz 300, eşiğe 200 ₺ kalır. Eşiğin altına inildiği için "ücretsiz kargo kazandın" mesajı (`PaymentRequestService.cs:1457`, tutarı havuzdan okur) artık teklif edilmez.
4. F1 iptalden önce Shipped olsaydı F1 550'de donar ve K5 bağlı kalırdı. K5 sonradan geri alınırsa bağı çözülür ve bir sonraki dosyaya girer.

### 7.3 Karar

`shipment-decision`, `ApplyDecision` (`ShipmentService.cs:177-207`) mantığıyla çalışır. Hedef Pending ya da Held ise kişinin öbür açık dosyaları da aynı karara uyar (U12). `ShipmentStatusChanged` etiket kuralı sunucuda tetiklenir.

### 7.4 Geçiş

- Mod Off'ta bugünkü yerel davranış sürer: iptal tutardan düşmez. Gerekçe: o dönemde dosyanın yazıcısı yerel bilgisayar, sahada eski sürümler var. Kuralı yalnız bir yolda uygulamak iki yazıcı arasında farklı tutar üretirdi. Kural mod On ile birlikte, tek yazıcıda devreye girer.
- Mod açılırken Shipped olmayan dosyalar türetilir. Fark raporu `enable` kuru koşusunda gösterilir (§4.10-6). Verilmiş kararlar değiştirilmez.

## 8. Geçiş

### 8.1 Sıra

| Adım | Ne | Koşul ve doğrulama |
|---|---|---|
| 0 | Spec onayı. Burak prod'da Orders, StreamSessions ve Shipments satır sayısını salt okunur sorguyla alır; `rowversion` ekleme süresi `restore-verify` kopyasında ölçülür. | Prod'a yazım yok. Sınıra yakınsa göç bölünür (§3.1). |
| 1 | **S1** merge ve otomatik deploy | Yayın penceresi dışında (Paz/Pzt/Çar/Per 20:00–01:00 TR değil). EndedAt tekdüze, K1 (ProductId düzeltmesi), K2 (damga yok, son-gelen-kazanır), K3, v1 kapanışında devir. Onarım işi eski bitmiş oturumları devreder. Doğrulama gerçek etkiden: dolu `Orders.SessionReleasedAt` sayısı, `sales/changes` ilk sayfası. |
| 2 | **S2** merge ve deploy | Komutlar mod Off'ta 409 `mode-off`. Davranış değişmez. |
| 3 | **S3** merge ve deploy | Kira ucu ve kapsam kuralı yalnız mod On'da etkili. CLI'lar gelir. |
| 4 | **W1 → W2 → W3** sırayla master'a; Ws Velopack sürümü; PC'ler sırayla güncellenir | Her PC'nin ilk açılışı §8.2. Mod Off'ta davranış bugünküyle aynı (§8.5). |
| 5 | `sales-sync status` (kuru); Burak lisans geneli kargo ayarını seçer | Kopyaların bildirdiği değerler yan yana gösterilir. |
| 6 | 7 gün boyunca eski sürüm izi yok → `sales-sync enable --license <id>` kuru → rapor sahibe → `--apply` | Yayın penceresi dışında, Burak'ın onayıyla. |
| 7 | Her PC'nin mod On'daki ilk turu (§8.4) | Durum satırı "yetişiyor", ardından "güncel ✓". |
| 8 | Saha kontrolleri (§10.4) | Gerçek etkiden. |
| 9 | **S4** daraltma | Mod On'dan ≥ 2 hafta sonra. |

### 8.2 Ws'nin ilk açılışı (mod Off)

1. Göç 049 koşar ve ReplicaId üretilir. `hello` gönderilir; yerel kargo ayarı bildirilir.
2. Normal giden kuyruklar boşaltılır.
3. Geri doldurma `sales-backfill-out` imleciyle (rowid) yapılır: önce bütün yerel oturumlar, sonra etiketler, sonra dosya bağları; `sales/backfill`'e, 200'lük partilerle. `SyncedAt` ve `Revision`'a dokunulmaz (F05). Sunucu yalnız boşu doldurur ve bildirim üretmez (§4.3). Kaçmış kapanışlar bu adımda kapanır.
4. `hello {backfillCompleted: true}` gönderilir.
5. Müşteri toplamları yerel etiketlerden bir kez türetilir; değişen satır sayısı günlüğe yazılır.

Geri doldurma idempotenttir. Yarıda kesilmesi, yeniden denenmesi ya da `cursorReset` zararsızdır.

### 8.3 Mod kapısı (mühendislik kararı M1)

Koşullar §4.10'da. Kapı neden dinamik değil? Dinamik kapı her istekte "bütün PC'ler Ws+ mı" diye bakardı. Eski bir PC'nin tek bir kalp atışı lisansı yayın ortasında Off'a düşürürdü. O zaman devredilmiş yayınlar düzenlenemez olur, kargo yerel otoriteye geri döner ve kira kalkar; bunun güvenli bir dönüş tarafı yok. Ortak bakiye §7.2 de aynı gerekçeyle statik kapı seçti. Eski PC riskini anahtar değil, çalışma anı korumaları kapatır:

| Mod On'da eski sürüm (≤0.9.10) | Sonuç |
|---|---|
| Yeni etiket ekleme ve baskı doldurma | Kabul edilir (gerçek satış). |
| Devredilmiş yayında fiyat, iptal ya da yedek değişikliği | Yok sayılır; `LastLegacySalesPushAt` yazılır, uyarı düşer (K5). Damgalı birimler her durumda korunur (K2). |
| `shipments/sync` | Yok sayılır, iz yazılır. |
| Önizleme ve apply | 409 `client-update-required`. GUID'li kişide mesaj engellenir. |
| Başkalarının değişikliklerini görmek | Göremez. Güncellenene kadar bayat görünüm; para yolları yukarıdaki satırlarla korunur. |

### 8.4 Mod On'daki ilk tur (her PC)

1. Bütün giden kuyruklar boşaltılır: müşteri, oturum ve etiket, kargo, dekont.
2. `sales/state` okunur, kargo ayarı önbelleğe alınır.
3. Akış 0'dan çekilir. Kendi bitmiş oturumları devredilmiş olarak gelir ve `Authority = 'server'` olur. Uzak yayınlar eklenir. Yerel `Shipment` tablosu sunucu satırlarıyla değiştirilir (guard altında).
4. Ufka ulaşılınca (boş sayfa ve bekleyen öğe yok) toplamlar tek işlemde türetilir; `FirstCatchUpAt` yazılır.

O anda canlı bir yayın varsa yerel kalır ve normal yoldan devreder.

### 8.5 Karışık sürüm ve kip geçişleri

| Durum | Davranış |
|---|---|
| Mod Off, Ws ile eski sürüm yan yana | Bütün bilgisayarlar bugünkü gibi. Ws ek olarak yeni alanları gönderir, kapanışı kuyruk boşalınca devreder, AttachLabel'dan sonra etiketi kuyruğa alır (tetikleyici), toplamları türetir. Sunucu: K1, K3, tekdüze EndedAt; damga yok. |
| Mod On, bütün PC'ler Ws | Tam tasarım. |
| Mod On, eski PC döner | §8.3 tablosu. Admin'e uyarı. Mod kendiliğinden kapanmaz. |
| On → Off (`disable`) | İlk turda her PC sırayla: (1) bekleyen komutları göndermeyi dener, gitmeyenleri iptal edip operatöre listeler; (2) akışı son kez boş sayfaya kadar uygular, sunucunun son hâlini alır; (3) kendi oturumlarını `Authority = 'local'` yapar, uzak oturumlar salt okunur önbellek olarak kalır; (4) yerel kargo tablosu yerel otoriteye döner (tetikleyicide `Mode = 0`); (5) bundan sonra bugünkü davranış. Damgalı birime gelen push etkisizdir; yanıt `ignored` olur, kayıt ve uyarı düşer. |
| Off → On (yeniden) | §8.4 baştan. |

### 8.6 Geri dönüş kuralları

- **S1 otomatik geri alınırsa** (`/ready`): imaj S1 öncesine döner, göçler kalır. O sırada v2 konuşan istemci yok, çünkü Ws S3'ten sonra çıkar.
- **S2 ya da S3 otomatik geri alınırsa:** bir önceki imaj birim damgalarına zaten uyar (K2 S1'de). Komut ve kira uçları 404 döner:
  - istemcide devredilmiş yayın düzenlemesi kapanır
  - ödeme isteği GUID'li kişide engellenir (fail-closed, §6.5)
  - canlı yayın sürer
  - kapanışlar istemcinin yeniden göndermesiyle devreder (§5.2); onarım işi kalanları kapatır (§4.9)
- **YASAK — mod On bir lisans varken S1 öncesi imaja elle sabitlemek.** Eski kod damgalara uymaz: bayat push komut kararını geri alır, ProductId yine NULL'lanır, devir yazılmaz. Tespit: `SELECT COUNT(*) FROM Licenses WHERE SalesSyncMode = 1` > 0. `deploy/README.md`'ye ortak bakiye §7.4 biçiminde bir uyarı eklenir. Etiketin kaynağında `git cat-file -e <sha>:OrderDeck.Shared/Sales/LabelRules.cs` başarısızsa o imaj S1 öncesidir.
- **WPF'in ≤0.9.10'a dönmesi (Velopack, istisnai destek eylemi):** önce lisans Off yapılır (§8.5).
  - 049 kalır. Eski kod `RemoteLiveSession`'ı görmez; uzak oturumların `EndedAt`'i hep dolu. Eski `GetActive`, `StartStream` ve kurtarma ekranı bu yüzden etkilenmez.
  - Eski kodun `Authority = 'server'` satıra yazma denemesi ABORT ile görünür biçimde düşer.
  - Uzak etiket satırlarında `SyncedAt` dolu olduğu için eski kod onları göndermez.
  - Sürüm notu: dönen sürümde açılışta "Önceki yayınlardan basılmamış etiket var" sorulursa "Hayır" denmeli; eski kod bu soruda başka bilgisayarın kuyruğunu da teklif eder (`LabelRepository.cs:96-107`).

### 8.7 Kopya veritabanı ve yedekten geri yükleme

- `RestoreService.RestoreAsync` (`OrderDeck.App/Services/RestoreService.cs:44`) geri yüklemeden sonra yeni bir ReplicaId üretir. Bir sonraki `hello` eski kimliği `previousReplicaId` olarak taşır.
- Sunucu, önceki kopyanın devredilmemiş oturumlarının sahipliğini yeni kopyaya geçirir. Koşul: yeni kopyanın ilk `hello`'sundan sonra önceki kopyadan hiç istek gelmemiş olmalı. Geçiş günlüğe yazılır.
- Disk klonu (aynı ReplicaId, farklı donanım parmak izi) admin uyarısı üretir ve modun açılmasını engeller.
- Geri yüklenen eski veritabanındaki satırlar akış imleci geri sarıldığı için sunucunun son hâlini yeniden indirir. `SyncedAt` dolu satırlarda sunucu kazanır. D1'in "eski yedek iptali diriltir" açığı burada yok, çünkü geçmiş hiçbir zaman yeniden oynatılmaz; geri doldurma yalnız boşu doldurur.
- Kalan risk: yedek anında `SyncedAt` NULL olan bir satır devredilmemiş bir yayına aitse, gönderildiğinde son-gelen-kazanır ile yazılır. Bugünkü riskle aynı sınıftadır, testle sabitlenir (§10.3).

## 9. KVKK

- **Akışta, okuma anında (S1):** asıl projeksiyonu silinmiş müşterinin siparişi `DisplayName = null`, `MessageText = ""` ile iner.
- **Sunucuda kalıcı:** projeksiyonu silen tek yol `ShopperPurgeService` (`ShopperPurgeService.cs:171-221`, `MarkPurged` `:221`). Aynı işlemde o asıl kayda bağlı Orders satırlarında `DisplayName = null`, `MessageText = ""` ve `TextScrubbedAt = now` yazılır. ChangeSeq ilerlediği için silme bütün PC'lere iner.
  - Username, Code ve Price mali kayıt olarak kalır. Yerel politikayla aynı (`CustomerRepository.cs:1496-1500`).
  - Ayar anahtarı `SalesSync:ScrubOrderTextOnPurge`, varsayılan açık. Sahip onayı açık maddede (§11).
- **Yerelde:** müşteri akışından gelen silme o kişinin bütün yerel Label satırlarında (kendi ve uzak) DisplayName ve MessageText'i guard altında boşaltır. Satır, CustomerId, kod ve fiyat kalır. `TwoPcConvergenceTests.cs:572-598` sözleşmesi ("satış kaydı silmeden etkilenmez") korunur.
- Akıştan inen müşteri taslağı mezar taşı boşaltmasından geçer (`CustomerSyncRepository.cs:570`). Etiket inişi silinmiş bir kimliği diriltmez.
- `SalesCommandLogs`, `PaymentRequestRecords`, `SalesConflictLog` ve günlükler kişisel metin saklamaz; yalnız Id, tutar ve makine adı tutarlar.
- Yayılım: etiket metni artık daha çok bilgisayarda duruyor (§12). Silme, yukarıdaki yolla hepsine ulaşır.

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
11. `force-end` ile sahibin kapanışı eşzamanlı: tek devir, EndedAt tekdüze.
12. Göç Express sürümünde ve `ONLINE` olmadan koşar; süre ölçülür.
13. Mod On'da protokolsüz push komut sonucunu geri alamaz; iz yazılır.
14. v1 güncellemesi ProductId'yi NULL'lamaz ve stoğu geri sarmaz (defter birleştirilmiş durumdan beslenir).
15. Kira süresi dolup `finish` gelmezse kayıt `Unconfirmed` olur ve öbür kopyanın `begin`'inde `already-requested` olarak döner.

### 10.2 InMemory ve birim

- Ortak kural vektörleri (JSON) hem sunucu hem istemci test projesinde koşar: `SameCode`, `Classify`, `CodePrice`, `Contribution`, `PaidPriceOnCorrection`, K1–K7.
- Komut doğrulamaları ve 409 sınıfları. CLI `enable` koşulları ve kuru rapor.
- **Bildirim bastırma:** `sales/backfill` ile eklenen basılmış sipariş push, WhatsApp `OrderReceived` kuralı ve shopper push üretmez. Komutlar bildirim üretmez; tek istisna `shipment-decision`'ın etiket kuralı.
- Akış sayfası kurulumu: yan liste, KVKK boşaltması, `cursorReset`.

### 10.3 WPF

- **`TwoPcSalesConvergenceTests` + `FakeSalesServer`.** Desen: `TwoPcConvergenceTests` ve `FakeCustomerServer`; sıra permütasyonlarıyla koşar. Senaryolar:
  - A yayın yapar, bitirir, B görür
  - B iptal eder; A bunu görür, kâğıt çıkmaz
  - B iptal eder, ardından A'nın geç push'u gelir (`ignored`)
  - force-end ve A'nın geri dönüşü (`rejected` listesi)
  - kalıntı kuyruk etiketinin çevrimdışı iptali ve ilk baskısı (bekleyen komut, tek kâğıt)
  - yedekten dönüş
  - `cursorReset`
  - On → Off → On geçişleri
  - kira: in-progress, already-requested, Unconfirmed
- Tetikleyici şema testi: Label, Shipment ve StreamSession'a yazan her SQL listede. ABORT'un canlı yolları kırmadığı (CorrectPrices, AttachLabel mod Off'ta) ayrıca test edilir.
- `Migration049RehearsalTests`: önceki sürüm şema 49'da yayın açar, etiket yazar, basar ve iptal eder.
- Aynı akış öğesini iki kez uygulamak toplamda sıfır fark üretir. `StockSyncedAt` dolu kalır; stok iki kez düşmez.
- Devir kapısı: "boşaldı" okuması ile kapanış arasında yerel düzenleme reddedilir; kapanış kuyruk boşalmadan `Release = true` göndermez.
- Baskıdan önce yeniden okuma: başka bilgisayarda iptal edilen kalıntı etiket basılmaz.
- DI testi: hosted service hem kayıtlı hem başlatılıyor (`WpfStartupEnvironment.cs:189-200`).
- Testlerde sabit kimlik bilgisi yazılmaz; `TestPhone` gibi üreteçler kullanılır (CLAUDE.md).

### 10.4 Saha kontrolleri (gerçek etkiden; deploy doğrulama dersi)

1. PC-A'da yayın, bitir: PC-B'de ≤ 60 sn içinde görünür. DB'de `ReleasedAt`, `SessionReleasedAt` ve sayılar.
2. PC-B'de iptal ve kod fiyatı: PC-A'da ≤ 30 sn. DB'de `CancelSetAt`, `PriceSetAt`, `SalesCommandLogs`.
3. PC-A'nın bayat push'u PC-B'nin iptalini geri almaz (`ignored` kaydı).
4. PC-B'de dekont, bağla, eşik; ardından iptal: dosya tutarı düşer (§7.2 örneği).
5. Aynı kişiye iki bilgisayardan aynı anda istek: biri `in-progress-elsewhere`, ardından `already-requested` sorusu. `PaymentRequestRecords` satırları.
6. Stok: aynı etiket bir kez düşer (panel bakiyesi).
7. Son 3 yayının toplamı her PC'de panelle aynı.
8. Kalıntı kuyruk etiketi çevrimdışı iptal edilir, bağlantı gelince sunucuda iptal olur.

### 10.5 Gözlem

Günlük sayılar: devir süresi (kapanıştan devre), K5 ve K6 retleri, `ignored` sayısı, `in-progress-elsewhere` ve `stale-total` sayıları, onarım işinin düzelttiği satırlar, protokolsüz istek izi. Ad ya da metin yazılmaz.

## 11. Sahip kararları

**Sahip kararları (2026-10-13, kesin)**

| # | Soru | Karar | Tasarımdaki karşılığı |
|---|---|---|---|
| Y1 | Başka PC bir yayını canlıyken mi, bittikten sonra mı değiştirebilsin? | Bittikten sonra. Canlı yayın başka PC'de salt okunur. | Yayın hâlleri (§2), `session-live` (§4.6), RemoteLiveSession yalnız başlık (§3.2, §5.7) |
| Y2 | Yayınlar hep aynı PC'den mi açılıyor? | Evet, aynı PC'den. | Sadeleştirme: canlı yayının etiketleri hiç inmez; force-end ve eşzamanlı canlı yayın nadir durumlar. Doğruluk korunur: sahiplik yayın başına, her PC yayın açabilir (§2). |
| Y3 | Yayın sonrası düzenleme internet istesin mi? | Kabul. Canlı PC çevrimdışı çalışmayı sürdürür. | §5.8; kalıntı kuyruk için bekleyen komut (§5.5) |
| Y4 | Kargo ücreti ve eşik tek bir lisans geneli ayar mı olsun? | Evet. | `LicenseShippingSettings` (§3.1, §4.8, §6.2) |
| Y5 | Kargo dosyasına bağlandıktan sonra iptal edilen etiket tutardan ve eşikten düşsün mü? | Evet. | §7.2 (mod On'da) |
| Y6 | Fatura ve e-Arşiv dökümü tek PC'den mi alınıyor? | Evet, tek PC'den. | Raporlar bütün PC'lerin satışlarını içerir (§5.9). Teknik kilit eklenmez (§12). |
| Y7 | Sahip PC yayın ortasında susarsa başka PC onaylı "yayını bitir" yapabilsin mi? | Evet, ≥ 15 dk sessizlikten sonra. | `force-end` (§4.6), çip ve onay (§5.7) |

Önceki sahip kararları aynen geçerli: #508 F1–F3 (kod başına tek fiyat), #506 karar 2 (tekrar göndermeden önce sor) ve karar 4 (birleştir), ortak bakiye K0–K6.

**Mühendislik kararları**

| # | Karar | Gerekçe |
|---|---|---|
| M1 | Lisans başına statik mod kapısı (CLI). Açılış ön koşulları: bütün aktif PC'ler Ws+, geri doldurma tamam, 7 gün protokolsüz iz yok. Çalışma anında eski sürüme karşı korumalar (§8.3). | Dinamik kapının güvenli dönüş tarafı yok (§8.3). |
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
| M12 | Y5 yalnız mod On'da; mod Off'ta yerel yol değişmez. | Geçişte iki yazıcının farklı tutar üretmemesi için. |
| M13 | Bağ çözme ayrı komut değil, iptal ve geri alma kuralının etkisi. | Arayüz eklenmeden Y5 karşılanır. |

**Açık kalanlar**
- **KVKK metin boşaltması (sahip onayı bekliyor).** Silmede sunucudaki ve bütün PC'lerdeki etiket metinleri (takma ad, yorum) boşaltılsın mı? Tasarımın varsayılanı **evet** (`SalesSync:ScrubOrderTextOnPurge = true`). Sahip "hayır" derse yalnız ayar değişir. Akıştaki okuma anı boşaltması her durumda kalır.
- Eski, sahibi hiç kalmamış açık oturumların toplu kapatılması: `sales-sync force-end` CLI'ı var. Her kapatma sahibin onayıyla, `sales-audit` listesinden yapılır.

## 12. Riskler ve bilerek yapılmayanlar

**Riskler**
1. **`rowversion` ekleme süresi.** Orders tablosunun tamamı yeniden yazılır. Önlem: ölçüm, gerekirse ayrı bakım göçü (§3.1). Postgres planında rowversion'ın karşılığı yok; müşteri akışı bu borcu zaten taşıyor, Postgres göçü ikisini birlikte çözer.
2. **Kurallar iki tarafta çalışıyor.** Önlem: `OrderDeck.Shared` ortak kodu ve ortak test vektörleri (§4.11).
3. **Bağlantı bağımlılığı.** Yayın sonrası düzenleme internet ister (Y3 ile kabul).
4. **Devir takılabilir.** Bir etiket kalıcı olarak gönderilemiyorsa (ör. `unknown-session` 400, `LicensesSessionsSyncController.cs:233-247`) yayın devredilmez. Durum satırı bunu gösterir, `sales-audit` sayar. Çözüm: hatalı satırın düzeltilmesi ya da `force-end`.
5. **Yanlış force-end.** Ağ bölünmesinde sahip yalnız çevrimdışıyken yayını başka PC bitirebilir. Sahibin sonraki fiyat ve iptal değişiklikleri reddedilir, ama listelenir. Yeni etiketler kabul edilir; o durumda tek fiyat ihlali (DM5) olabilir, `sales-audit` sayar ve bir sonraki `set-code-price` onu da kapsar.
6. **Toplamlar görünür biçimde değişir.** Türetilmiş toplamlar kaymış sayaçlardan farklı çıkabilir. Mod açılınca dosya tutarları türetilir; Y5 açık dosyalarda geçmişe dönük etki eder. Sayılar raporlanır, verilmiş kararlar değiştirilmez.
7. **Raporlar bütün PC'leri içerir.** Fatura dökümü iki PC'den alınırsa aynı satış iki kez faturalanır. Y6 ile operasyonel kural; teknik kilit yok.
8. **Kopya veritabanı.** Önlem: parmak izi denetimi ve mod kapısı (§8.7).
9. **Hız sınırı.** Aynı NAT arkasındaki PC'ler 100 istek/dk/IP bütçesini paylaşır. İlk yetişme birkaç tur sürebilir; tur başına sayfa sınırı var.
10. **Eski PC dönerse.** Bayat görüntü; para yolu korunur (§8.3). Kesin çözüm PC'yi güncellemek.
11. **KVKK yayılımı.** Mesaj metni daha çok bilgisayarda duruyor. Silme bütün kopyalara iner (§9).
12. **Kişi anahtarı ayrışması.** Grup senkronu gecikmesinde üye kesişimi yakalar. Hiç kesişmeyen üye kümelerinde #506'nın birleştirme sorusu kalır.
13. **Bildirim boşluğu.** Basılmadan gönderilip sonra basılan etiketin bildirim üretmemesi (`LicensesSessionsSyncController.cs:324-332`) aynen sürer; komutlar da üretmez.
14. **İş büyüklüğü.** En riskli PR W2'dir (yönlendirici ve akış). W3, `PaymentRequestService`'in yalnız girişine ve çıkışına sarılır; durum makinesi (`PaymentJobRepository`) değişmez.

**Bilerek yapılmayanlar**
- Başka PC'nin canlı yayınını düzenlemek ya da canlıyken izlemek (Y1).
- D1'in istemci saatli birim damgaları, `SessionCodePrices` tablosu ve uzak kod fiyatının canlı PC'ye otomatik uygulanması. Kâğıt ile kayıt ayrışıyordu.
- D3'ün `ActiveSaleScopes` tablosu (§6.6).
- D3'ün R1/R2 iki aşamalı WPF sürümü; tek mod kapısı onun yerini alır.
- Dinamik mod kapısı ve modun kendiliğinden kapanması.
- Elle "dosyadan çıkar" arayüzü (M13).
- Mod Off'ta Y5 (M12).
- Çekilişler, PaymentJob senkronu, dekont inişi.
- Panel ve Shopper istemci değişikliği.
- Fatura dökümüne teknik kilit (Y6).
- Kişi (grup) bazında kargo havuzu; bugün olduğu gibi müşteri satırı bazında kalır.

## 13. PR planı

Her PR ayrı açılır; bir sonraki ancak öncekinin master'a girmesinden sonra açılır. Yığılmış PR yok (yığılmış PR tuzağı). Merge ve deploy yayın penceresine denk gelmez. Commit'lerde Co-Authored-By satırı bulunur.

| PR | Repo | İçerik | Efor (gün) |
|---|---|---|---|
| **S1** `feat(sales-sync): sahiplik, devir, birim kuralı ve satış akışı` | LiveDeck (sunucu) | Göç (§3.1, S1 satırları; `ONLINE` yok). `sessions/sync` (tekdüze EndedAt, devir, `Release`). `orders/sync` K1–K7 (S1'de K2 damgasız, K4 mod Off), birleştirilmiş durumdan defter, v2 yanıtı. `sales/backfill` (bildirimsiz). `sales/changes` (yan liste, KVKK, cursorReset). `sales/state` ve `hello`. `sales-release-repair` işi. `sales-audit`. `ClientVersion` yardımcısı. `OrderDeck.Shared/Sales/LabelRules.cs` ve vektörler. `deploy/README` uyarısı. Testcontainers 1–3, 12, 14. | 5 |
| **S2** `feat(sales-sync): kapanmış yayın komutları ve sunucuya ait kargo` | LiveDeck (sunucu) | `SalesCommandLogs`. On komut türü (§4.6): görünürlük, beklenen sürüm, applock, damgalar, defter, dosya türetme (DM7, Y5). `shipment-decision` etiket kuralı. `force-end`. `shipping-settings`. Mod On'da `shipments/sync` iz kuralı. Testcontainers 4–7, 11, 13. | 5 |
| **S3** `feat(payment-request): bilgisayarlar arası kira ve kapsam başına tek düşüm` | LiveDeck (sunucu) | `PaymentRequestRecords` ve `PaymentRequestMembers`. `begin`, `finish` ve liste uçları (PersonKey, üye kesişimi, stale-total, settings-stale, Unconfirmed). Apply kapsam kuralı (applock, defter yüklemi). Mod On'da `client-update-required`. `sales-sync` CLI'ları. Testcontainers 8–10, 15. | 3 |
| **W1** `feat(sales-sync): 049, v2 gönderim, devir ve geri doldurma` | LiveDeck (WPF) | Göç 049 (kolonlar, tablolar, tetikleyiciler). ReplicaId ve `hello`. v2 DTO'ları. Gönderim kapısı, kapanış sırası ve devir kapısı. Yanıt işleme (applied/ignored/rejected). `sales-backfill-out`. Türetilmiş toplamlar (`CustomerAggregateSql`). `GetActive` ve `GetUnprintedOutsideSession` süzgeçleri. Baskıdan önce yeniden okuma. Şema ve prova testleri. | 4,5 |
| **W2** `feat(sales-sync): akış uygulaması ve kapanmış yayında düzenleme` | LiveDeck (WPF) | `SalesChangesPullService` (kayıt ve başlatma). `ApplyItem` (taslak müşteri, U5, StockSyncedAt). `RemoteLiveSession` ve çip. `ILabelEditor` ve `SalesCommandClient`. `PendingSalesCommand` ve örtü. Kargo aynası ve dekont adımı. Force-end arayüzü. Arayüz durumları (§5.7). Kip geçişleri (§8.4, §8.5). `TwoPcSalesConvergenceTests`. | 7,5 |
| **W3** `feat(payment-request): kira, sunucu toplamı ve lisans geneli kargo ayarı` | LiveDeck (WPF) | Ön gönderim, `begin`/`finish` sarmalı ve 409 eşlemeleri. `GetPrintedTotal`. Uzak kayıtlar soruda, rozetlerde ve #508 planında. `scope-has-deduction` benimsemesi. `ShippingSettingsProvider` ve ayar ekranı. Testler. | 3,5 |
| — | Velopack Ws, PC'ler sırayla, mod açılışı, saha kontrolleri | 2 |
| **S4** `chore(sales-sync): daraltma` | LiveDeck (sunucu) | Mod On'dan ≥ 2 hafta sonra. Mod On'da protokolsüz sipariş güncellemesi sessizce yok sayılmak yerine 409 `client-update-required` alır. Ölü kod (`ShipmentRepository.GetByStatus`/`GetLabelIds`, `LabelService.AddShippingFee`, `RemoveBackup`, `LabelRepository.MarkPrinted`) ayrı bir karar olarak listelenir. | 1 |
| — | OrderDeck-Mobile, OrderDeck-Shopper | Değişiklik yok. | 0 |

**Toplam ≈ 31,5 iş günü.** Kazanan tasarımların tahmini 23–23,5 gündü. Fark aşılardan geliyor: lisans geneli kargo ayarı, Y5 kuralı, `add-backup`, bekleyen komut ve örtü, kira kayıtları ve Unconfirmed, KVKK boşaltması, onarım işi, baskıdan önce yeniden okuma, kip geçişleri ve force-end arayüzü. Asgari ilk değer S1 + W1'dir (≈ 9,5 gün): ProductId düzeltmesi, tekdüze EndedAt, kaçmış kapanışların kapanması ve geçmişin sunucuda tamamlanması. Görünürlük ve değiştirebilme mod On ile gelir.

**Diğer iki işe göre sıra ve bağımlılıklar** (sahip sırası: önce bu senkron, sonra ortak bakiye, sonra banka havalesi)

1. **Bu spec:** S1 → S2 → S3 → W1 → W2 → W3 → Ws → mod On → S4.
2. **Ortak bakiye** (`2026-10-11-ortak-bakiye-design.md`), bu spec'in S3'ü master'a girdikten sonra başlar.
   - Aynı denetleyiciye dokunuyorlar (`LicensesCustomerBalanceApplyController`): ortak bakiye S1 scope, transactions ve reverse'i, S2 apply'ı yeniden yazıyor.
   - Ortak bakiye S2'nin `DrawAsync`'i §6.6 kuralını çapa düzeyinde korumalıdır.
   - S1'in `ClientVersion` yardımcısı kullanılır; `PersonBalance:MinClientVersion` ve `SalesSync:MinClientVersion` aynı karşılaştırmayı paylaşır.
   - Wh ⊇ Ws olmalı: havuz kipinin kapısı bütün PC'leri Wh+ ister; böylece hepsi satış senkronunu da taşır.
   - Bu spec ortak bakiyeye bağımlı değildir. PersonKey, BIN2 harmanı gelmeden de bellekte ordinal hesaplanır (§6.7).
3. **Banka havalesi** (`2026-10-10-banka-havale-onay-design.md`), ortak bakiyeden sonra gelir (onun K6'sı havuza bağlı). Bu spec bankaya üç dayanak sağlar. Bunlar öneridir; banka spec'ine yansıtılması ayrı bir docs PR'ı ister:
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
| 5 | H1 aşı (D2+D3) | Önemli | PersonKey `g:`/`c:` + üye kümesi kesişimi | İşlendi §2, §6.4-c, M10 |
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
