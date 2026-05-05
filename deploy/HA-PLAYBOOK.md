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
- **Off-host backup destination**: B2 / Wasabi / AWS S3 bucket. Already wired
  via `BACKUP_S3_*` env vars.
- **DNS-based failover**: lower TTL on `license.orderdeckapp.com` to 60s.
  Health-check provider (Cloudflare, Route53, NS1) flips the A record when
  primary is unhealthy.

### Cutover playbook

1. Restore latest SQL `.bak` (the one produced by the cron documented in
   `deploy/README.md`) to the standby's SQL Server.
2. Restore DataProtection keys (manually rsync'd nightly — see "Operational
   gaps" below).
3. Update `BACKUP_MASTER_KEYS_*` and `BACKUP_S3_*` env vars to match primary.
4. Bring the standby's containers up.
5. Update DNS A record to point at the standby IP.
6. Wait for TTL.
7. (Important) flip the primary's containers OFF so two writers can't both
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

1. **DataProtection keys backup** — ✅ DONE 2026-05-05. Nightly rsync to
   Cloudflare R2 via [`deploy/scripts/backup-keys-to-r2.sh`](scripts/backup-keys-to-r2.sh),
   cron `30 3 * * *`. Target: `s3://orderdeck-prod-backups/keys/`. Logs:
   `/var/log/orderdeck-keys-backup.log`. Without this, password reset
   tokens + customer JWTs signed by lost keys would be unrecoverable on
   host failure.

2. **SQL `.bak` to off-host storage** — ✅ DONE 2026-05-05. Nightly
   `BACKUP DATABASE` inside the sqlserver container, gzip on host (SQL
   Express has no native compression), upload to R2 via
   [`deploy/scripts/backup-sql-to-r2.sh`](scripts/backup-sql-to-r2.sh),
   cron `0 3 * * *`. Target: `s3://orderdeck-prod-backups/sql-bak/`.
   Retention: 3 days local, 30 days remote. Logs:
   `/var/log/orderdeck-sql-backup.log`.

3. **Disk-full monitoring**. Tier-1 standby is useless if the primary's disk
   fills and SQL silently rejects inserts. Hook `/metrics` to Grafana
   alerts: `aspnetcore_diagnostics_exceptions_total` ramp + custom alert on
   `node_filesystem_free_bytes < 1GB` (requires node_exporter). **Status:
   not yet automated** — UptimeRobot covers HTTP liveness only.

4. **DNS provider with health checks**. Without this, "promote standby" is a
   manual action that takes you long enough that customers notice. Cloudflare
   free tier gives 5-min health checks; paid is 30-second. Route53 is 10s.
   **Status: not yet configured** — single-VPS today, deferred until Tier
   1 is triggered.

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
