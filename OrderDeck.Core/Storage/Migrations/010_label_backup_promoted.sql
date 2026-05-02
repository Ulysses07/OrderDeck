-- Marks a Label as having been created via PromoteBackupToLabel, so the
-- printed sticker can show a small "Y" (yedek = backup) in the corner. This
-- is a visual-only flag — revenue and aggregate calculations stay identical.
-- 0 = normal label (default), 1 = promoted from a LabelBackup row.

ALTER TABLE Label ADD COLUMN IsBackupPromoted INTEGER NOT NULL DEFAULT 0;

UPDATE _meta SET SchemaVersion = 10 WHERE Id = 1;
