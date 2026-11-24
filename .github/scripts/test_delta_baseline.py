#!/usr/bin/env python3
"""delta-baseline.py testleri (build-test.yml → script-tests).

Çalıştırma (repo kökünden; Windows'ta `python`, CI'da `python3`):
  python -m unittest discover -s .github/scripts -p "test_*.py" -v

Sağlama değerleri dosyaya yazılmaz, her koşuda üretilir (repo public; tarayıcı
uzun onaltılık dizgiyi sırdan ayıramaz).
"""

import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import random
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("delta-baseline.py")
_spec = importlib.util.spec_from_file_location("delta_baseline", SCRIPT)
db = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(db)


def sha_of(data):
    if isinstance(data, str):
        data = data.encode("utf-8")
    return hashlib.sha256(data).hexdigest().upper()


def asset(version, kind, size=1000):
    name = f"OrderDeck-{version}-{kind.lower()}.nupkg"
    return {"PackageId": "OrderDeck", "Version": version, "Type": kind,
            "FileName": name, "SHA256": sha_of(name), "Size": size}


def manifest(*versions, deltas=True):
    """Her sürüm için Full (+ Delta) kaydı, verilen sırayla."""
    assets = []
    for version in versions:
        assets.append(asset(version, "Full"))
        if deltas:
            assets.append(asset(version, "Delta"))
    return {"Assets": assets}


def names(selection):
    return [f["fileName"] for f in selection["files"]]


class SurumSirasi(unittest.TestCase):
    def test_iki_haneli_yama_tek_haneliden_buyuk(self):
        # Ad (metin) sırası 0.9.9'u 0.9.10'un üstüne koyar; hatanın çıktığı yer.
        self.assertLess(db.version_key("0.9.9"), db.version_key("0.9.10"))
        self.assertLess(db.version_key("0.9.13"), db.version_key("0.10.0"))
        self.assertLess(db.version_key("0.10.0"), db.version_key("1.0.0"))

    def test_on_surum_surumun_kendisinden_kucuk(self):
        self.assertLess(db.version_key("1.0.0-rc.1"), db.version_key("1.0.0"))
        self.assertLess(db.version_key("0.9.13"), db.version_key("1.0.0-rc.1"))

    def test_on_surum_kimlikleri_semver_sirasinda(self):
        # SemVer 2.0 §11: sayısal < harfli, sayısal sayı olarak, kısa önek < uzun.
        ordered = ["1.0.0-2", "1.0.0-10", "1.0.0-alpha", "1.0.0-alpha.1",
                   "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-rc.1", "1.0.0"]
        keys = [db.version_key(v) for v in ordered]
        self.assertEqual(keys, sorted(keys))
        self.assertEqual(len(set(keys)), len(keys))

    def test_derleme_eki_sirayi_etkilemez(self):
        self.assertEqual(db.version_key("1.2.3+abc.5"), db.version_key("1.2.3"))

    def test_on_surum_etiketi_buyuk_kucuk_harf_ayirmaz(self):
        # Tabanı asıl seçen vpk (NuGet sıralaması) harfli etiketleri büyük/küçük
        # harf ayırmadan karşılaştırır; düz ASCII sırası "RC"yi "beta"nın önüne atardı.
        self.assertLess(db.version_key("1.0.0-beta.1"), db.version_key("1.0.0-RC.1"))
        self.assertEqual(db.version_key("1.0.0-RC.1"), db.version_key("1.0.0-rc.1"))

    def test_bozuk_bicim_reddedilir(self):
        for bad in ["", "1.2", "1.2.3.4", "v1.2.3", "1.2.x", "1..3", "01.2.3x", None]:
            with self.subTest(bad=bad):
                with self.assertRaises(db.FeedError):
                    db.version_key(bad)


class TabanSecimi(unittest.TestCase):
    def test_bugunku_feed_0_9_13u_secer_0_9_9u_degil(self):
        # v0.9.13 yayınından sonraki gerçek manifestin şekli: {0.9.13, 0.9.9}.
        # Dosya tarihine bakan eski adım buradan yine 0.9.9'u alırdı.
        sel = db.select_baseline(manifest("0.9.13", "0.9.9"), "0.9.14", 80)
        self.assertEqual(sel["version"], "0.9.13")
        self.assertEqual(names(sel), ["OrderDeck-0.9.13-full.nupkg", "OrderDeck-0.9.13-delta.nupkg"])

    def test_hatanin_ilk_ciktigi_durum_0_9_11_icin_0_9_10(self):
        sel = db.select_baseline(manifest("0.9.10", "0.9.9"), "0.9.11", 60)
        self.assertEqual(sel["version"], "0.9.10")

    def test_manifest_sirasi_sonucu_degistirmez(self):
        base = manifest("0.9.9", "0.9.13", "0.9.10", "0.9.12")
        rnd = random.Random(1309)
        for _ in range(20):
            shuffled = {"Assets": rnd.sample(base["Assets"], len(base["Assets"]))}
            sel = db.select_baseline(shuffled, "0.9.14", 80)
            self.assertEqual(sel["version"], "0.9.13")
            # Sıra sabit: önce Full, sonra Delta.
            self.assertEqual(names(sel), ["OrderDeck-0.9.13-full.nupkg", "OrderDeck-0.9.13-delta.nupkg"])

    def test_ikinci_hane_buyuyunce(self):
        self.assertEqual(db.select_baseline(manifest("0.9.13", "0.9.9"), "0.10.0", 80)["version"], "0.9.13")
        self.assertEqual(db.select_baseline(manifest("0.10.0", "0.9.13"), "0.10.1", 82)["version"], "0.10.0")

    def test_secilen_kaydin_sha_ve_boyutu_tasinir(self):
        m = {"Assets": [asset("0.9.13", "Full", 106269556), asset("0.9.13", "Delta", 25836301)]}
        sel = db.select_baseline(m, "0.9.14", 80)
        full, delta = sel["files"]
        self.assertEqual((full["type"], full["size"], full["sha256"]),
                         ("Full", 106269556, sha_of("OrderDeck-0.9.13-full.nupkg")))
        self.assertEqual((delta["type"], delta["size"], delta["sha256"]),
                         ("Delta", 25836301, sha_of("OrderDeck-0.9.13-delta.nupkg")))

    def test_tabanin_deltasi_yoksa_yalniz_full(self):
        sel = db.select_baseline(manifest("0.9.13", deltas=False), "0.9.14", 80)
        self.assertEqual(names(sel), ["OrderDeck-0.9.13-full.nupkg"])

    def test_surum_feedde_zaten_yayinlanmissa_durur(self):
        # Manifest en son yüklenir: sürüm manifestteyse yayın tamamlanmıştır.
        # Yeniden derlemek aynı sürüm numarasıyla FARKLI paket yayınlar; ilk
        # paketi almış kurulumlarda sonraki delta'lar tutmaz.
        with self.assertRaisesRegex(db.FeedError, "zaten"):
            db.select_baseline(manifest("0.9.13", "0.9.12"), "0.9.13", 80)
        with self.assertRaisesRegex(db.FeedError, "zaten"):
            db.select_baseline(manifest("0.5.0", deltas=False), "0.5.0", 1, allow_no_baseline=True)

    def test_feedde_daha_yeni_surum_varsa_durur(self):
        with self.assertRaisesRegex(db.FeedError, "0.9.13"):
            db.select_baseline(manifest("0.9.13", "0.9.12"), "0.9.12", 80)

    def test_yeni_surum_yalniz_delta_kaydiyla_gorunse_de_sayilir(self):
        m = {"Assets": [asset("0.9.13", "Full"), asset("0.9.15", "Delta")]}
        with self.assertRaisesRegex(db.FeedError, "0.9.15"):
            db.select_baseline(m, "0.9.14", 3)

    def test_feed_bos_gorunuyorsa_varsayilan_olarak_durur(self):
        # 40 sürümden sonra "paket yok, manifest yok" ilk sürüm değil, bozuk ya
        # da ulaşılamayan feed demektir; sessizce deltasız sürüm çıkmasın.
        for empty in (None, {"Assets": []}):
            with self.subTest(manifest=empty):
                with self.assertRaisesRegex(db.FeedError, "boş"):
                    db.select_baseline(empty, "0.9.14", 0)

    def test_ilk_surume_acikca_izin_verilirse_taban_yok(self):
        for empty in (None, {"Assets": []}):
            with self.subTest(manifest=empty):
                sel = db.select_baseline(empty, "0.5.0", 0, allow_no_baseline=True)
                self.assertEqual(sel, {"version": None, "files": []})

    def test_manifest_yok_ama_paket_varsa_izinle_bile_durur(self):
        for empty in (None, {"Assets": []}):
            with self.subTest(manifest=empty):
                with self.assertRaisesRegex(db.FeedError, "80 paket"):
                    db.select_baseline(empty, "0.9.14", 80, allow_no_baseline=True)

    def test_eski_surumun_yalniz_deltasi_varsa_durur(self):
        m = {"Assets": [asset("0.9.13", "Delta")]}
        with self.assertRaises(db.FeedError):
            db.select_baseline(m, "0.9.14", 80)

    def test_on_surumlu_secim(self):
        m = {"Assets": [asset("1.0.0-rc.1", "Full"), asset("0.9.13", "Full")]}
        self.assertEqual(db.select_baseline(m, "1.0.0", 4)["version"], "1.0.0-rc.1")
        self.assertEqual(db.select_baseline(m, "1.0.0-rc.2", 4)["version"], "1.0.0-rc.1")
        with self.assertRaisesRegex(db.FeedError, "1.0.0"):  # sürüm, kendi ön sürümünden yenidir
            db.select_baseline({"Assets": [asset("1.0.0", "Full")]}, "1.0.0-rc.2", 2)

    def test_ayni_surume_iki_full_kaydi_varsa_durur(self):
        m = {"Assets": [asset("0.9.13", "Full"), asset("0.9.13", "Full")]}
        with self.assertRaises(db.FeedError):
            db.select_baseline(m, "0.9.14", 80)

    def test_bozuk_dosya_adi_reddedilir(self):
        for bad in ["../OrderDeck-0.9.13-full.nupkg", "a b.nupkg", "OrderDeck-0.9.13-full.zip",
                    "x.nupkg; rm -rf /", "", None, "$(id).nupkg"]:
            with self.subTest(bad=bad):
                entry = asset("0.9.13", "Full")
                entry["FileName"] = bad
                with self.assertRaises(db.FeedError):
                    db.select_baseline({"Assets": [entry]}, "0.9.14", 80)

    def test_bozuk_sha_ya_da_boyut_reddedilir(self):
        cases = [("SHA256", "abc"), ("SHA256", None), ("SHA256", "Z" * 64),
                 ("Size", 0), ("Size", -5), ("Size", "1000"), ("Size", None), ("Size", True)]
        for field, value in cases:
            with self.subTest(field=field, value=value):
                entry = asset("0.9.13", "Full")
                entry[field] = value
                with self.assertRaises(db.FeedError):
                    db.select_baseline({"Assets": [entry]}, "0.9.14", 80)

    def test_tabanin_delta_kaydi_da_dogrulanir(self):
        for field, value in [("FileName", "../x.nupkg"), ("SHA256", "abc"), ("Size", 0)]:
            with self.subTest(field=field):
                delta = asset("0.9.13", "Delta")
                delta[field] = value
                with self.assertRaises(db.FeedError):
                    db.select_baseline({"Assets": [asset("0.9.13", "Full"), delta]}, "0.9.14", 2)

    def test_bozuk_manifest_sekli_reddedilir(self):
        # Paket adedi 0 ve ilk sürüme izin açıkken: bozuk manifest "yok/boş"
        # sayılıp tabansız sürüme dönüşmemeli, her durumda hata olmalı.
        for bad in [{}, {"Assets": None}, {"Assets": {}}, [], "x", {"Assets": ["x"]},
                    {"Assets": [{"Type": "Full"}]}, {"Assets": [{"Version": "0.9.13"}]}]:
            for count in (0, 80):
                with self.subTest(bad=bad, count=count):
                    with self.assertRaises(db.FeedError) as caught:
                        db.select_baseline(bad, "0.9.14", count, allow_no_baseline=True)
                    self.assertNotIn("paket var ama", str(caught.exception))

    def test_yeni_surum_bicimi_bozuksa_durur(self):
        with self.assertRaises(db.FeedError):
            db.select_baseline(manifest("0.9.13"), "v0.9.14", 80)


class YayinOncesiDenetim(unittest.TestCase):
    """publish, feed'e yazmadan hemen önce: taban seçildiğinden beri feed değişti mi?"""

    def test_feed_degismediyse_gecer(self):
        db.recheck_baseline(manifest("0.9.13", "0.9.9"), "0.9.14", "0.9.13")

    def test_arada_baska_surum_yayinlandiysa_durur(self):
        # Üst üste iki etiket: 0.9.15 koşusu tabanını (0.9.13) seçtikten sonra
        # 0.9.14 yayınlandı. 0.9.15'in delta'sı artık yanlış tabana göre.
        with self.assertRaisesRegex(db.FeedError, "0.9.13.*0.9.14|0.9.14.*0.9.13"):
            db.recheck_baseline(manifest("0.9.14", "0.9.13"), "0.9.15", "0.9.13")

    def test_feedde_daha_yeni_surum_varsa_durur(self):
        # Eski bir koşunun yalnız publish işi yeniden çalıştırılırsa feed geri gider.
        with self.assertRaisesRegex(db.FeedError, "0.9.15"):
            db.recheck_baseline(manifest("0.9.15", "0.9.14"), "0.9.14", "0.9.13")

    def test_bu_surumun_kendi_kaydi_sayilmaz(self):
        # Manifest yüklendikten sonra düşen publish (ör. GitHub Release adımı)
        # yeniden koşabilmeli: aynı paketleri yeniden yükler.
        db.recheck_baseline(manifest("0.9.14", "0.9.13"), "0.9.14", "0.9.13")

    def test_ilk_surumde_manifest_yoksa_gecer(self):
        db.recheck_baseline(None, "0.5.0", "")
        db.recheck_baseline({"Assets": []}, "0.5.0", None)
        db.recheck_baseline(manifest("0.5.0", deltas=False), "0.5.0", "")

    def test_taban_varken_manifest_kaybolduysa_durur(self):
        for gone in (None, {"Assets": []}):
            with self.subTest(manifest=gone):
                with self.assertRaisesRegex(db.FeedError, "0.9.13"):
                    db.recheck_baseline(gone, "0.9.14", "0.9.13")

    def test_tabansiz_uretildiyse_ama_feedde_surum_varsa_durur(self):
        with self.assertRaisesRegex(db.FeedError, "0.9.13"):
            db.recheck_baseline(manifest("0.9.13"), "0.9.14", "")

    def test_bozuk_manifestte_durur(self):
        with self.assertRaises(db.FeedError):
            db.recheck_baseline({"Assets": ["x"]}, "0.9.14", "0.9.13")


class IndirilenDogrulama(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.dir = pathlib.Path(self._tmp.name)

    def put(self, name, data):
        (self.dir / name).write_bytes(data)
        return {"type": "Full", "fileName": name, "sha256": sha_of(data), "size": len(data)}

    def test_boyut_ve_sha_tutuyorsa_gecer(self):
        files = [self.put("a-full.nupkg", b"tam paket" * 1000), self.put("a-delta.nupkg", b"delta")]
        db.verify_files({"version": "1.0.0", "files": files}, str(self.dir))

    def test_sha_buyuk_kucuk_harf_farki_onemsiz(self):
        entry = self.put("a-full.nupkg", b"icerik")
        entry["sha256"] = entry["sha256"].lower()
        db.verify_files({"version": "1.0.0", "files": [entry]}, str(self.dir))

    def test_dosya_yoksa_durur(self):
        entry = self.put("a-full.nupkg", b"icerik")
        (self.dir / "a-full.nupkg").unlink()
        with self.assertRaisesRegex(db.FeedError, "a-full.nupkg"):
            db.verify_files({"version": "1.0.0", "files": [entry]}, str(self.dir))

    def test_yarim_inen_dosya_boyuttan_yakalanir(self):
        entry = self.put("a-full.nupkg", b"icerik-tam")
        (self.dir / "a-full.nupkg").write_bytes(b"icerik")
        with self.assertRaisesRegex(db.FeedError, "boyut"):
            db.verify_files({"version": "1.0.0", "files": [entry]}, str(self.dir))

    def test_ayni_boyutta_farkli_icerik_shadan_yakalanir(self):
        entry = self.put("a-full.nupkg", b"icerik-1")
        (self.dir / "a-full.nupkg").write_bytes(b"icerik-2")
        with self.assertRaisesRegex(db.FeedError, "SHA256"):
            db.verify_files({"version": "1.0.0", "files": [entry]}, str(self.dir))

    def test_taban_yoksa_dogrulanacak_sey_yok(self):
        db.verify_files({"version": None, "files": []}, str(self.dir))

    def test_her_dosya_dogrulanir(self):
        good, bad = b"a" * 100, b"b" * 100
        for corrupt in (0, 1):
            with self.subTest(bozuk=corrupt):
                files = []
                for index, name in enumerate(("p-full.nupkg", "p-delta.nupkg")):
                    (self.dir / name).write_bytes(bad if index == corrupt else good)
                    files.append({"type": "x", "fileName": name, "sha256": sha_of(good), "size": 100})
                with self.assertRaises(db.FeedError):
                    db.verify_files({"version": "1.0.0", "files": files}, str(self.dir))

    def test_saglama_dosyanin_tamamini_kapsar(self):
        # Okuma 1 MiB'lık parçalarla: ilk parçadan sonrası da sağlamaya girmeli.
        data = bytes(range(256)) * 4100  # ~1,05 MiB
        entry = self.put("buyuk-full.nupkg", data)
        db.verify_files({"version": "1.0.0", "files": [entry]}, str(self.dir))
        (self.dir / "buyuk-full.nupkg").write_bytes(data[:-1] + b"X")
        with self.assertRaisesRegex(db.FeedError, "SHA256"):
            db.verify_files({"version": "1.0.0", "files": [entry]}, str(self.dir))


class KomutSatiri(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.dir = pathlib.Path(self._tmp.name)

    def run_main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = db.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def write_manifest(self, data):
        path = self.dir / "releases.win.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        return str(path)

    def test_select_dosya_adlarini_stdouta_secimi_dosyaya_yazar(self):
        m = self.write_manifest(manifest("0.9.13", "0.9.9"))
        out_json, gh_out = self.dir / "secim.json", self.dir / "gh-output.txt"
        gh_out.write_text("onceki=1\n", encoding="utf-8")
        code, out, err = self.run_main(
            "select", "--version", "0.9.14", "--remote-nupkg-count", "80",
            "--manifest", m, "--out", str(out_json), "--github-output", str(gh_out))
        self.assertEqual(code, 0, err)
        # stdout YALNIZ dosya adları: iş akışı bunu satır satır scp'ye verir.
        self.assertEqual(out.splitlines(), ["OrderDeck-0.9.13-full.nupkg", "OrderDeck-0.9.13-delta.nupkg"])
        self.assertEqual(json.loads(out_json.read_text(encoding="utf-8"))["version"], "0.9.13")
        self.assertEqual(gh_out.read_text(encoding="utf-8"), "onceki=1\nbaseline_version=0.9.13\n")
        self.assertIn("0.9.13", err)

    def test_select_ilk_surumde_bos_cikti_ve_bos_surum(self):
        out_json, gh_out = self.dir / "secim.json", self.dir / "gh-output.txt"
        code, out, err = self.run_main(
            "select", "--version", "0.5.0", "--remote-nupkg-count", "0", "--allow-no-baseline",
            "--out", str(out_json), "--github-output", str(gh_out))
        self.assertEqual(code, 0, err)
        self.assertEqual(out, "")
        self.assertEqual(json.loads(out_json.read_text(encoding="utf-8")), {"version": None, "files": []})
        self.assertEqual(gh_out.read_text(encoding="utf-8"), "baseline_version=\n")

    def test_select_bos_feedde_izin_verilmediyse_durur(self):
        out_json, gh_out = self.dir / "secim.json", self.dir / "gh-output.txt"
        code, out, err = self.run_main(
            "select", "--version", "0.9.14", "--remote-nupkg-count", "0",
            "--out", str(out_json), "--github-output", str(gh_out))
        self.assertEqual((code, out), (1, ""))
        self.assertIn("::error::", err)
        self.assertFalse(out_json.exists())
        self.assertFalse(gh_out.exists())

    def test_select_paket_adedini_kurala_tasir(self):
        # Manifest yok ama feed'de paket var: izin açık olsa da durmalı.
        code, out, err = self.run_main(
            "select", "--version", "0.9.14", "--remote-nupkg-count", "80", "--allow-no-baseline",
            "--out", str(self.dir / "secim.json"))
        self.assertEqual((code, out), (1, ""))
        self.assertIn("80 paket", err)

    def test_select_kural_ihlalinde_1_doner_stdout_bos_secim_yazilmaz(self):
        m = self.write_manifest(manifest("0.9.13", "0.9.12"))
        out_json = self.dir / "secim.json"
        code, out, err = self.run_main(
            "select", "--version", "0.9.12", "--remote-nupkg-count", "80",
            "--manifest", m, "--out", str(out_json))
        self.assertEqual(code, 1)
        self.assertEqual(out, "")
        self.assertIn("::error::", err)
        self.assertFalse(out_json.exists())

    def test_select_json_olmayan_manifestte_durur(self):
        # Paket adedi 0 ve izin açık: bozuk manifest "yok" sayılıp tabansız
        # sürüme dönüşmemeli.
        path = self.dir / "releases.win.json"
        path.write_text("<html>502 Bad Gateway</html>", encoding="utf-8")
        code, out, err = self.run_main(
            "select", "--version", "0.9.14", "--remote-nupkg-count", "0", "--allow-no-baseline",
            "--manifest", str(path), "--out", str(self.dir / "secim.json"))
        self.assertEqual((code, out), (1, ""))
        self.assertIn("::error::", err)

    def test_select_verilen_manifest_dosyasi_yoksa_durur(self):
        # "Manifest yok" yalnız --manifest verilmeyince geçerli; verilen yol
        # yoksa bu bir indirme hatasıdır, ilk sürüm sayılmamalı.
        code, out, err = self.run_main(
            "select", "--version", "0.5.0", "--remote-nupkg-count", "0", "--allow-no-baseline",
            "--manifest", str(self.dir / "yok.json"), "--out", str(self.dir / "secim.json"))
        self.assertEqual((code, out), (1, ""))

    def test_recheck_gecer_ve_degisen_feedde_1_doner(self):
        m = self.write_manifest(manifest("0.9.13", "0.9.9"))
        code, out, err = self.run_main(
            "recheck", "--version", "0.9.14", "--expect-baseline", "0.9.13", "--manifest", m)
        self.assertEqual((code, out), (0, ""), err)

        m = self.write_manifest(manifest("0.9.14", "0.9.13"))
        code, out, err = self.run_main(
            "recheck", "--version", "0.9.15", "--expect-baseline", "0.9.13", "--manifest", m)
        self.assertEqual((code, out), (1, ""))
        self.assertIn("::error::", err)

    def test_recheck_manifestsiz_ilk_surum_ve_kaybolan_manifest(self):
        code, _, err = self.run_main("recheck", "--version", "0.5.0", "--expect-baseline", "")
        self.assertEqual(code, 0, err)
        code, _, err = self.run_main("recheck", "--version", "0.9.14", "--expect-baseline", "0.9.13")
        self.assertEqual(code, 1)
        self.assertIn("::error::", err)

    def test_recheck_json_olmayan_manifestte_durur(self):
        path = self.dir / "sunucu.json"
        path.write_text("Welcome to the server\n", encoding="utf-8")
        code, _, err = self.run_main(
            "recheck", "--version", "0.5.0", "--expect-baseline", "", "--manifest", str(path))
        self.assertEqual(code, 1)
        self.assertIn("::error::", err)

    def test_verify_bozuk_secim_dosyasinda_durur(self):
        sel = self.dir / "secim.json"
        sel.write_text(json.dumps({"version": "1.0.0"}), encoding="utf-8")
        code, _, err = self.run_main("verify", "--selection", str(sel), "--dir", str(self.dir))
        self.assertEqual(code, 1)
        self.assertIn("::error::", err)

    def test_verify_gecer_ve_bozuk_dosyada_1_doner(self):
        data = b"paket-icerigi" * 50
        (self.dir / "p-full.nupkg").write_bytes(data)
        sel = self.dir / "secim.json"
        sel.write_text(json.dumps({"version": "1.0.0", "files": [
            {"type": "Full", "fileName": "p-full.nupkg", "sha256": sha_of(data), "size": len(data)}]}),
            encoding="utf-8")
        code, _, err = self.run_main("verify", "--selection", str(sel), "--dir", str(self.dir))
        self.assertEqual(code, 0, err)

        (self.dir / "p-full.nupkg").write_bytes(data[:-1] + b"X")
        code, _, err = self.run_main("verify", "--selection", str(sel), "--dir", str(self.dir))
        self.assertEqual(code, 1)
        self.assertIn("::error::", err)

    def test_betik_dogrudan_calistirilabilir(self):
        # İş akışı betiği `python3 .github/scripts/delta-baseline.py …` diye çağırır.
        m = self.write_manifest(manifest("0.9.13", "0.9.9"))
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        done = subprocess.run(
            [sys.executable, str(SCRIPT), "select", "--version", "0.9.14",
             "--remote-nupkg-count", "80", "--manifest", m, "--out", str(self.dir / "secim.json")],
            capture_output=True, text=True, encoding="utf-8", env=env)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(),
                         ["OrderDeck-0.9.13-full.nupkg", "OrderDeck-0.9.13-delta.nupkg"])


if __name__ == "__main__":
    unittest.main()
