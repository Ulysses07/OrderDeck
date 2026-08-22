#!/usr/bin/env bash
# Nightly encrypted `.env` → R2. See HA-PLAYBOOK.md gap G5.
#
# Neden: müşteri yedek blob'ları AES-256-GCM ile şifreli ve anahtar
# `.env`'deki BACKUP_MASTER_KEY (+ BACKUP_MASTERKEYS_*). SQL .bak zaten
# R2'de, yani host kaybında CustomerBackups satırları (KeyVersion dahil)
# geri gelir; blob'lar da artık R2'de. Ama ana anahtar `.env` ile birlikte
# yok olursa hiçbiri ÇÖZÜLEMEZ — blob replikasyonu tek başına tiyatro.
#
# Şifreleme neden şart: `.env` yalnız yedek anahtarını değil SQL parolasını,
# JWT imzalama anahtarını, Netgsm/WhatsApp kimliklerini ve R2 anahtarlarını
# da taşıyor. Düz hâlde atmak, R2'yi ele geçiren birine sistemin tamamını
# verirdi. Tehdit modeli açık: bu şifreleme VPS'i ele geçirene karşı hiçbir
# şey yapmaz (adam zaten `.env`'i düz okuyor), R2'nin sızmasına karşı yapar.
#
# Cron: 45 3 * * * /opt/orderdeck/scripts/backup-env-to-r2.sh >>/var/log/orderdeck-env-backup.log 2>&1
set -euo pipefail

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX env-backup start"

# shellcheck source=lib-backup-crypt.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/lib-backup-crypt.sh"

require_passphrase

ENV_FILE="/opt/orderdeck/.env"
BUCKET="s3://orderdeck-prod-backups/env"
STAMP=$(date -u +%Y-%m-%d)
KEEP_DAYS=30

if [[ ! -r "$ENV_FILE" ]]; then
  echo "$LOG_PREFIX HATA: $ENV_FILE okunamiyor"
  exit 1
fi

TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT
chmod 700 "$TMPDIR"

CIPHER="$TMPDIR/env-${STAMP}.gpg"

encrypt_verified "$ENV_FILE" "$CIPHER"
SIZE=$(stat -c%s "$CIPHER")

aws s3 cp "$CIPHER" "${BUCKET}/env-${STAMP}.gpg" --profile r2 --only-show-errors

prune_dated_objects "$BUCKET" "$KEEP_DAYS"

echo "$LOG_PREFIX env-backup done size=${SIZE}B remote=${BUCKET}/env-${STAMP}.gpg"
