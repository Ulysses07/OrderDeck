-- wpf-sales-sync-reset.sql — masaüstü (WPF) yerel veritabanını satış senkronundan ÇIKARIR, böylece
-- önceki sürüm (≤0.9.10) aynı veritabanında yazabilir. Spec 2026-10-13 §8.5 adım 2'nin ve §8.6'nın
-- SQL karşılığı. Ne zaman ve nasıl koşulacağı: deploy/README.md, "Masaüstü: satış senkronunu yerelde
-- sıfırlama".
--
-- NEDEN: önceki ikili göç 049'un tetikleyicilerini bilmez ve Mode = 1 ya da Authority = 'server'
-- durumundan çıkamaz. TR_Shipment_ServerOwned_* ve TR_Label_ShipmentLink dekontun kargo adımını,
-- TR_Label_ServerAuthority_* kendi geçmiş yayınlarındaki iptal ve düzeltmeyi düşürür. Olağan yol Ws'nin
-- Off turudur; bu betik yalnız Ws hiç açılamadığında kullanılan istisnai destek eylemidir.
--
-- KURALLAR
--   - Yalnız yerleşik SQL; uygulama fonksiyonu yok. sqlite3 komut satırında da, Microsoft.Data.Sqlite
--     ile de aynı sonucu verir. Yabancı anahtar denetimine dayanmaz (komut satırında varsayılan
--     kapalıdır): uzak etiketler uzak oturumlardan ÖNCE açıkça silinir.
--   - Tek işlem. Önce listeler (engeller, sonra plan sayıları), sonra kilit. Kilit düşerse hiçbir satır
--     değişmez: Microsoft.Data.Sqlite ve `sqlite3 -bail` ilk hatada durur, işlem kapanınca geri sarılır;
--     -bail'siz komut satırı devam etse de sonraki her yazım kilit satırını (temp.reset_guard) şart
--     koşar ve hiçbir şey yazmaz.
--   - Engeller: (1) bekleyen satış komutu (PendingSalesCommand) — devredilmiş yayından kalan iptal ya
--     da ilk baskı sunucuya gitmemiş demek; yerel yazıma çevrilmesi Ws'nin Off turunun işi (§8.5-3).
--     (2) karışık dosya: Shipped olmayan ve hem bu bilgisayarın (Origin = 'local') hem başka
--     bilgisayarın (Origin = 'remote') oturumlarından etiket taşıyan dosya. Sunucunun `disable`
--     kuralı onu büyük paya verir (§4.10); yerelde hangi payın büyük olduğu tek başına karar
--     olamaz. İkisi de Burak'a listelenir.
--   - Kişisel veri yazdırmaz: listelerde yalnız Id ve sayı.
--   - İdempotent: sıfırlanmış veritabanında ikinci koşu hiçbir şey değiştirmez.
--
-- SIRA (kilit geçtiyse)
--   1) Kargo dosyası kararları, uzak etiketler SİLİNMEDEN ÖNCE (silindikten sonra uzak dosya "etiketsiz"
--      görünürdü). Sunucunun `disable` kuralı yerel veriyle (§4.10, §8.5-2). Shipped dosyaya dokunulmaz
--      (kargoya verilmiş dosya her kipte son durumdur, §7.1). Shipped olmayan dosya:
--        - bağlı etiketlerinin hepsi yerel oturumlardaysa KALIR ve sahibi bu kopya olur
--          (OriginReplicaId = _meta.ReplicaId; mod On'da başka PC'nin bu PC'nin etiketleri için
--          açtığı dosya Off'ta bu PC'ye geçer);
--        - bağlı etiketi var ama hiçbiri yerel değilse SİLİNİR (başka bilgisayarın dosyası);
--        - SIFIR ETİKETLİ dosya: sunucu kuralında bağlı sipariş yoksa sahip değişmez; dosya bu
--          kopyanınsa (OriginReplicaId bu kopya, geri yüklemeden önceki kopya ya da NULL — NULL'u
--          049'dan sonra eski ikili açmıştır, o da bu veritabanı) KALIR, başka kopyanınsa SİLİNİR.
--   2) Uzak etiketler, uzak oturumlar (Origin = 'remote') ve RemoteLiveSession silinir.
--   3) Kendi oturumları yerel otoriteye döner (Authority = 'local' WHERE Origin = 'local').
--   4) SalesSyncState.Mode = 0.
--   5) Müşteri toplamları kalan etiketlerden yeniden türetilir: LabelRules.Contribution — basılmış ∧
--      iptal edilmemiş ∧ geçici yedek değil → (1, Price). Kargo ücreti satırı dahil (bugünkü kural).
--
-- Kalan bayatlık (bilerek): betikte akış yok, yerel ayna son mod On turundaki hâlinde kalır.
-- Kargolanmış dosyanın yeniden açılmasını sunucu önler (Shipped tek yönlü); açık dosyaların tutarı ve
-- durumu eski ikilinin shipments/sync'iyle sunucuya yazılabilir. Dönüşten sonra `sales-audit` ile o
-- kopyanın açık dosyaları kontrol edilir (deploy/README.md).

BEGIN IMMEDIATE;

DROP TABLE IF EXISTS temp.reset_guard;
DROP TABLE IF EXISTS temp.reset_shipment;

-- Shipped olmayan her dosyanın yerel ve uzak bağlı etiket sayısı (karar uzak etiketler silinmeden
-- önce verilir). Oturumu bulunamayan etiket yerel sayılır.
CREATE TEMP TABLE reset_shipment AS
SELECT sh.Id AS Id,
       sh.OriginReplicaId AS OriginReplicaId,
       (SELECT COUNT(*) FROM Label l LEFT JOIN StreamSession s ON s.Id = l.SessionId
         WHERE l.ShipmentId = sh.Id AND COALESCE(s.Origin, 'local') <> 'remote') AS LocalLabels,
       (SELECT COUNT(*) FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
         WHERE l.ShipmentId = sh.Id AND s.Origin = 'remote') AS RemoteLabels
  FROM Shipment sh
 WHERE sh.Status <> 'Shipped';

-- LİSTE 1 — engeller. Boş değilse betik aşağıdaki kilitte durur ve hiçbir şey değişmez.
SELECT 'bekleyen-satis-komutu' AS Engel, CommandId AS Id, Kind AS Ayrinti
  FROM PendingSalesCommand
UNION ALL
SELECT 'karisik-kargo-dosyasi', Id, 'yerel ' || LocalLabels || ', uzak ' || RemoteLabels
  FROM temp.reset_shipment
 WHERE LocalLabels > 0 AND RemoteLabels > 0
ORDER BY 1, 2;

-- LİSTE 2 — plan (yalnız sayılar).
SELECT (SELECT Mode FROM SalesSyncState WHERE Id = 1) AS Mod,
       (SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote') AS UzakYayin,
       (SELECT COUNT(*) FROM Label l JOIN StreamSession s ON s.Id = l.SessionId WHERE s.Origin = 'remote') AS UzakEtiket,
       (SELECT COUNT(*) FROM RemoteLiveSession) AS UzakCanliBaslik,
       (SELECT COUNT(*) FROM StreamSession WHERE Origin = 'local' AND Authority <> 'local') AS YereleDonecekYayin,
       (SELECT COUNT(*) FROM temp.reset_shipment WHERE LocalLabels > 0 AND RemoteLabels = 0) AS KalanDosya,
       (SELECT COUNT(*) FROM temp.reset_shipment
         WHERE LocalLabels = 0
           AND (RemoteLabels > 0
                OR (OriginReplicaId IS NOT NULL
                    AND OriginReplicaId IS NOT (SELECT ReplicaId FROM _meta WHERE Id = 1)
                    AND OriginReplicaId IS NOT (SELECT PreviousReplicaId FROM _meta WHERE Id = 1)))) AS SilinecekDosya,
       (SELECT COUNT(*) FROM PendingSalesCommand) AS BekleyenKomut;

-- KİLİT. CHECK düşerse satır eklenmez; aşağıdaki her yazım bu satırı şart koşar.
CREATE TEMP TABLE reset_guard (
    Ok              INTEGER NOT NULL,
    PendingCommands INTEGER NOT NULL CONSTRAINT bekleyen_satis_komutu_var_betik_durdu CHECK (PendingCommands = 0),
    MixedShipments  INTEGER NOT NULL CONSTRAINT karisik_kargo_dosyasi_var_betik_durdu CHECK (MixedShipments = 0)
);
INSERT INTO reset_guard (Ok, PendingCommands, MixedShipments)
SELECT 1,
       (SELECT COUNT(*) FROM PendingSalesCommand),
       (SELECT COUNT(*) FROM temp.reset_shipment WHERE LocalLabels > 0 AND RemoteLabels > 0);

-- 1) Kargo dosyaları (uzak etiketler silinmeden önce).
UPDATE Shipment
   SET OriginReplicaId = (SELECT ReplicaId FROM _meta WHERE Id = 1)
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard)
   AND Id IN (SELECT Id FROM temp.reset_shipment WHERE LocalLabels > 0 AND RemoteLabels = 0)
   AND OriginReplicaId IS NOT (SELECT ReplicaId FROM _meta WHERE Id = 1);

DELETE FROM Shipment
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard)
   AND Id IN (SELECT Id FROM temp.reset_shipment
               WHERE LocalLabels = 0
                 AND (RemoteLabels > 0
                      OR (OriginReplicaId IS NOT NULL
                          AND OriginReplicaId IS NOT (SELECT ReplicaId FROM _meta WHERE Id = 1)
                          AND OriginReplicaId IS NOT (SELECT PreviousReplicaId FROM _meta WHERE Id = 1))));

-- 2) Uzak satırlar. Etiketler oturumlardan ÖNCE: yabancı anahtar kapalıyken de öksüz kalmaz.
DELETE FROM Label
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard)
   AND SessionId IN (SELECT Id FROM StreamSession WHERE Origin = 'remote');

DELETE FROM StreamSession
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard)
   AND Origin = 'remote';

DELETE FROM RemoteLiveSession
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard);

-- 3) Kendi oturumları yerel otoriteye.
UPDATE StreamSession
   SET Authority = 'local'
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard)
   AND Origin = 'local' AND Authority <> 'local';

-- 4) Mod Off.
UPDATE SalesSyncState
   SET Mode = 0
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard)
   AND Id = 1 AND Mode <> 0;

-- 5) Müşteri toplamları (LabelRules.Contribution). Bu iki kolon hiçbir Customer tetikleyicisinin
-- listesinde değil: uygulama fonksiyonu çağrılmaz, gönderim sayacı ilerlemez.
UPDATE Customer
   SET TotalLabelsPrinted = (SELECT COUNT(*) FROM Label l
                              WHERE l.CustomerId = Customer.Id AND l.PrintedAt IS NOT NULL
                                AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0),
       TotalAmount        = (SELECT COALESCE(SUM(l.Price), 0.0) FROM Label l
                              WHERE l.CustomerId = Customer.Id AND l.PrintedAt IS NOT NULL
                                AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0)
 WHERE EXISTS (SELECT 1 FROM temp.reset_guard)
   AND (TotalLabelsPrinted IS NOT (SELECT COUNT(*) FROM Label l
                                    WHERE l.CustomerId = Customer.Id AND l.PrintedAt IS NOT NULL
                                      AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0)
        OR TotalAmount IS NOT (SELECT COALESCE(SUM(l.Price), 0.0) FROM Label l
                                WHERE l.CustomerId = Customer.Id AND l.PrintedAt IS NOT NULL
                                  AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0));

DROP TABLE IF EXISTS temp.reset_guard;
DROP TABLE IF EXISTS temp.reset_shipment;

COMMIT;
