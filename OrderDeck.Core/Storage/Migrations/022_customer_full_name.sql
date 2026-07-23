-- Kayıt formundaki gerçek Ad Soyad'ı ayrı sakla. Şimdiye kadar sadece
-- DisplayName'e fallback olarak yazılıyordu; chat'ten gelen satırlarda
-- DisplayName platform takma adı olduğu için (COALESCE korur) gerçek isim
-- kayboluyordu. Ayrı kolon: intake her zaman buraya yazar, UI "Ad Soyad"da
-- bunu gösterir (yoksa DisplayName'e düşer).
ALTER TABLE Customer ADD COLUMN FullName TEXT;

UPDATE _meta SET SchemaVersion = 22 WHERE Id = 1;
