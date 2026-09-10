-- F05 (2026-09-09 denetimi): outbox onayında kayıp güncelleme koruması.
--
-- Sync servisleri partiyi push'layıp dönüşte MarkSynced ile SyncedAt yazar.
-- Push UÇUŞTAYKEN operatör satırı değiştirirse (iptal, yazdırma, fiyat)
-- mutasyon SyncedAt'i NULL'a çeker ama hemen ardından gelen onay üstüne
-- yazar → satır "senkronize" görünür, değişiklik sunucuya HİÇ gitmez
-- (denetim kanıtı: OUTBOX_ACK_RACE — sentCancelled=False, localCancelled=True,
-- pending=0). Label'da CancelledAt kaybı stok iadesini de kaybettirir.
--
-- Çözüm: her mutasyon Revision'ı artırır; MarkSynced yalnız okunan Revision
-- hâlâ aynıysa yazar (compare-and-set). Kaybeden onay 0 satır günceller,
-- satır bekleyen kalır ve bir sonraki tick'te GÜNCEL hâliyle tekrar gider.
--
-- Payment'a gerek yok: satırlar insert-sonrası değişmiyor (SyncedAt'i NULL'a
-- çeken tek bir mutasyon yok), yarışacak güncelleme de yok.

ALTER TABLE Label         ADD COLUMN Revision INTEGER NOT NULL DEFAULT 0;
ALTER TABLE StreamSession ADD COLUMN Revision INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Shipment      ADD COLUMN Revision INTEGER NOT NULL DEFAULT 0;

UPDATE _meta SET SchemaVersion = 32 WHERE Id = 1;
