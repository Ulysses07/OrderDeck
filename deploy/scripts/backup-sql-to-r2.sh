#!/usr/bin/env bash
# Nightly SQL .bak → R2. See HA-PLAYBOOK.md gap G2.
# Cron: 0 3 * * * /opt/orderdeck/scripts/backup-sql-to-r2.sh >>/var/log/orderdeck-sql-backup.log 2>&1
set -euo pipefail

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX backup-sql start"

SQL_PASSWORD=$(grep ^SQL_PASSWORD /opt/orderdeck/.env | cut -d= -f2)
STAMP=$(date -u +%Y-%m-%d)
BAK="/var/opt/mssql/backup/orderdeck-${STAMP}.bak"
HOST_BAK="/opt/orderdeck/sql-data/backup/orderdeck-${STAMP}.bak"

# 1) BACKUP DATABASE inside container.
#    INIT overwrites if today's file already exists (rerun-safe).
docker exec orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SQL_PASSWORD" -C -No \
  -Q "BACKUP DATABASE OrderDeckLicense TO DISK = N'$BAK' WITH FORMAT, INIT" \
  > /tmp/sql-backup.log 2>&1 || { echo "$LOG_PREFIX BACKUP DATABASE failed:"; cat /tmp/sql-backup.log; exit 1; }

if [[ ! -f "$HOST_BAK" ]]; then echo "$LOG_PREFIX bak file missing on host: $HOST_BAK"; exit 1; fi

# 2) Compress (Express has no native COMPRESSION; gzip externally).
gzip -9 -f "$HOST_BAK"
GZ="${HOST_BAK}.gz"
SIZE=$(stat -c%s "$GZ")

# 3) Upload to R2 with date prefix.
aws s3 cp "$GZ" "s3://orderdeck-prod-backups/sql-bak/orderdeck-${STAMP}.bak.gz" \
  --profile r2 --only-show-errors

# 4) Local retention: keep last 3 days on disk (R2 keeps 30).
find /opt/orderdeck/sql-data/backup -name "orderdeck-*.bak.gz" -mtime +3 -delete

# 5) Remote retention: prune R2 objects older than 30 days.
THIRTY_DAYS_AGO=$(date -u -d "30 days ago" +%Y-%m-%d)
aws s3 ls s3://orderdeck-prod-backups/sql-bak/ --profile r2 | awk '{print $NF}' | while read -r obj; do
  obj_date=$(echo "$obj" | grep -oE "[0-9]{4}-[0-9]{2}-[0-9]{2}" || true)
  if [[ -n "$obj_date" && "$obj_date" < "$THIRTY_DAYS_AGO" ]]; then
    aws s3 rm "s3://orderdeck-prod-backups/sql-bak/$obj" --profile r2 --only-show-errors
    echo "$LOG_PREFIX pruned old: $obj"
  fi
done

echo "$LOG_PREFIX backup-sql done size=${SIZE}B remote=s3://orderdeck-prod-backups/sql-bak/orderdeck-${STAMP}.bak.gz"
