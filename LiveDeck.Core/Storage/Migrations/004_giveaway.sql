-- Phase 2b: çekiliş tabloları (re-introducing Giveaway/GiveawayParticipant
-- which were dropped in P1b's 002 migration; this version is tuned for the P2b spec).

CREATE TABLE IF NOT EXISTS Giveaway (
    Id                 TEXT PRIMARY KEY,
    SessionId          TEXT NOT NULL,
    Keyword            TEXT NOT NULL,
    DurationSeconds    INTEGER NOT NULL,
    WinnerCount        INTEGER NOT NULL,
    PlatformFilter     TEXT,
    PreventRewinning   INTEGER NOT NULL DEFAULT 1,
    RandomSeed         TEXT NOT NULL,
    StartedAt          INTEGER NOT NULL,
    EndedAt            INTEGER,
    CancelledAt        INTEGER,
    FOREIGN KEY (SessionId) REFERENCES StreamSession(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_Giveaway_Session ON Giveaway(SessionId);
CREATE INDEX IF NOT EXISTS IX_Giveaway_Active  ON Giveaway(SessionId, EndedAt, CancelledAt);

CREATE TABLE IF NOT EXISTS GiveawayParticipant (
    Id           TEXT PRIMARY KEY,
    GiveawayId   TEXT NOT NULL,
    CustomerId   TEXT NOT NULL,
    Platform     TEXT NOT NULL,
    Username     TEXT NOT NULL,
    EnteredAt    INTEGER NOT NULL,
    IsWinner     INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (GiveawayId) REFERENCES Giveaway(Id) ON DELETE CASCADE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_GiveawayParticipant_Unique
    ON GiveawayParticipant(GiveawayId, Platform, Username);

CREATE INDEX IF NOT EXISTS IX_GiveawayParticipant_Winners
    ON GiveawayParticipant(GiveawayId, IsWinner);

UPDATE _meta SET SchemaVersion = 4 WHERE Id = 1;
