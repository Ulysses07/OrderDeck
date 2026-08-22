#!/usr/bin/env bash
# license-server'ı bir önceki sürüme geri al. Denetim maddesi O-12.
#
# Neden ayrı script, workflow içinde gömülü SSH bloğu değil:
#   1. Gömülü blok çift tırnak içinde çalışıyor, yani `$(...)` ve `$VAR`
#      RUNNER'da genişliyor. Uzakta değerlendirilmesi gereken her şey
#      kaçırılmak zorunda ve tek bir eksik `\` sessizce yanlış değeri yazar —
#      geri alma yolunda bunun bedeli, tam ihtiyaç anında yanlış sürüme
#      dönmektir.
#   2. Geri alma en çok gece 3'te, CI'sız, elle lazım olur. Script olarak
#      durursa `--apply` ile elle çalıştırılabilir.
#
# CI bu dosyayı her deploy'da VPS'e kopyalar (docker-compose.yml gibi), böylece
# "güvenlik ağı kurulu değildi" diye bir hata sınıfı kalmıyor.
#
# Modlar:
#   --capture --run-id <id> --compose-changed true|false
#       Deploy .env'i değiştirmeden ÖNCE mevcut durumu `.rollback`'a yazar.
#   --apply [--run-id <id>]
#       Kayıtlı duruma geri döner. --run-id verilirse ve dosya başka bir
#       koşuya aitse REDDEDER (bayat durumla geri alma yapılmaz).
#   --clear
#       Başarılı deploy sonrası durumu temizler.
set -euo pipefail

cd /opt/orderdeck

STATE=".rollback"
MODE=""
RUN_ID=""
COMPOSE_CHANGED=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --capture|--apply|--clear) MODE="${1#--}"; shift ;;
    --run-id)                  RUN_ID="${2:-}"; shift 2 ;;
    --compose-changed)         COMPOSE_CHANGED="${2:-}"; shift 2 ;;
    *) echo "bilinmeyen arguman: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$MODE" ]] || { echo "kullanim: $0 --capture|--apply|--clear [...]" >&2; exit 2; }

field() { sed -n "s/^$1=//p" "$STATE" 2>/dev/null | head -1; }

case "$MODE" in

capture)
  # Deploy adımının 3 denemelik retry'ı var. İlk deneme .env'i sed'leyip
  # sonrasında düşerse, ikinci denemenin "önceki sürüm" diye YENİ etiketi
  # kaydetmesi geri almayı bozuk imaja döndürürdü. O yüzden aynı koşuya ait
  # bir durum dosyası varsa dokunulmuyor.
  if [[ -f "$STATE" ]] && [[ "$(field RUN_ID)" == "$RUN_ID" ]]; then
    echo "rollback durumu bu kosu icin zaten kayitli, korunuyor"
    exit 0
  fi

  tag=$(sed -n 's/^LICENSE_SERVER_TAG=//p' .env 2>/dev/null | head -1 || true)
  sha=$(cat .deployed_sha 2>/dev/null || true)

  umask 077
  {
    echo "RUN_ID=${RUN_ID}"
    echo "PREV_TAG=${tag}"
    echo "PREV_SHA=${sha}"
    echo "COMPOSE_CHANGED=${COMPOSE_CHANGED}"
  } > "$STATE"
  echo "rollback durumu kaydedildi: tag=${tag:-<yok>} sha=${sha:0:7}"
  ;;

apply)
  [[ -f "$STATE" ]] || { echo "HATA: $STATE yok — geri alinacak kayitli durum yok." >&2; exit 1; }

  saved_run=$(field RUN_ID)
  prev_tag=$(field PREV_TAG)
  prev_sha=$(field PREV_SHA)
  changed=$(field COMPOSE_CHANGED)

  # Runner ölürse `.rollback` ortada kalabilir. Sonraki bir koşunun onu
  # kullanıp aylar önceki bir sürüme dönmesindense reddetmesi yeğdir.
  if [[ -n "$RUN_ID" && "$saved_run" != "$RUN_ID" ]]; then
    echo "HATA: $STATE baska bir kosuya ait (kayitli=${saved_run:-<yok>}, beklenen=${RUN_ID})." >&2
    echo "Bayat durumla geri alma yapilmaz. Elle geri alma icin --run-id'siz calistir." >&2
    exit 1
  fi

  if [[ -z "$prev_tag" ]]; then
    echo "HATA: PREV_TAG bos — donulecek onceki surum yok (ilk deploy?)." >&2
    exit 1
  fi

  if [[ "$changed" == "true" && -f docker-compose.yml.prev ]]; then
    # Bozuk hâli teşhis için sakla; üzerine yazılır, birikmez.
    cp -f docker-compose.yml docker-compose.yml.failed
    mv -f docker-compose.yml.prev docker-compose.yml
    echo "compose geri alindi (bozuk hali: docker-compose.yml.failed)"
    UP=(docker compose up -d)
  else
    UP=(docker compose up -d license-server)
  fi

  cur_tag=$(sed -n 's/^LICENSE_SERVER_TAG=//p' .env | head -1 || true)
  sed -i.bak "s|^LICENSE_SERVER_TAG=.*|LICENSE_SERVER_TAG=${prev_tag}|" .env
  echo "LICENSE_SERVER_TAG: ${cur_tag:-<yok>} -> ${prev_tag}"

  # Eski imaj büyük ihtimalle yerelde (prune 72h tutuyor); pull yalnızca
  # emniyet. Yerelde de yoksa `up -d` zaten patlar ve CI yuksek sesle soyler.
  docker compose pull license-server || true
  "${UP[@]}"

  # `.deployed_sha` gerçekte koşan sürümü göstermeli. Bırakılırsa teşhis
  # yanıltır ve bir sonraki koşunun sıralama koruması yanlış temele bakar.
  printf '%s\n' "${prev_sha}" > .deployed_sha

  # Kısa bir yerel sağlık işareti. Asıl uçtan doğrulamayı (public /ready)
  # CI yapıyor; buradaki amaç Caddy/DNS'e bağlı olmayan bir sinyal.
  sleep 5
  status=$(docker inspect -f '{{.State.Status}}' orderdeck-license 2>/dev/null || echo "yok")
  echo "orderdeck-license durumu: ${status}"
  if [[ "$status" != "running" ]]; then
    echo "HATA: konteyner geri alma sonrasi 'running' degil." >&2
    exit 1
  fi

  rm -f "$STATE" docker-compose.yml.prev
  echo "geri alma tamam: ${prev_tag} (sha ${prev_sha:0:7})"
  ;;

clear)
  rm -f "$STATE" docker-compose.yml.prev
  echo "rollback durumu temizlendi"
  ;;

esac
