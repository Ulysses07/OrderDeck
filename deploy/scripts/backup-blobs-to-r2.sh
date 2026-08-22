#!/usr/bin/env bash
# Nightly customer backup blobs → R2. See HA-PLAYBOOK.md gap G6.
#
# `/opt/orderdeck/backups/{customerId}/*.bin` tek VPS'te duruyordu: host
# kaybı hepsini götürürdü. Blob'lar diske yazılmadan önce zaten AES-256-GCM
# ile şifreleniyor, dolayısıyla R2'de düz hâlde saklanan bir şey yok.
#
# Neden uygulama içi replikasyon değil: eski `Backup:S3` yolu POST bittikten
# sonra `Task.Run` ile ateşleyip unutuyordu — kayıt yok, yeniden deneme yok,
# ve her master merge'i sunucuyu yeniden başlattığı için uçuştaki kopyalama
# sessizce kayboluyordu. "Bu blob'un kopyası var mı" sorusu cevaplanamıyordu.
# `aws s3 sync` yapı gereği artımlı ve tekrar güvenli: bir gece kaçırılan
# dosya ertesi gece kendiliğinden gider.
#
# Cron: 0 4 * * * /opt/orderdeck/scripts/backup-blobs-to-r2.sh >>/var/log/orderdeck-blobs-backup.log 2>&1
set -euo pipefail

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX blobs-sync start"

SRC="/opt/orderdeck/backups"
DST="s3://orderdeck-prod-backups/customer-blobs"
ALLOW_MASS_DELETE="${ALLOW_MASS_DELETE:-0}"

mkdir -p "$SRC"

LOCAL=$(find "$SRC" -name '*.bin' -type f | wc -l)
# `aws s3 ls` boş prefix'te 1 ile çıkar; `|| true` olmadan set -e burayı keser.
REMOTE=$(aws s3 ls "${DST}/" --profile r2 --recursive 2>/dev/null | grep -c '\.bin$' || true)

echo "$LOG_PREFIX local=${LOCAL} remote=${REMOTE}"

# --delete ŞART: retention ve KVKK silme talebi yerelde uygulanıyor, off-site
# kopya da izlemezse silinmiş müşterinin verisi R2'de yaşamaya devam eder.
#
# Bedeli: R2'de nesne sürümleme YOK (yalnız prefix bazlı bucket lock var),
# yani yerelde yanlışlıkla oluşan toplu silme aynaya yansır ve geri alınamaz.
# Tel tuzağı bunun için: yerel sayı uzaktakinin yarısının altına düştüyse
# senkron çalışmaz. Normal retention (5 + aylık kilometre taşları) bu eşiğe
# yaklaşmaz; gerçekten büyük bir müşteri silindiyse operatör
# ALLOW_MASS_DELETE=1 ile bilerek çalıştırır.
if (( REMOTE > 0 )) && (( LOCAL * 2 < REMOTE )) && [[ "$ALLOW_MASS_DELETE" != "1" ]]; then
  echo "$LOG_PREFIX DURDURULDU: yerel blob sayisi (${LOCAL}) uzaktakinin"
  echo "$LOG_PREFIX yarisindan az (${REMOTE}). Toplu silme aynaya yansimasin"
  echo "$LOG_PREFIX diye senkron yapilmadi. Kasitliysa:"
  echo "$LOG_PREFIX   ALLOW_MASS_DELETE=1 $0"
  exit 1
fi

aws s3 sync "$SRC/" "${DST}/" --profile r2 --only-show-errors --delete

# Senkron sonrası sayım: sessiz başarısızlık (kısmi yükleme, yetki hatası
# yutulması) ancak böyle görünür olur. Uyarı yeterli — asıl kopya yerelde
# duruyor ve ertesi gece tekrar denenecek.
AFTER=$(aws s3 ls "${DST}/" --profile r2 --recursive 2>/dev/null | grep -c '\.bin$' || true)
if (( AFTER != LOCAL )); then
  echo "$LOG_PREFIX UYARI: senkron sonrasi uzak sayi (${AFTER}) yerelle (${LOCAL}) uyusmuyor"
fi

SIZE=$(du -sh "$SRC" | cut -f1)
echo "$LOG_PREFIX blobs-sync done local=${LOCAL} remote=${AFTER} size=${SIZE}"
