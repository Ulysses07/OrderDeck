#!/usr/bin/env python3
"""Sunucu test parçalarının eksiksizlik koruması (build-test.yml → license-server-test).

Her parça (license-server-shard) koştuğu süreçte gördüğü ve seçtiği vakaları bir
manifeste yazar (OrderDeck.LicenseServer.Tests/TestHelpers/ShardingTestFramework.cs).
Parçalar yeşil olsa bile şunlar sessizce test kaybettirebilirdi: özetin bir
parçada farklı hesaplanması, bir parçaya kaçak bir --filter eklenmesi, eksik ya
da fazladan bir matris girdisi, adaptörün vakaları yürütücüye eksik vermesi. Bu
betik onları kırmızıya çevirir:

  1. Matristeki her (tür, sıra) için tam bir manifest var; başlıktaki adet matrisle aynı.
  2. Bütün manifestlerin ALL kümeleri (tür etiketiyle) ve vaka sayıları birebir aynı.
  3. MINE'ların çoklu küme birleşimi ALL'a eşit: her anahtar tam bir kez (eksik =
     hiç koşmamış test, fazla = iki kez koşmuş test); her MINE parçanın türünde;
     boş MINE yok; seçilen vakaların toplamı tüm vakalara eşit.
  4. ALL, assembly'nin kendi raporuna dayanmayan bağımsız listeyle (dotnet test
     --list-tests) aynı: görünen ad kökü kümesi ve vaka sayısı. --list-tests
     görünen adı basar; kök = ilk '(' öncesi, kırpılmış (teori satırları aynı
     kökte birleşir; DisplayName/methodDisplay kullanan testler de eşleşir).
     Kural TestShard.DisplayRootOf ile aynı olmalı.
  5. Manifest seçimi kanıtlar, yürütmeyi değil (MINE ile yürütücüye giden liste
     aynı değişkenden gelir). Bu yüzden her parçanın TRX'i zorunlu: TRX'te sonucu
     olan metot kümesi MINE'a eşit ve sonuç sayısı en az seçilen vaka kadar
     (çalışma anında açılan teori satırları yalnız ekler; Skip'li testler
     NotExecuted olarak yine listede).
  6. Bilgi: parça başına vaka/sonuç sayısı ve TRX'teki start→finish süresi.

Kullanım: check-test-shards.py --dir <indirilen artifact'ler> --matrix '<json>'
Çıkış kodu 0 = geçti, 1 = en az bir ihlal.
"""

import argparse
import collections
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET
from datetime import datetime

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
LIST_FILE = "list-tests.txt"


def read_manifest(path, errors):
    header = None
    all_entries = set()
    mine = []
    with open(path, encoding="utf-8") as f:
        for lineno, raw in enumerate(f, 1):
            line = raw.rstrip("\n")
            if not line:
                continue
            parts = line.split("\t")
            tag = parts[0]
            if tag == "SHARD" and len(parts) == 6:
                if header is not None:
                    errors.append(f"{path}: birden çok SHARD başlığı (yürütücü aynı süreçte iki kez çağrıldı?)")
                header = {
                    "kind": parts[1],
                    "index": int(parts[2]),
                    "count": int(parts[3]),
                    "all_cases": int(parts[4]),
                    "mine_cases": int(parts[5]),
                }
            elif tag == "ALL" and len(parts) == 4:
                # (tür, Sınıf.Metot, görünen ad kökü)
                all_entries.add((parts[1], parts[2], parts[3]))
            elif tag == "MINE" and len(parts) == 2:
                mine.append(parts[1])
            else:
                errors.append(f"{path}:{lineno}: tanınmayan satır: {line[:120]!r}")
    if header is None:
        errors.append(f"{path}: SHARD başlığı yok")
    return header, all_entries, mine


def display_root(name):
    """TestShard.DisplayRootOf ile aynı kural: ilk '(' öncesi, kırpılmış."""
    return name.split("(", 1)[0].strip()


def read_list_tests(path):
    """`dotnet test --list-tests`: başlık satırlarından sonra her vaka 4 boşluk
    girintiyle, görünen adıyla (teoride '(arg: ...)' ekiyle) yazılır."""
    roots = set()
    cases = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            if not raw.startswith("    "):
                continue
            name = raw.strip()
            if not name:
                continue
            cases += 1
            roots.add(display_root(name))
    return roots, cases


def parse_trx_time(value):
    # .NET yedi haneli kesir yazar (2026-10-25T12:00:00.1234567+00:00); Python altıya kadar okur.
    if "." in value:
        head, rest = value.split(".", 1)
        frac = "".join(ch for ch in rest if ch.isdigit())
        tz = rest[len(frac):]
        value = f"{head}.{frac[:6]}{tz}"
    return datetime.fromisoformat(value)


def read_trx(path):
    """Süre, sayaçlar ve sonucu olan metotlar. TRX'te her UnitTestResult bir
    UnitTest tanımına (testId) bağlı; tanımın TestMethod'u className + name
    taşır (xUnit adaptöründe Sınıf.Metot, iç içe sınıfta Dış+İç — manifestteki
    MethodKey ile aynı biçim)."""
    root = ET.parse(path).getroot()
    info = {}
    methods_by_id = {}
    for unit in root.findall(f"{TRX_NS}TestDefinitions/{TRX_NS}UnitTest"):
        method = unit.find(f"{TRX_NS}TestMethod")
        if method is not None:
            methods_by_id[unit.get("id")] = f"{method.get('className')}.{method.get('name')}"
    ran = set()
    results = 0
    unmapped = 0
    for result in root.findall(f"{TRX_NS}Results/{TRX_NS}UnitTestResult"):
        results += 1
        method = methods_by_id.get(result.get("testId"))
        if method is None:
            unmapped += 1
        else:
            ran.add(method)
    info["ran"] = ran
    info["results"] = results
    info["unmapped"] = unmapped
    times = root.find(f"{TRX_NS}Times")
    if times is not None and times.get("start") and times.get("finish"):
        try:
            delta = parse_trx_time(times.get("finish")) - parse_trx_time(times.get("start"))
            info["seconds"] = delta.total_seconds()
        except ValueError:
            pass
    counters = root.find(f"{TRX_NS}ResultSummary/{TRX_NS}Counters")
    if counters is not None:
        info["executed"] = counters.get("executed")
        info["passed"] = counters.get("passed")
        info["failed"] = counters.get("failed")
    return info


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", required=True)
    parser.add_argument("--matrix", required=True, help="changes işinin matrix çıktısı ({include:[{kind,index,count}]})")
    args = parser.parse_args()

    errors = []
    expected = {(e["kind"], int(e["index"])): int(e["count"]) for e in json.loads(args.matrix)["include"]}
    if not expected:
        errors.append("matris boş")

    # --- 1. her matris girdisi için tam bir manifest ---
    manifests = {}
    for path in sorted(glob.glob(os.path.join(args.dir, "**", "*.manifest"), recursive=True)):
        header, all_entries, mine = read_manifest(path, errors)
        if header is None:
            continue
        key = (header["kind"], header["index"])
        if key in manifests:
            errors.append(f"{key[0]}:{key[1]} için birden çok manifest ({path})")
            continue
        if key not in expected:
            errors.append(f"{path}: {key[0]}:{key[1]} matriste yok")
        elif header["count"] != expected[key]:
            errors.append(f"{path}: başlıktaki adet {header['count']}, matriste {expected[key]}")
        manifests[key] = (header, all_entries, mine, path)
    for key in sorted(expected):
        if key not in manifests:
            errors.append(f"{key[0]}:{key[1]}/{expected[key]} parçasının manifesti yok (parça koşmadı ya da yazamadı)")

    if not manifests:
        report(errors, [], None)
        return 1

    # --- 2. ALL kümeleri ve vaka sayıları aynı ---
    ref_key = sorted(manifests)[0]
    ref_header, ref_all, _, ref_path = manifests[ref_key]
    for key, (header, all_entries, _, path) in sorted(manifests.items()):
        if all_entries != ref_all:
            only_here = sorted(all_entries - ref_all)[:5]
            only_ref = sorted(ref_all - all_entries)[:5]
            errors.append(
                f"{path}: ALL kümesi {ref_path} ile farklı ({len(all_entries)} / {len(ref_all)}); "
                f"yalnız burada: {only_here}, yalnız orada: {only_ref} (kaçak --filter?)")
        if header["all_cases"] != ref_header["all_cases"]:
            errors.append(f"{path}: vaka sayısı {header['all_cases']}, {ref_path}'de {ref_header['all_cases']}")

    kind_of = {}
    display_roots = set()
    for kind, method, root in ref_all:
        if method in kind_of and kind_of[method] != kind:
            errors.append(f"{method}: iki farklı türde ({kind_of[method]}, {kind})")
        kind_of[method] = kind
        display_roots.add(root)

    # --- 3. MINE birleşimi = ALL, her anahtar tam bir kez ---
    owners = collections.defaultdict(list)
    mine_cases_total = 0
    for key, (header, _, mine, path) in sorted(manifests.items()):
        label = f"{key[0]}:{key[1]}"
        if not mine:
            errors.append(f"{label}: hiç vaka seçmedi (boş parça)")
        mine_cases_total += header["mine_cases"]
        for method in mine:
            owners[method].append(label)
            if kind_of.get(method) not in (None, key[0]):
                errors.append(f"{method}: tür {kind_of[method]}, ama {label} parçası seçti")
    missing = sorted(set(kind_of) - set(owners))
    extra = sorted(m for m in owners if m not in kind_of)
    double = sorted((m, o) for m, o in owners.items() if len(o) > 1)
    if missing:
        errors.append(f"{len(missing)} metot hiçbir parçada koşmadı: {missing[:10]}")
    if extra:
        errors.append(f"{len(extra)} metot ALL'da yok ama seçilmiş: {extra[:10]}")
    if double:
        errors.append(f"{len(double)} metot birden çok parçada koştu: {double[:10]}")
    if mine_cases_total != ref_header["all_cases"]:
        errors.append(f"seçilen vakaların toplamı {mine_cases_total}, tüm vakalar {ref_header['all_cases']}")

    # --- 4. bağımsız liste (--list-tests) ---
    list_paths = glob.glob(os.path.join(args.dir, "**", LIST_FILE), recursive=True)
    listed = None
    if len(list_paths) != 1:
        errors.append(f"{LIST_FILE} tam bir kez bulunmalı, {len(list_paths)} bulundu")
    else:
        listed_roots, listed_cases = read_list_tests(list_paths[0])
        listed = listed_cases
        if not listed_roots:
            errors.append(f"{LIST_FILE} boş ya da okunamadı")
        not_run = sorted(listed_roots - display_roots)
        unknown = sorted(display_roots - listed_roots)
        if not_run:
            errors.append(f"{len(not_run)} görünen ad --list-tests'te var, manifestlerde yok: {not_run[:10]}")
        if unknown:
            errors.append(f"{len(unknown)} görünen ad manifestlerde var, --list-tests'te yok: {unknown[:10]}")
        if listed_cases != ref_header["all_cases"]:
            errors.append(f"--list-tests {listed_cases} vaka, parçalar {ref_header['all_cases']} vaka gördü")

    # --- 5. yürütme: her parçanın TRX'inde tam olarak seçtiği metotlar koştu ---
    rows = []
    trx_by_name = {os.path.basename(p): p for p in glob.glob(os.path.join(args.dir, "**", "*.trx"), recursive=True)}
    for key, (header, _, mine, _) in sorted(manifests.items()):
        label = f"{key[0]}:{key[1]}"
        trx_name = f"license-server-{key[0]}-{key[1]}.trx"
        trx = trx_by_name.get(trx_name)
        info = {}
        if trx is None:
            errors.append(f"{label}: {trx_name} yok; seçilen testlerin koştuğu doğrulanamıyor")
        else:
            try:
                info = read_trx(trx)
            except ET.ParseError as e:
                errors.append(f"{label}: {trx_name} okunamadı ({e})")
        if "ran" in info:
            selected = set(mine)
            not_ran = sorted(selected - info["ran"])
            stray = sorted(info["ran"] - selected)
            if not_ran:
                errors.append(f"{label}: {len(not_ran)} seçilen metodun TRX'te sonucu yok (seçildi ama koşmadı): {not_ran[:10]}")
            if stray:
                errors.append(f"{label}: {len(stray)} metot TRX'te koşmuş ama parça seçmemiş: {stray[:10]}")
            if info["unmapped"]:
                errors.append(f"{label}: TRX'te {info['unmapped']} sonuç bir test tanımına bağlanamadı")
            if info["results"] < header["mine_cases"]:
                errors.append(
                    f"{label}: TRX'te {info['results']} sonuç, parça {header['mine_cases']} vaka seçti "
                    f"(sonuç en az seçilen vaka kadar olmalı)")
        rows.append((f"{label}/{header['count']}", len(mine), header["mine_cases"], info))

    # --- 6. bilgi: parça başına sayı ve süre (aşağıdaki tablo) ---

    summary = {
        "methods": len(kind_of),
        "cases": ref_header["all_cases"],
        "listed": listed,
        "relational": sum(1 for k in kind_of.values() if k == "relational"),
        "inmemory": sum(1 for k in kind_of.values() if k == "inmemory"),
    }
    report(errors, rows, summary)
    return 1 if errors else 0


def report(errors, rows, summary):
    lines = ["## Sunucu test parçaları", ""]
    if summary:
        lines.append(
            f"Toplam {summary['cases']} vaka / {summary['methods']} metot "
            f"(ilişkisel {summary['relational']}, InMemory {summary['inmemory']} metot); "
            f"--list-tests: {summary['listed']} vaka.")
        lines.append("")
    if rows:
        lines.append("| parça | metot | vaka | TRX sonuç | TRX koşulan | başarısız | süre (sn) |")
        lines.append("|---|---:|---:|---:|---:|---:|---:|")
        for label, methods, cases, info in rows:
            seconds = f"{info['seconds']:.0f}" if "seconds" in info else "?"
            lines.append(
                f"| {label} | {methods} | {cases} | {info.get('results', '?')} | {info.get('executed', '?')} "
                f"| {info.get('failed', '?')} | {seconds} |")
        lines.append("")
    if errors:
        lines.append(f"**Eksiksizlik denetimi BAŞARISIZ ({len(errors)} ihlal):**")
        lines.extend(f"- {e}" for e in errors)
    else:
        lines.append(
            "Eksiksizlik denetimi geçti: her vaka tam bir parçada seçildi, birleşim --list-tests ile aynı "
            "ve her parçanın TRX'inde tam olarak seçtiği metotlar koştu.")
    text = "\n".join(lines) + "\n"
    print(text)
    for e in errors:
        print(f"::error::{e}")
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as f:
            f.write(text)


if __name__ == "__main__":
    sys.exit(main())
