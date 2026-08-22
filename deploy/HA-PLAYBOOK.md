# OrderDeck License Server — High-Availability Playbook

**Audience:** the operator who decides "single VPS uptime isn't good enough anymore."
**Status:** documented; code is HA-ready, infra is the operator's call.

---

## What "single VPS" actually risks

Today's prod (`72.62.53.86`) runs three containers (SQL Server Express, the
ASP.NET Core license server, Caddy) on one box. A single host failure takes
down:

- Customer license validation (desktop apps drop into offline-grace, then lock
  out after the configured window — currently 14 days).
- New license issue / activation flows.
- The Phase 5a backup ingestion path. Existing backups stay readable from the
  filesystem, but no new uploads.
- Admin dashboard.

Concrete failure modes we've seen on similar VPS providers:
- Hypervisor crash → ~5-30 min outage.
- Provider-side network maintenance → 30 min unannounced.
- Disk fill (audit logs, backup blobs) → silent SQL Express insert failures.
- DDoS → Caddy chokes; we have no upstream WAF.

---

## What's HA-ready in code (already)

The `fix/remaining-high-gaps` work shipped scaffolding so HA isn't a code
project, just an infra one:

- **Read replica support** (Phase 5e). `LicenseReadOnlyDbContext` is registered
  alongside `LicenseDbContext`. When `ConnectionStrings:LicenseDbReadOnly` is
  set in env, admin list/detail queries can route there. Falls back to the
  primary connection when unset, so single-VPS deployments stay bytewise
  identical.
- **S3 off-host backup replication** (Phase 5b). Encrypted blobs sync to
  S3-compatible storage on every upload. Survives total VPS loss.
- **Health probes**. `/healthz` (liveness) and `/ready` (DB ping) are public,
  unauthenticated. Any L4/L7 load balancer can use them for failover.
- **Stateless app** (mostly). The license server keeps no in-memory state that
  would prevent running multiple replicas — except DataProtection keys (which
  must be shared) and Hangfire (which must coordinate via DB).
- **Observability**. `/metrics` Prometheus scrape + OTLP push (set
  `OTEL_EXPORTER_OTLP_ENDPOINT`). Required to know an instance is sick.

---

## Tier 1 — "warm standby" (recommended first step)

Add a second VPS in a different provider region. Keep it idle most of the
time; promote on primary failure.

### Components

- **Primary VPS** (current): runs SQL Server + license-server + Caddy.
- **Standby VPS** (new): same Docker Compose stack, mostly powered off or
  running with traffic disabled at Caddy level.
- **Off-host backup destination**: Cloudflare R2 bucket
  `orderdeck-prod-backups`. Already wired via the nightly cron scripts in
  `deploy/scripts/` (see "Operational gaps" 1, 2, 5, 6) — the app itself does
  no replication.
- **DNS-based failover**: lower TTL on `license.orderdeckapp.com` to 60s.
  Health-check provider (Cloudflare, Route53, NS1) flips the A record when
  primary is unhealthy.

### Cutover playbook

0. Log in to **Cloudflare** and mint a fresh *Object Read only* R2 token. The
   existing credentials live on the host you just lost, and the copy inside
   `.env` can't help you — `.env` is itself in the bucket. What the password
   manager must hold is Cloudflare account access, not a long-lived token.
1. Restore latest SQL `.bak` (the one produced by the cron documented in
   `deploy/README.md`) to the standby's SQL Server. It is GPG symmetric-
   encrypted: `gpg --decrypt orderdeck-<date>.bak.gz.gpg | gunzip > x.bak`.
2. Restore DataProtection keys from `s3://orderdeck-prod-backups/keys/` —
   also GPG-encrypted: `gpg --decrypt keys-<date>.tar.gz.gpg | tar -xz -C
   /opt/orderdeck/keys` (see "Operational gaps" 1).
3. Restore `.env` from `s3://orderdeck-prod-backups/env/` — it is GPG
   symmetric-encrypted, the passphrase is in the password manager, NOT on the
   host (see "Operational gaps" 5). This is what carries `BACKUP_MASTER_KEYS_*`;
   without it the customer backup blobs are undecryptable ciphertext.
4. Restore customer backup blobs from
   `s3://orderdeck-prod-backups/customer-blobs/` into `/opt/orderdeck/backups`
   (see "Operational gaps" 6).
5. Bring the standby's containers up.
6. Update DNS A record to point at the standby IP.
7. Wait for TTL.
8. (Important) flip the primary's containers OFF so two writers can't both
   serve the same customers.

RTO: ~10 min if SQL `.bak` is fresh. RPO: depends on `.bak` cadence (default
nightly → up to 24h of customer activity lost). Add hourly diff backups to
shrink RPO.

### Cost

~5-10 USD/mo for the standby VPS sitting idle (1-2 GB RAM is plenty). S3 storage
costs scale with backup volume; B2 is the cheapest at ~$0.005/GB/mo.

---

## Tier 2 — "active-active" (overkill for current scale)

Two VPS, both serving traffic, behind a load balancer. SQL Server AlwaysOn
Availability Group with one primary + one secondary replica. Hangfire jobs
coordinated via the shared DB (already supported).

### What changes in code

- Both license-server containers point at the same SQL primary for writes.
- Read paths use `ConnectionStrings:LicenseDbReadOnly` against the secondary —
  the code already does this when the connection string is set.
- DataProtection keys must come from a shared store (Redis or shared filesystem).
  Currently `./keys` is a local volume; that needs to change.
- Hangfire respects single-instance scheduling via SQL Server lock contention.
  No code changes; just confirm `JobStorage.Current` points at the same DB.

### What changes in infra

- SQL Server Standard or Enterprise. **Express does not support AlwaysOn**
  read replicas — this is a hard upgrade requirement for active-active.
- Cloud LB or a self-hosted HAProxy/Caddy pair with VRRP.
- Shared DataProtection key ring. Two practical options:
  1. NFS / SMB mount of `/root/.aspnet/DataProtection-Keys` from a small NAS
     instance both VPS read from.
  2. Use `services.AddDataProtection().PersistKeysToDbContext<LicenseDbContext>()`
     — adds a DataProtectionKeys table and the keys live in SQL. Recommended:
     SQL is already replicated and we don't have to manage another mount.

RTO: ~30s (LB health check interval). RPO: 0 for committed writes (sync
replication on AG).

### Cost

~50 USD/mo all-in: 2 app VPS + 2 SQL VPS (or one managed SQL service) + LB.

---

## Operational gaps to close BEFORE either tier

These are the parts code can't help with — pure ops hygiene.

1. **DataProtection keys backup** — ✅ DONE 2026-05-05, **encrypted 2026-08-22**.
   Nightly GPG-symmetric tarball to Cloudflare R2 via
   [`deploy/scripts/backup-keys-to-r2.sh`](scripts/backup-keys-to-r2.sh),
   cron `30 3 * * *`. Target:
   `s3://orderdeck-prod-backups/keys/keys-<date>.tar.gz.gpg`, 30-day
   retention. Logs: `/var/log/orderdeck-keys-backup.log`. Without this,
   password reset tokens + customer JWTs signed by lost keys would be
   unrecoverable on host failure.

   Until 2026-08-22 these were `aws s3 sync`ed as **plaintext XML**, which
   inverted the point of the gap: anyone who could read the bucket held the
   signing key ring and could mint any token they liked. `sync` can't encrypt
   per file, hence the switch to a dated tarball — also a more coherent thing
   to restore than a half-synced directory.

2. **SQL `.bak` to off-host storage** — ✅ DONE 2026-05-05, **encrypted
   2026-08-22**. Nightly `BACKUP DATABASE` inside the sqlserver container,
   gzip on host (SQL Express has no native compression), GPG-symmetric, upload
   to R2 via [`deploy/scripts/backup-sql-to-r2.sh`](scripts/backup-sql-to-r2.sh),
   cron `0 3 * * *`. Target:
   `s3://orderdeck-prod-backups/sql-bak/orderdeck-<date>.bak.gz.gpg`.
   Retention: 3 days local (plaintext, deliberately), 30 days remote. Logs:
   `/var/log/orderdeck-sql-backup.log`.

   The `.bak` is raw customer data — `Customers`, `Payments`,
   `GiveawayParticipants`, i.e. KVKK-scoped PII. While it sat there as plain
   gzip, encrypting `.env` (gap 5) was theatre: an attacker holding a bucket
   token has no need for the key ring when the data itself is right there. The
   2026-08-22 restore drill demonstrated exactly this — a single **read-only**
   R2 token was enough to restore the entire production database on a clean
   machine.

3. **Disk-full monitoring** — ✅ DONE 2026-05-05. Hourly cron runs
   [`deploy/scripts/disk-check.sh`](scripts/disk-check.sh): if root
   filesystem usage crosses 85% it sends a one-shot email to
   `Admin__AlertEmail` via `msmtp` (reuses the existing Brevo SMTP
   credentials from `.env`). State file at
   `/var/lib/orderdeck-disk-alert/active` dedupes — no spam while the
   condition persists. Recovery (≥5 % below threshold) clears state and
   sends a single "recovered" mail. Cron `17 * * * *`. Logs:
   `/var/log/orderdeck-disk-check.log` and `/var/log/msmtp.log`. Lighter
   than full Grafana/node_exporter; fits the single-VPS scale.

4. **DNS provider with health checks**. Without this, "promote standby" is a
   manual action that takes you long enough that customers notice. Cloudflare
   free tier gives 5-min health checks; paid is 30-second. Route53 is 10s.
   **Status: not yet configured** — single-VPS today, deferred until Tier
   1 is triggered.

5. **`.env` off-host, encrypted** — ✅ DONE 2026-08-22. Nightly GPG symmetric
   (AES256) copy to R2 via
   [`deploy/scripts/backup-env-to-r2.sh`](scripts/backup-env-to-r2.sh), cron
   `45 3 * * *`. Target: `s3://orderdeck-prod-backups/env/`, 30-day retention.
   Logs: `/var/log/orderdeck-env-backup.log`.

   This is the gap that made gaps 1, 2 and 6 only *look* complete. Customer
   backup blobs are AES-256-GCM ciphertext and the master key ring lives
   **only** in `.env` (`BACKUP_MASTER_KEYS_*` / `BACKUP_MASTER_KEY`); the SQL
   `.bak` carries `CustomerBackups.KeyVersion`, which picks *which* key — not
   the key itself. Lose the host without `.env` and every off-host artefact
   is unrecoverable noise.

   The passphrase is **not generated by the script** and must not live only on
   the VPS — a backup encrypted with a secret that dies with the host is
   worthless. It is read from `/opt/orderdeck/.env-backup-pass` (mode 600),
   generated once by the operator and stored in the password manager. Setup
   and restore commands: `deploy/README.md`, "Off-host replication".

   The script verifies the ciphertext round-trips (decrypt + `cmp` against the
   source) **before** uploading, so a broken passphrase file fails loudly on
   the night it breaks instead of on the night you need it.

6. **Customer backup blobs off-host** — ✅ DONE 2026-08-22. Nightly
   `aws s3 sync --delete` of `/opt/orderdeck/backups` to R2 via
   [`deploy/scripts/backup-blobs-to-r2.sh`](scripts/backup-blobs-to-r2.sh),
   cron `0 4 * * *`. Target:
   `s3://orderdeck-prod-backups/customer-blobs/`. Logs:
   `/var/log/orderdeck-blobs-backup.log`.

   Replaces the in-app `Backup:S3` sink, which was **deleted** rather than
   fixed. Two reasons: it fired-and-forgot on `Task.Run` with no record and no
   retry, so an in-flight copy vanished on every deploy restart and "does an
   off-host copy of this blob exist?" was unanswerable; and it would not have
   worked against R2 at all (missing `AuthenticationRegion` +
   `DisablePayloadSigning`), i.e. a config knob that looks enable-able and
   silently isn't. `aws s3 sync` is incremental and restart-safe by
   construction.

   `--delete` is deliberate: retention pruning and KVKK erasure have to
   propagate to the off-host copy, otherwise deleting customer data on the
   primary leaves it sitting in R2. But **R2 has no object versioning** (only
   prefix-scoped bucket locks), so a mistaken local wipe would replicate with
   nothing to roll back to. Hence the tripwire: if the remote already holds
   objects and the local count has dropped below half the remote count, the
   script aborts and does nothing. Override for a genuine bulk deletion with
   `ALLOW_MASS_DELETE=1`.

7. **Restore drill** — ✅ DONE 2026-08-22. Full chain exercised on a clean
   machine, touching nothing in production: fresh read-only R2 token from the
   Cloudflare dashboard → pull all four artefacts → decrypt `.env` with the
   password-manager passphrase (not the host copy — the point was to test the
   password-manager copy) → restore the `.bak` into a throwaway
   `mssql/server:2022-latest` container → read `KeyVersion` +
   `ChecksumSha256` from `CustomerBackups` → decrypt the blob with that key
   version → **SHA256 matched the stored checksum** → the extracted
   `orderdeck.db` opened with 9 tables / 13,224 rows. Everything was then
   destroyed and the drill token revoked.

   The drill is what turned gaps 1 and 2 from "done" into "done but
   unencrypted", which is why they were rewritten the same day. Re-run it
   after any change to the envelope format, the key ring, or the passphrase.

---

## Decision matrix

| Customer count | Tier | Notes |
|----------------|------|-------|
| 1 - 50         | Single VPS | Acceptable. Restore from S3 backup if it dies. |
| 50 - 200       | Tier 1 (warm standby) | DNS-based failover; ~10 min RTO. |
| 200 - 1000     | Tier 2 (active-active) | SQL Standard required. ~30s RTO. |
| 1000+          | Cloud-managed (Azure SQL / RDS) | Outside the scope of this playbook. |

For OrderDeck's current ~0-100 customer trajectory, **Tier 1 is the right next
step**. Tier 2 is premature optimisation.

---

## Trigger conditions

Cut over to Tier 1 when ANY of these are true:

- First production customer with a contract that names an SLA target.
- An incident causes >2h customer-visible downtime.
- Total backup storage > 100 GB (then losing the VPS becomes a recovery
  exercise that itself takes hours).
- Customer count > 50.

Until then: keep the single-VPS topology. The `fix/remaining-high-gaps` code
makes it cheap to flip later.
