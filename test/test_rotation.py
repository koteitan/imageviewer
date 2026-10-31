#!/usr/bin/env python3
"""Tests for the imageviewer behaviour written in doc/rotation.md.

Run with test/run.sh (builds the app without deploying, prepares test/.venv, runs this file).

Flow: make test images (Pillow / pillow-heif) -> run imageviewer's own code on copies of them
through test/harness.ps1 (powershell, nothing is shown on the screen) -> check the results with
Pillow / pillow-heif / the readers in formats.py, i.e. with a second implementation.

Each check has an id; the ids follow the sections of doc/rotation.md:
  C* 共通, P* PNG, J* JPEG, H* HEIC / HEIF / AVIF, W* WebP, G* GIF, B* BMP, T* TIFF, X* JPEG XR, I* ICO / DDS
"""
import datetime
import io
import json
import os
import random
import re
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
import pillow_heif
from PIL import Image, ImageCms, ImageOps, PngImagePlugin

import formats as F

pillow_heif.register_heif_opener()
ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "bin" / "Release" / "imageviewer.exe"
HARNESS = ROOT / "test" / "harness.ps1"
RUN = ROOT / "test" / "out" / datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
FIX, WORK, DATA = RUN / "fixtures", RUN / "work", RUN / "data"

# ---------------------------------------------------------------- helpers


def winpath(p):
    return subprocess.check_output(["wslpath", "-w", str(p)], text=True).strip()


def rgb(im):
    return np.asarray(im.convert("RGB")).astype(int)


def rot(im, deg):
    """Rotate clockwise."""
    return im.rotate(-deg, expand=True) if deg % 360 else im.copy()


D4 = {
    "r0": lambda im: im.copy(),
    "r90": lambda im: rot(im, 90),
    "r180": lambda im: rot(im, 180),
    "r270": lambda im: rot(im, 270),
    "m0": lambda im: im.transpose(Image.FLIP_LEFT_RIGHT),
    "m90": lambda im: rot(im.transpose(Image.FLIP_LEFT_RIGHT), 90),
    "m180": lambda im: rot(im.transpose(Image.FLIP_LEFT_RIGHT), 180),
    "m270": lambda im: rot(im.transpose(Image.FLIP_LEFT_RIGHT), 270),
}


def best_match(img, ref, with_second=False):
    """(name of the D4 transform of ref closest to img, mean abs difference[, mean of the second closest])."""
    scores = []
    for name, f in D4.items():
        t = f(ref)
        if t.size == img.size:
            scores.append((float(np.abs(rgb(t) - rgb(img)).mean()), name))
    scores.sort()
    if not scores:
        return (None, None, None) if with_second else (None, None)
    if with_second:
        return scores[0][1], scores[0][0], scores[1][0] if len(scores) > 1 else None
    return scores[0][1], scores[0][0]


ZIGZAG = [0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
          35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63]


def transposed_tables(tables):
    """Quantization tables transposed (rows <-> columns), as a lossless 90/270 degree rotation writes them."""
    out = []
    for tq, body in tables:
        size = len(body) // 64
        vals = [body[i * size:(i + 1) * size] for i in range(64)]
        natural = [None] * 64
        for i, pos in enumerate(ZIGZAG):
            natural[pos] = vals[i]
        tr = [natural[(k % 8) * 8 + k // 8] for k in range(64)]
        out.append((tq, b"".join(tr[pos] for pos in ZIGZAG)))
    return sorted(out)


def tables_lossless(saved, original):
    """Quantization tables of a losslessly rotated JPEG: the original ones, or transposed for 90/270 degrees."""
    t0 = F.jpeg_tables(original)[0]
    return F.jpeg_tables(saved)[0] in (t0, transposed_tables(t0))


def apply_heif_transforms(im, transforms):
    """irot (counter-clockwise) and imir (1: left-right, 0: top-bottom) in order; clap is already applied."""
    for typ, value in transforms:
        if typ == "irot":
            im = rot(im, (360 - value) % 360)
        elif typ == "imir":
            im = im.transpose(Image.FLIP_LEFT_RIGHT if value == 1 else Image.FLIP_TOP_BOTTOM)
    return im


def same_pixels(a, b):
    return a.size == b.size and np.array_equal(rgb(a), rgb(b))


def max_diff(a, b):
    if a.size != b.size:
        return 999
    return int(np.abs(rgb(a) - rgb(b)).max())


def decode_heif(path):
    if str(path).endswith(".avif"):
        im = Image.open(path)
        im.load()
        return ImageOps.exif_transpose(im)   # libavif reports irot / imir as an EXIF orientation
    return pillow_heif.open_heif(path).to_pillow()


def copy(src, name):
    dst = WORK / name
    shutil.copyfile(src, dst)
    return dst


class Harness:
    """Collects jobs, runs them in one powershell process, keeps the results by id."""

    def __init__(self):
        self.jobs, self.results, self.n = [], {}, 0

    def add(self, kind, **fields):
        self.n += 1
        jid = f"{kind}{self.n}"
        job = {"id": jid, "kind": kind}
        for k, v in fields.items():
            job[k] = winpath(v) if isinstance(v, Path) else v
        self.jobs.append(job)
        return jid

    def run(self):
        jobs, results = RUN / f"jobs{self.n}.json", RUN / f"results{self.n}.json"
        jobs.write_text(json.dumps(self.jobs, ensure_ascii=False), encoding="utf-8")
        cmd = ["powershell.exe", "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", winpath(HARNESS),
               "-exe", winpath(EXE), "-jobs", winpath(jobs), "-results", winpath(results), "-data", winpath(DATA)]
        subprocess.run(cmd, check=True)
        data = json.loads(results.read_text(encoding="utf-8-sig"))
        for r in data if isinstance(data, list) else [data]:
            self.results[r["id"]] = r
        self.jobs = []

    def __getitem__(self, jid):
        return self.results[jid]


H = Harness()
REPORT = []


def check(tid, title, ok, detail=""):
    REPORT.append((tid, title, bool(ok), detail))


# ---------------------------------------------------------------- fixtures


def base_image(w=64, h=48, seed=1):
    """Four colored quadrants (no symmetry) plus noise, so lossy re-compression is easy to see."""
    rnd = np.random.default_rng(seed)
    a = np.zeros((h, w, 3), int)
    a[: h // 2, : w // 2] = (220, 40, 40)
    a[: h // 2, w // 2:] = (40, 200, 40)
    a[h // 2:, : w // 2] = (40, 60, 220)
    a[h // 2:, w // 2:] = (230, 220, 40)
    a += rnd.integers(-25, 26, size=a.shape)
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8), "RGB")


def jpeg_bytes(im, **kw):
    b = io.BytesIO()
    im.save(b, "JPEG", **kw)
    return b.getvalue()


def xmp_packet(orientation, w, h):
    return ('<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            '<rdf:Description rdf:about="" xmlns:tiff="http://ns.adobe.com/tiff/1.0/" xmlns:exif="http://ns.adobe.com/exif/1.0/" '
            f'tiff:Orientation="{orientation}" exif:PixelXDimension="{w}" exif:PixelYDimension="{h}"/>'
            '</rdf:RDF></x:xmpmeta>')


ICC = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()   # a profile other than Windows' sRGB file


def make_jpeg(path, im, orientation, mode="RGB"):
    """JPEG with EXIF (orientation, date, ColorSpace, pixel size, thumbnail), ICC, XMP, an APP10, MPF and trailing data."""
    w, h = im.size
    thumb = jpeg_bytes(im.resize((8, 6)), quality=80)
    tiff = F.build_exif(orientation=orientation, pixel=(w, h), color_space=65535, date="2020:01:02 03:04:05", thumbnail=thumb)
    src = im.convert(mode)
    data = jpeg_bytes(src, quality=90, exif=b"Exif\0\0" + tiff, icc_profile=ICC if mode == "RGB" else None)
    extra = [(0xE1, b"http://ns.adobe.com/xap/1.0/\0" + xmp_packet(orientation, w, h).encode()),
             (0xEA, b"TEST\0custom segment"),
             (0xE2, b"MPF\0" + b"\0" * 16)]
    data = F.jpeg_insert_segments(data, extra) + b"TRAILING-DATA-AFTER-EOI"
    path.write_bytes(data)


def make_png(path, im, extra_before_idat):
    b = io.BytesIO()
    im.save(b, "PNG")
    chunks = F.png_chunks(b.getvalue())
    out = [chunks[0]] + extra_before_idat + [c for c in chunks[1:] if c[0] not in ("IHDR",)]
    path.write_bytes(F.png_build(out))


def make_heic(path, im, orientation=None, second=None):
    heif = pillow_heif.from_pillow(im)
    if orientation:
        ex = Image.Exif()
        ex[0x0112] = orientation
        heif.info["exif"] = ex.tobytes()
    if second is not None:
        heif.add_from_pillow(second)
    heif.save(path, quality=90)


def make_fixtures():
    FIX.mkdir(parents=True)
    WORK.mkdir()
    DATA.mkdir()
    base = base_image()
    base.save(FIX / "base.png")
    alpha = base.convert("RGBA")
    a = np.asarray(alpha).copy()
    a[:, : alpha.width // 4, 3] = 0             # left quarter fully transparent
    Image.fromarray(a, "RGBA").save(FIX / "base_alpha.png")

    # PNG
    exif6 = F.build_exif(orientation=6)
    make_png(FIX / "png_exif6.png", base, [("eXIf", exif6)])
    make_png(FIX / "png_xmp6.png", base, [("iTXt", F.png_itxt("XML:com.adobe.xmp", xmp_packet(6, 64, 48)))])
    make_png(FIX / "png_full.png", base, [("sRGB", b"\0"), ("eXIf", F.build_exif(orientation=1, date="2020:01:02 03:04:05")),
                                          ("pHYs", bytes.fromhex("00000ec400000ec401")),
                                          ("iTXt", F.png_itxt("XML:com.adobe.xmp", xmp_packet(1, 64, 48)))])
    # JPEG (64x48: multiples of 16 -> lossless; 40x30 -> re-compressed)
    for o in range(1, 9):
        make_jpeg(FIX / f"jpg_o{o}.jpg", base, o)
    make_jpeg(FIX / "jpg_odd_o6.jpg", base_image(40, 30, 2), 6)
    make_jpeg(FIX / "jpg_odd_cmyk.jpg", base_image(40, 30, 3), 1, mode="CMYK")
    # HEIF
    make_heic(FIX / "heic_plain.heic", base)
    make_heic(FIX / "heic_o6.heic", base, 6)
    make_heic(FIX / "heic_o7.heic", base, 7)
    make_heic(FIX / "heic_o2.heic", base, 2)
    base.save(FIX / "avif_plain.avif", quality=90)
    d = (FIX / "heic_o6.heic").read_bytes()
    (FIX / "heic_clap_after_irot.heic").write_bytes(F.heif_move_clap_after_irot(d))
    (FIX / "heic_moov.heic").write_bytes(F.heif_append_box(d, "moov"))
    make_heic(FIX / "heic_two_raw.heic", base, 6, second=base_image(64, 48, 5))
    d = (FIX / "heic_two_raw.heic").read_bytes()
    meta, mk, props, assoc, ipma, primary = F.heif_meta(d)
    other = next(i for i, lst in assoc.items() if i != primary and any(props[x - 1].type == "ispe" for x, _, _ in lst))
    (FIX / "heic_two.heic").write_bytes(F.heif_share_primary_irot(d, other))
    # WebP (EXIF chunk after the image data, as written by Pillow / libwebp)
    ex = Image.Exif()
    ex[0x0112] = 6
    base.save(FIX / "webp_o6.webp", lossless=True, exif=ex.tobytes())
    Image.open(FIX / "base_alpha.png").save(FIX / "webp_alpha.webp", lossless=True)
    # GIF
    base.convert("P", palette=Image.ADAPTIVE, colors=16).save(FIX / "gif_single.gif")
    f1 = base.convert("P", palette=Image.ADAPTIVE, colors=16)
    f2 = rot(base, 180).convert("P", palette=Image.ADAPTIVE, colors=16)
    f1.save(FIX / "gif_anim.gif", save_all=True, append_images=[f2], duration=100, loop=0)
    # BMP, TIFF
    base.save(FIX / "bmp24.bmp")
    base.save(FIX / "tif_o6.tif", tiffinfo={274: 6})
    base.save(FIX / "tif_multi.tif", save_all=True, append_images=[rot(base, 180)])
    # ICO, DDS
    base.resize((64, 64)).save(FIX / "ico_multi.ico", sizes=[(16, 16), (32, 32), (64, 64)], bitmap_format="bmp")
    base.convert("RGBA").save(FIX / "dds.dds", pixel_format="DXT5")
    # contents that do not match the extension
    shutil.copyfile(FIX / "jpg_o1.jpg", FIX / "jpeg_as.webp")
    shutil.copyfile(FIX / "base.png", FIX / "png_as.heic")
    # folder for the left/right order
    order = WORK / "order"
    order.mkdir()
    t = datetime.datetime.now().timestamp() - 3600
    for i, n in enumerate("ABC"):
        base.save(order / f"{n}.png")
        os.utime(order / f"{n}.png", (t + 60 * i, t + 60 * i))
    return base


# ---------------------------------------------------------------- tests


def main():
    if not EXE.exists():
        sys.exit(f"{EXE} not found: build first (test/run.sh does)")
    base = make_fixtures()

    # JPEG XR fixtures need WIC
    H.add("make_jxr", src=FIX / "base.png", out=FIX / "jxr_r0.jxr", rotation="Rotate0")
    H.add("make_jxr", src=FIX / "base.png", out=FIX / "jxr_r90.jxr", rotation="Rotate90")
    H.run()

    J = {}   # job ids

    # ---- loads of fixtures (what the viewer shows)
    shown = {}
    for name in ["png_exif6.png", "png_xmp6.png", "png_full.png"] + [f"jpg_o{o}.jpg" for o in range(1, 9)] + \
            ["jpg_odd_o6.jpg", "heic_o6.heic", "heic_o7.heic", "heic_o2.heic", "heic_plain.heic", "avif_plain.avif",
             "webp_o6.webp", "gif_anim.gif", "gif_single.gif", "tif_o6.tif", "jxr_r0.jxr", "jxr_r90.jxr",
             "ico_multi.ico", "dds.dds", "bmp24.bmp"]:
        shown[name] = RUN / f"shown-{name}.png"
        J["load", name] = H.add("load", path=FIX / name, out=shown[name])

    for name in ["heic_o6.heic", "heic_o7.heic", "heic_o2.heic"]:
        n = FIX / ("neutral-" + name)
        n.write_bytes(F.heif_neutralized((FIX / name).read_bytes()))
        shown["neutral-" + name] = RUN / f"shown-neutral-{name}.png"
        J["load", "neutral-" + name] = H.add("load", path=n, out=shown["neutral-" + name])

    # ---- plans
    for name in ["jpeg_as.webp", "png_as.heic", "jpg_o1.jpg", "jpg_odd_o6.jpg", "heic_clap_after_irot.heic", "heic_moov.heic",
                 "webp_o6.webp", "gif_anim.gif", "tif_multi.tif", "ico_multi.ico", "dds.dds", "heic_o6.heic"]:
        J["plan", name] = H.add("plan", path=FIX / name)

    # ---- saves on copies
    saved = {}

    def save(src, deg, tag=""):
        dst = copy(FIX / src, f"r{deg}{tag}-{src}")
        saved[src, deg, tag] = dst
        J["save", src, deg, tag] = H.add("save", path=dst, deg=deg)
        return dst

    for o in range(1, 9):
        for deg in (90, 180, 270):
            save(f"jpg_o{o}.jpg", deg)
    save("jpg_odd_o6.jpg", 90)
    save("jpg_odd_cmyk.jpg", 90)
    for name in ["png_full.png", "png_xmp6.png", "png_exif6.png", "gif_single.gif", "bmp24.bmp", "tif_o6.tif",
                 "jxr_r0.jxr", "jpeg_as.webp", "png_as.heic", "heic_two.heic", "avif_plain.avif"]:
        save(name, 90)
    for name in ["heic_plain.heic", "heic_o6.heic", "heic_o7.heic", "heic_o2.heic"]:
        for deg in (90, 180, 270):
            save(name, deg)
    # lossless chain (continued in the second run): 90, then back 270; and 4 more x 90
    a = save("jpg_o1.jpg", 90, "chain")
    # WebP conversions
    conv = {}
    for name, kind, ext in [("webp_o6.webp", "Jpeg", ".jpg"), ("webp_o6.webp", "Png", ".png"), ("webp_alpha.webp", "Jpeg", ".jpg"),
                            ("webp_alpha.webp", "Png", ".png")]:
        src = copy(FIX / name, "conv-" + name)
        target = src.with_suffix(ext)
        conv[name, kind] = target
        J["convert", name, kind] = H.add("convert", path=src, deg=90, kind2=kind, target=target)
    # read-only target
    J["readonly"] = H.add("readonly_save", path=FIX / "jpg_o1.jpg", deg=90)
    # viewer: Ctrl+wheel, popup, save through the viewer, left/right order
    v_png = copy(FIX / "base.png", "viewer.png")
    J["v_wheel"] = H.add("viewer", path=v_png, actions=["state", "ctrlwheel:120", "ctrlwheel:-120", "ctrlwheel:-120", "wheel:120"])
    v_save = copy(FIX / "base.png", "viewer-save.png")
    J["v_save"] = H.add("viewer", path=v_save, actions=["popup", "rotate:1", "popup", "rotate:1", "popup", "rotate:1", "popup",
                                                        "rotate:3", "save", "popup"])
    J["v_lossy"] = H.add("viewer", path=copy(FIX / "jpg_odd_o6.jpg", "viewer-odd.jpg"), actions=["rotate:1", "popup"])
    J["v_webp"] = H.add("viewer", path=copy(FIX / "webp_o6.webp", "viewer.webp"), actions=["rotate:1", "popup"])
    J["v_ico"] = H.add("viewer", path=copy(FIX / "ico_multi.ico", "viewer.ico"), actions=["rotate:1", "popup"])
    J["v_order"] = H.add("viewer", path=WORK / "order" / "B.png", actions=["rotate:1", "save", "navigate:1"])
    H.run()

    # second run: the chain on the saved file, and loads after saving
    back = copy(a, "back-" + a.name)
    J["back"] = H.add("save", path=back, deg=270)
    four = copy(a, "four-" + a.name)
    for _ in range(4):
        H.add("save", path=four, deg=90)
    J["after", "cmyk"] = H.add("load", path=saved["jpg_odd_cmyk.jpg", 90, ""], out=RUN / "after-cmyk.png")
    J["load", "jpg_odd_cmyk.jpg"] = H.add("load", path=FIX / "jpg_odd_cmyk.jpg", out=RUN / "shown-cmyk.png")
    after = {}
    for key, path in saved.items():
        if key[0].split("_")[0] in ("png", "gif", "bmp", "tif", "jxr", "heic", "avif") or key[0] in ("jpeg_as.webp",):
            after[key] = RUN / f"after-{path.name}.png"
            J["after", key] = H.add("load", path=path, out=after[key])
    H.run()

    def ok(jid):
        r = H[jid]
        return r.get("ok"), r.get("error")

    # ================================================================ 共通
    def _section0():
        r = H[J["plan", "jpeg_as.webp"]]
        s = saved["jpeg_as.webp", 90, ""]
        check("C1a", "中身で形式を判断: 拡張子 .webp・中身 JPEG は JPEG として保存(名前はそのまま)",
              r["mode"] == "Overwrite" and ok(J["save", "jpeg_as.webp", 90, ""])[0] and s.read_bytes()[:3] == b"\xff\xd8\xff"
              and tables_lossless(s.read_bytes(), (FIX / "jpeg_as.webp").read_bytes()),
              f"plan={r['mode']}")
        r = H[J["plan", "png_as.heic"]]
        s = saved["png_as.heic", 90, ""]
        check("C1b", "中身で形式を判断: 拡張子 .heic・中身 PNG は PNG として保存",
              r["mode"] == "Overwrite" and s.read_bytes()[:4] == b"\x89PNG" and same_pixels(Image.open(s), rot(base, 90)), f"plan={r['mode']}")

        steps = H[J["v_wheel"]]["steps"]
        st = [x["state"] for x in steps]
        rots = [x["rotation"] for x in st]
        c0 = (st[0]["x"] + st[0]["w"] // 2, st[0]["y"] + st[0]["h"] // 2)
        c1 = (st[1]["x"] + st[1]["w"] // 2, st[1]["y"] + st[1]["h"] // 2)
        check("C2a", "ctrl+wheel: 奥(+120)で右に 90°、手前(-120)で左に 90°", rots[:4] == [0, 90, 0, 270], f"rotations {rots}")
        check("C2b", "ctrl+wheel: 回すと幅と高さが入れ替わり、中心は動かない",
              (st[1]["w"], st[1]["h"]) == (st[0]["h"], st[0]["w"]) and abs(c0[0] - c1[0]) <= 1 and abs(c0[1] - c1[1]) <= 1,
              f"{st[0]['w']}x{st[0]['h']} -> {st[1]['w']}x{st[1]['h']}, center {c0} -> {c1}")
        check("C2c", "ctrl なしの wheel は回転しない(拡大縮小)", st[4]["rotation"] == st[3]["rotation"] and st[4]["scale"] > st[3]["scale"],
              f"rotation {st[3]['rotation']} -> {st[4]['rotation']}, scale {st[3]['scale']} -> {st[4]['scale']}")

        steps = H[J["v_save"]]["steps"]
        pops = [x for x in steps if x["action"] == "popup"]
        check("C3a", "右クリックビュー: 回転していないときは「回転」の行を出さない", pops[0]["rotate_visible"] == "Collapsed", pops[0]["rotate_visible"])
        check("C3b", "右クリックビュー: 回転すると「回転」の行と保存ボタンを出す",
              pops[1]["rotate_visible"] == "Visible" and pops[1]["save_enabled"] and pops[1]["rotate_text"] == "右に 90°(上書き保存します)",
              pops[1]["rotate_text"])
        check("C3c", "右クリックビュー: 角度の表示(180°, 左に 90°)",
              pops[2]["rotate_text"].startswith("180°") and pops[3]["rotate_text"].startswith("左に 90°"),
              f"{pops[2]['rotate_text']} / {pops[3]['rotate_text']}")
        check("C3d", "「回転: 保存」で表示している向きのとおりに保存し、回転の表示を 0 に戻す",
              same_pixels(Image.open(v_save), rot(base, 180)) and steps[-1]["state"]["rotation"] == 0 and pops[-1]["rotate_visible"] == "Collapsed",
              f"rotation after save {steps[-1]['state']['rotation']}")
        t = H[J["v_lossy"]]["steps"][-1]
        check("C3e", "右クリックビュー: 劣化する JPEG では「画質が少し落ちます」と出す",
              "画質が少し落ちます" in t["rotate_text"] and t["save_enabled"], t["rotate_text"])
        t = H[J["v_webp"]]["steps"][-1]
        check("C3f", "右クリックビュー: WebP では別の形式で保存すると出す", "WebP は保存できないので" in t["rotate_text"] and t["save_enabled"], t["rotate_text"])
        t = H[J["v_ico"]]["steps"][-1]
        check("C3g", "右クリックビュー: 保存できない形式では保存ボタンを押せない", not t["save_enabled"], t["rotate_text"])

        leftovers = [p.name for p in WORK.rglob("*.imageviewer.tmp")]
        r = H[J["readonly"]]
        check("C4a", "保存は一時ファイル経由: 保存後に一時ファイルが残らない", not leftovers, str(leftovers))
        check("C4b", "保存に失敗したとき(読み取り専用)、元のファイルと一時ファイル", r.get("error") and not r["changed"] and not r["leftover"],
              f"error={r.get('error')!r} changed={r['changed']} leftover={r['leftover']}")
        steps = H[J["v_order"]]["steps"]
        b_newest = (WORK / "order" / "B.png").stat().st_mtime > (WORK / "order" / "C.png").stat().st_mtime
        check("C5", "左右キーの順番は、フォルダを最初に開いたときの更新日時で決める(保存で B が一番新しくなっても B の次は C)",
              b_newest and steps[-1]["state"]["path"].endswith("C.png"), steps[-1]["state"]["path"])

    # ================================================================ PNG
    def _section1():
        check("P1", "PNG: eXIf の向きの値は使わない", same_pixels(Image.open(shown["png_exif6.png"]), base), "")
        check("P2", "PNG: WIC が返す XMP の tiff:Orientation は使う", same_pixels(Image.open(shown["png_xmp6.png"]), rot(base, 90)), "")
        s = saved["png_full.png", 90, ""]
        check("P3", "PNG: 画素単位で回転して保存する", same_pixels(Image.open(after["png_full.png", 90, ""]), rot(Image.open(shown["png_full.png"]), 90))
              and same_pixels(Image.open(s), rot(base, 90)), "")
        texts = [F.png_itxt_text(b)[1] for t, b in F.png_chunks(saved["png_xmp6.png", 90, ""].read_bytes()) if t == "iTXt"]
        check("P4", "PNG: XMP に tiff:Orientation があるときは、その値を 1 にする", texts and re.search(r'tiff:Orientation(="|>)1', texts[0]), texts[:1])
        co, cs = F.png_chunks((FIX / "png_full.png").read_bytes()), F.png_chunks(s.read_bytes())
        idat = lambda cs: b"".join(b for t, b in cs if t == "IDAT")
        check("P5", "PNG 副作用: 圧縮データ(IDAT)を作り直す", idat(co) != idat(cs), f"IDAT {len(idat(co))} -> {len(idat(cs))} bytes")
        g = [b for t, b in cs if t == "gAMA"]
        check("P6", "PNG 副作用: gAMA チャンク(45455)が増える", g and int.from_bytes(g[0], "big") == 45455, str(g))
        order = lambda cs: [t for t, _ in cs if t in ("eXIf", "pHYs")]
        check("P7", "PNG 副作用: チャンクの並びが変わる(eXIf が pHYs の後ろに移る)", order(co) == ["eXIf", "pHYs"] and order(cs) == ["pHYs", "eXIf"],
              f"{order(co)} -> {order(cs)}")
        xo = [b for t, b in co if t == "iTXt"]
        xs = [b for t, b in cs if t == "iTXt"]
        check("P8", "PNG 副作用: XMP(iTXt)の書き方が変わる", xo and xs and xo[0] != xs[0], "")

    # ================================================================ JPEG
    def _section2():
        for o in range(1, 9):
            ref = ImageOps.exif_transpose(Image.open(FIX / f"jpg_o{o}.jpg"))
            name, mean = best_match(Image.open(shown[f"jpg_o{o}.jpg"]), ref)
            check(f"J1-o{o}", f"JPEG: 開いたとき EXIF の向きの値 {o} に従って回して表示する", name == "r0" and mean < 3, f"{name} mean {mean:.2f}")
        original = (FIX / "jpg_o1.jpg").read_bytes()
        raw = lambda p: Image.open(p)
        check("J2a", "JPEG(16 の倍数): 劣化しない — 右に 90° 保存して左に 90° 保存すると、圧縮前の画素が完全に戻る",
              ok(J["back"])[0] and same_pixels(raw(back), raw(FIX / "jpg_o1.jpg")), f"max diff {max_diff(raw(back), raw(FIX / 'jpg_o1.jpg'))}")
        check("J2b", "JPEG(16 の倍数): 劣化しない — さらに 90° を 4 回保存するとバイト単位で元に戻る", four.read_bytes() == a.read_bytes(), "")
        bad = []
        for o in range(1, 9):
            disp = ImageOps.exif_transpose(Image.open(FIX / f"jpg_o{o}.jpg"))
            for deg in (90, 180, 270):
                s = saved[f"jpg_o{o}.jpg", deg, ""]
                if not ok(J["save", f"jpg_o{o}.jpg", deg, ""])[0]:
                    bad.append(f"o{o} {deg}: {ok(J['save', f'jpg_o{o}.jpg', deg, ''])[1]}")
                    continue
                d = s.read_bytes()
                ex = F.read_exif(F.jpeg_exif(d))
                img = Image.open(s)
                m = max_diff(img, rot(disp, deg))
                tables_kept = tables_lossless(d, (FIX / f"jpg_o{o}.jpg").read_bytes())
                if ex["ifd0"].get(F.ORIENTATION) != 1 or m > 6 or not tables_kept:
                    bad.append(f"o{o} {deg}: orientation {ex['ifd0'].get(F.ORIENTATION)} max diff {m} tables kept {tables_kept}")
        check("J3", "JPEG(16 の倍数): 向き 1〜8 × 90/180/270 で、表示の向きのとおりに劣化なしで回す(5・7 は 2 回に分けて)", not bad, "; ".join(bad))

        s = saved["jpg_o6.jpg", 90, ""]
        d, d0 = s.read_bytes(), (FIX / "jpg_o6.jpg").read_bytes()
        apps0 = [(m, b) for m, b in F.jpeg_app_segments(d0) if not (m == 0xE2 and b.startswith(b"MPF\0"))]
        apps = F.jpeg_app_segments(d)
        kind = lambda m, b: "EXIF" if b.startswith(b"Exif\0\0") else "XMP" if b.startswith(b"http://ns.adobe.com/xap") else f"{m:02X}"
        same_rest = [(m, b) for m, b in apps0 if kind(m, b) not in ("EXIF", "XMP")] == [(m, b) for m, b in apps if kind(m, b) not in ("EXIF", "XMP")]
        check("J4", "JPEG: 付属情報(APP0〜APP15)は元のファイルのものを写す(ICC・APP10・JFIF)",
              [kind(m, b) for m, b in apps0] == [kind(m, b) for m, b in apps] and same_rest, f"{[kind(m, b) for m, b in apps]}")
        ex0, ex = F.read_exif(F.jpeg_exif(d0)), F.read_exif(F.jpeg_exif(d))
        w, h = F.jpeg_size(d)
        check("J5", "JPEG: EXIF の向きの値 → 1", ex["ifd0"].get(F.ORIENTATION) == 1, str(ex["ifd0"].get(F.ORIENTATION)))
        check("J6", "JPEG: EXIF の PixelXDimension / PixelYDimension → 回したあとの値",
              (ex["exif"].get(F.PIXEL_X), ex["exif"].get(F.PIXEL_Y)) == (w, h), f"{ex['exif'].get(F.PIXEL_X)}x{ex['exif'].get(F.PIXEL_Y)} vs {w}x{h}")
        check("J7", "JPEG: EXIF のサムネイル(IFD1)への参照を外す", ex0["next_ifd"] != 0 and ex["next_ifd"] == 0, f"{ex0['next_ifd']} -> {ex['next_ifd']}")
        others0 = {k: v for k, v in ex0["exif"].items() if k not in (F.PIXEL_X, F.PIXEL_Y)}
        others = {k: v for k, v in ex["exif"].items() if k not in (F.PIXEL_X, F.PIXEL_Y)}
        check("J8", "JPEG: EXIF で書き換えるのは向き・ピクセル数・サムネイルの参照だけ(ColorSpace・撮影日時は元の値)", others0 == others, f"{others0} -> {others}")
        xmp = F.jpeg_xmp(d).decode()
        check("J9", "JPEG: XMP の tiff:Orientation → 1、exif:PixelX/YDimension → 回したあとの値",
              'tiff:Orientation="1"' in xmp and f'exif:PixelXDimension="{w}"' in xmp and f'exif:PixelYDimension="{h}"' in xmp, xmp[-170:])
        check("J10", "JPEG: MPF(APP2)を外し、ファイルの末尾(EOI の後ろ)のデータが無くなる",
              not any(m == 0xE2 and b.startswith(b"MPF\0") for m, b in apps) and F.jpeg_trailing(d) == b"" and F.jpeg_trailing(d0) != b"", "")
        nq, nh = F.jpeg_segment_counts(d)
        check("J11", "JPEG 副作用(劣化なし): 量子化表とハフマン表を表ごとに別の区切りに入れる", nq == len(F.jpeg_tables(d)[0]) and nh == len(F.jpeg_tables(d)[1]),
              f"DQT segments {F.jpeg_segment_counts(d0)[0]} -> {nq}, DHT segments {F.jpeg_segment_counts(d0)[1]} -> {nh}")
        check("J12", "JPEG 副作用(劣化なし): 圧縮データのバイトが変わる", F.jpeg_scan(d) != F.jpeg_scan(d0), f"{len(F.jpeg_scan(d0))} -> {len(F.jpeg_scan(d))} bytes")

        r = H[J["plan", "jpg_odd_o6.jpg"]]
        s = saved["jpg_odd_o6.jpg", 90, ""]
        d, d0 = s.read_bytes(), (FIX / "jpg_odd_o6.jpg").read_bytes()
        disp = ImageOps.exif_transpose(Image.open(FIX / "jpg_odd_o6.jpg"))
        name, mean = best_match(Image.open(s), rot(disp, 90))
        ex = F.read_exif(F.jpeg_exif(d))
        w, h = F.jpeg_size(d)
        icc = [b for m, b in F.jpeg_app_segments(d) if m == 0xE2 and b.startswith(b"ICC_PROFILE")]
        check("J13a", "JPEG(16 の倍数でない): 保存できる(劣化あり)", r["mode"] == "OverwriteLossy" and r["reason"] == "画質が少し落ちます" and ok(J["save", "jpg_odd_o6.jpg", 90, ""])[0],
              f"{r['mode']} {r['reason']!r}")
        check("J13b", "JPEG(16 の倍数でない): 画素に戻して回し、圧縮し直す(表示の向きのとおり)", name == "r0" and mean < 4 and F.jpeg_tables(d)[0] != F.jpeg_tables(d0)[0],
              f"{name} mean {mean:.2f}")
        check("J13c", "JPEG(16 の倍数でない): 付属情報は元のものを写して、向き 1・ピクセル数を書き換える",
              ex["ifd0"].get(F.ORIENTATION) == 1 and (ex["exif"].get(F.PIXEL_X), ex["exif"].get(F.PIXEL_Y)) == (w, h)
              and icc and icc[0] == [b for m, b in F.jpeg_app_segments(d0) if m == 0xE2 and b.startswith(b"ICC_PROFILE")][0]
              and F.jpeg_trailing(d) == b"", f"orientation {ex['ifd0'].get(F.ORIENTATION)} pixel {ex['exif'].get(F.PIXEL_X)}x{ex['exif'].get(F.PIXEL_Y)} size {w}x{h}")
        s = saved["jpg_odd_cmyk.jpg", 90, ""]
        res = ok(J["save", "jpg_odd_cmyk.jpg", 90, ""])
        if res[0]:
            dc = s.read_bytes()
            adobe = [b for m, b in F.jpeg_app_segments(dc) if m == 0xEE and b.startswith(b"Adobe")]
            name, mean = best_match(Image.open(RUN / "after-cmyk.png"), rot(Image.open(RUN / "shown-cmyk.png"), 90))
            # With the original APP14 (transform 0) instead, the colors read by Windows are off by ~58 on average;
            # with the re-encoded image's APP14 (transform 2 = YCCK) they are off by ~9 (re-compressing CMYK loses more than RGB, ~4).
            check("J13d", "JPEG(16 の倍数でない, CMYK): 圧縮し直した画像の Adobe APP14(YCCK)を使う", name == "r0" and mean < 20
                  and len(adobe) == 1 and adobe[0][-1] == 2, f"{name} mean {mean:.2f}, Adobe transform {[x[-1] for x in adobe]}")
        else:
            check("J13d", "JPEG(16 の倍数でない, CMYK): 圧縮し直した画像の Adobe APP14 を使う(色が正しい)", False, f"save failed: {res[1]}")

    # ================================================================ HEIC / HEIF / AVIF
    def _section3():
        for name in ["heic_o6.heic", "heic_o7.heic", "heic_o2.heic"]:
            expected = apply_heif_transforms(Image.open(shown["neutral-" + name]), F.heif_transforms((FIX / name).read_bytes()))
            m, mean = best_match(Image.open(shown[name]), expected)
            check(f"H1-{name}", f"HEIF: 開いたとき irot / imir をかけた画像を表示する({name})", m == "r0" and mean < 1,
                  f"{m} mean {mean:.2f}, transforms {F.heif_transforms((FIX / name).read_bytes())}")
        bad = []
        for name in ["heic_plain.heic", "heic_o6.heic", "heic_o7.heic", "heic_o2.heic"]:
            orig = decode_heif(FIX / name)
            for deg in (90, 180, 270):
                s = saved[name, deg, ""]
                if not same_pixels(decode_heif(s), rot(orig, deg)):
                    bad.append(f"{name} {deg} (libheif)")
                wic = best_match(Image.open(after[name, deg, ""]), rot(Image.open(shown.get(name, RUN / "x")) if name in shown else orig, deg))
                if wic[0] != "r0":
                    bad.append(f"{name} {deg} (WIC {wic})")
        check("H2", "HEIF: 主画像の irot を書き換えて回す(HEIC 4 種 × 90/180/270、libheif と WIC で確認)", not bad, "; ".join(bad))
        s0, s = (FIX / "heic_o6.heic").read_bytes(), saved["heic_o6.heic", 90, ""].read_bytes()
        diff_bytes = sum(1 for x, y in zip(s0, s) if x != y)
        check("H3a", "HEIF: irot があるときは、角度の値をその場で書き換える", len(s0) == len(s) and 1 <= diff_bytes <= 3 and
              F.heif_transforms(s0)[-1][1] - F.heif_transforms(s)[-1][1] in (90, -270), f"{diff_bytes} bytes differ, {F.heif_transforms(s0)} -> {F.heif_transforms(s)}")
        p0, p = (FIX / "heic_plain.heic").read_bytes(), saved["heic_plain.heic", 90, ""].read_bytes()
        check("H3b", "HEIF: irot がないときは irot を足す(最後に、左回り 270° = 右に 90°)", len(p) - len(p0) in (10, 11) and F.heif_transforms(p)[-1] == ("irot", 270),
              f"+{len(p) - len(p0)} bytes, {F.heif_transforms(p0)} -> {F.heif_transforms(p)}")
        o2 = saved["heic_o2.heic", 90, ""].read_bytes()
        check("H3c", "HEIF: imir があるときは irot を imir の前に入れる", [t for t, _ in F.heif_transforms(o2)][-2:] == ["irot", "imir"], str(F.heif_transforms(o2)))
        bad = []
        for name in ["heic_o6.heic", "heic_o7.heic", "heic_o2.heic"]:
            for deg in (90, 180, 270):
                f = saved[name, deg, ""]
                raw = pillow_heif.open_heif(io.BytesIO(F.heif_neutralized(f.read_bytes()))).to_pillow()
                o = F.heif_exif_orientation(f.read_bytes())
                ex = Image.Exif()
                ex[0x0112] = o
                raw.info["exif"] = ex.tobytes()
                if not same_pixels(ImageOps.exif_transpose(raw), decode_heif(f)):
                    bad.append(f"{name} {deg}: exif {o}")
        check("H4", "HEIF: EXIF の向きの値を、irot / imir をかけた結果を表す値に書き換える", not bad, "; ".join(bad))
        r = H[J["plan", "heic_clap_after_irot.heic"]]
        check("H5", "HEIF: clap が irot より後ろにあるものは保存できない", r["mode"] == "Blocked" and "clap" in r["reason"], f"{r['mode']} {r['reason']}")
        r = H[J["plan", "heic_moov.heic"]]
        check("H6", "HEIF: 動く HEIF(moov 箱があるもの)は保存できない", r["mode"] == "Blocked", f"{r['mode']} {r['reason']}")
        s0, s = (FIX / "heic_two.heic").read_bytes(), saved["heic_two.heic", 90, ""]
        second0 = pillow_heif.open_heif(FIX / "heic_two.heic")[1].to_pillow()
        second = pillow_heif.open_heif(s)[1].to_pillow()
        check("H7", "HEIF 副作用: 主画像と同じ irot を使っている画像も一緒に回る", same_pixels(second, rot(second0, 90)), "")
        s = saved["avif_plain.avif", 90, ""]
        check("H8", "AVIF: irot を足して回す(libavif と WIC で確認)", same_pixels(decode_heif(s), rot(decode_heif(FIX / "avif_plain.avif"), 90))
              and best_match(Image.open(after["avif_plain.avif", 90, ""]), rot(Image.open(shown["avif_plain.avif"]), 90))[0] == "r0", "")

    # ================================================================ WebP
    def _section4():
        check("W1", "WebP: 開いたとき EXIF の向きの値に従って回して表示する", same_pixels(Image.open(shown["webp_o6.webp"]), rot(base, 90)), "")
        r = H[J["plan", "webp_o6.webp"]]
        check("W2", "WebP: WebP では保存できない(別の形式で保存する)", r["mode"] == "ConvertWebP", f"{r['mode']} {r['reason']}")
        j = conv["webp_o6.webp", "Jpeg"]
        disp = rot(base, 90)
        m, mean, second = best_match(Image.open(j), rot(disp, 90), with_second=True) if j.exists() else (None, None, None)
        lossy_tables = F.jpeg_tables(saved["jpg_odd_o6.jpg", 90, ""].read_bytes())[0]
        check("W3", "WebP → JPG: 表示の向きのとおりに回し、画質 95 で圧縮する(量子化表が劣化ありの JPEG 保存と同じ)",
              ok(J["convert", "webp_o6.webp", "Jpeg"])[0] and m == "r0" and mean * 3 < second and F.jpeg_tables(j.read_bytes())[0] == lossy_tables,
              f"{m} mean {mean}, next {second}")
        ja = conv["webp_alpha.webp", "Jpeg"]
        corner = rgb(Image.open(ja))[0:12, :] if ja.exists() else None   # transparent quarter, away from the edge (JPEG ringing)
        check("W4", "WebP → JPG: 透明な部分は白にする", ja.exists() and corner.mean() >= 250, f"mean {None if corner is None else corner.mean():.1f}")
        pn = conv["webp_o6.webp", "Png"]
        pa = conv["webp_alpha.webp", "Png"]
        alpha_ok = pa.exists() and np.array_equal(np.asarray(Image.open(pa).convert("RGBA"))[..., 3],
                                                  np.asarray(rot(Image.open(FIX / "webp_alpha.webp").convert("RGBA"), 90))[..., 3])
        check("W5", "WebP → PNG: 画素の値はそのまま(回転だけ。透明の情報も残る)", pn.exists() and same_pixels(Image.open(pn), rot(disp, 90)) and alpha_ok,
              f"alpha kept {alpha_ok}")
        jd, pd = j.read_bytes() if j.exists() else b"", pn.read_bytes() if pn.exists() else b""
        check("W6", "WebP: 新しいファイルには EXIF / XMP が入らない",
              jd and F.jpeg_exif(jd) is None and F.jpeg_xmp(jd) is None and pd and not any(t in ("eXIf", "iTXt") for t, _ in F.png_chunks(pd)), "")

    # ================================================================ GIF / BMP / TIFF / JPEG XR / ICO / DDS
    def _section5():
        frames = Image.open(FIX / "gif_anim.gif")
        check("G1", "GIF: 開いたとき 1 コマ目を表示する", same_pixels(Image.open(shown["gif_anim.gif"]), frames.convert("RGB")), "")
        s = saved["gif_single.gif", 90, ""]
        gs = Image.open(s)
        check("G2", "GIF: 画素単位で回転して保存する(パレット付きのまま)", gs.mode == "P" and same_pixels(gs, rot(Image.open(FIX / "gif_single.gif"), 90)), gs.mode)
        r = H[J["plan", "gif_anim.gif"]]
        check("G3", "GIF: コマが 2 つ以上あるものは保存できない", r["mode"] == "Blocked", f"{r['mode']} {r['reason']}")
        s = saved["bmp24.bmp", 90, ""]
        h0, h1 = F.bmp_header((FIX / "bmp24.bmp").read_bytes()), F.bmp_header(s.read_bytes())
        check("B1", "BMP: 画素単位で回転して保存する", same_pixels(Image.open(s), rot(base, 90)), "")
        check("B2", "BMP 副作用: 幅と高さ(biWidth / biHeight)が入れ替わる", (h1["biWidth"], abs(h1["biHeight"])) == (h0["biHeight"], h0["biWidth"]),
              f"{h0['biWidth']}x{h0['biHeight']} -> {h1['biWidth']}x{h1['biHeight']}")
        check("B3", "BMP 副作用: biSizeImage が 0 になる", h0["biSizeImage"] != 0 and h1["biSizeImage"] == 0, f"{h0['biSizeImage']} -> {h1['biSizeImage']}")
        check("T1", "TIFF: 開いたとき向きのタグに従って回して表示する", same_pixels(Image.open(shown["tif_o6.tif"]), rot(base, 90)), "")
        s = saved["tif_o6.tif", 90, ""]
        ts, t0 = Image.open(s), Image.open(FIX / "tif_o6.tif")
        check("T2", "TIFF: 画素単位で回転して保存し、向きのタグを 1 にする", ts.tag_v2.get(274, 1) == 1 and same_pixels(ts, rot(rot(base, 90), 90)),
              f"orientation {ts.tag_v2.get(274)}")
        r = H[J["plan", "tif_multi.tif"]]
        check("T3", "TIFF: ページが 2 つ以上あるものは保存できない", r["mode"] == "Blocked", f"{r['mode']} {r['reason']}")
        check("T4", "TIFF 副作用: 圧縮方式とファイルの大きさが変わる", ts.tag_v2.get(259) != t0.tag_v2.get(259) and s.stat().st_size != (FIX / "tif_o6.tif").stat().st_size,
              f"compression {t0.tag_v2.get(259)} -> {ts.tag_v2.get(259)}, {(FIX / 'tif_o6.tif').stat().st_size} -> {s.stat().st_size} bytes")
        check("X1", "JPEG XR: 開いたとき WIC が回転をかけた画像を表示する", same_pixels(Image.open(shown["jxr_r90.jxr"]), rot(base, 90)), "")
        check("X2", "JPEG XR: 画素単位で回転して、可逆の設定で圧縮し直す", same_pixels(Image.open(after["jxr_r0.jxr", 90, ""]), rot(Image.open(shown["jxr_r0.jxr"]), 90)), "")
        r = H[J["load", "ico_multi.ico"]]
        check("I1", "ICO: 開いたとき一番大きい画像を表示する", (r["w"], r["h"]) == (64, 64), f"{r['w']}x{r['h']}")
        check("I2", "ICO / DDS: 保存できない", H[J["plan", "ico_multi.ico"]]["mode"] == "Blocked" and H[J["plan", "dds.dds"]]["mode"] == "Blocked",
              f"ico {H[J['plan', 'ico_multi.ico']]['mode']}, dds {H[J['plan', 'dds.dds']]['mode']}")
        r = H[J["load", "dds.dds"]]
        m, mean = best_match(Image.open(shown["dds.dds"]), Image.open(FIX / "dds.dds")) if r.get("ok") else (None, None)
        check("I3", "DDS: 開いたとき保存された並びのまま表示する", m == "r0" and mean < 2, r.get("error") or f"{m} mean {mean}")

    for fn, title in [(_section0, '共通'), (_section1, 'PNG'), (_section2, 'JPEG'), (_section3, 'HEIC / HEIF / AVIF'), (_section4, 'WebP'), (_section5, 'GIF / BMP / TIFF / JPEG XR / ICO / DDS')]:
        try:
            fn()
        except Exception as e:
            check("ERROR", f"{title}: 確認の途中でエラー", False, f"{type(e).__name__}: {e}")

    # ---------------------------------------------------------------- report
    failed = [x for x in REPORT if not x[2]]
    width = max(len(x[0]) for x in REPORT)
    for tid, title, good, detail in REPORT:
        print(f"{'PASS' if good else 'FAIL'}  {tid:<{width}}  {title}" + ("" if good else f"\n      -> {detail}"))
    print(f"\n{len(REPORT) - len(failed)} passed, {len(failed)} failed   (work files: {RUN})")
    print("not automated (needs the screen): WebP save dialog choices and overwrite question, showing the new file after conversion, real mouse wheel")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
