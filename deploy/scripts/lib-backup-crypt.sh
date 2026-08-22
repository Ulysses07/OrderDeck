#!/usr/bin/env bash
# Ortak GPG simetrik sifreleme + uzak temizlik yardimcilari.
# Kaynak: backup-env-to-r2.sh, backup-sql-to-r2.sh, backup-keys-to-r2.sh
#
# Neden ortak dosya: cozulebilirlik turu (sifrele → coz → cmp) bir guvenlik
# ozelligi, uc kopyaya dagilirsa aralarinda sessizce kayar. Uc script de ayni
# parolayi ve ayni R2 bucket'ini kullaniyor.
#
# Tehdit modeli: bu sifreleme VPS'i ele geciren birine karsi HICBIR sey yapmaz
# (adam zaten kaynak dosyalari duz okuyor). R2'nin/token'in sizmasina karsi
# yapar. 2026-08-22 restore tatbikatinda tek bir salt-okunur R2 token'iyla tum
# prod veritabani duz metin restore edilebildi — bu dosya o deligi kapatmak
# icin var.

: "${PASS_FILE:=/opt/orderdeck/.env-backup-pass}"

# Parolayi bu scriptler URETMEZ. Uretselerdi parola yalnizca bu host'ta yasayan
# bir dosya olurdu ve host kaybinda R2'deki sifreli yedekler de kullanilamaz
# hale gelirdi — yani tam korumaya calistigimiz senaryoda ise yaramazdi.
# Kurulum: deploy/README.md, "Off-host replication".
require_passphrase() {
  if [[ ! -s "$PASS_FILE" ]]; then
    echo "${LOG_PREFIX:-} HATA: $PASS_FILE yok veya bos."
    echo "${LOG_PREFIX:-} Kurulum icin bkz. deploy/README.md — parola once"
    echo "${LOG_PREFIX:-} parola yoneticisine kaydedilmeli, sonra bu dosyaya yazilmali."
    exit 1
  fi
}

# encrypt_verified <kaynak> <hedef.gpg>
#
# Yukleme ONCESI cozulebilirlik kontrolu yapar. Sifrelenmis ama acilamayan bir
# yedek hic yedek almamaktan daha kotudur: felaket anina kadar yedegin
# oldugunu sanirsin. Bozuk bir parola dosyasi boylece bozuldugu gece patlar,
# ihtiyac duyulan gece degil.
encrypt_verified() {
  local src="$1" dst="$2" rt="${2}.roundtrip"

  gpg --batch --yes --quiet \
      --symmetric --cipher-algo AES256 \
      --passphrase-file "$PASS_FILE" \
      --output "$dst" "$src"

  gpg --batch --yes --quiet \
      --decrypt --passphrase-file "$PASS_FILE" \
      --output "$rt" "$dst"

  if ! cmp -s "$src" "$rt"; then
    rm -f "$rt"
    echo "${LOG_PREFIX:-} HATA: sifre-coz turu basarisiz ($src), yukleme yapilmadi"
    exit 1
  fi
  rm -f "$rt"
}

# prune_dated_objects <s3-prefix-slashsiz> <gun>
#
# Yalnizca adinda YYYY-AA-GG tasiyan nesneleri siler. Tarihsiz bir nesne
# (ornegin sifrelemeye gecmeden once birakilmis eski bir dosya) bilerek
# dokunulmadan kalir — otomatik temizligin tanimadigi bir seyi silmesi
# istenmiyor.
prune_dated_objects() {
  local prefix="$1" keep="$2" cutoff
  cutoff=$(date -u -d "${keep} days ago" +%Y-%m-%d)

  aws s3 ls "${prefix}/" --profile r2 2>/dev/null | awk '{print $NF}' | while read -r obj; do
    local obj_date
    obj_date=$(echo "$obj" | grep -oE "[0-9]{4}-[0-9]{2}-[0-9]{2}" || true)
    if [[ -n "$obj_date" && "$obj_date" < "$cutoff" ]]; then
      aws s3 rm "${prefix}/$obj" --profile r2 --only-show-errors
      echo "${LOG_PREFIX:-} pruned old: $obj"
    fi
  done
}
