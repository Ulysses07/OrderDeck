-- Phase 4g: WhatsApp phone for payment requests (E.164 format)
ALTER TABLE Customer ADD COLUMN Phone TEXT;

UPDATE _meta SET SchemaVersion = 7 WHERE Id = 1;
