-- Arayüz Faz 1 (spec §9.1): sağ paneldeki ürün kartı ad, fotoğraf ve beden
-- başına adet gösteriyor; uygulamada bunların karşılığı yoktu.
--
-- Bilinçli sınırlar:
--  * Bu tablolar YALNIZ yerel SQLite'ta. Sunucuda karşılığı yok, senkron yok.
--    Sebep: PostgreSQL göçü arayüz yenilemesi bitmeden başlamayacak; yerel
--    SQLite göçten etkilenmiyor, dolayısıyla bu iş iki kez yapılmayacak.
--  * Fiyat kolonu YOK. Karttaki fiyat, hero'daki aktif fiyat girişinin
--    aynısı; ürüne ayrı fiyat alanı eklemek yeni bir kavram olurdu.
--  * Quantity düz bir sayı; hareket defteri DEĞİL. Etiket kuyruğa girince
--    stok düşmüyor — operatör adetleri elle giriyor. Otomatik düşüş ve
--    hareket tabanlı defter stok projesine ait (bkz. stok spec'i).
--  * PhotoPath, %LOCALAPPDATA%\OrderDeck\products\ altındaki dosyaya GÖRECELİ
--    yol (uygulamanın yerleşik veri klasörü kuralı; bkz.
--    AnimationHoverPreviewService.cs:142). Mutlak yol yazılmıyor ki kullanıcı
--    profili taşınınca kayıt kırılmasın.
CREATE TABLE Product (
    Code      TEXT PRIMARY KEY COLLATE NOCASE,
    Name      TEXT NOT NULL,
    PhotoPath TEXT,
    UpdatedAt INTEGER NOT NULL
);

CREATE TABLE ProductSize (
    Code      TEXT NOT NULL COLLATE NOCASE,
    Size      TEXT NOT NULL COLLATE NOCASE,
    Quantity  INTEGER NOT NULL,
    SortOrder INTEGER NOT NULL,
    PRIMARY KEY (Code, Size),
    FOREIGN KEY (Code) REFERENCES Product(Code) ON DELETE CASCADE
);

UPDATE _meta SET SchemaVersion = 24 WHERE Id = 1;
