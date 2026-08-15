-- Yayın kodu replikaya iniyor. Operatör kod kutusuna STOK kodunu değil YAYIN
-- kodunu yazar; yayın kodu ürün + SATICI EKSENİ DEĞERİ düzeyindedir, yani tek
-- ürünün birden çok kodu olabilir ("ATEŞ" = Elbise/Siyah, "BUZ" = Elbise/Beyaz).
--
-- Sunucudaki çekme ucu kodları ürünün içinde gömülü dizi olarak gönderiyor ve
-- dizide Id YOK (bkz. CatalogBroadcastCodeDto). Bu yüzden burada da yapay
-- birincil anahtar tanımlamıyoruz; satırlar her senkron turunda tamamen
-- siliniyor ve baştan yazılıyor, kimliğe ihtiyaç duyan tek şey yok.
CREATE TABLE CatalogBroadcastCode (
    ProductId       TEXT NOT NULL,  -- CatalogProduct.Id; FK bilerek yok (bkz. 025).
    -- Satıcı ekseninin değeri ("Siyah"). Ürünün satıcı ekseni yoksa NULL —
    -- o zaman kod ürünün tamamını gösterir.
    SellerAxisValue TEXT,
    -- Operatörün yazdığı hâl; kartta/loglarda bunu gösteriyoruz.
    Code            TEXT NOT NULL,
    -- Aramanın karşılaştırıldığı biçim; sunucu SearchNormalizer.Normalize ile
    -- üretip gönderiyor, yerelde YENİDEN HESAPLANMAZ. Sebep: normalize kuralı
    -- ileride değişirse sunucu ile yerel sessizce ayrışmasın — tek doğru kaynak
    -- sunucu.
    CodeNormalized  TEXT NOT NULL,
    CreatedAt       INTEGER NOT NULL,  -- unix saniye
    -- Sunucunun gönderdiği dizideki konum. Sunucu en yeni kodu başa koyuyor
    -- (OrderByDescending CreatedAt, ThenByDescending Id); sıra taşınıyor,
    -- yerelde yeniden hesaplanmıyor (025'teki SortOrder kuralının aynısı).
    SortOrder       INTEGER NOT NULL
);

-- Kod kutusundaki arama: WHERE CodeNormalized = ? → indeks kullanılır.
-- UNIQUE DEĞİL, 025'teki gerekçeyle: replika kendisine verilen veriyi
-- reddetmemeli; benzersizliği sunucu (LicenseId, CodeNormalized) üstünde
-- zaten uyguluyor.
CREATE INDEX IX_CatalogBroadcastCode_CodeNormalized ON CatalogBroadcastCode(CodeNormalized);
CREATE INDEX IX_CatalogBroadcastCode_ProductId ON CatalogBroadcastCode(ProductId);

-- CatalogVariant'tan üç ölü kolon çıkıyor: Axis1Code, Axis2Code, VariantCode.
-- Varyant kodu kavramı sunucudan tamamen kaldırıldı (bkz. 920c40c); replika
-- bugün VariantCode'a ürünün stok kodunu yazan geçici bir uyum kalkanıyla
-- besleniyor. Kolonlar gidince o kalkanın da bir işi kalmıyor.
--
-- Neden DROP COLUMN değil, yeniden kurma: SQLite'ın ALTER TABLE DROP COLUMN'u
-- 3.35+ gerektiriyor ve indeks/ifade bağımlılığı olan kolonlarda hata veriyor.
-- CREATE-yeni / INSERT-SELECT / DROP / RENAME kalıbı sürümden bağımsız çalışır
-- ve ÖNBELLEĞİ KORUR: kullanıcı bu göçten sonra bir sonraki senkrona kadar
-- kataloğu görmeye devam eder.
CREATE TABLE CatalogVariant_new (
    Id          TEXT PRIMARY KEY,
    ProductId   TEXT NOT NULL,
    Axis1Value  TEXT,
    Axis2Value  TEXT,
    Barcode     TEXT,
    IsActive    INTEGER NOT NULL,
    SortOrder   INTEGER NOT NULL
);

INSERT INTO CatalogVariant_new (Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder)
SELECT Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder FROM CatalogVariant;

DROP TABLE CatalogVariant;
ALTER TABLE CatalogVariant_new RENAME TO CatalogVariant;

-- İndeksi yeniden kurmak ZORUNLU: DROP TABLE indeksi de düşürdü.
CREATE INDEX IX_CatalogVariant_ProductId ON CatalogVariant(ProductId);

UPDATE _meta SET SchemaVersion = 27 WHERE Id = 1;
