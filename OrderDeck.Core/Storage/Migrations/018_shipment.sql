-- Kargo eşik PR-B: Shipment entity persistence.
-- Spec: docs/superpowers/specs/2026-05-12-cumulative-shipping-trigger-design.md
--
-- Bir Shipment, müşterinin biriken kargo dosyasıdır. Müşteri yayında alım
-- yaptıkça mevcut açık Shipment'a Label'lar eklenir; vendor "Evet kargolansın"
-- dediğinde Status=Shipped'e döner ve kapanır. Sonraki yayında yeni Shipment
-- (Status=Pending) açılır.
--
-- Status: Pending | Held | RecipientPays | Shipped
--   Pending       — yeni oluştu, henüz vendor karar vermedi
--   Held          — vendor "beklet" dedi, kümülatif eşik bekliyor
--   RecipientPays — vendor "alıcı ödemeli" dedi (sticky kargo türü)
--   Shipped       — kargocuya verildi, kapalı (terminal state)
--
-- CumulativeAmount denormalize alan: bağlı Label'ların Price toplamı.
-- Her Label attach/detach sonrası ShipmentRepository update edilir; PR-C
-- ShipmentService bu invariant'ı korur.

CREATE TABLE Shipment (
    Id               TEXT PRIMARY KEY,
    CustomerId       TEXT NOT NULL,
    Status           TEXT NOT NULL DEFAULT 'Pending',
    CreatedAt        INTEGER NOT NULL,
    HeldAt           INTEGER NULL,
    ShippedAt        INTEGER NULL,
    CumulativeAmount REAL NOT NULL DEFAULT 0
);

-- "Bu müşterinin açık Shipment'ı var mı?" sorgusu için (PR-C trigger akışı).
CREATE INDEX IX_Shipment_Customer_Status ON Shipment (CustomerId, Status);

-- Mobile Panel "Bekleyen Kargolar" / "Alıcı Ödemeli" tab'ları için (PR-D).
CREATE INDEX IX_Shipment_Status_CreatedAt ON Shipment (Status, CreatedAt);

-- Label → Shipment FK. Nullable çünkü mevcut Label'lar (PR-B öncesi) Shipment'a
-- bağlı değil; PR-C migration logic'iyle açık Label'lar yeni Shipment'lara
-- atanır. Yeni Label oluşturma akışı PR-C'de service üzerinden Shipment'a
-- attach eder.
ALTER TABLE Label ADD COLUMN ShipmentId TEXT NULL;

CREATE INDEX IX_Label_Shipment ON Label (ShipmentId);

UPDATE _meta SET SchemaVersion = 18 WHERE Id = 1;
