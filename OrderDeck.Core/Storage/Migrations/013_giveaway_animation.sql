-- Phase 1 (animation library): per-giveaway animation id.
-- Default 'wheel' means existing rows + future rows where the operator
-- doesn't override fall back to the original spinning wheel — zero
-- regression for existing giveaways.

ALTER TABLE Giveaway ADD COLUMN AnimationId TEXT NOT NULL DEFAULT 'wheel';

UPDATE _meta SET SchemaVersion = 13 WHERE Id = 1;
