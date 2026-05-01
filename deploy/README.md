# OrderDeck License Server — VPS Deployment

## Architecture
- **SQL Server 2022 Express** (Docker) — DB, internal port 1433
- **OrderDeck.LicenseServer** (.NET 10 ASP.NET Core, Docker) — internal port 8080
- **Caddy 2** (Docker) — reverse proxy, ports 80/443, automatic Let's Encrypt TLS
- All on a private Docker network `web`

## Layout on VPS

```
/opt/orderdeck/
├── docker-compose.yml
├── Caddyfile
├── .env                  # secrets (gitignored, file mode 600)
├── app/                  # source tree (git checkout or scp tarball)
│   ├── OrderDeck.LicenseServer/
│   ├── OrderDeck.Core/
│   ├── OrderDeck.Licensing/
│   └── ... (everything Dockerfile needs to build)
├── keys/                 # ASP.NET Core DataProtection keys (Docker volume mount)
├── sql-data/             # SQL Server data files (Docker volume mount)
└── caddy_data            # Docker named volume — Let's Encrypt certs
```

## Initial deploy

1. Provision .env (see template below)
2. Place app source under ./app
3. `docker compose up -d --build`
4. Apply EF migrations from inside the license-server container:
   `docker compose exec license-server dotnet OrderDeck.LicenseServer.dll --migrate`
   (or run `dotnet ef database update` against the SQL Server connection string)

## .env template

Copy to `/opt/orderdeck/.env` (file mode 600, do NOT commit):

```env
SQL_PASSWORD=ReplaceWithStrong32CharPassword!
JWT_SECRET=ReplaceWith64CharRandomBase64String_GenerateWithOpenSSL
ADMIN_USERNAME=admin
ADMIN_PASSWORD_HASH=ReplaceWithBCryptHash

# Optional: SMTP (set to real values when email features needed)
SMTP_HOST=smtp.example.com
SMTP_PORT=587
SMTP_USESSL=true
SMTP_USERNAME=
SMTP_PASSWORD=
SMTP_FROM=noreply@orderdeckapp.com

# Phase 5a — Cloud backup (set via setup-backup-key.sh)
BACKUP_MASTER_KEY=GenerateWith_setup-backup-key.sh
```

Generate strong values:
```bash
SQL_PASSWORD: openssl rand -base64 24 | tr -d '/+=' | head -c 32
JWT_SECRET:   openssl rand -base64 48
ADMIN_PASSWORD_HASH: see Phase 4a admin bootstrap docs (BCrypt-Net)
```

## Operations

- **Start**: `docker compose up -d`
- **Stop**: `docker compose down`
- **Restart license-server only** (after code update): `docker compose up -d --build license-server`
- **Logs (live)**: `docker compose logs -f license-server`
- **DB backup**: `docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PASSWORD" -Q "BACKUP DATABASE OrderDeckLicense TO DISK = '/var/opt/mssql/backup/orderdeck-$(date +%F).bak'"`

## EF migration history bootstrap (one-time, before first Migrate() deploy)

The original deploy used `EnsureCreated()` so the DB has all the schema but no
`__EFMigrationsHistory` table. The app now calls `Database.Migrate()` on
startup; without the history table EF would try to re-apply every migration
and fail with "table already exists".

Apply once before the next deploy:

```bash
scp deploy/bootstrap-migration-history.sql root@72.62.53.86:/tmp/
ssh root@72.62.53.86
docker cp /tmp/bootstrap-migration-history.sql orderdeck-sqlserver:/tmp/
docker exec -i orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SQL_PASSWORD" -d OrderDeckLicense -C -No \
  -i /tmp/bootstrap-migration-history.sql
```

Idempotent — re-running is a no-op.

## Cloud backup setup (Phase 5a)

After initial deploy, bootstrap the AES master key:

```bash
ssh root@72.62.53.86
/opt/orderdeck/setup-backup-key.sh
```

This generates a 64-hex (32-byte) random key, writes it to `/opt/orderdeck/.env`,
and restarts the license-server. Backups are stored at `/opt/orderdeck/backups/{customerId}/`.

**Rotation (Phase 5b — versioned key ring):** Master keys are now a versioned
ring. Rotating no longer breaks history — old keys stay in the ring and decrypt
the blobs they originally wrote. To rotate:

1. Generate a fresh 64-hex key (`openssl rand -hex 32`).
2. Add it to `.env` at the next free version slot, keeping all existing keys
   in place. For example, going from active=v0 to active=v1:

   ```env
   # Existing legacy field — leave it; it's the v0 key the historical blobs
   # were encrypted with. Removing it would brick those blobs.
   BACKUP_MASTER_KEY=<existing 64-hex>

   # New ring entries
   BACKUP_MASTERKEYS_1=<new 64-hex>
   BACKUP_ACTIVEKEYVERSION=1
   ```
3. Restart the license-server. From now on every new upload writes a v1
   envelope; v0 blobs continue to decrypt with the v0 key.
4. Eventually (when no v0 blobs remain — check via SQL
   `SELECT KeyVersion, COUNT(*) FROM CustomerBackups GROUP BY KeyVersion;`),
   you can remove the v0 key from `.env`. Keep ALL versions referenced by
   any live row.

**Compromise scenario:** if a key version is suspected leaked, bump active
to a fresh version, then audit + delete affected blobs (they're encrypted
under the leaked key, retention can't help). Customers will upload fresh
backups under the new active version.

### Off-host replication (S3-compatible, optional)

By default backups live only under `/opt/orderdeck/backups/` on the VPS.
A host loss takes everything with it. Enable S3 replication by adding the
following to `/opt/orderdeck/.env`:

```env
BACKUP_S3_ENABLED=true
BACKUP_S3_SERVICE_URL=https://s3.us-west-001.backblazeb2.com   # B2 / AWS / Wasabi / MinIO
BACKUP_S3_ACCESS_KEY=…
BACKUP_S3_SECRET_KEY=…
BACKUP_S3_BUCKET=orderdeck-prod
BACKUP_S3_PREFIX=orderdeck-backups/
```

Behavior:
- Each successful POST `/api/v1/me/backups` triggers a fire-and-forget upload
  of the encrypted blob to S3 with key `{prefix}{customerId}/{filename}.bin`.
- BestEffort=true (default in code): S3 errors logged + ignored, customer
  POST still returns 200. Set `Backup:S3:BestEffort=false` to fail the POST
  on S3 errors (stronger durability, more end-user latency).
- Blobs are already AES-256-GCM encrypted before upload; bucket can be
  public-read with no risk to backup contents (still recommend private).
- No automatic deletion mirror — local retention prunes the VPS, S3 keeps
  forever. Configure S3 lifecycle policies separately if needed.

## DNS

A record: `license.orderdeckapp.com` → VPS IP (72.62.53.86), TTL 300, NOT proxied.
Caddy will automatically obtain the Let's Encrypt cert on first request once DNS resolves.
