-- LiveDeck initial schema. All timestamps are unix seconds (INTEGER).
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

CREATE TABLE IF NOT EXISTS ActiveCode (
    Id          TEXT PRIMARY KEY,
    SessionId   TEXT NOT NULL,
    Code        TEXT NOT NULL,
    Sizes       TEXT NOT NULL DEFAULT '[]',
    Price       REAL NOT NULL,
    ImageUrl    TEXT,
    Aliases     TEXT,
    StartedAt   INTEGER NOT NULL,
    EndedAt     INTEGER,
    FOREIGN KEY (SessionId) REFERENCES StreamSession(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ActiveCode_Session_Code ON ActiveCode(SessionId, Code);
CREATE INDEX IF NOT EXISTS IX_ActiveCode_Session_Ended ON ActiveCode(SessionId, EndedAt);

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

CREATE TABLE IF NOT EXISTS OrderItem (
    Id                    TEXT PRIMARY KEY,
    SessionId             TEXT NOT NULL,
    ActiveCodeId          TEXT NOT NULL,
    CustomerId            TEXT NOT NULL,
    Code                  TEXT NOT NULL,
    Size                  TEXT NOT NULL,
    Quantity              INTEGER NOT NULL DEFAULT 1,
    UnitPrice             REAL NOT NULL,
    TotalPrice            REAL NOT NULL,
    Confidence            INTEGER NOT NULL,
    Status                TEXT NOT NULL,
    OriginalMessageText   TEXT NOT NULL,
    CapturedAt            INTEGER NOT NULL,
    StatusUpdatedAt       INTEGER NOT NULL,
    LabelPrintedAt        INTEGER,
    Notes                 TEXT,
    FOREIGN KEY (SessionId)    REFERENCES StreamSession(Id) ON DELETE CASCADE,
    FOREIGN KEY (ActiveCodeId) REFERENCES ActiveCode(Id),
    FOREIGN KEY (CustomerId)   REFERENCES Customer(Id)
);

CREATE INDEX IF NOT EXISTS IX_OrderItem_Session_Status_Captured
    ON OrderItem(SessionId, Status, CapturedAt DESC);
CREATE INDEX IF NOT EXISTS IX_OrderItem_Customer ON OrderItem(CustomerId);

CREATE TABLE IF NOT EXISTS Giveaway (
    Id                  TEXT PRIMARY KEY,
    SessionId           TEXT NOT NULL,
    Keyword             TEXT NOT NULL,
    Prize               TEXT,
    WinnerCount         INTEGER NOT NULL DEFAULT 1,
    PlatformFilter      TEXT,
    PreventRewinning    INTEGER NOT NULL DEFAULT 1,
    StartedAt           INTEGER NOT NULL,
    EndedAt             INTEGER,
    DrawnAt             INTEGER,
    RandomSeed          TEXT NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES StreamSession(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_Giveaway_Session ON Giveaway(SessionId);

CREATE TABLE IF NOT EXISTS GiveawayParticipant (
    Id          TEXT PRIMARY KEY,
    GiveawayId  TEXT NOT NULL,
    CustomerId  TEXT NOT NULL,
    Platform    TEXT NOT NULL,
    Username    TEXT NOT NULL,
    EnteredAt   INTEGER NOT NULL,
    IsWinner    INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (GiveawayId) REFERENCES Giveaway(Id) ON DELETE CASCADE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
);

CREATE INDEX IF NOT EXISTS IX_GiveawayParticipant_Giveaway_Winner
    ON GiveawayParticipant(GiveawayId, IsWinner);
CREATE INDEX IF NOT EXISTS IX_GiveawayParticipant_Customer_Winner
    ON GiveawayParticipant(CustomerId, IsWinner);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

UPDATE _meta SET SchemaVersion = 1 WHERE Id = 1;
