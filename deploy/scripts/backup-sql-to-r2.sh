#!/usr/bin/env bash
# Nightly SQL .bak → R2, GPG şifreli. See HA-PLAYBOOK.md gap G2.
#
# Şifreleme neden şart: `.bak` ham müşteri verisidir (Customers, Payments,
# GiveawayParticipants — KVKK kapsamında). Düz gzip olarak R2'de dururken
# `.env`'i şifrelemek tiyatroydu: R2 token'ını ele geçiren birinin `.env`'e
# ihtiyacı yok, veri zaten `.bak`'ta. 2026-08-22 restore tatbikatında tek bir
# *salt-okunur* token'la tüm prod veritabanı temiz bir makinede restore edildi.
#
# Yerel kopya (KVKK ön inceleme Ö-12, 2026-10): sunucunun kendi diskinde de
# YALNIZ şifreli kopya kalır, en çok 1 gün. Önceden şifresiz .bak.gz ~4 gün
# duruyordu; politika "şifreli yedekler" diyordu. Düz .bak / .bak.gz yalnız
# şifrelenene kadar yaşar: betik nerede düşerse düşsün EXIT tuzağı siler.
# Önceki günlerin kopyası bu koşunun BAŞINDA silinir (0. adım): koşu düşse de
# diskte bir günden eski kopya kalmaz; kopyalar R2'de 30 gün durur.
# Yerelden geri yükleme (x.bak düz kopyadır, işin bitince sil):
#   gpg --batch --passphrase-file /opt/orderdeck/.env-backup-pass \
#     --decrypt /opt/orderdeck/sql-data/backup/orderdeck-<tarih>.bak.gz.gpg | gunzip > x.bak
#
# Cron: 0 3 * * * /opt/orderdeck/scripts/backup-sql-to-r2.sh >>/var/log/orderdeck-sql-backup.log 2>&1
set -euo pipefail
# Betiğin yazdığı her dosya (şifreli kopya, şifre-çöz turunun düz ara dosyası) yalnız sahibine açık.
umask 077

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX backup-sql start"

# shellcheck source=lib-backup-crypt.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/lib-backup-crypt.sh"

STAMP=$(date -u +%Y-%m-%d)
LOCAL_DIR="/opt/orderdeck/sql-data/backup"
CIPHER="${LOCAL_DIR}/orderdeck-${STAMP}.bak.gz.gpg"

# Tek koşu: aynı anda ikinci bir kopya (elle koşu gece koşusuna denk gelirse) 0. adımda
# birincinin yarıdaki .bak'ını ve çalışma klasörünü, tuzağıyla da ortak adlı .bak'ı
# silerdi; ikisi de düşer, dünkü kopya da gitmiş olurdu. İkincisi hiçbir şeye
# dokunmadan çıkar. Kilit süreç bitince (düşse de) kendiliğinden bırakılır.
exec 9>/run/orderdeck-sql-backup.lock
flock -n 9 || { echo "$LOG_PREFIX başka bir koşu sürüyor, bu koşu hiçbir şeye dokunmadan çıktı"; exit 1; }

# 0) Yerel saklama, koşunun BAŞINDA ve parola denetiminden de önce: koşu hangi
#    adımda düşerse düşsün (parola dosyası yok, SQL kapalı, R2'ye ulaşılamıyor)
#    diskte bir günden eski kopya kalmaz. Zamana değil ada bakar: cron saati
#    kayarsa dünkü kopya -mtime eşiğine takılıp kalmasın. Bugünkü kopya (aynı gün
#    yeniden koşu) 3. adımda yenisiyle ezilir. Eski sürümden kalmış ya da elle
#    alınmış düz orderdeck-* kopyaları ve yarım kalmış çalışma klasörleri de gider.
find "$LOCAL_DIR" -maxdepth 1 -type f -name "orderdeck-*.bak.gz.gpg" ! -name "$(basename "$CIPHER")" -delete
find "$LOCAL_DIR" -maxdepth 1 -type f \( -name "orderdeck-*.bak.gz" -o -name "orderdeck-*.bak" \) -delete
find "$LOCAL_DIR" -maxdepth 1 -type d -name ".enc.*" -exec rm -rf {} +
# Başka adla bırakılmış yedeği (ör. geri yüklemenin x.bak'ı) silmez, tanımadığı
# bir şeyi silmesin; ama her gece günlüğe yazar.
find "$LOCAL_DIR" -maxdepth 1 -type f \( -name "*.bak" -o -name "*.bak.gz" -o -name "*.bak.gz.gpg" \) ! -name "orderdeck-*" \
  | while IFS= read -r stray; do
      echo "$LOG_PREFIX UYARI: tanınmayan yedek dosyası yerelde duruyor, betik silmez (KVKK Ö-12; işin bittiyse sil): $stray"
    done

# Parola kontrolü BACKUP DATABASE'den önce: parola yoksa yükleyemeyeceğimiz bir
# yedek için SQL'i yarım saat meşgul etmenin anlamı yok.
require_passphrase

SQL_PASSWORD=$(grep ^SQL_PASSWORD /opt/orderdeck/.env | cut -d= -f2)
KEEP_DAYS=30
BUCKET="s3://orderdeck-prod-backups/sql-bak"
BAK="/var/opt/mssql/backup/orderdeck-${STAMP}.bak"
HOST_BAK="${LOCAL_DIR}/orderdeck-${STAMP}.bak"
GZ="${HOST_BAK}.gz"
# Şifreleme ve şifre-çöz turu 700 izinli ayrı bir klasörde (aynı dosya sisteminde:
# büyük dosya /tmp'ye taşınmaz). LOCAL_DIR SQL konteynerine bağlı; şifre-çöz
# turunun düz ara dosyası (.roundtrip) orada başka izinlerle bir an bile durmasın.
WORK=$(mktemp -d "${LOCAL_DIR}/.enc.XXXXXX")
chmod 700 "$WORK"

# Düz kopyalar ve şifre-çöz turunun ara dosyası betik nasıl biterse bitsin silinir.
trap 'rm -f "$HOST_BAK" "$GZ"; rm -rf "$WORK"' EXIT

# 1) BACKUP DATABASE inside container.
#    INIT overwrites if today's file already exists (rerun-safe).
docker exec orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SQL_PASSWORD" -C -No \
  -Q "BACKUP DATABASE OrderDeckLicense TO DISK = N'$BAK' WITH FORMAT, INIT" \
  > /tmp/sql-backup.log 2>&1 || { echo "$LOG_PREFIX BACKUP DATABASE failed:"; cat /tmp/sql-backup.log; exit 1; }

if [[ ! -f "$HOST_BAK" ]]; then echo "$LOG_PREFIX bak file missing on host: $HOST_BAK"; exit 1; fi

# 2) Compress (Express has no native COMPRESSION; gzip externally).
gzip -9 -f "$HOST_BAK"

# 3) Encrypt in the private work dir (roundtrip file lives there too), then
#    drop the plaintext at once and move the cipher next to the backups.
#    Sıra önemli: önce sıkıştır sonra şifrele — tersi sıkışmaz.
encrypt_verified "$GZ" "$WORK/cipher.gpg"
rm -f "$GZ"
mv "$WORK/cipher.gpg" "$CIPHER"
SIZE=$(stat -c%s "$CIPHER")

# 4) Upload to R2 with date prefix.
aws s3 cp "$CIPHER" "${BUCKET}/orderdeck-${STAMP}.bak.gz.gpg" \
  --profile r2 --only-show-errors

# 5) Remote retention.
prune_dated_objects "$BUCKET" "$KEEP_DAYS"

echo "$LOG_PREFIX backup-sql done size=${SIZE}B remote=${BUCKET}/orderdeck-${STAMP}.bak.gz.gpg local=${CIPHER}"
