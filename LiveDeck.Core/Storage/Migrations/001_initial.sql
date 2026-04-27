-- LiveDeck initial schema (P1b-flattened). All timestamps are unix seconds (INTEGER).
-- This migration is idempotent: re-applying it on a populated DB does nothing.

CREATE TABLE IF NOT EXISTS _meta (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    SchemaVersion INTEGER NOT NULL
);

INSERT OR IGNORE INTO _meta (Id, SchemaVersion) VALUES (1, 0);

CREATE TABLE IF NOT EXISTS StreamSession (
    Id           TEXT PRIMARY KEY,
    Title        TEXT,
    StartedAt    INTEGER NOT NULL,
    EndedAt      INTEGER,
    Platforms    TEXT NOT NULL DEFAULT '[]',
    Notes        TEXT
);

CREATE INDEX IF NOT EXISTS IX_StreamSession_StartedAt ON StreamSession(StartedAt DESC);

CREATE TABLE IF NOT EXISTS Customer (
    Id                TEXT PRIMARY KEY,
    Platform          TEXT NOT NULL,
    Username          TEXT NOT NULL,
    DisplayName       TEXT,
    AvatarUrl         TEXT,
    FirstSeenAt       INTEGER NOT NULL,
    LastSeenAt        INTEGER NOT NULL,
    TotalOrders       INTEGER NOT NULL DEFAULT 0,
    CompletedOrders   INTEGER NOT NULL DEFAULT 0,
    CancelledOrders   INTEGER NOT NULL DEFAULT 0,
    TrustScore        INTEGER NOT NULL DEFAULT 100,
    IsBlacklisted     INTEGER NOT NULL DEFAULT 0,
    BlacklistReason   TEXT,
    Notes             TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_Customer_Platform_Username ON Customer(Platform, Username);
CREATE INDEX IF NOT EXISTS IX_Customer_Blacklisted ON Customer(IsBlacklisted);
CREATE INDEX IF NOT EXISTS IX_Customer_TrustScore ON Customer(TrustScore DESC);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

UPDATE _meta SET SchemaVersion = 1 WHERE Id = 1;
