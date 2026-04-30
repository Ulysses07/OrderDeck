-- Phase 1b pivot: drop OrderItem/ActiveCode/Giveaway, introduce Label, evolve Customer.
-- Idempotent at the runner level: re-applying skipped via _meta.SchemaVersion.

PRAGMA foreign_keys = OFF;

DROP TABLE IF EXISTS GiveawayParticipant;
DROP TABLE IF EXISTS Giveaway;
DROP TABLE IF EXISTS OrderItem;
DROP TABLE IF EXISTS ActiveCode;

ALTER TABLE Customer ADD COLUMN TotalLabelsPrinted INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Customer ADD COLUMN TotalAmount        REAL    NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS Label (
    Id           TEXT PRIMARY KEY,
    SessionId    TEXT NOT NULL,
    CustomerId   TEXT NOT NULL,
    Platform     TEXT NOT NULL,
    Username     TEXT NOT NULL,
    MessageText  TEXT NOT NULL,
    Code         TEXT,
    Price        REAL NOT NULL,
    AddedAt      INTEGER NOT NULL,
    PrintedAt    INTEGER,
    FOREIGN KEY (SessionId)  REFERENCES StreamSession(Id) ON DELETE CASCADE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
);

CREATE INDEX IF NOT EXISTS IX_Label_Session_PrintedAt ON Label(SessionId, PrintedAt);
CREATE INDEX IF NOT EXISTS IX_Label_Customer          ON Label(CustomerId);

PRAGMA foreign_keys = ON;

UPDATE _meta SET SchemaVersion = 2 WHERE Id = 1;
