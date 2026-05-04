# Backup Restore Drill

OrderDeck stores customer database backups encrypted with AES-256-GCM
under `/opt/orderdeck/backups/<customer-id>/*.bin`. The encryption code
has unit tests, but **a successful round-trip in CI is not the same as a
successful restore from production data with the production key ring** —
key rotations, env-var drift, and disk corruption can silently invalidate
recovery without anyone noticing until disaster hits.

This drill verifies the chain end-to-end without touching any production
state. Run it weekly; treat a failure as a P1.

## Running the drill

```bash
ssh root@72.62.53.86 'bash /opt/orderdeck/restore-test.sh'
```

The script:

1. Picks the most-recently-modified blob under `/opt/orderdeck/backups/`
   (or the path you pass as `$1`).
2. Maps the host path to its in-container equivalent (`/app/Backups/...`).
3. Executes `RestoreVerify` inside the running `license-server` container
   so the same key ring + encryption code that wrote the blob is the one
   reading it back.
4. Pipes the blob through:
   - **Decrypt** (AES-GCM auth tag check)
   - **ZIP integrity** (open archive, count entries, extract)
   - **SQLite open** (read-only) + `PRAGMA integrity_check`
   - **Cleanup** (wipes `/tmp/orderdeck-restore-test` inside the container)
5. Exits 0 only if every check passed.

## Sample passing output

```
=== OrderDeck Backup Restore Drill ===
Latest blob: /opt/orderdeck/backups/a1b2c3d4-.../20260504-103022-abc.bin
Key version: 0
Container path: /app/Backups/a1b2c3d4-.../20260504-103022-abc.bin

[OK] Decrypt: 12048 envelope bytes → 12020 plaintext bytes (keyVersion=0)
[OK] ZIP integrity: 3 entries extracted to /tmp/orderdeck-restore-test/extracted
[OK] SQLite open: 24 tables — Customer, License, Session, ...
[OK] SQLite PRAGMA integrity_check: ok
[OK] Cleanup: /tmp/orderdeck-restore-test removed
RESTORE DRILL PASSED

RESTORE DRILL PASSED — backup /opt/orderdeck/backups/.../...bin decrypts and parses correctly.
```

## Diagnosing failures

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `[FAIL] Decrypt: AES-GCM auth tag mismatch` | Wrong key for this blob's `KeyVersion`, or blob has been tampered with on disk | Cross-check `.env` `BACKUP_MASTERKEYS_*`. Re-run with explicit `<key-version>`. If `.env` was rotated and old `MasterKeys[0]` got dropped, restore the old key entry. |
| `[FAIL] ZIP: corrupted archive` | Blob decrypted to garbage (key matched by coincidence) OR write-time disk corruption | Try an older blob from the same customer. If multiple newest blobs fail, the writer side is at fault. |
| `[FAIL] SQLite PRAGMA integrity_check: ...errors` | The customer's local SQLite was already corrupt when they uploaded the backup | Not a server-side problem; advise the customer their next upload will overwrite. |
| Script can't find any blob | `/opt/orderdeck/backups` empty (no customer has uploaded yet) or storage path mis-mapped | Check `Backup__StorageRoot` in `docker-compose.yml`. |

## When to run

- **Weekly** as a baseline — scheduled on Monday, results into ops
  ticket if anything failed during the previous week.
- **Always** after rotating `BACKUP_MASTERKEYS_*` or bumping
  `BACKUP_ACTIVEKEYVERSION` in `.env` — confirms the new key ring still
  decrypts the entire historical archive (test old + new blobs).
- **After a license-server image rebuild** that touches anything under
  `OrderDeck.LicenseServer/Services/Backup/` — protection against
  refactor regressions.
- **Before any plan to migrate the DB or VPS** — never move infra without
  a passing drill within the previous 24 hours.

## Future automation

Today the drill is a manual SSH command. The wrapper script's exit code
makes it cron-friendly; a cron entry like:

```
# /etc/cron.d/orderdeck-restore-drill
30 4 * * MON  root  /opt/orderdeck/restore-test.sh > /var/log/orderdeck-restore-test.log 2>&1 || (mail -s "OrderDeck restore drill FAILED" you@example.com < /var/log/orderdeck-restore-test.log)
```

— picks the latest blob every Monday at 04:30 and emails on failure.
That's the V2 enhancement; for now, do it by hand once a week.
