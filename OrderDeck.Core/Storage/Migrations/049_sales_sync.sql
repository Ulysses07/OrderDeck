-- 049 — Yayın ve etiket senkronu, W1 (spec 2026-10-13-yayin-etiket-senkron-design §3.2).
--
-- YALNIZ EKLEYİCİ. Hiçbir kolon düşmez, adı ya da tipi değişmez; göçten önce var olan kolonların
-- içeriği değişmez (Migration049RehearsalTests özetle sınar). Yazılan yalnız iki tohum var: bu
-- veritabanının kopya kimliği (_meta.ReplicaId) ve var olan kargo dosyalarının
-- Shipment.OriginReplicaId'si (yeni kolon). Tetikleyiciler en sonda kurulur: tohum yazımları
-- hiçbir şeyi kuyruğa almaz.
--
-- YALNIZ YERLEŞİK SQL (045 ve 048 kuralı, 045_customer_unit_stamps.sql). Hiçbir şema nesnesi —
-- tetikleyici, CHECK, varsayılan, indeks — uygulama fonksiyonu (od_*) çağırmaz. Çağırsaydı,
-- fonksiyonu kaydetmeyen eski ikiliye (≤0.9.10) dönüşte o nesneye dokunan her yazım
-- "no such function" ile düşerdi; aynı gerekçeyle sıfırlama betiği
-- (deploy/scripts/wpf-sales-sync-reset.sql) sqlite3 komut satırında da koşar.
--
-- MOD OFF'TA ABORT TETİKLEYİCİLERİNİN HİÇBİRİ ETKİLİ OLMAZ. Koşulları ya
-- SalesSyncState.Mode = 1 ya da oturumun Authority = 'server' olmasıdır. Mod Off'ta (bugün her
-- lisans) Mode 0'dır ve Authority hep 'local' kalır: Ws mod Off'ta devri görse de yalnız
-- ReleasedAt'i saklar, Authority yazmaz (§2, §5.2). Canlı yayın, baskı, iptal, fiyat düzeltme,
-- ödeme isteği ve kargo bu yüzden bugünkü gibi yazar. Mod Off'taki tek davranış farkı kuyruğa
-- alma tetikleyicileridir (aşağıda).
--
-- KUYRUĞA ALMA NEDEN TETİKLEYİCİDE (045 kararının etiketteki karşılığı, §3.2 notları, M7).
-- Label'a, Shipment'a ve StreamSession'a yazan çok sayıda yol var; birinin SyncedAt'i düşürmeyi
-- unutması o değişikliğin hiçbir yere gitmemesi demek. Bugünkü elle "SyncedAt = NULL,
-- Revision = Revision + 1" yazımları KALIR: Revision'ı kendileri artırdıkları için tetikleyici
-- onlarda etkisizdir (koşul NEW.Revision = OLD.Revision), çift artış olmaz. Kuyruğa almayan tek
-- yazım ShipmentRepository.AttachLabel'dı (UPDATE Label SET ShipmentId, Revision'sız); 049'dan
-- sonra o da kuyruğa girer. DAVRANIŞ DEĞİŞİKLİĞİ: dekontun kargo adımı bağladığı her etiketi bir
-- kez daha gönderir (eski sürüm dahil, v1 gövdesiyle). Sunucu v1'de kargo bağına dokunmaz (K4),
-- yani bu yalnız fazladan bir gönderimdir.
--   - Değişiklik NULL'a dayanıklı karşılaştırılır (IS NOT): aynı değeri yeniden yazmak kuyruğa
--     almaz.
--   - StockSyncedAt yalnız stok-ilgili alanda (CancelledAt, IsTentativeBackup; 030) düşer; fiyat,
--     baskı ve kargo bağı stok defterini değiştirmez (030 ve LabelRepository.MarkPrinted gerekçesi).
--   - TR_Shipment_Enqueue yalnız mod 0'da: mod 1'de dosya sunucunundur, yerel ayna gönderilmez.
--
-- SyncApplyGuard (045): akış uygulaması, komut yanıtı ve push yanıtının `current`'ı düzenleme
-- DEĞİLDİR. Dokuz tetikleyicinin hepsi kilit satırı varken susar: inen satır yankı olarak geri
-- gönderilmez (DM8, F05 karşılaştır-ve-yaz bozulmaz) ve sunucunun yazdığı satır ABORT edilmez.
--
-- ABORT LİSTELERİ. Etiketin otorite tetikleyicisi birim ve oluşum kolonlarını kapsar, CustomerId
-- HARİÇ: yerel yeniden anahtarlama (CustomerSyncRepository.MoveReferences) her kipte ve her
-- oturumda yazabilmeli. SyncedAt, StockSyncedAt, Revision ve ServerVersion HİÇBİR listede yok:
-- MarkSynced (F05), push yanıtının sürüm yazımı ve geri doldurma her durumda geçer.
--
-- ÖNCEKİ SÜRÜM (≤0.9.10, şema 48) şema 49'da çalışır: kolonları adla okur, yeni kolonların ya
-- varsayılanı var ya NULL'a izinlidir; eklediği oturum 'local' doğar. RemoteLiveSession ayrı
-- tablo olduğu için eski GetActive, StartStream ve açılıştaki kurtarma ekranı başka bilgisayarın
-- canlı yayınını görmez (M5). Mode = 1 ya da Authority = 'server' durumunda eski ikilinin ilgili
-- yazımları görünür biçimde düşer; dönüşten önce PC Off turunu bitirmiş olmalı ya da sıfırlama
-- betiği koşulmalı (§8.6, deploy/README.md).
--
-- Zaman kolonları unix SANİYE (StreamSession.EndedAt ile aynı birim).
--
-- Spec'in açık bıraktığı şema ayrıntıları burada kapanır:
--   - _meta.PreviousReplicaId: geri yüklemeden sonra RestoreService yeni bir ReplicaId üretir ve
--     yedekten gelen kimliği buraya yazar; hello onu previousReplicaId olarak taşır (§8.7).
--     Geri yüklenen dosya kendi kimliğini getirdiği için kalıcı yeri veritabanının kendisidir.
--   - RemoteLiveSession.UnsentLabelCount: sales/state'teki liveSessions[].unsentLabelCount
--     (§4.5); "N etiket gönderilmeyi bekliyor" çipi (§5.7).
--   - SalesFeedFailure.ChangeSeq ve FirstFailedAt: CustomerFeedFailure'ın ikizi (U10) — öğe
--     değişince (yeni ChangeSeq) deneme sayacı sıfırdan başlar.

-- Kopya kimliği (§2): veritabanı dosyasına bağlı; AppSettings.InstallationId değil (o ayar
-- dosyasına bağlı). 16 rastgele bayt, 32 küçük onaltılık hane = Guid "N" biçimi.
ALTER TABLE _meta ADD COLUMN ReplicaId TEXT;
ALTER TABLE _meta ADD COLUMN PreviousReplicaId TEXT;
UPDATE _meta SET ReplicaId = lower(hex(randomblob(16))) WHERE Id = 1 AND ReplicaId IS NULL;

-- Origin: 'local' = bu veritabanında açıldı, 'remote' = akıştan indi (W2).
-- Authority: 'local' = satırı bu bilgisayar yazar; 'server' = yalnız komut yanıtı ve akış yazar.
-- Kendi oturumu ancak mod On'dayken devir sunucudan görülünce 'server' olur (§2, §5.2).
-- ReleasedAt: sunucunun devir anı; mod Off'ta yalnız saklanır.
ALTER TABLE StreamSession ADD COLUMN Origin TEXT NOT NULL DEFAULT 'local'
    CHECK (Origin IN ('local', 'remote'));
ALTER TABLE StreamSession ADD COLUMN Authority TEXT NOT NULL DEFAULT 'local'
    CHECK (Authority IN ('local', 'server'));
ALTER TABLE StreamSession ADD COLUMN OwnerMachineName TEXT;
ALTER TABLE StreamSession ADD COLUMN ReleasedAt INTEGER;

-- Sunucunun Version'ı: komutun beklenen sürümü ve v2 push'unun baseVersion'ı (§4.2).
ALTER TABLE Label ADD COLUMN ServerVersion INTEGER;
ALTER TABLE Shipment ADD COLUMN ServerVersion INTEGER;

-- Sunucudaki Shipments.OriginReplicaId'nin aynası: mod Off'ta dosyanın tek yazıcısı (§3.1, §7.4).
-- Yerelde açılan dosyada bu kopya. Var olan dosyaların hepsi bu veritabanında açıldı (049'dan
-- önce dosya yalnız yerelde açılabiliyordu). NULL: 049'dan sonra kolonu yazmayan bir yolun (eski
-- ikili dahil) açtığı dosya — o da bu veritabanının.
ALTER TABLE Shipment ADD COLUMN OriginReplicaId TEXT;
UPDATE Shipment SET OriginReplicaId = (SELECT ReplicaId FROM _meta WHERE Id = 1);

-- Başka bilgisayarın canlı ya da devrediliyor yayını (§3.2, §4.5). Bu satırlar StreamSession'a
-- HİÇ girmez: GetActive'in "EndedAt IS NULL olan herhangi bir satır" koşulu ve eski sürüme
-- dönüşte StartStream ile kurtarma ekranı yanılmaz (M5).
CREATE TABLE RemoteLiveSession (
    Id               TEXT    NOT NULL PRIMARY KEY,
    Title            TEXT,
    StartedAt        INTEGER NOT NULL,
    EndedAt          INTEGER,
    OwnerMachineName TEXT,
    OwnerLastSeenAt  INTEGER,
    State            TEXT    NOT NULL CHECK (State IN ('live', 'handing-over')),
    UnsentLabelCount INTEGER
);

-- Tek satır. Mode 0 = Off (bugünkü davranış), 1 = On. ModeSeenAt: sales/state'in modeChangedAt'i,
-- bu kopyanın son gördüğü (§8.5). Kargo ayarı mod On'da lisans geneli ayarın önbelleği (§6.2).
-- ScrubOrderText: SalesSync:ScrubOrderTextOnPurge (§9). *DerivedAt / FirstCatchUpAt /
-- PaymentHistorySentAt: bir kez yapılan işlerin damgası (§8.2, §8.4).
CREATE TABLE SalesSyncState (
    Id                    INTEGER PRIMARY KEY CHECK (Id = 1),
    Mode                  INTEGER NOT NULL DEFAULT 0 CHECK (Mode IN (0, 1)),
    ModeSeenAt            INTEGER,
    ShippingVersion       INTEGER,
    FreeShippingThreshold REAL,
    ShippingFee           REAL,
    ScrubOrderText        INTEGER NOT NULL DEFAULT 0 CHECK (ScrubOrderText IN (0, 1)),
    FirstCatchUpAt        INTEGER,
    AggregatesDerivedAt   INTEGER,
    ShipmentsDerivedAt    INTEGER,
    PaymentHistorySentAt  INTEGER
);
INSERT INTO SalesSyncState (Id, Mode, ScrubOrderText) VALUES (1, 0, 0);

-- Gönderilemeyen finish ve mod Off ödeme kayıtları (§6.4). İmleç SyncCursor('payment-history-out').
CREATE TABLE PendingPaymentRecord (
    RecordId    TEXT    NOT NULL PRIMARY KEY,
    Kind        TEXT    NOT NULL,
    PayloadJson TEXT    NOT NULL,
    CreatedAt   INTEGER NOT NULL,
    Attempts    INTEGER NOT NULL DEFAULT 0,
    LastError   TEXT
);

-- Çevrimdışı kuyruğa yalnız iki komut girer: kalıntı etiketin iptali ve ilk baskısı (§5.5).
-- Sabit CommandId, idempotent. Etiket bağı örtü kuralı içindir (bekleyen iptal ya da baskıdaki
-- etiket kuyrukta yeniden teklif edilmez).
CREATE TABLE PendingSalesCommand (
    CommandId   TEXT    NOT NULL PRIMARY KEY,
    Kind        TEXT    NOT NULL CHECK (Kind IN ('cancel', 'mark-printed')),
    PayloadJson TEXT    NOT NULL,
    CreatedAt   INTEGER NOT NULL,
    Attempts    INTEGER NOT NULL DEFAULT 0,
    LastError   TEXT
);

CREATE TABLE PendingSalesCommandLabel (
    LabelId   TEXT NOT NULL,
    CommandId TEXT NOT NULL REFERENCES PendingSalesCommand(CommandId) ON DELETE CASCADE,
    PRIMARY KEY (LabelId, CommandId)
);
CREATE INDEX IX_PendingSalesCommandLabel_CommandId ON PendingSalesCommandLabel(CommandId);

-- Uygulanamayan akış öğesinin kalıcı deneme sayacı (U10, CustomerFeedFailure'ın ikizi). Kind:
-- akış öğesinin türü (oturum, sipariş, dosya); aynı Id iki türde çakışmaz.
CREATE TABLE SalesFeedFailure (
    Kind          TEXT    NOT NULL,
    ItemId        TEXT    NOT NULL,
    ChangeSeq     INTEGER NOT NULL,
    Attempts      INTEGER NOT NULL,
    LastError     TEXT,
    FirstFailedAt INTEGER NOT NULL,
    SkippedAt     INTEGER,
    PRIMARY KEY (Kind, ItemId)
);

-- Yok sayılan ya da reddedilen değişikliklerin izi (§5.2, §5.3, §8.2-4). Yalnız Id taşır; ad,
-- kullanıcı adı ya da mesaj metni YAZILMAZ (§9). LabelId komut düzeyindeki kayıtta boş.
CREATE TABLE SalesConflictLog (
    Id      INTEGER PRIMARY KEY,
    At      INTEGER NOT NULL,
    LabelId TEXT,
    Kind    TEXT    NOT NULL,
    Reason  TEXT
);

-- SyncCursor (038) ve Customer değişmez. Yeni imleçler ('sales-changes-in', 'sales-backfill-out',
-- 'payment-history-out') satırsız başlar; satır yoksa 0 sayılır.

-- ── Tetikleyiciler (§3.2 tablosu) ─────────────────────────────────────────────────────────
-- Her birinde NOT EXISTS (SELECT 1 FROM SyncApplyGuard). Gövdeler yalnız yerleşik SQL.

-- Etiketin birimi ya da yedek bağı değişti ve yazan Revision'ı artırmadı → kuyruğa al.
-- İç UPDATE'in kolonları (SyncedAt, Revision, StockSyncedAt) hiçbir Label tetikleyicisinin
-- listesinde değil: özyineleme ve ABORT yok.
CREATE TRIGGER TR_Label_Enqueue
AFTER UPDATE OF Price, CancelledAt, CancelReason, PrintedAt, PrintBatchId, PrintSeq,
                IsTentativeBackup, PriceCorrectedAt, PaidPrice, ShipmentId, ParentLabelId
ON Label
WHEN new.Revision = old.Revision
 AND (old.Price IS NOT new.Price
      OR old.CancelledAt IS NOT new.CancelledAt
      OR old.CancelReason IS NOT new.CancelReason
      OR old.PrintedAt IS NOT new.PrintedAt
      OR old.PrintBatchId IS NOT new.PrintBatchId
      OR old.PrintSeq IS NOT new.PrintSeq
      OR old.IsTentativeBackup IS NOT new.IsTentativeBackup
      OR old.PriceCorrectedAt IS NOT new.PriceCorrectedAt
      OR old.PaidPrice IS NOT new.PaidPrice
      OR old.ShipmentId IS NOT new.ShipmentId
      OR old.ParentLabelId IS NOT new.ParentLabelId)
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Label SET
        SyncedAt = NULL,
        Revision = Revision + 1,
        StockSyncedAt = CASE WHEN old.CancelledAt IS NOT new.CancelledAt
                               OR old.IsTentativeBackup IS NOT new.IsTentativeBackup
                             THEN NULL ELSE StockSyncedAt END
     WHERE rowid = new.rowid;
END;

-- Devredilmiş (Authority = 'server') oturumun etiketinin birimini ya da oluşum alanını yerel yol
-- yazamaz: değişiklik push'ta K5 ile reddedilip kaybolurdu (§2, §5.3). Birimler: fiyat {Price,
-- PriceCorrectedAt, PaidPrice}, iptal {CancelledAt, CancelReason}, yedek {IsTentativeBackup},
-- kargo bağı {ShipmentId}, baskı {PrintedAt, PrintBatchId, PrintSeq}. Oluşum: SessionId, Platform,
-- Username, DisplayName, MessageText, Code, AddedAt, IsShippingFee, IsBackupPromoted, ProductId,
-- ProductVariantId, ParentLabelId. CustomerId bilerek yok (yeniden anahtarlama).
CREATE TRIGGER TR_Label_ServerAuthority_U
BEFORE UPDATE OF Price, PriceCorrectedAt, PaidPrice, CancelledAt, CancelReason, IsTentativeBackup,
                 ShipmentId, PrintedAt, PrintBatchId, PrintSeq,
                 SessionId, Platform, Username, DisplayName, MessageText, Code, AddedAt,
                 IsShippingFee, IsBackupPromoted, ProductId, ProductVariantId, ParentLabelId
ON Label
WHEN (EXISTS (SELECT 1 FROM StreamSession s WHERE s.Id = old.SessionId AND s.Authority = 'server')
      OR EXISTS (SELECT 1 FROM StreamSession s WHERE s.Id = new.SessionId AND s.Authority = 'server'))
 AND (old.Price IS NOT new.Price
      OR old.PriceCorrectedAt IS NOT new.PriceCorrectedAt
      OR old.PaidPrice IS NOT new.PaidPrice
      OR old.CancelledAt IS NOT new.CancelledAt
      OR old.CancelReason IS NOT new.CancelReason
      OR old.IsTentativeBackup IS NOT new.IsTentativeBackup
      OR old.ShipmentId IS NOT new.ShipmentId
      OR old.PrintedAt IS NOT new.PrintedAt
      OR old.PrintBatchId IS NOT new.PrintBatchId
      OR old.PrintSeq IS NOT new.PrintSeq
      OR old.SessionId IS NOT new.SessionId
      OR old.Platform IS NOT new.Platform
      OR old.Username IS NOT new.Username
      OR old.DisplayName IS NOT new.DisplayName
      OR old.MessageText IS NOT new.MessageText
      OR old.Code IS NOT new.Code
      OR old.AddedAt IS NOT new.AddedAt
      OR old.IsShippingFee IS NOT new.IsShippingFee
      OR old.IsBackupPromoted IS NOT new.IsBackupPromoted
      OR old.ProductId IS NOT new.ProductId
      OR old.ProductVariantId IS NOT new.ProductVariantId
      OR old.ParentLabelId IS NOT new.ParentLabelId)
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    SELECT RAISE(ABORT, 'sales-server-authority');
END;

-- Devredilmiş oturuma yerel ekleme yok (ekleme komutla ya da akışla, kilit altında gelir).
CREATE TRIGGER TR_Label_ServerAuthority_I
BEFORE INSERT ON Label
WHEN EXISTS (SELECT 1 FROM StreamSession s WHERE s.Id = new.SessionId AND s.Authority = 'server')
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    SELECT RAISE(ABORT, 'sales-server-authority');
END;

-- Mod On'da kargo bağının tek yazıcısı sunucu komutudur, canlı oturum dahil (K4, DM1).
CREATE TRIGGER TR_Label_ShipmentLink
BEFORE UPDATE OF ShipmentId ON Label
WHEN (SELECT Mode FROM SalesSyncState WHERE Id = 1) = 1
 AND old.ShipmentId IS NOT new.ShipmentId
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    SELECT RAISE(ABORT, 'sales-shipment-server-owned');
END;

-- Mod On'da yerel Shipment salt okunur bir aynadır (§7.1).
CREATE TRIGGER TR_Shipment_ServerOwned_I
BEFORE INSERT ON Shipment
WHEN (SELECT Mode FROM SalesSyncState WHERE Id = 1) = 1
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    SELECT RAISE(ABORT, 'sales-shipment-server-owned');
END;

CREATE TRIGGER TR_Shipment_ServerOwned_U
BEFORE UPDATE OF Status, HeldAt, ShippedAt, CumulativeAmount ON Shipment
WHEN (SELECT Mode FROM SalesSyncState WHERE Id = 1) = 1
 AND (old.Status IS NOT new.Status
      OR old.HeldAt IS NOT new.HeldAt
      OR old.ShippedAt IS NOT new.ShippedAt
      OR old.CumulativeAmount IS NOT new.CumulativeAmount)
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    SELECT RAISE(ABORT, 'sales-shipment-server-owned');
END;

-- Mod Off'ta dosyanın tek yazıcısı bu kopya: değişen dosya shipments/sync ile gider (Y5'in yerel
-- yolu dahil, §7.4). Satır yoksa Off sayılır.
CREATE TRIGGER TR_Shipment_Enqueue
AFTER UPDATE OF Status, HeldAt, ShippedAt, CumulativeAmount ON Shipment
WHEN new.Revision = old.Revision
 AND COALESCE((SELECT Mode FROM SalesSyncState WHERE Id = 1), 0) = 0
 AND (old.Status IS NOT new.Status
      OR old.HeldAt IS NOT new.HeldAt
      OR old.ShippedAt IS NOT new.ShippedAt
      OR old.CumulativeAmount IS NOT new.CumulativeAmount)
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Shipment SET SyncedAt = NULL, Revision = Revision + 1 WHERE rowid = new.rowid;
END;

-- Devredilmiş oturumu yerel yol değiştiremez (yayın başlığı ve notu düzenleme yolu bugün de yok).
CREATE TRIGGER TR_StreamSession_ServerAuthority
BEFORE UPDATE OF EndedAt, Title, Platforms, Notes ON StreamSession
WHEN old.Authority = 'server'
 AND (old.EndedAt IS NOT new.EndedAt
      OR old.Title IS NOT new.Title
      OR old.Platforms IS NOT new.Platforms
      OR old.Notes IS NOT new.Notes)
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    SELECT RAISE(ABORT, 'sales-server-authority');
END;

CREATE TRIGGER TR_StreamSession_Enqueue
AFTER UPDATE OF EndedAt, Title, Platforms, Notes ON StreamSession
WHEN new.Revision = old.Revision
 AND (old.EndedAt IS NOT new.EndedAt
      OR old.Title IS NOT new.Title
      OR old.Platforms IS NOT new.Platforms
      OR old.Notes IS NOT new.Notes)
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE StreamSession SET SyncedAt = NULL, Revision = Revision + 1 WHERE rowid = new.rowid;
END;

UPDATE _meta SET SchemaVersion = 49 WHERE Id = 1;
