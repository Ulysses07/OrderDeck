#!/usr/bin/env bash
# Nightly DataProtection keys → R2, GPG şifreli. See HA-PLAYBOOK.md gap G1.
#
# Bu anahtarlar olmadan host kaybında müşteri JWT'leri + parola sıfırlama
# token'ları kurtarılamaz. Ama düz XML olarak R2'de dururken de tersi geçerli:
# R2'yi okuyabilen biri imzalama anahtar halkasını ele geçirir ve istediği
# token'ı üretebilir. Yani şifresiz replikasyon, kurtardığı riskin yerine daha
# beterini koyuyordu.
#
# `aws s3 sync` yerine tarihli tarball: sync, dosya başına şifreleme yapamaz.
# Anahtarlar KB boyutunda ve nadiren değişiyor, o yüzden her gece tam anlık
# görüntü almak bedava — üstelik restore ederken tutarlı bir halka veriyor,
# yarı senkronize bir dizin değil.
#
# Cron: 30 3 * * * /opt/orderdeck/scripts/backup-keys-to-r2.sh >>/var/log/orderdeck-keys-backup.log 2>&1
set -euo pipefail

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX keys-backup start"

# shellcheck source=lib-backup-crypt.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/lib-backup-crypt.sh"

require_passphrase

KEYS_DIR="/opt/orderdeck/keys"
BUCKET="s3://orderdeck-prod-backups/keys"
STAMP=$(date -u +%Y-%m-%d)
KEEP_DAYS=30

if [[ ! -d "$KEYS_DIR" ]]; then
  echo "$LOG_PREFIX HATA: $KEYS_DIR yok"
  exit 1
fi

COUNT=$(find "$KEYS_DIR" -maxdepth 1 -type f | wc -l)
if (( COUNT == 0 )); then
  echo "$LOG_PREFIX HATA: $KEYS_DIR bos — boyle bir yedek yuklemek, dolu olan"
  echo "$LOG_PREFIX bir onceki gunun anlik goruntusunu retention ile sessizce"
  echo "$LOG_PREFIX gecersiz kilardi. Yukleme yapilmadi."
  exit 1
fi

TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT
chmod 700 "$TMPDIR"

TAR="$TMPDIR/keys-${STAMP}.tar.gz"
CIPHER="${TAR}.gpg"

tar -czf "$TAR" -C "$KEYS_DIR" .
encrypt_verified "$TAR" "$CIPHER"
SIZE=$(stat -c%s "$CIPHER")

aws s3 cp "$CIPHER" "${BUCKET}/keys-${STAMP}.tar.gz.gpg" --profile r2 --only-show-errors

prune_dated_objects "$BUCKET" "$KEEP_DAYS"

echo "$LOG_PREFIX keys-backup done count=${COUNT} size=${SIZE}B remote=${BUCKET}/keys-${STAMP}.tar.gz.gpg"
