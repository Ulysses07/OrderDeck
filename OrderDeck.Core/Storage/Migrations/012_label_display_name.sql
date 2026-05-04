-- Pretty-name column for the queue UI. YouTube identifies users by an opaque
-- channel ID (UCxxx...) which we store as Username for stable customer linking,
-- but the operator wants to see the human-readable display name (e.g. "Ayşe
-- Yılmaz") on the queue row. Other platforms (IG/TT/FB) usually have a
-- handle for Username AND a separate display name; capturing both keeps the
-- behaviour consistent across platforms.
--
-- Nullable: legacy rows + edge cases where the chat payload omits a display
-- name fall back to Username in the UI (LabelViewModel.Display).

ALTER TABLE Label ADD COLUMN DisplayName TEXT NULL;

UPDATE _meta SET SchemaVersion = 12 WHERE Id = 1;
