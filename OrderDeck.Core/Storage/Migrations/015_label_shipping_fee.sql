-- Kargo PR B: Label entity'ye IsShippingFee bayrağı. Kargo ücreti label'ı
-- normal sale label'ı gibi davranır (queue, print, customer aggregate) ama
-- bayraklı olarak ayrılır:
--   * PR C: dekont eşleştirme bu satırı dahil ederek "kargo dahil mi?" check
--   * PR E: print template'da farklı render + Excel raporda ayrı sütun
--   * Şu an (PR B): sadece persistence, davranış değişikliği yok

ALTER TABLE Label ADD COLUMN IsShippingFee INTEGER NOT NULL DEFAULT 0;

-- Index gerekmiyor — kargo label'ları toplam içinde küçük oran, ayrı sorgu
-- gerekmiyorsa filter PrintedAt/CancelledAt index'leri zaten kapsıyor.

UPDATE _meta SET SchemaVersion = 15 WHERE Id = 1;
