-- Stok Faz 1b: katalog artık sunucuda. Bu üç tablo, sunucudaki kataloğun
-- SALT-OKUNUR replikası — WPF hiçbir zaman buraya kullanıcı verisi yazmaz,
-- CatalogSyncService her turda hepsini baştan yazar.
--
-- Neden tam baştan yazma: sunucudaki çekme ucu TAM ANLIK GÖRÜNTÜ döndürüyor.
-- `after` bir değişim imleci değil, birincil anahtar üstünde keyset sayfalama
-- imleci; silinen/arşivlenen ürün yanıtta hiç görünmez ve mezar taşı da yok.
-- Birleştirme yapsaydık silinen ürün WPF'te hayalet satır olarak kalır ve
-- yayında yanlış ürüne eşleşirdi.
--
-- Kimlikler sunucudaki GUID'ler, TEXT "N" biçiminde (32 hane, tiresiz) —
-- CustomerRepository'deki kural. Zaman damgaları unix SANİYE (IClock ile aynı
-- birim). decimal -> REAL (Label.Price ile aynı, bkz. 002).

CREATE TABLE CatalogProduct (
    Id             TEXT PRIMARY KEY,
    CategoryId     TEXT,
    -- Kanonik kod: sunucu bunu SearchNormalizer.Normalize ile yazıyor.
    Code           TEXT NOT NULL,
    -- Aranan iğnenin karşılaştırılacağı biçim. Sunucudaki Code zaten kanonik
    -- olduğu için ikisi bugün aynı; kolon yine de ayrı duruyor ki eşleştirme
    -- kuralı sunucudaki yazma kuralından bağımsız olarak burada okunabilsin.
    CodeNormalized TEXT NOT NULL,
    Name           TEXT NOT NULL,
    DefaultPrice   REAL NOT NULL,
    ShelfLocation  TEXT,
    Axis1Name      TEXT,
    Axis1Role      INTEGER,
    Axis2Name      TEXT,
    Axis2Role      INTEGER,
    -- R2 nesne anahtarı; fotoğraf önbelleğinin anahtarı da bu. URL SAKLANMAZ
    -- (imzalı ve 5 dakika ömürlü).
    CoverPhotoKey  TEXT,
    UpdatedAt      INTEGER NOT NULL
);

-- UNIQUE DEĞİL, bilerek: replika kendisine verilen veriyi reddetmemeli.
-- Benzersizliği sunucu (LicenseId, Code) üstünde zaten uyguluyor; burada
-- unique olsaydı beklenmedik bir çakışma bütün senkron transaction'ını
-- düşürür ve replika sessizce eskirdi.
--
-- Bu indeks yalnız EŞİTLİK aramasını hızlandırır: WHERE CodeNormalized = ?
-- → SEARCH ... USING INDEX. LIKE aramaları (WHERE CodeNormalized LIKE 'A%'
-- veya WHERE '…yorum…' LIKE '%'||CodeNormalized||'%') indeksi KULLANMAZ —
-- SQLite LIKE optimizasyonu BINARY collation'da çalışmaz (NOCASE ister).
-- Bu nedenle yorum eşleştirmesi metni token'lara bölüp token başına eşitlik
-- araması yapmalı. Katalog lisans başına yüzler mertebesinde olduğu için
-- tam tarama felaket değil, ama tercih bilinçli olsun; LIKE '%...%' yazan
-- biri sessizce tam tarama yapar.
CREATE INDEX IX_CatalogProduct_CodeNormalized ON CatalogProduct(CodeNormalized);

-- FK YOK, bilerek: SqliteConnectionFactory ve InMemorySqlite ikisi de
-- ForeignKeys=true kuruyor (024'teki ProductSize cascade'i çalışıyor).
-- Buradaki FK yokluğu aynı UNIQUE yokluğuyla aynı mantık — tek bozuk sayfa
-- bütün senkron transaction'ını düşürmesin. Bedeli: cascade yok, bu yüzden
-- Replace varyantları AÇIKÇA silmek zorunda; aksi hâlde kısmi tazeleme
-- öksüz satır üretir.
CREATE TABLE CatalogVariant (
    Id          TEXT PRIMARY KEY,
    ProductId   TEXT NOT NULL,  -- CatalogProduct.Id; FK bilerek yok, yukarıya bak.
    Axis1Value  TEXT,
    Axis1Code   TEXT,
    Axis2Value  TEXT,
    Axis2Code   TEXT,
    VariantCode TEXT NOT NULL,
    Barcode     TEXT,
    IsActive    INTEGER NOT NULL,
    -- Sunucunun JSON dizisindeki konum (0-tabanlı dizi indeksi). Sunucu
    -- varyantları OrderBy(v => v.VariantCode) ile sıralayıp dizi olarak
    -- gönderiyor; CatalogReplicaRepository bunu Select((v, i) => ... i) ile
    -- doldurur. Neden yerelde yeniden sıralamıyoruz: sunucu sırası SQL Server
    -- collation'ında üretiliyor, SQLite'ın ordinal sıralaması farklı düşebilir
    -- — o yüzden sıra taşınıyor, yeniden hesaplanmıyor.
    SortOrder   INTEGER NOT NULL
);

CREATE INDEX IX_CatalogVariant_ProductId ON CatalogVariant(ProductId);

CREATE TABLE CatalogCategory (
    Id               TEXT PRIMARY KEY,
    ParentCategoryId TEXT,
    Name             TEXT NOT NULL,
    -- Id tabanlı yol (/{id:N}/...); sıralaması ağacı ata-önce diziyor.
    Path             TEXT NOT NULL,
    SortOrder        INTEGER NOT NULL,
    IsActive         INTEGER NOT NULL
);

UPDATE _meta SET SchemaVersion = 25 WHERE Id = 1;
