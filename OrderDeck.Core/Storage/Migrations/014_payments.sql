-- Payment outbox: lokal banka dekont kaydı. PaymentSyncService LicenseServer'a
-- push'lar (SyncedAt NULL satırları), server-side onay/red status'unu reverse
-- sync ile çeker (sonraki PR).
--
-- ReferansNo unique: aynı dekont iki kez girilemez (operator manuel ekleme
-- veya backend echo). Status text olarak saklanır (kolay debug, az satır).

CREATE TABLE Payment (
    Id              TEXT PRIMARY KEY,
    PayerName       TEXT NOT NULL,
    Amount          TEXT NOT NULL,       -- decimal as text (SQLite REAL precision risk)
    PaidAt          INTEGER NOT NULL,    -- unix seconds
    ReferansNo      TEXT NOT NULL UNIQUE,
    PdfHash         TEXT NULL,
    Status          TEXT NOT NULL DEFAULT 'Pending',
    CreatedAt       INTEGER NOT NULL,
    UpdatedAt       INTEGER NOT NULL,
    SyncedAt        INTEGER NULL,
    ApprovedAt      INTEGER NULL,
    RejectedAt      INTEGER NULL,
    RejectReason    TEXT NULL
);

-- Outbox queue lookup: list unsynced rows oldest-first.
CREATE INDEX IX_Payment_SyncedAt_CreatedAt ON Payment (SyncedAt, CreatedAt);

-- Status filter lookup (e.g. operator UI filter by Pending).
CREATE INDEX IX_Payment_Status_CreatedAt ON Payment (Status, CreatedAt);

UPDATE _meta SET SchemaVersion = 14 WHERE Id = 1;
