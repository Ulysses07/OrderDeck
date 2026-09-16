-- R11-D01 (2026-09-16 denetimi): KVKK silme kararı, yerelde satır YOKSA hiç
-- kaydedilmiyordu.
--
-- 039 tombstone'u Customer SATIRINA yazıyor (PurgedAt). Ama ingest
-- (ShopperRegistrationIngestService) PurgedAt'li bir kayıt için yerelde
-- eşleşen satır bulamazsa hiçbir şey yazmadan imleci ilerletiyordu. Karar
-- böylece hiçbir yerde durmuyor; sonradan gelen bir intake form cevabı ya da
-- chat satırı aynı kimliği SIFIRDAN, tam kişisel veriyle (ad, adres, telefon,
-- e-posta, TCKN) açıyordu. Silinen kişi yayıncının diskinde diriliyordu ve
-- imleç ilerlediği için tombstone bir daha inmiyordu.
--
-- ÇÖZÜM: kararın satırdan bağımsız bir yeri olsun. Kimlik (Platform, Username)
-- başına tek satır; Customer satırı olsun olmasın yazılır. Satır açan her yol
-- (intake, legacy form, chat/ingest insert) yazımın hemen ardından aynı
-- işlemde "kimliği tombstone'luysa temizle" ifadesini koşar — yani karar
-- ÖNCEDEN okunan bir "if purged" dalıyla değil, UYGULANAN YAZIYLA sağlanır
-- (039'un koyduğu sözleşmenin aynısı).
--
-- Satır neden hâlâ açılıyor (boş da olsa)? Çünkü bariyer o satırın kendisi:
-- PurgedAt dolu bir Customer satırı, mevcut TÜM güncelleme yollarının
-- "AND PurgedAt IS NULL" kapısına zaten takılır. Satır açmayıp yalnız bu
-- tabloya bakmak, ileride eklenecek her yeni yazma yolunun bu tabloyu
-- hatırlamasını gerektirirdi.
--
-- Username COLLATE NOCASE: eşleştirme her yerde harf duyarsız
-- (FindExistingForIntake). Platform duyarlı — orada da öyle.
--
-- Geriye dönük doldurma: bugüne kadar TEMİZLENMİŞ satırların kimlikleri.
-- Yerelde satırı hiç olmamış silmeler kurtarılamaz (imleç onların üstünden
-- çoktan geçti); o geçmiş onarımı ayrı bir karar (denetim §5) — bu göç
-- ileriye dönük bariyeri kurar.

CREATE TABLE CustomerPurgeTombstone (
    Platform TEXT    NOT NULL,
    Username TEXT    NOT NULL COLLATE NOCASE,
    PurgedAt INTEGER NOT NULL,
    PRIMARY KEY (Platform, Username)
);

INSERT OR IGNORE INTO CustomerPurgeTombstone (Platform, Username, PurgedAt)
SELECT Platform, Username, PurgedAt
FROM Customer
WHERE PurgedAt IS NOT NULL;

UPDATE _meta SET SchemaVersion = 42 WHERE Id = 1;
