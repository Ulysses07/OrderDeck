-- R6-04 (2026-09-14 R8 denetimi): sync imleçleri settings.json'da, tarif
-- ettikleri veri ise SQLite'ta yaşıyordu. İki dosyanın yaşam döngüsü ayrı:
-- yedek yalnız veritabanını taşır, geri yükleme yalnız veritabanını değiştirir
-- (RestoreService), lisans değişimi ikisine de dokunmaz. İmleç ile veri nesli
-- birbirinden kopunca üç ayrı bozulma çıkıyor:
--
--   1) DIŞ YÖN — eski yedek geri yüklendi, settings'teki watermark yeni
--      dünyadan kalma (denetim deneyi: watermark 21, verideki en büyük SyncSeq
--      2). Sync sonsuza dek 0 satır gönderir; belirti yok, kayıt yerinde
--      duruyor, yalnız sunucuya hiç gitmiyor.
--   2) İÇ YÖN — geri yüklenen veritabanındaki kişisel veri, ingest imleci
--      tombstone'ların (PurgedAt) ötesinde olduğu için bir daha TEMİZLENMEZ.
--      KVKK silmesi sahada geri açılmış olur.
--   3) HEDEF — lisans A'dan B'ye geçilince imleç aynen kalır; B'nin ilk
--      gönderimi/alımı imlecin altında kaldığı için atlanır.
--
-- ÇÖZÜM: imleç, tarif ettiği veriyle AYNI dosyada yaşar (CatalogStockCursor
-- ile aynı desen, göç 029). Böylece yedek/geri yükleme imleci veri nesliyle
-- BİRLİKTE taşır (1 ve 2 yapısal olarak kapanır), LicenseKey anahtarı hedef
-- eksenini bağlar (3 kapanır), Name yön+aileyi kodlar.
--
-- Tohumlama SQL'de DEĞİL servis tarafında: mevcut kurulumların imleci
-- settings.json'da ve göç oradan okuyamaz. Her servis ilk dokunuşta satırı
-- settings'teki eski değerden tohumlar, sonra eski alanı temizler — temizlik
-- şart, yoksa 038-öncesi bir yedek geri yüklendiğinde (tablo boş) bayat
-- settings değeri yeniden tohum olur ve 1/2 geri gelirdi. Satır yoksa ve eski
-- alan da boşsa tam tarama yapılır: dış yön için güvenli (N03-g emsali —
-- sunucu upsert idempotent + PurgedAt kapılı), iç yön için tombstone'ları
-- yeniden okumak tam da istenen davranış.
--
-- Kolonlar üç imleç biçimini de taşıyabilsin diye hepsi NULL'lu: Seq tekil
-- artan sayaç aileleri (customer-projection-out), UpdatedAt+LastId (zaman, id)
-- çifti aileleri (shopper-ingest-in). UpdatedAt ISO-8601 round-trip metni,
-- LastId Guid "D" metni — SQLite'ta tarih/uuid tipi yok, karşılaştırma değil
-- yalnız saklama yapıldığı için metin yeterli.

CREATE TABLE SyncCursor (
    Name       TEXT NOT NULL,
    LicenseKey TEXT NOT NULL,
    Seq        INTEGER NULL,
    UpdatedAt  TEXT NULL,
    LastId     TEXT NULL,
    PRIMARY KEY (Name, LicenseKey)
);

UPDATE _meta SET SchemaVersion = 38 WHERE Id = 1;
