-- Kümülatif kargo PR-D (2026-05-13): Shipment outbox sync metadata.
-- WPF authoritative model: WPF lokal Shipment'ları LicenseServer'a push'lar.
-- SyncedAt NULL iken outbox kuyruğunda; ShipmentSyncService periodic tick'inde
-- push + MarkSynced.
--
-- Pattern: Payment.SyncedAt ile aynı (migration 014 payments).

ALTER TABLE Shipment ADD COLUMN SyncedAt INTEGER NULL;

-- Outbox query: SyncedAt IS NULL ve son güncellemeden bu yana değişmiş.
-- Index Status'tan bağımsız olmalı çünkü Shipped state'inde de sync gerekir.
CREATE INDEX IX_Shipment_SyncedAt ON Shipment (SyncedAt)
    WHERE SyncedAt IS NULL;

UPDATE _meta SET SchemaVersion = 19 WHERE Id = 1;
