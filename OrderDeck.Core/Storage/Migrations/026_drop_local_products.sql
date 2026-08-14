-- Stok Faz 1b: ürün tanımının sahibi artık SUNUCU KATALOĞU (025'teki
-- CatalogProduct/CatalogVariant replikası). Yerel Product/ProductSize
-- tabloları 024'te "sunucuda karşılığı yok, senkron yok" gerekçesiyle
-- açılmıştı; o gerekçe ortadan kalktı.
--
-- Veri taşınmıyor. Sebep: 2026-08-13'te sahadaki kurulumlarda bu tablolar
-- BOŞ olduğu doğrulandı — ürün kartı özelliği kullanılmamış. Taşıma kodu
-- yazmak, hiç var olmayan bir veri için kalıcı bakım yükü olurdu.
--
-- Çocuk tablo önce düşüyor; bu bir ALIŞKANLIK, zorunluluk DEĞİL. Ölçüldü:
-- 024'teki FK "ON DELETE CASCADE" olduğu ve DROP TABLE örtük silme yaptığı
-- için iki sıra da (satır varken ve "Foreign Keys=true" ile) sorunsuz koşuyor.
-- Sırayı yine de böyle bırakıyoruz ki gelecekte cascade'siz bir FK ile
-- karşılaşan biri kalıbı bozmak zorunda kalmasın.
DROP TABLE IF EXISTS ProductSize;
DROP TABLE IF EXISTS Product;

UPDATE _meta SET SchemaVersion = 26 WHERE Id = 1;
