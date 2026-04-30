-- Phase 4f: address from intake form submissions
ALTER TABLE Customer ADD COLUMN Address TEXT;

UPDATE _meta SET SchemaVersion = 6 WHERE Id = 1;
