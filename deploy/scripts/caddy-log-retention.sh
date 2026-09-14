#!/usr/bin/env bash
# Caddy erişim loglarında SATIR yaşını 30 günle sınırlar (R9-OPS02).
#
# Neden var: `roll_keep_for 720h` yalnız DÖNDÜRÜLMÜŞ dosyaları kapsar ve
# temizlik yeni bir roll oluşmasına bağlıdır. Aktif dosya sadece boyutla
# (50MiB) döner; az trafikte aylarca dolmaz ve gizlilik politikası §4'ün
# "erişim logları 30 gün içinde silinir" beyanı fiilen boşa düşer.
#
# Nasıl: her dosyanın İLK satırındaki `ts`e bakar (dosya mtime'ı satır yaşını
# söylemez — düşük trafikli dosya aylar öncesinden satır taşıyabilir):
#   • aktif dosya, ilk satırı ROTATE_AT_DAYS'i (27) geçtiyse `.aged-*` diye
#     kenara alınır ve caddy RESTART edilir. `caddy reload` BİLEREK değil:
#     Caddy aynı dosya adının writer'ını havuzdan yeniden kullanır, dosyayı
#     yeniden açmaz — taşınan inode'a yazmaya devam ederdi. Restart birkaç
#     saniye, en kötü ayda ~1 kez; WPF istemcisi ağ hatasında retry+grace'li.
#   • diğer her dosya (caddy'nin kendi roll'ları dahil), ilk satırı
#     MAX_AGE_DAYS'i (30) geçtiyse TÜMÜYLE silinir. Dosyadaki genç satırlar
#     erken gider — gizlilik hedefi "en geç 30 gün", erken silme ihlal değil.
# 27+3 payı: günlük cron aksasa da hiçbir satır 30 günü aşmadan yakalanır.
#
# Cron: 43 4 * * * /opt/orderdeck/scripts/caddy-log-retention.sh >>/var/log/orderdeck-caddy-retention.log 2>&1
set -euo pipefail

LOG_DIR="${CADDY_LOG_DIR:-/opt/orderdeck/caddy-logs}"   # test için override edilebilir
MAX_AGE_DAYS=30
ROTATE_AT_DAYS=27
ACTIVE_FILES=(license-access.log web-access.log)

now=$(date +%s)
prefix="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"

first_ts() { # ilk satırın epoch saniyesi; parse edilemezse boş
  local f="$1" line
  if [[ "$f" == *.gz ]]; then
    line=$(zcat "$f" 2>/dev/null | head -n1) || true
  else
    line=$(head -n1 "$f") || true
  fi
  sed -n 's/.*"ts":\([0-9]\{1,\}\)[.,].*/\1/p' <<<"$line"
}

age_days() { echo $(( (now - $1) / 86400 )); }

restart_needed=0
shopt -s nullglob
for f in "$LOG_DIR"/*; do
  [ -s "$f" ] || continue
  base=$(basename "$f")
  ts=$(first_ts "$f")
  if [ -z "$ts" ]; then
    echo "$prefix UYARI: $base ilk satırından ts okunamadı, atlandı"
    continue
  fi
  age=$(age_days "$ts")
  is_active=0
  for a in "${ACTIVE_FILES[@]}"; do [ "$base" = "$a" ] && is_active=1; done
  if (( is_active )); then
    if (( age >= ROTATE_AT_DAYS )); then
      mv "$f" "$f.aged-$(date +%Y%m%d%H%M%S)"
      restart_needed=1
      echo "$prefix ROTATE: $base (ilk satır ${age} gün) kenara alındı"
    fi
  elif (( age >= MAX_AGE_DAYS )); then
    rm -f "$f"
    echo "$prefix SİL: $base (ilk satır ${age} gün)"
  fi
done

if (( restart_needed )); then
  # lumberjack taşınan dosyanın handle'ını restart'a kadar bırakmaz;
  # restart sonrası ilk istekle taze aktif dosya oluşur.
  docker restart orderdeck-caddy >/dev/null
  echo "$prefix orderdeck-caddy restart edildi (yeni aktif log dosyası için)"
fi
