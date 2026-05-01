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

## Cloud backup setup (Phase 5a)

After initial deploy, bootstrap the AES master key:

```bash
ssh root@72.62.53.86
/opt/orderdeck/setup-backup-key.sh
```

This generates a 64-hex (32-byte) random key, writes it to `/opt/orderdeck/.env`,
and restarts the license-server. Backups are stored at `/opt/orderdeck/backups/{customerId}/`.

**Rotation warning:** rotating the key makes all existing encrypted backups
unreadable (no re-encryption flow in v1).

## DNS

A record: `license.orderdeckapp.com` → VPS IP (72.62.53.86), TTL 300, NOT proxied.
Caddy will automatically obtain the Let's Encrypt cert on first request once DNS resolves.
