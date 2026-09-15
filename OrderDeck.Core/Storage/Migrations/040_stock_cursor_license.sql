-- R10-D03 (2026-09-15 denetimi): stok replikası ve imleci hedef lisansa
-- bağlı değildi. CatalogStockCursor tek global satır (Id=1); A lisansıyla
-- ilerletilmiş imleç, hedef B'ye geçince de aynen gönderiliyordu. Sunucu
-- "> since" filtresini DOĞRU uyguladığı için B'nin imleçten eski meşru stok
-- geçmişi hiç çekilmiyor, A'nın bayat bakiye satırları da yerelde B'ninmiş
-- gibi kalıyordu (denetim probu: B'nin 7 adetlik ürünü iki normal turda da
-- gelmedi).
--
-- ÇÖZÜM: imleç satırı sahibini taşır. StockBalanceRepository.EnsureTarget
-- her turun başında sahibi mevcut lisans anahtarıyla karşılaştırır; farklıysa
-- (NULL dahil) replika + imleç tek transaction'da sıfırlanır ve sahip yazılır
-- — kontrollü tam yeniden kurulum. NULL bilerek "bilinmiyor" sayılır:
-- 040 öncesi imlecin hangi lisansla ilerletildiği kayıtlı değil, tam da
-- D03'ün tarif ettiği belirsizlik; ilk turda yeniden kurulum ucuz (replika
-- zaten sunucudan türetilebilir), yanlış sahibe güvenmekse sessiz veri kaybı.

ALTER TABLE CatalogStockCursor ADD COLUMN LicenseKey TEXT NULL;

UPDATE _meta SET SchemaVersion = 40 WHERE Id = 1;
