-- 029: Sunucu stok defterinin yerel bakiye replikası.
--
-- Sunucu hareket (StockMovement) tutar, bakiye SAKLANMAZ — SUM ile hesaplanır.
-- Bu tablo o hesabın anlık görüntüsüdür: her satır bir (ürün, varyant)
-- anahtarının sunucudaki TOPLAM bakiyesi. İstemci asla toplama yapmaz,
-- gelen değeri olduğu gibi yazar (sil-ve-ekle).
--
-- UNIQUE kısıtı BİLEREK YOK: SQLite'ta UNIQUE iki NULL'u eşit saymaz, yani
-- UNIQUE(ProductId, ProductVariantId) ürün-seviyesi (varyantsız) satırları
-- tekilleştiremezdi. Tekillik StockBalanceRepository.ApplyPage içinde
-- "DELETE ... WHERE ProductVariantId IS @variantId" ile (= değil, IS)
-- elle sağlanıyor.
--
-- Quantity NEGATİF OLABİLİR: stok bitince satış engellenmiyor, bakiye eksiye
-- düşüyor ve arayüzde vurgulanıyor. CHECK koymak canlı yayını kilitlerdi.
CREATE TABLE IF NOT EXISTS CatalogStockBalance (
    ProductId        TEXT    NOT NULL,
    ProductVariantId TEXT    NULL,
    Quantity         INTEGER NOT NULL
);

-- Ürün kartı her açılışta tek ürünün satırlarını çekiyor; erişim deseni bu.
CREATE INDEX IF NOT EXISTS IX_CatalogStockBalance_ProductId
    ON CatalogStockBalance(ProductId);

-- Çekme imleci. Tek satır (Id = 1) — CHECK bunu şemada zorluyor.
--
-- İmleç BİLEŞİK: tek sipariş senkronunda doğan hareketlerin hepsi aynı
-- CreatedAt'i taşır, sadece zamanla imleç tutulsa "take" sınırı eşitlik
-- kümesinin ortasından keser ve kalan satırlar sonsuza dek atlanırdı.
--
-- CursorCreatedAt ISO-8601 "O" biçiminde TEXT (ofsetli, round-trip);
-- CursorId GUID'in "N" biçimi — repodaki diğer GUID kolonlarıyla aynı.
CREATE TABLE IF NOT EXISTS CatalogStockCursor (
    Id              INTEGER PRIMARY KEY CHECK (Id = 1),
    CursorCreatedAt TEXT NOT NULL,
    CursorId        TEXT NOT NULL
);

-- Başlangıç imleci: zamanın başı + boş GUID. Sunucu "> since" karşılaştırması
-- yaptığı için bu, "her şeyi baştan çek" demektir.
INSERT OR IGNORE INTO CatalogStockCursor (Id, CursorCreatedAt, CursorId)
VALUES (1, '0001-01-01T00:00:00.0000000+00:00', '00000000000000000000000000000000');

UPDATE _meta SET SchemaVersion = 29 WHERE Id = 1;
