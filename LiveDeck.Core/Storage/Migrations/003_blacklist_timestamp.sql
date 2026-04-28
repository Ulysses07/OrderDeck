-- Phase 2a: track when a customer was blacklisted.
ALTER TABLE Customer ADD COLUMN BlacklistedAt INTEGER;

UPDATE _meta SET SchemaVersion = 3 WHERE Id = 1;
