"""Small readers/writers for the file structures the tests look inside: JPEG segments,
EXIF (TIFF) structures, PNG chunks, BMP headers, WebP chunks and HEIF (ISOBMFF) boxes.

They are independent of the app (which uses Windows' WIC), so the tests check the app
with a second implementation.
"""
import struct
import zlib

# ---------------------------------------------------------------- JPEG


def jpeg_segments(d):
    """[(marker, payload)] of the header part up to SOS, and the offset of SOS."""
    assert d[:2] == b"\xff\xd8", "not a JPEG"
    p, out = 2, []
    while True:
        while d[p] == 0xFF and d[p + 1] == 0xFF:
            p += 1
        assert d[p] == 0xFF, f"bad marker at {p}"
        m = d[p + 1]
        if m == 0xDA:
            return out, p
        n = struct.unpack(">H", d[p + 2:p + 4])[0]
        out.append((m, d[p + 4:p + 2 + n]))
        p += 2 + n


def jpeg_app_segments(d):
    """APPn and COM segments as [(marker, payload)]."""
    return [(m, b) for m, b in jpeg_segments(d)[0] if 0xE0 <= m <= 0xEF or m == 0xFE]


def jpeg_tables(d):
    """(quantization tables, Huffman tables) as sorted lists, independent of how they are split into segments."""
    dqt, dht = [], []
    for m, body in jpeg_segments(d)[0]:
        q = 0
        if m == 0xDB:
            while q < len(body):
                pq, tq = body[q] >> 4, body[q] & 15
                size = 64 * (2 if pq else 1)
                dqt.append((tq, body[q + 1:q + 1 + size]))
                q += 1 + size
        elif m == 0xC4:
            while q < len(body):
                counts = body[q + 1:q + 17]
                total = sum(counts)
                dht.append((body[q], bytes(counts) + body[q + 17:q + 17 + total]))
                q += 17 + total
    return sorted(dqt), sorted(dht)


def jpeg_segment_counts(d):
    segs, _ = jpeg_segments(d)
    return sum(1 for m, _ in segs if m == 0xDB), sum(1 for m, _ in segs if m == 0xC4)


def jpeg_scan(d):
    """Bytes from SOS to the end of the EOI marker (trailing data after EOI excluded)."""
    _, sos = jpeg_segments(d)
    eoi = d.rfind(b"\xff\xd9")
    return d[sos:eoi + 2]


def jpeg_trailing(d):
    """Bytes after the last EOI marker."""
    return d[d.rfind(b"\xff\xd9") + 2:]


def jpeg_size(d):
    for m, body in jpeg_segments(d)[0]:
        if 0xC0 <= m <= 0xCF and m not in (0xC4, 0xC8, 0xCC):
            h, w = struct.unpack(">HH", body[1:5])
            return w, h
    raise ValueError("no SOF")


def jpeg_insert_segments(d, segments, after_soi=True):
    """Insert [(marker, payload)] right after SOI."""
    raw = b"".join(b"\xff" + bytes([m]) + struct.pack(">H", len(p) + 2) + p for m, p in segments)
    return d[:2] + raw + d[2:]


def jpeg_exif(d):
    for m, b in jpeg_app_segments(d):
        if m == 0xE1 and b.startswith(b"Exif\0\0"):
            return b[6:]
    return None


def jpeg_xmp(d):
    for m, b in jpeg_app_segments(d):
        if m == 0xE1 and b.startswith(b"http://ns.adobe.com/xap/1.0/\0"):
            return b[len(b"http://ns.adobe.com/xap/1.0/\0"):]
    return None


# ---------------------------------------------------------------- EXIF (TIFF structure)

ORIENTATION, EXIF_IFD, COLOR_SPACE, PIXEL_X, PIXEL_Y, DATE_ORIGINAL = 0x0112, 0x8769, 0xA001, 0xA002, 0xA003, 0x9003


def build_exif(orientation=None, pixel=None, color_space=None, date=None, thumbnail=None):
    """Big-endian TIFF structure: IFD0 (Orientation, Exif IFD pointer), Exif IFD, optional IFD1 thumbnail."""
    ifd0 = []
    if orientation is not None:
        ifd0.append((ORIENTATION, 3, 1, orientation))
    exif = []
    if date is not None:
        exif.append((DATE_ORIGINAL, 2, 20, None))
    if color_space is not None:
        exif.append((COLOR_SPACE, 3, 1, color_space))
    if pixel is not None:
        exif.append((PIXEL_X, 4, 1, pixel[0]))
        exif.append((PIXEL_Y, 4, 1, pixel[1]))
    ifd0.append((EXIF_IFD, 4, 1, None))
    ifd0_at = 8
    exif_at = ifd0_at + 2 + 12 * len(ifd0) + 4
    date_at = exif_at + 2 + 12 * len(exif) + 4
    ifd1_at = date_at + (20 if date is not None else 0)
    thumb_at = ifd1_at + 2 + 12 * 2 + 4

    def ifd(entries, next_ifd):
        out = struct.pack(">H", len(entries))
        for tag, typ, count, value in entries:
            if tag == EXIF_IFD:
                value = exif_at
            if tag == DATE_ORIGINAL:
                value = date_at
            if typ == 3:
                out += struct.pack(">HHIHH", tag, typ, count, value, 0)
            else:
                out += struct.pack(">HHII", tag, typ, count, value)
        return out + struct.pack(">I", next_ifd)

    data = b"MM\0*" + struct.pack(">I", ifd0_at)
    data += ifd(ifd0, ifd1_at if thumbnail else 0)
    data += ifd(exif, 0)
    if date is not None:
        data += date.encode("ascii")[:19].ljust(19, b"\0") + b"\0"
    if thumbnail:
        data += struct.pack(">H", 2)
        data += struct.pack(">HHII", 0x0201, 4, 1, thumb_at)
        data += struct.pack(">HHII", 0x0202, 4, 1, len(thumbnail))
        data += struct.pack(">I", 0) + thumbnail
    return data


def read_exif(tiff):
    """{'ifd0': {tag: value}, 'exif': {tag: value}, 'next_ifd': offset} for SHORT/LONG/ASCII values."""
    le = tiff[:2] == b"II"
    e = "<" if le else ">"

    def ifd(at):
        n = struct.unpack(e + "H", tiff[at:at + 2])[0]
        out = {}
        for i in range(n):
            tag, typ, count = struct.unpack(e + "HHI", tiff[at + 2 + 12 * i:at + 10 + 12 * i])
            vpos = at + 10 + 12 * i
            if typ == 3:
                out[tag] = struct.unpack(e + "H", tiff[vpos:vpos + 2])[0]
            elif typ == 4:
                out[tag] = struct.unpack(e + "I", tiff[vpos:vpos + 4])[0]
            elif typ == 2:
                off = struct.unpack(e + "I", tiff[vpos:vpos + 4])[0] if count > 4 else vpos
                out[tag] = tiff[off:off + count].rstrip(b"\0").decode("ascii", "replace")
            else:
                out[tag] = (typ, count)
        nxt = struct.unpack(e + "I", tiff[at + 2 + 12 * n:at + 6 + 12 * n])[0]
        return out, nxt

    ifd0_at = struct.unpack(e + "I", tiff[4:8])[0]
    ifd0, nxt = ifd(ifd0_at)
    exif = ifd(ifd0[EXIF_IFD])[0] if EXIF_IFD in ifd0 else {}
    return {"ifd0": ifd0, "exif": exif, "next_ifd": nxt}


# ---------------------------------------------------------------- PNG


def png_chunks(d):
    assert d[:8] == b"\x89PNG\r\n\x1a\n"
    p, out = 8, []
    while p < len(d):
        n, t = struct.unpack(">I4s", d[p:p + 8])
        out.append((t.decode("latin1"), d[p + 8:p + 8 + n]))
        p += 12 + n
    return out


def png_build(chunks):
    out = b"\x89PNG\r\n\x1a\n"
    for t, body in chunks:
        tb = t.encode("latin1")
        out += struct.pack(">I", len(body)) + tb + body + struct.pack(">I", zlib.crc32(tb + body) & 0xFFFFFFFF)
    return out


def png_itxt(keyword, text):
    return keyword.encode("latin1") + b"\0\0\0\0\0" + text.encode("utf8")


def png_itxt_text(body):
    k, rest = body.split(b"\0", 1)
    # compression flag, method, language tag \0, translated keyword \0, text
    rest = rest[2:]
    _, rest = rest.split(b"\0", 1)
    _, text = rest.split(b"\0", 1)
    return k.decode("latin1"), text.decode("utf8")


# ---------------------------------------------------------------- BMP


def bmp_header(d):
    names = ["bfType", "bfSize", "r1", "r2", "bfOffBits", "biSize", "biWidth", "biHeight", "biPlanes",
             "biBitCount", "biCompression", "biSizeImage", "biXPelsPerMeter", "biYPelsPerMeter", "biClrUsed", "biClrImportant"]
    return dict(zip(names, struct.unpack("<2sIHHIIiiHHIIiiII", d[:54])))


# ---------------------------------------------------------------- WebP


def webp_chunks(d):
    assert d[:4] == b"RIFF" and d[8:12] == b"WEBP"
    p, out = 12, []
    while p + 8 <= len(d):
        t = d[p:p + 4].decode("latin1")
        n = struct.unpack("<I", d[p + 4:p + 8])[0]
        out.append((t, d[p + 8:p + 8 + n]))
        p += 8 + n + (n & 1)
    return out


# ---------------------------------------------------------------- HEIF (ISOBMFF)


class Box:
    def __init__(self, pos, size, header, typ):
        self.pos, self.size, self.header, self.type = pos, size, header, typ

    @property
    def body(self):
        return self.pos + self.header

    @property
    def end(self):
        return self.pos + self.size


def boxes(d, start, end):
    out, p = [], start
    while p + 8 <= end:
        size, typ = struct.unpack(">I4s", d[p:p + 8])
        header = 8
        if size == 1:
            size = struct.unpack(">Q", d[p + 8:p + 16])[0]
            header = 16
        elif size == 0:
            size = end - p
        out.append(Box(p, size, header, typ.decode("latin1")))
        p += size
    return out


def one(bs, typ):
    found = [b for b in bs if b.type == typ]
    assert len(found) == 1, f"{typ}: {len(found)}"
    return found[0]


def heif_meta(d):
    """(meta, children of meta, ipco properties [Box], ipma associations {item: [(index, essential, pos)]}, ipma box, primary id)."""
    meta = one(boxes(d, 0, len(d)), "meta")
    mk = boxes(d, meta.body + 4, meta.end)
    pitm = one(mk, "pitm")
    primary = struct.unpack(">H", d[pitm.body + 4:pitm.body + 6])[0] if d[pitm.body] == 0 else struct.unpack(">I", d[pitm.body + 4:pitm.body + 8])[0]
    iprp = one(mk, "iprp")
    pk = boxes(d, iprp.body, iprp.end)
    ipco, ipma = one(pk, "ipco"), one(pk, "ipma")
    props = boxes(d, ipco.body, ipco.end)
    ver, flags = d[ipma.body], int.from_bytes(d[ipma.body + 1:ipma.body + 4], "big")
    q = ipma.body + 4
    n = struct.unpack(">I", d[q:q + 4])[0]
    q += 4
    assoc = {}
    for _ in range(n):
        if ver < 1:
            item = struct.unpack(">H", d[q:q + 2])[0]
            q += 2
        else:
            item = struct.unpack(">I", d[q:q + 4])[0]
            q += 4
        cnt = d[q]
        q += 1
        lst = []
        for _ in range(cnt):
            if flags & 1:
                v = struct.unpack(">H", d[q:q + 2])[0]
                lst.append((v & 0x7FFF, bool(v >> 15), q))
                q += 2
            else:
                lst.append((d[q] & 0x7F, bool(d[q] >> 7), q))
                q += 1
        assoc[item] = lst
    return meta, mk, props, assoc, ipma, primary


def heif_transforms(d, item=None):
    """[(type, value)] of irot / imir / clap for an item (default: primary), in association order."""
    meta, mk, props, assoc, ipma, primary = heif_meta(d)
    out = []
    for idx, _, _ in assoc[item if item is not None else primary]:
        b = props[idx - 1]
        if b.type == "irot":
            out.append(("irot", (d[b.body] & 3) * 90))
        elif b.type == "imir":
            out.append(("imir", d[b.body] & 1))
        elif b.type == "clap":
            out.append(("clap", None))
    return out


def heif_property_index(d, item, typ):
    meta, mk, props, assoc, ipma, primary = heif_meta(d)
    for idx, _, _ in assoc[item]:
        if props[idx - 1].type == typ:
            return idx
    return None


def heif_neutralized(d):
    """Copy with every irot angle 0 and every imir turned into 'irot 0' (same size): decodes without rotation/mirror."""
    d = bytearray(d)
    meta, mk, props, assoc, ipma, primary = heif_meta(bytes(d))
    for b in props:
        if b.type == "irot":
            d[b.body] = 0
        elif b.type == "imir":
            d[b.pos + 4:b.pos + 8] = b"irot"
            d[b.body] = 0
    return bytes(d)


def heif_move_clap_after_irot(d):
    """Swap the clap and irot associations of the primary item (both 1 byte): clap is then applied after irot."""
    d = bytearray(d)
    meta, mk, props, assoc, ipma, primary = heif_meta(bytes(d))
    lst = assoc[primary]
    ci = next(i for i, (idx, _, _) in enumerate(lst) if props[idx - 1].type == "clap")
    ri = next(i for i, (idx, _, _) in enumerate(lst) if props[idx - 1].type == "irot")
    assert ci < ri
    pc, pr = lst[ci][2], lst[ri][2]
    d[pc], d[pr] = d[pr], d[pc]
    return bytes(d)


def heif_append_box(d, typ):
    """An empty top-level box at the end of the file."""
    return d + struct.pack(">I", 8) + typ.encode("latin1")


def _iloc_entries(d, iloc):
    p = iloc.body
    ver = d[p]
    p += 4
    off_size, len_size = d[p] >> 4, d[p] & 15
    base_size, idx_size = d[p + 1] >> 4, (d[p + 1] & 15 if ver in (1, 2) else 0)
    p += 2
    count = struct.unpack(">H", d[p:p + 2])[0] if ver < 2 else struct.unpack(">I", d[p:p + 4])[0]
    p += 2 if ver < 2 else 4
    out = []
    for _ in range(count):
        p += 2 if ver < 2 else 4
        method = 0
        if ver in (1, 2):
            method = struct.unpack(">H", d[p:p + 2])[0] & 15
            p += 2
        p += 2
        base_pos, base = p, int.from_bytes(d[p:p + base_size], "big") if base_size else 0
        p += base_size
        n_ext = struct.unpack(">H", d[p:p + 2])[0]
        p += 2
        exts = []
        for _ in range(n_ext):
            p += idx_size
            exts.append((p, int.from_bytes(d[p:p + off_size], "big") if off_size else 0))
            p += off_size + len_size
        out.append((method, base_pos, base_size, base, off_size, exts))
    return out


def heif_share_primary_irot(d, item):
    """Make `item` also use the primary item's irot property (append one association, shift iloc offsets)."""
    meta, mk, props, assoc, ipma, primary = heif_meta(d)
    idx = heif_property_index(d, primary, "irot")
    assert idx is not None and idx < 0x80 and not (int.from_bytes(d[ipma.body + 1:ipma.body + 4], "big") & 1)
    lst = assoc[item]
    count_pos = lst[0][2] - 1
    insert_at = lst[-1][2] + 1
    iloc = one(mk, "iloc")
    out = bytearray(d)
    out[count_pos] += 1
    for b in (ipma, one(boxes(d, meta.body + 4, meta.end), "iprp"), meta):
        size = struct.unpack(">I", d[b.pos:b.pos + 4])[0]
        out[b.pos:b.pos + 4] = struct.pack(">I", size + 1)
    for method, base_pos, base_size, base, off_size, exts in _iloc_entries(d, iloc):
        if method != 0:
            continue
        if base_size:
            if base >= insert_at:
                out[base_pos:base_pos + base_size] = (base + 1).to_bytes(base_size, "big")
        else:
            for pos, off in exts:
                if off >= insert_at:
                    out[pos:pos + off_size] = (off + 1).to_bytes(off_size, "big")
    return bytes(out[:insert_at]) + bytes([0x80 | idx]) + bytes(out[insert_at:])


def heif_exif_orientation(d):
    """EXIF orientation stored in the Exif item, or None."""
    meta, mk, props, assoc, ipma, primary = heif_meta(d)
    iinf = one(mk, "iinf")
    p = iinf.body
    p += 4 + (2 if d[p] == 0 else 4)
    exif_item = None
    for b in boxes(d, p, iinf.end):
        if b.type != "infe" or d[b.body] < 2:
            continue
        v = d[b.body]
        ip = b.body + 4
        item = struct.unpack(">H", d[ip:ip + 2])[0] if v == 2 else struct.unpack(">I", d[ip:ip + 4])[0]
        ip += (2 if v == 2 else 4) + 2
        if d[ip:ip + 4] == b"Exif":
            exif_item = item
    if exif_item is None:
        return None
    iloc = one(mk, "iloc")
    ver = d[iloc.body]
    p = iloc.body + 4
    off_size, len_size = d[p] >> 4, d[p] & 15
    base_size, idx_size = d[p + 1] >> 4, (d[p + 1] & 15 if ver in (1, 2) else 0)
    p += 2
    count = struct.unpack(">H", d[p:p + 2])[0] if ver < 2 else struct.unpack(">I", d[p:p + 4])[0]
    p += 2 if ver < 2 else 4
    for _ in range(count):
        item = struct.unpack(">H", d[p:p + 2])[0] if ver < 2 else struct.unpack(">I", d[p:p + 4])[0]
        p += 2 if ver < 2 else 4
        method = 0
        if ver in (1, 2):
            method = struct.unpack(">H", d[p:p + 2])[0] & 15
            p += 2
        p += 2
        base = int.from_bytes(d[p:p + base_size], "big") if base_size else 0
        p += base_size
        n_ext = struct.unpack(">H", d[p:p + 2])[0]
        p += 2
        first = None
        for _ in range(n_ext):
            p += idx_size
            off = int.from_bytes(d[p:p + off_size], "big") if off_size else 0
            if first is None:
                first = off
            p += off_size + len_size
        if item == exif_item and method == 0:
            start = base + first
            tiff = start + 4 + struct.unpack(">I", d[start:start + 4])[0]
            return read_exif(d[tiff:])["ifd0"].get(ORIENTATION)
    return None
