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

ENV_FILE="/opt/orderdeck/.env"
PASS_FILE="/opt/orderdeck/.env-backup-pass"
BUCKET="s3://orderdeck-prod-backups/env"
STAMP=$(date -u +%Y-%m-%d)
KEEP_DAYS=30

# Parolayı BU SCRIPT ÜRETMEZ ve üretmemeli. Ürettiği anda parola yalnız bu
# host'ta yaşayan bir dosya olurdu — host'u kaybettiğimizde R2'deki şifreli
# `.env` de birlikte kullanılamaz hâle gelirdi, yani korumaya çalıştığımız
# senaryoda tam olarak işe yaramazdı. Parola makine dışında (parola
# yöneticisinde) durmalı; kurulum adımı deploy/README.md'de.
if [[ ! -s "$PASS_FILE" ]]; then
  echo "$LOG_PREFIX HATA: $PASS_FILE yok veya bos."
  echo "$LOG_PREFIX Kurulum icin bkz. deploy/README.md — parola once parola"
  echo "$LOG_PREFIX yoneticisine kaydedilmeli, sonra bu dosyaya yazilmali."
  exit 1
fi

if [[ ! -r "$ENV_FILE" ]]; then
  echo "$LOG_PREFIX HATA: $ENV_FILE okunamiyor"
  exit 1
fi

TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT
chmod 700 "$TMPDIR"

CIPHER="$TMPDIR/env-${STAMP}.gpg"

gpg --batch --yes --quiet \
    --symmetric --cipher-algo AES256 \
    --passphrase-file "$PASS_FILE" \
    --output "$CIPHER" \
    "$ENV_FILE"

# Yükleme ÖNCESİ çözülebilirlik kontrolü. Şifrelenmiş ama açılamayan bir
# yedek, hiç yedek almamaktan daha kötü: felaket anına kadar yedeğin
# olduğunu sanırsın. Kontrol ucuz — dosya birkaç KB.
gpg --batch --yes --quiet \
    --decrypt --passphrase-file "$PASS_FILE" \
    --output "$TMPDIR/roundtrip" \
    "$CIPHER"

if ! cmp -s "$ENV_FILE" "$TMPDIR/roundtrip"; then
  echo "$LOG_PREFIX HATA: sifre-coz turu basarisiz, yukleme yapilmadi"
  exit 1
fi

SIZE=$(stat -c%s "$CIPHER")
aws s3 cp "$CIPHER" "${BUCKET}/env-${STAMP}.gpg" --profile r2 --only-show-errors

# Uzak temizlik: KEEP_DAYS'ten eski nesneleri sil.
CUTOFF=$(date -u -d "${KEEP_DAYS} days ago" +%Y-%m-%d)
aws s3 ls "${BUCKET}/" --profile r2 2>/dev/null | awk '{print $NF}' | while read -r obj; do
  obj_date=$(echo "$obj" | grep -oE "[0-9]{4}-[0-9]{2}-[0-9]{2}" || true)
  if [[ -n "$obj_date" && "$obj_date" < "$CUTOFF" ]]; then
    aws s3 rm "${BUCKET}/$obj" --profile r2 --only-show-errors
    echo "$LOG_PREFIX pruned old: $obj"
  fi
done

echo "$LOG_PREFIX env-backup done size=${SIZE}B remote=${BUCKET}/env-${STAMP}.gpg"
