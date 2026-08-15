-- Sipariş satırı artık hangi katalog ürününe/varyantına ait olduğunu taşıyor.
-- Bu iki kolon olmadan sunucu stok düşemez: sunucu yalnız Code metnini görüyordu
-- ve o metin yayın kodu, ürün kimliği değil.
--
-- İkisi de NULL kalabilir ve bu MEŞRU bir durum:
--   * Bilinmeyen kod / kod yokken yazılan satır → ikisi de NULL, stok hareketi yok.
--   * Eksensiz ürün (kolye) → ProductId dolu, ProductVariantId NULL; stok ürün
--     düzeyinden düşer (kabul kriteri 11).
-- Kimlikler sunucudaki GUID'ler, TEXT "N" biçiminde (32 hane, tiresiz).
-- FK yok: bunlar SUNUCU kimlikleri, yerel katalog replikası ise her senkronda
-- baştan yazılıyor — FK kursak, sunucuda arşivlenen bir ürün eski sipariş
-- satırlarını kilitlerdi.
ALTER TABLE Label ADD COLUMN ProductId TEXT;
ALTER TABLE Label ADD COLUMN ProductVariantId TEXT;

UPDATE _meta SET SchemaVersion = 28 WHERE Id = 1;
