-- R10-D02 (2026-09-15 denetimi): yerel tombstone kalıcı değildi.
-- ScrubPersonalData alanları boşaltıyor ama karar iz bırakmıyordu; silmeden
-- ÖNCE sunucudan çekilmiş, silmeden SONRA uygulanan bir form cevabı
-- (UpsertPersonFromIntake/UpsertFromIntakeForm) ya da fullname backfill'i
-- temizlenmiş alanları geri dolduruyordu. Ingest imleci tombstone'un ötesinde
-- olduğu için scrub bir daha koşmuyor, ihlal kalıcılaşıyordu (denetim probu:
-- gecikmiş intake ad+telefon+adresi, gecikmiş backfill adı diriltti).
--
-- ÇÖZÜM: silme kararı satırın kendisinde yaşar (sunucudaki Shopper.PurgedAt
-- deseninin yereli). Karar üstünlüğü UYGULANAN YAZIDA sağlanır: intake ve
-- backfill UPDATE'leri "AND PurgedAt IS NULL" koşulu taşır — yazımdan önce
-- "if purged" okumak yarışı kapatmazdı. Eski yedek geri yüklenirse imleç de
-- veriyle birlikte döner (göç 038), tombstone yeniden okunur ve damga
-- yeniden basılır (COALESCE ilk tarihi korur).
--
-- Geriye dönük doldurma: 039 öncesi temizlenmiş satırlar '[Silindi]'
-- DisplayName'inden tanınır — bu değer yalnız ScrubPersonalData tarafından
-- yazılıyor. Kesin silme anı bilinmediği için damga göç anıdır; bariyer için
-- yeterli (NULL olup olmaması karar taşır, tarih adli kayıt).

ALTER TABLE Customer ADD COLUMN PurgedAt INTEGER NULL;

UPDATE Customer SET PurgedAt = strftime('%s', 'now')
WHERE DisplayName = '[Silindi]';

UPDATE _meta SET SchemaVersion = 39 WHERE Id = 1;
