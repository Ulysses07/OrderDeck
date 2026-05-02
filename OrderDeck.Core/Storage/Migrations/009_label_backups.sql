-- Backup buyers attached to Label rows. Used when the operator wants to remember
-- "X also said they'd buy this if A cancels" during the live stream. If A later
-- cancels (often next-day), the cancel flow surfaces the backup list so the
-- operator can promote a backup to a new Label with one click.
--
-- ON DELETE CASCADE: backups are scoped to their parent Label's lifetime —
-- deleting a label cleans up its backups (FK enforcement is on, see
-- SqliteConnectionFactory: ForeignKeys = true).

CREATE TABLE IF NOT EXISTS LabelBackup (
    Id          TEXT    PRIMARY KEY,
    LabelId     TEXT    NOT NULL,
    Platform    TEXT    NOT NULL,
    Username    TEXT    NOT NULL,
    DisplayName TEXT    NOT NULL,
    MessageText TEXT    NULL,
    AddedAt     INTEGER NOT NULL,
    FOREIGN KEY (LabelId) REFERENCES Label(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_LabelBackup_LabelId ON LabelBackup(LabelId);

UPDATE _meta SET SchemaVersion = 9 WHERE Id = 1;
