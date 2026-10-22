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
# OrderDeck__Bank__HashKey satırı süzülür (Bitwarden'da): bkz. aşağıdaki FILTERED.
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

# IBAN tanıma kodlarının anahtarı (OrderDeck__Bank__HashKey) BİLEREK yedeğe
# girmez (KVKK ön inceleme Ö-6, 2026-10): özetlerle aynı kovada ve aynı
# parolayla durmasın. Asıl kopyası parola yöneticisinde (Bitwarden); geri
# yüklemede .env'e elle eklenir. Yorum satırına alınmış eski değer ve "=" önünde
# boşluk da süzülür; başka bir değişkenin değerinde geçen ad süzülmez.
HASHKEY_LINE='^[[:space:]]*(#[[:space:]]*)?(export[[:space:]]+)?OrderDeck__Bank__HashKey[[:space:]]*='
FILTERED="$TMPDIR/env.filtered"
# grep çıkışı 1 = hiçbir satır kalmadı (aşağıda boş dosya olarak yakalanır); 2 = okuma hatası.
rc=0
grep -Ev "$HASHKEY_LINE" "$ENV_FILE" > "$FILTERED" || rc=$?
if (( rc > 1 )); then
  echo "$LOG_PREFIX HATA: .env süzülemedi (grep çıkışı $rc), yükleme yapılmadı"
  exit 1
fi
if [[ ! -s "$FILTERED" ]]; then
  echo "$LOG_PREFIX HATA: süzülmüş .env boş, yükleme yapılmadı"
  exit 1
fi
FILTERED_LINES=$(grep -cE "$HASHKEY_LINE" "$ENV_FILE" || true)

encrypt_verified "$FILTERED" "$CIPHER"
SIZE=$(stat -c%s "$CIPHER")

aws s3 cp "$CIPHER" "${BUCKET}/env-${STAMP}.gpg" --profile r2 --only-show-errors

prune_dated_objects "$BUCKET" "$KEEP_DAYS"

echo "$LOG_PREFIX env-backup done size=${SIZE}B hashkey_lines_filtered=${FILTERED_LINES} remote=${BUCKET}/env-${STAMP}.gpg"
