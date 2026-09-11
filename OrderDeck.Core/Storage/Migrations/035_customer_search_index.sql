-- R3-04 (2026-09-11 denetimi): müşteri araması her tuş vuruşunda TÜM Customer
-- tablosunu belleğe alıyordu (CustomerRepository.Search -> GetAll()).
-- Ölçüm (Debug, denetim raporu bölüm 21): 1.000 satır 34 ms / 0,9 MB;
-- 10.000 satır 281 ms / 9,4 MB; 50.000 satır 1.451 ms / 45 MB.
-- Maliyet taramada değil, 50.000 Customer nesnesini .NET'e materialize etmekte.
--
-- ÇÖZÜM: eşleştirmeyi SQL'e indir. Bunun ön şartı, eşleştirmenin karşılaştırdığı
-- METNİN kolonda hazır durması — çünkü kural Türkçe'ye özel:
-- CustomerSearch.Fold() i/İ/ı/I'yı tek harfe indirip ToLowerInvariant uyguluyor,
-- SQLite'ın lower()'ı ise YALNIZ ASCII küçültüyor ("Ş" olduğu gibi kalır).
-- Katlamayı SQL ifadesiyle taklit etmek sessizce ayrışırdı; bu yüzden katlama
-- C#'ta yapılıp SONUCU kolona yazılıyor (od_search_key / od_phone_key,
-- bkz. SqliteSearchFunctions). SQL yalnızca iki katlanmış metni INSTR ile
-- karşılaştırıyor — bu, Contains(..., Ordinal) ile bayt bayt aynı iş.
--
-- ANAHTARLARI KİM GÜNCEL TUTUYOR: tetikleyiciler, repo metotları değil.
-- Customer'a yazan dokuz ayrı ifade var; her birine anahtar hesabı eklemek
-- bugünü çözer, onuncu yazma yolu eklendiği gün arama SESSİZCE eskir (kayıt
-- durur, sadece aranamaz olur). Tetikleyiciyle bu sınıf yapısal olarak imkânsız.
--
-- FTS5 + trigram NEDEN: tam tarama 500.000 satırda ~135 ms; trigram indeksiyle
-- nadir terim ~1 ms. trigram (SQLite 3.34+, biz 3.50.3'teyiz) 3 harflik
-- parçaları indekslediği için ORTADAN eşleşmeyi (substring) de hızlandırır —
-- klasik ters indeks yalnız ön-ek bulur, bizim sözleşmemiz ise Contains.
-- case_sensitive 1: metin C# tarafında zaten katlanmış geliyor, FTS5'in kendi
-- Unicode küçültmesi devreye girerse Türkçe kuralımızdan sapar.
--
-- LastSeenAt indeksi: sonuçlar "en son görülen" sırasında. İndeks sayesinde
-- YOĞUN terimlerde (binlerce eşleşme) en yeniden geriye yürüyüp ilk 50'de
-- durulabiliyor — ölçümde 0,2-0,6 ms. FTS ise NADİR terimlerde kazanıyor.
-- İkisi zıt yönde iyi olduğu için Search() ikisini sırayla kullanıyor.
--
-- SIRA ÖNEMLİ: kolonları ekle -> doldur -> FTS'i kur -> rebuild -> tetikleyici.
-- Tetikleyiciler doldurmadan ÖNCE kurulsaydı, toplu UPDATE her satır için
-- ayrıca FTS'e yazar (yavaş) ya da rebuild ile çakışırdı.

ALTER TABLE Customer ADD COLUMN SearchKey TEXT;
ALTER TABLE Customer ADD COLUMN PhoneKey  TEXT;

UPDATE Customer
   SET SearchKey = od_search_key(Username, DisplayName, FullName),
       PhoneKey  = od_phone_key(Phone);

CREATE INDEX IX_Customer_LastSeenAt ON Customer(LastSeenAt DESC);

-- Harici içerik (external content): metnin ikinci bir kopyasını TUTMAZ,
-- yalnız indeksi tutar. 500.000 satırda +46 MB (150 -> 196 MB).
CREATE VIRTUAL TABLE CustomerFts USING fts5(
    SearchKey,
    PhoneKey,
    content='Customer',
    content_rowid='rowid',
    tokenize="trigram case_sensitive 1"
);

INSERT INTO CustomerFts(CustomerFts) VALUES('rebuild');

CREATE TRIGGER Customer_search_ai AFTER INSERT ON Customer BEGIN
    UPDATE Customer
       SET SearchKey = od_search_key(new.Username, new.DisplayName, new.FullName),
           PhoneKey  = od_phone_key(new.Phone)
     WHERE rowid = new.rowid;
    INSERT INTO CustomerFts(rowid, SearchKey, PhoneKey)
    VALUES (new.rowid,
            od_search_key(new.Username, new.DisplayName, new.FullName),
            od_phone_key(new.Phone));
END;

-- "UPDATE OF <kolonlar>" iki işe birden yarıyor:
--  1) LastSeenAt/TotalAmount gibi sık güncellenen kolonlar tetikleyiciyi
--     boşuna çalıştırmıyor (etiket basımı sıcak yol).
--  2) Tetikleyicinin KENDİ yaptığı "SET SearchKey=..." güncellemesi bu listede
--     olmadığı için kendini yeniden tetiklemiyor — özyineleme yok.
CREATE TRIGGER Customer_search_au
AFTER UPDATE OF Username, DisplayName, FullName, Phone ON Customer BEGIN
    UPDATE Customer
       SET SearchKey = od_search_key(new.Username, new.DisplayName, new.FullName),
           PhoneKey  = od_phone_key(new.Phone)
     WHERE rowid = new.rowid;
    -- Harici içerikte silme, İNDEKSLENMİŞ ESKİ değerlerle yapılmak zorunda;
    -- old.* satırın güncelleme öncesi hâli olduğu için tam olarak o.
    INSERT INTO CustomerFts(CustomerFts, rowid, SearchKey, PhoneKey)
    VALUES ('delete', old.rowid, old.SearchKey, old.PhoneKey);
    INSERT INTO CustomerFts(rowid, SearchKey, PhoneKey)
    VALUES (new.rowid,
            od_search_key(new.Username, new.DisplayName, new.FullName),
            od_phone_key(new.Phone));
END;

CREATE TRIGGER Customer_search_ad AFTER DELETE ON Customer BEGIN
    INSERT INTO CustomerFts(CustomerFts, rowid, SearchKey, PhoneKey)
    VALUES ('delete', old.rowid, old.SearchKey, old.PhoneKey);
END;

UPDATE _meta SET SchemaVersion = 35 WHERE Id = 1;
