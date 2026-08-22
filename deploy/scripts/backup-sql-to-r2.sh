#!/usr/bin/env bash
# Nightly SQL .bak → R2, GPG şifreli. See HA-PLAYBOOK.md gap G2.
#
# Şifreleme neden şart: `.bak` ham müşteri verisidir (Customers, Payments,
# GiveawayParticipants — KVKK kapsamında). Düz gzip olarak R2'de dururken
# `.env`'i şifrelemek tiyatroydu: R2 token'ını ele geçiren birinin `.env`'e
# ihtiyacı yok, veri zaten `.bak`'ta. 2026-08-22 restore tatbikatında tek bir
# *salt-okunur* token'la tüm prod veritabanı temiz bir makinede restore edildi.
#
# Cron: 0 3 * * * /opt/orderdeck/scripts/backup-sql-to-r2.sh >>/var/log/orderdeck-sql-backup.log 2>&1
set -euo pipefail

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX backup-sql start"

# shellcheck source=lib-backup-crypt.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/lib-backup-crypt.sh"

# Parola kontrolü BACKUP DATABASE'den önce: parola yoksa yükleyemeyeceğimiz bir
# yedek için SQL'i yarım saat meşgul etmenin anlamı yok.
require_passphrase

SQL_PASSWORD=$(grep ^SQL_PASSWORD /opt/orderdeck/.env | cut -d= -f2)
STAMP=$(date -u +%Y-%m-%d)
KEEP_DAYS=30
BUCKET="s3://orderdeck-prod-backups/sql-bak"
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

# 3) Encrypt. Sıra önemli: önce sıkıştır sonra şifrele — tersi sıkışmaz.
TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT
chmod 700 "$TMPDIR"
CIPHER="$TMPDIR/orderdeck-${STAMP}.bak.gz.gpg"
encrypt_verified "$GZ" "$CIPHER"
SIZE=$(stat -c%s "$CIPHER")

# 4) Upload to R2 with date prefix.
aws s3 cp "$CIPHER" "${BUCKET}/orderdeck-${STAMP}.bak.gz.gpg" \
  --profile r2 --only-show-errors

# 5) Local retention: keep last 3 days on disk (R2 keeps 30). Yereldeki kopya
#    bilerek şifresiz: host'a erişebilen zaten canlı DB'ye erişiyor, şifrelemek
#    yalnız restore'u zorlaştırırdı.
find /opt/orderdeck/sql-data/backup -name "orderdeck-*.bak.gz" -mtime +3 -delete

# 6) Remote retention.
prune_dated_objects "$BUCKET" "$KEEP_DAYS"

echo "$LOG_PREFIX backup-sql done size=${SIZE}B remote=${BUCKET}/orderdeck-${STAMP}.bak.gz.gpg"
