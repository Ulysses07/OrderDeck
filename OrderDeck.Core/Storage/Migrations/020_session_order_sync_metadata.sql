-- Sipariş sync PR-2 (2026-05-13): StreamSession + Label outbox sync metadata.
-- WPF authoritative model — kayıtlar WPF'te oluşur, periyodik olarak
-- LicenseServer'a push'lanır. SyncedAt NULL iken outbox kuyruğunda.
--
-- Mevcut Label'lar (PR'dan önce var olan) SyncedAt=NULL ile kalır → ilk
-- service tick'inde tümü push'lanır. Eski yayın geçmişi mobile Panel'de
-- görünmeye başlar.

ALTER TABLE StreamSession ADD COLUMN SyncedAt INTEGER NULL;
ALTER TABLE Label ADD COLUMN SyncedAt INTEGER NULL;

CREATE INDEX IX_StreamSession_SyncedAt ON StreamSession (SyncedAt)
    WHERE SyncedAt IS NULL;
CREATE INDEX IX_Label_SyncedAt ON Label (SyncedAt)
    WHERE SyncedAt IS NULL;

UPDATE _meta SET SchemaVersion = 20 WHERE Id = 1;
