-- Backup buyers as first-class Labels (rev 2 of the backup feature).
--
-- Original design (009/010): backups lived in a separate LabelBackup table
-- as metadata only. They never got printed, only "promoted" to real labels
-- on cancellation. The auctioneer asked for physical-paper backup tickets
-- printed during the live so they can be set aside and stuck on the goods
-- if the original buyer cancels next-day.
--
-- New design: a backup is just a Label row with:
--   ParentLabelId   = the original Label.Id this is a standby for
--   IsTentativeBackup = 1 while the sale isn't confirmed (excluded from
--                       revenue + customer aggregates), flipped to 0 when
--                       the operator promotes it on cancellation.
-- Physical print pipeline is unchanged — Y badge condition (IsBackupPromoted)
-- still drives the corner stamp.
--
-- LabelBackup table is dropped: backups now live as Labels with the parent
-- pointer. Existing rows (none in production yet — feature shipped today) are
-- discarded.

ALTER TABLE Label ADD COLUMN ParentLabelId      TEXT    NULL;
ALTER TABLE Label ADD COLUMN IsTentativeBackup  INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS IX_Label_ParentLabelId ON Label(ParentLabelId);

DROP TABLE IF EXISTS LabelBackup;

UPDATE _meta SET SchemaVersion = 11 WHERE Id = 1;
