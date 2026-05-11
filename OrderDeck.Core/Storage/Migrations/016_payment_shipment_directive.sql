-- Kargo PR C: Payment kaydında kargo yönlendirme bilgisi.
-- Müşteri ürün toplamını ödedi ama kargo ücretini eklemediyse vendor
-- karar verir: kargosu beklesin mi (cumulative shipping bekle) yoksa
-- kargo şirketi alıcıdan tahsil etsin mi (RecipientPays).
--
-- Default 'Normal' = standart akış (kargo dahil veya threshold aşıldı,
-- karar gerektirmiyor). Mevcut tüm Payment satırları geriye uyumlu
-- olarak Normal alır.

ALTER TABLE Payment ADD COLUMN ShipmentDirective TEXT NOT NULL DEFAULT 'Normal';

-- Hold ve RecipientPays satırları için kargocu listesi sorgusu gerekecek
-- (PR D'de). Composite index status filter'la birlikte.
CREATE INDEX IX_Payment_ShipmentDirective ON Payment (ShipmentDirective, CreatedAt);

UPDATE _meta SET SchemaVersion = 16 WHERE Id = 1;
