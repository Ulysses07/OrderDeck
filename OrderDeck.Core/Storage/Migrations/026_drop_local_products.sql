-- Stok Faz 1b: ürün tanımının sahibi artık SUNUCU KATALOĞU (025'teki
-- CatalogProduct/CatalogVariant replikası). Yerel Product/ProductSize
-- tabloları 024'te "sunucuda karşılığı yok, senkron yok" gerekçesiyle
-- açılmıştı; o gerekçe ortadan kalktı.
--
-- Veri taşınmıyor. Sebep: 2026-08-13'te sahadaki kurulumlarda bu tablolar
-- BOŞ olduğu doğrulandı — ürün kartı özelliği kullanılmamış. Taşıma kodu
-- yazmak, hiç var olmayan bir veri için kalıcı bakım yükü olurdu.
--
-- Sıra önemli: ProductSize'ın Product'a FK'si var, önce çocuk düşüyor.
DROP TABLE IF EXISTS ProductSize;
DROP TABLE IF EXISTS Product;

UPDATE _meta SET SchemaVersion = 26 WHERE Id = 1;
