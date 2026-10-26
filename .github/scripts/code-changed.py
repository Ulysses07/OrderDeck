#!/usr/bin/env python3
"""PR'da .NET işlerinin (build-test, sunucu test parçaları, docker-build) koşması gerekiyor mu?

Girdi: değişen dosyalar, NUL ile ayrılmış (`git diff --name-only --no-renames -z`).
Çıktı: stdout'a `true` ya da `false`; gerekçe stderr'e.

`false` yalnız değişen dosyaların HEPSİ kod dışıysa: docs/**, kökteki *.md,
.github/workflows/web-*.yml ve web/**. Bilinmeyen her şey `true` (güvenli yön);
boş liste de `true`.

İstisna: sunucu testleri bazı web dosyalarını okuyor (KVKK politika metni
eşliği: IntakeFormKvkkTests, PolicyEffectiveDateParityTests). Bu yollar elle
listelenmez, her koşuda .cs dosyalarındaki "web/..." literal'lerinden toplanır;
değişen bir web dosyası bunlardan birine önekle uyarsa `true`. Yeni bir test
başka bir web dosyasını okumaya başlarsa yolu literal olarak yazması yeter.

--no-renames önemli: src/ → docs/ taşıması silme + ekleme olarak görünür, silinen
src/ yolu kod sayılır.

Master'a push'ta bu betik çağrılmaz; orada her şey koşar (deploy eksiksiz test
kanıtına dayanır).
"""

import re
import subprocess
import sys

WEB_WORKFLOW = re.compile(r"\.github/workflows/web-[^/]+\.yml")
WEB_LITERAL = re.compile(r'"(web/[^"]+)"')


def test_read_web_paths():
    out = subprocess.run(
        ["git", "grep", "-hoE", r'"web/[^"]+"', "--", "*.cs"],
        capture_output=True, text=True, encoding="utf-8")
    # git grep: 0 = bulundu, 1 = hiç yok, başka = hata (güvenli yön: hata yükselt).
    if out.returncode not in (0, 1):
        raise SystemExit(f"git grep başarısız ({out.returncode}): {out.stderr.strip()}")
    return sorted({m.group(1) for m in WEB_LITERAL.finditer(out.stdout)})


def non_code_reason(path, web_refs):
    if path.startswith("docs/"):
        return "docs"
    if "/" not in path and path.endswith(".md"):
        return "kök .md"
    if WEB_WORKFLOW.fullmatch(path):
        return "web iş akışı"
    if path.startswith("web/"):
        if any(path.startswith(ref) for ref in web_refs):
            return None  # sunucu testleri bu dosyayı okuyor
        return "web"
    return None


def main():
    data = sys.stdin.buffer.read().decode("utf-8")
    files = [f for f in data.replace("\n", "\0").split("\0") if f]
    if not files:
        print("boş diff → kod sayılır", file=sys.stderr)
        print("true")
        return

    web_refs = test_read_web_paths()
    print(f"testlerin okuduğu web dosyaları: {web_refs}", file=sys.stderr)
    code = []
    for path in files:
        reason = non_code_reason(path, web_refs)
        if reason is None:
            code.append(path)
        print(f"  {'KOD ' if reason is None else 'atla'}  {path}" + (f"  ({reason})" if reason else ""), file=sys.stderr)

    print(f"{len(files)} dosya, {len(code)} kod", file=sys.stderr)
    print("true" if code else "false")


if __name__ == "__main__":
    main()
