# Phase 5b — Backup Master Key Versioning Design

**Status:** Backlog
**Source:** Enterprise audit critical finding #6 (deferred from `fix/critical-security-hardening`)
**Author:** captured 2026-05-01 after the security-hardening sweep merged as `993cdc9`

---

## Problem

`BackupStorageService` envelope is `[12B nonce][16B tag][ciphertext]`. The AES-256
key comes from a single static config value `Backup:MasterKeyHex`. There is no
key version on disk, in the DB, or in the config — so:

- Rotating the master key (e.g. `setup-backup-key.sh`) makes **every existing
  encrypted blob unreadable**. The script even warns about this.
- An accidental rotation = total backup loss across all customers.
- Compliance / DR requirement to rotate master keys on a schedule (e.g. annually,
  or after a suspected leak) is impossible without breaking history.

This is acceptable while the fleet has ~1 smoke-test blob. It becomes a
production blocker once real customer data accumulates.

## Goals

1. Rotation must NOT invalidate historical blobs.
2. Old blobs decrypt with the key they were written with.
3. New blobs use the current "active" key.
4. Rollout must be backward-compatible with the unversioned blobs already in
   `/opt/orderdeck/backups/` and on S3 (if replication is enabled by then).

## Non-goals

- Per-customer keys (single fleet-wide key ring is enough).
- Key escrow / HSM integration.
- Re-encryption of historical blobs to the active key (pure forward-only:
  rotated keys keep decrypting their own history).

## Design

### Envelope format v1

```
[1B keyVersion][12B nonce][16B tag][ciphertext]
       ^             ^         ^         ^
       new        same      same     same
```

`keyVersion` is `0..255`. Versions ≥ 1 are explicit; version `0` is reserved as
a sentinel meaning "no version byte present — this is a v0 (legacy) blob".

### Detecting v0 vs v1 on read

We can NOT use the first byte alone — a legitimate v0 nonce can start with any
byte. Instead:

- v0 detection is **lookup-driven**, not byte-sniffed: `CustomerBackup.KeyVersion`
  defaults to `0` for rows existing before the migration. Decrypt path:
  ```
  if (row.KeyVersion == 0) decrypt as v0 envelope (first 12 bytes = nonce)
  else                     decrypt as v1 envelope (first byte = version, next 12 = nonce)
  ```
- New blobs always written as v1 with `row.KeyVersion = config.ActiveKeyVersion`.

This avoids any byte-sniffing ambiguity and keeps decrypt path linear.

### Configuration shape

```env
# Map of all keys ever used (or imported during migration).
# Version 0 is the LEGACY single-key value — keep populated as long as any
# v0 rows exist on disk.
BACKUP_MASTER_KEYS_0=<previous 64-hex master>
BACKUP_MASTER_KEYS_1=<new 64-hex master>
BACKUP_ACTIVE_KEY_VERSION=1
```

Code shape:
```csharp
public sealed class BackupOptions {
    public Dictionary<int, string> MasterKeys { get; set; } = new();
    public int ActiveKeyVersion { get; set; } = 0;
    // ... existing StorageRoot, MaxBlobSizeMb, S3 ...
}
```

`MasterKeyHex` is removed in favor of `MasterKeys[0]` for backward compat. The
existing `setup-backup-key.sh` rewrites `BACKUP_MASTER_KEY=…` env into
`BACKUP_MASTER_KEYS_0=…` during deploy of this phase (migration script).

### DB change

`CustomerBackup`:
- Add `KeyVersion INT NOT NULL DEFAULT 0`.

Migration `AddBackupKeyVersion`. The `DEFAULT 0` backfills every existing row to
v0 automatically — exactly what the on-disk v0 blobs need.

### Code changes

`BackupStorageService`:
- Constructor: build a `Dictionary<int, byte[]>` from `BackupOptions.MasterKeys`.
  Validate every value is 64 hex chars. Validate `ActiveKeyVersion` exists in
  the dictionary.
- `Encrypt(plaintext)` now returns envelope WITH version byte at position 0,
  using the active key. Returns `(byte[] envelope, int keyVersion)` so the
  controller can persist the version to DB.
- `Decrypt(byte[] envelope, int keyVersion)`: pick key from dictionary by
  version. v0 (no version byte) and v1+ (with version byte) handled by caller
  passing the right version + slicing the envelope accordingly.

`MeBackupsController.Upload`:
- Capture `keyVersion` from Encrypt and store in `CustomerBackup.KeyVersion`.

`MeBackupsController.Download` and `BackupViewerService`:
- Pass `row.KeyVersion` into Decrypt.

### Rotation flow

Annual or on-demand rotation:
1. Generate a new 64-hex key (existing `setup-backup-key.sh` already does this).
2. Promote the new key:
   ```
   BACKUP_MASTER_KEYS_2=<new>
   BACKUP_ACTIVE_KEY_VERSION=2
   ```
   Keys 0 and 1 STAY in env — old blobs need them.
3. Restart license-server.
4. From now on, new uploads use key v2. Existing blobs continue decrypting with
   their original keys.

If a key is suspected leaked and must be revoked:
- Bump active version (rotate to a fresh key).
- Manually delete affected blobs (their old key is compromised; the leaked key
  no longer encrypts new data).
- Force affected customers to upload fresh backups.

### S3 replication interaction

S3 sink uploads the raw envelope as-is. v0 blobs replicated before this phase
are still readable as v0; v1 blobs are self-describing on the wire. No change
needed in `S3BackupSink`.

## Migration plan (one-time, when this phase ships)

1. Rename `BACKUP_MASTER_KEY` → `BACKUP_MASTER_KEYS_0` in `/opt/orderdeck/.env`.
   Keep the same value — that key still owns all existing blobs.
2. Add `BACKUP_ACTIVE_KEY_VERSION=0` initially (no actual rotation, just the
   format upgrade).
3. Apply migration `AddBackupKeyVersion` (default 0 backfills everything).
4. Deploy new code. New uploads: still v0 (since active=0), but now with
   explicit version tracking. **No blob format change yet** — v0 envelopes are
   bytewise identical to current envelopes.
5. Verify production reads/writes still work.
6. Optional, when ready: generate v1 key, bump `BACKUP_ACTIVE_KEY_VERSION=1`.
   New uploads now write v1 envelopes with the version byte prefix.

This staged rollout means **the format change does not need a flag day** —
existing blobs work, new blobs gradually become v1.

## Risks

- **Missing-key crash**: if env var for an old key version is dropped before
  every blob using it is purged, decrypt panics. Mitigation: BackupStorageService
  startup validation — if any `CustomerBackup` row references a `KeyVersion`
  not in the configured map, emit a startup warning + log every missing
  version. Don't crash; just refuse to decrypt those specific rows.
- **Confusing UI**: admin viewer should display "Key v2" badge on backup
  rows so an operator can correlate which keys are still load-bearing.
- **Rotation script footgun**: `setup-backup-key.sh` was the warning vector
  for v0; it must be updated NOT to overwrite `BACKUP_MASTER_KEYS_0` —
  instead append `_1`, `_2`, etc., and bump `ACTIVE_KEY_VERSION`.

## Tests

- Round-trip with v0 envelope (no version byte) using DB row `KeyVersion=0`.
- Round-trip with v1 envelope (version byte prefix) using DB row `KeyVersion=1`.
- Decrypt rejects mismatched (envelope) vs (DB row) version.
- Rotation simulation: write blob with active=1, then bump active=2, write
  another, verify both decrypt correctly afterwards.
- Missing-key startup: configure ActiveKeyVersion=2 but only ship key 1 in
  config → service refuses to start with a clear error.

## Effort estimate

~2-3 hours focused work + careful integration testing. Ship as Phase 5b in a
dedicated branch `feature/phase-5b-key-versioning`. Don't combine with other
work — backup format changes need surgical attention.

## When to ship

Trigger conditions (any one):
- First production customer accumulates >10 historical backups.
- Compliance/contract requires annual key rotation.
- Suspected master-key compromise (then it's not Phase 5b, it's an emergency).

Until then, keep the warning in `setup-backup-key.sh` and the reference to this
spec from `deploy/README.md`.
