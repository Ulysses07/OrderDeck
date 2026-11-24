#!/usr/bin/env python3
"""Velopack delta tabanını seçer ve indirilen paketleri doğrular (release.yml → feed-baseline).

Delta paketi "bir önceki YAYINLANMIŞ sürüm"e göre üretilmelidir. Kurulu uygulama
delta'yı kendi paketinin üstüne uygular; taban başka bir sürümse sağlama tutmaz
ve uygulama tam pakete (yaklaşık 106 MB) döner.

Neden ayrı ve testli bir betik: v0.9.11, v0.9.12 ve v0.9.13'te taban hep 0.9.9
seçildi. Eski adım feed klasöründe DEĞİŞİKLİK ZAMANI en yeni iki pakete
bakıyordu (`ls -1t | head -n 2`). publish işi taban paketlerini de yeniden
yüklüyordu ve scp ad sırasıyla gönderdiği için `OrderDeck-0.9.9-*` dosyaları
`OrderDeck-0.9.1x-*` dosyalarından sonra gidiyor, en yeni zaman her sürümden
sonra yine 0.9.9'da kalıyordu. Üç sürümün delta'sı 24–26 MB çıktı ve 0.9.9
dışındaki hiçbir kurulumda uygulanamadı; iş akışındaki "delta üretildi mi"
denetimi yine de yeşildi.

select kuralları:
  - Doğruluk kaynağı sunucudaki releases.win.json'dur: istemcinin gördüğü odur.
    Dosya tarihine ve ad sırasına bakılmaz.
  - Taban = manifestteki, yeni sürümden KÜÇÜK en büyük Full sürüm (SemVer 2.0
    sırası). Aynı sürümün Delta kaydı varsa o da indirilir: yeni manifestte yer
    alır ve bir sürüm geriden gelen kurulum iki delta'yı zincirleyebilir.
  - Manifestte yeni sürümden BÜYÜK bir sürüm varsa dur: yayın feed'i geri götürür.
  - Manifest yok ve feed'de paket yok → ilk sürüm; taban yok, delta üretilmez.
  - Manifest yok ya da boş ama feed'de paket var → dur. Tutarsız feed'de
    sessizce deltasız sürüm çıkmasın.
  - Seçilen kaydın dosya adı, SHA256'sı ya da boyutu bozuksa dur.

verify: indirilen her dosyanın boyutu ve SHA256'sı manifesttekiyle aynı olmalı
(yarım inen ya da başka bir dosyayla değişmiş taban, yanlış delta üretir).

Kullanım:
  delta-baseline.py select --version 0.9.14 --remote-nupkg-count 80 \\
      [--manifest baseline/releases.win.json] --out secim.json \\
      [--github-output "$GITHUB_OUTPUT"]
    stdout: indirilecek dosya adları, satır başına bir (taban yoksa boş).
    Gerekçe stderr'e. --github-output verilirse `baseline_version=<sürüm>` eklenir
    (taban yoksa değer boş).
  delta-baseline.py verify --selection secim.json --dir baseline

Çıkış kodu 0 = tamam, 1 = kural ihlali (stderr'de `::error::` satırı).
Testler: test_delta_baseline.py (build-test.yml → script-tests).
"""

import argparse
import hashlib
import json
import os
import re
import sys

# SemVer 2.0: MAJOR.MINOR.PATCH[-ön sürüm][+derleme eki]. Dört haneli NuGet
# sürümleri ve baştaki "v" bilerek kabul edilmez (Velopack SemVer ister).
VERSION = re.compile(
    r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?")

# Ad uzak kabuğa (scp yolu) gider: yalnız düz paket adına izin ver.
FILE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]*\.nupkg")
SHA256 = re.compile(r"[0-9A-Fa-f]{64}")


class FeedError(Exception):
    """Feed'in durumu ya da bir kayıt, güvenle taban seçmeye izin vermiyor."""


def version_key(text):
    """Sürümü SemVer 2.0 önceliğine göre sıralanabilir bir anahtara çevirir."""
    match = VERSION.fullmatch(text) if isinstance(text, str) else None
    if match is None:
        raise FeedError(f"desteklenmeyen sürüm biçimi: {text!r} (beklenen: 1.2.3 ya da 1.2.3-rc.1)")
    core = tuple(int(part) for part in match.group(1, 2, 3))
    prerelease = match.group(4)
    if prerelease is None:
        return core + ((1,),)  # ön sürümü olmayan, aynı çekirdeğin ön sürümlerinden büyüktür
    # Sayısal kimlik < harfli kimlik; sayısallar sayı, harfliler ASCII sırasıyla.
    identifiers = tuple(
        (0, int(part), "") if part.isdigit() else (1, 0, part)
        for part in prerelease.split("."))
    return core + ((0, identifiers),)


def _entries(manifest):
    if not isinstance(manifest, dict) or not isinstance(manifest.get("Assets"), list):
        raise FeedError("releases.win.json beklenen biçimde değil ('Assets' listesi yok)")
    entries = []
    for index, raw in enumerate(manifest["Assets"]):
        if not isinstance(raw, dict):
            raise FeedError(f"manifest kaydı {index} bir nesne değil")
        version, kind = raw.get("Version"), raw.get("Type")
        if not isinstance(kind, str):
            raise FeedError(f"manifest kaydı {index}: 'Type' yok")
        entries.append({"key": version_key(version), "version": version, "kind": kind, "raw": raw})
    return entries


def _file(entry):
    raw = entry["raw"]
    label = f"{entry['version']} {entry['kind']}"
    name, digest, size = raw.get("FileName"), raw.get("SHA256"), raw.get("Size")
    if not isinstance(name, str) or FILE_NAME.fullmatch(name) is None:
        raise FeedError(f"{label}: dosya adı kabul edilmedi: {name!r}")
    if not isinstance(digest, str) or SHA256.fullmatch(digest) is None:
        raise FeedError(f"{label} ({name}): SHA256 yok ya da bozuk")
    if isinstance(size, bool) or not isinstance(size, int) or size <= 0:
        raise FeedError(f"{label} ({name}): boyut yok ya da bozuk: {size!r}")
    return {"type": entry["kind"], "fileName": name, "sha256": digest, "size": size}


def _single(entries, version, kind):
    found = [e for e in entries if e["version"] == version and e["kind"] == kind]
    if len(found) > 1:
        raise FeedError(f"manifestte {version} için birden çok {kind} kaydı var")
    return found[0] if found else None


def select_baseline(manifest, new_version, remote_nupkg_count):
    """Tabanı seçer. Dönüş: {"version": sürüm | None, "files": [Full, (Delta)]}."""
    new_key = version_key(new_version)
    none = {"version": None, "files": []}

    if manifest is None:
        if remote_nupkg_count > 0:
            raise FeedError(
                f"feed'de {remote_nupkg_count} paket var ama releases.win.json yok; "
                "taban güvenle seçilemez")
        return none

    entries = _entries(manifest)
    if not entries:
        if remote_nupkg_count > 0:
            raise FeedError(
                f"feed'de {remote_nupkg_count} paket var ama releases.win.json boş; "
                "taban güvenle seçilemez")
        return none

    newer = sorted({e["version"] for e in entries if e["key"] > new_key}, key=version_key)
    if newer:
        raise FeedError(
            f"feed'de {new_version} sürümünden yeni sürüm var ({', '.join(newer)}); "
            "bu sürüm yayınlanırsa feed geri gider")

    older = [e for e in entries if e["key"] < new_key]
    if not older:
        return none  # manifestte yalnız bu sürümün kendisi var: öncesi olmayan sürüm yeniden koşuyor

    fulls = [e for e in older if e["kind"] == "Full"]
    if not fulls:
        raise FeedError("manifestte eski sürüm kaydı var ama hiçbiri Full değil; taban seçilemez")

    version = max(fulls, key=lambda e: e["key"])["version"]
    files = [_file(_single(entries, version, "Full"))]
    delta = _single(entries, version, "Delta")
    if delta is not None:
        files.append(_file(delta))
    return {"version": version, "files": files}


def verify_files(selection, directory):
    """İndirilen dosyaların boyutunu ve SHA256'sını seçimdeki değerlerle karşılaştırır."""
    for expected in selection["files"]:
        name = expected["fileName"]
        path = os.path.join(directory, name)
        if not os.path.isfile(path):
            raise FeedError(f"{name}: indirilen dosya yok")
        size = os.path.getsize(path)
        if size != expected["size"]:
            raise FeedError(f"{name}: boyut {size} B, manifestte {expected['size']} B (yarım mı indi?)")
        digest = hashlib.sha256()
        with open(path, "rb") as stream:
            for chunk in iter(lambda: stream.read(1 << 20), b""):
                digest.update(chunk)
        if digest.hexdigest().lower() != expected["sha256"].lower():
            raise FeedError(f"{name}: SHA256 manifesttekiyle aynı değil")


def _load_json(path, what):
    try:
        with open(path, encoding="utf-8-sig") as stream:
            return json.load(stream)
    except OSError as error:
        raise FeedError(f"{what} okunamadı: {path} ({error.strerror or error})") from error
    except ValueError as error:
        raise FeedError(f"{what} geçerli JSON değil: {path} ({error})") from error


def _select(args):
    manifest = None if args.manifest is None else _load_json(args.manifest, "releases.win.json")
    selection = select_baseline(manifest, args.version, args.remote_nupkg_count)

    # Satır sonu her platformda "\n": çıktıları bash ve GitHub satır satır okur.
    with open(args.out, "w", encoding="utf-8", newline="\n") as stream:
        json.dump(selection, stream, indent=2)
        stream.write("\n")
    if args.github_output:
        with open(args.github_output, "a", encoding="utf-8", newline="\n") as stream:
            stream.write(f"baseline_version={selection['version'] or ''}\n")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(newline="\n")

    if selection["version"] is None:
        print(f"Delta tabanı yok: feed'de {args.version} öncesi sürüm bulunmuyor (ilk sürüm).", file=sys.stderr)
    else:
        kinds = " + ".join(f["type"] for f in selection["files"])
        print(f"Delta tabanı: {selection['version']} ({kinds}); yeni sürüm {args.version}.", file=sys.stderr)
    for item in selection["files"]:
        print(item["fileName"])


def _verify(args):
    selection = _load_json(args.selection, "seçim dosyası")
    if not isinstance(selection, dict) or not isinstance(selection.get("files"), list):
        raise FeedError(f"seçim dosyası beklenen biçimde değil: {args.selection}")
    verify_files(selection, args.dir)
    for item in selection["files"]:
        print(f"doğrulandı: {item['fileName']} ({item['size']} B)", file=sys.stderr)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Velopack delta tabanını seç ve doğrula.")
    commands = parser.add_subparsers(dest="command", required=True)

    select = commands.add_parser("select", help="tabanı manifestten seç")
    select.add_argument("--version", required=True, help="yayınlanan sürüm (baştaki v olmadan)")
    select.add_argument("--remote-nupkg-count", required=True, type=int,
                        help="sunucudaki feed klasöründe kaç .nupkg var")
    select.add_argument("--manifest", help="sunucudan indirilen releases.win.json (sunucuda yoksa verme)")
    select.add_argument("--out", required=True, help="seçimin yazılacağı JSON dosyası")
    select.add_argument("--github-output", help="baseline_version satırının ekleneceği dosya")
    select.set_defaults(run=_select)

    verify = commands.add_parser("verify", help="indirilen dosyaları seçimle karşılaştır")
    verify.add_argument("--selection", required=True, help="select'in yazdığı JSON dosyası")
    verify.add_argument("--dir", required=True, help="dosyaların indirildiği klasör")
    verify.set_defaults(run=_verify)

    args = parser.parse_args(argv)
    try:
        args.run(args)
    except FeedError as error:
        print(f"::error::Delta tabanı: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
