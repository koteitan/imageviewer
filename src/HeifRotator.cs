using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ImageViewer;

/// <summary>
/// Rotates a HEIF image (HEIC = HEVC inside, AVIF = AV1 inside) without touching the compressed pixels:
/// it changes the 'irot' (rotation) property of the primary image, which every HEIF reader must apply.
///
/// HEIF is an ISO base media file (like MP4): nested boxes [size][type][body]. The parts used here:
///   meta / pitm               id of the primary image
///   meta / iprp / ipco        list of properties (ispe, colr, clap, irot, imir, ...), numbered from 1
///   meta / iprp / ipma        which properties each image uses, in the order they are applied
///   meta / iloc               where each image's data is in the file (absolute offsets)
///   meta / iinf               item types (the 'Exif' item holds EXIF)
///
/// If the image already has an irot, its angle is changed in place. Otherwise a new irot box is
/// appended to ipco and linked in ipma; that makes the meta box longer, so every absolute offset
/// in iloc that points behind the insertion is moved by the same amount.
/// The EXIF orientation (informative only in HEIF) is updated to match, as cameras write it.
/// </summary>
internal static class HeifRotator
{
    private sealed class Box
    {
        public int Pos;          // start of the box
        public int Header;       // 8, or 16 with a 64-bit size
        public long Size;
        public string Type = "";
        public int Body => Pos + Header;
        public int End => (int)(Pos + Size);
    }

    /// <summary>Why the file cannot be rotated this way, or null if it can.</summary>
    public static string? CannotRotateReason(byte[] data)
    {
        try
        {
            Rotate(data, 90);
            return null;
        }
        catch (NotSupportedException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The file rotated clockwise by <paramref name="degrees"/> (90, 180, 270).</summary>
    public static byte[] Rotate(byte[] d, int degrees)
    {
        try
        {
            return RotateCore(d, ((degrees % 360) + 360) % 360);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new NotSupportedException("HEIF の構造を読めませんでした");
        }
    }

    private static byte[] RotateCore(byte[] d, int cw)
    {
        var top = Children(d, 0, d.Length);
        if (top.Any(b => b.Type == "moov")) throw No("動く HEIF / AVIF(画像シーケンス)は保存できません");
        var meta = One(top, "meta");
        var mk = Children(d, meta.Body + 4, meta.End); // meta is a FullBox
        var pitm = One(mk, "pitm");
        var iprp = One(mk, "iprp");
        var iloc = One(mk, "iloc");
        uint primary = d[pitm.Body] == 0 ? U16(d, pitm.Body + 4) : U32(d, pitm.Body + 4);

        var pk = Children(d, iprp.Body, iprp.End);
        var ipco = One(pk, "ipco");
        var ipma = One(pk, "ipma");
        var props = Children(d, ipco.Body, ipco.End);
        string TypeOf(int index) => index >= 1 && index <= props.Count ? props[index - 1].Type : "";

        // ---- ipma: find the association list of the primary image
        int ver = d[ipma.Body];
        bool wide = (U24(d, ipma.Body + 1) & 1) != 0; // 15-bit property indexes
        int q = ipma.Body + 4;
        uint entries = U32(d, q);
        q += 4;
        int countPos = -1, listEnd = -1;
        var assoc = new List<(int index, int pos)>();
        for (uint e = 0; e < entries; e++)
        {
            uint item = ver < 1 ? U16(d, q) : U32(d, q);
            q += ver < 1 ? 2 : 4;
            int cPos = q;
            int n = d[q++];
            var list = new List<(int, int)>();
            for (int j = 0; j < n; j++)
            {
                int index = wide ? (int)(U16(d, q) & 0x7FFF) : d[q] & 0x7F;
                list.Add((index, q));
                q += wide ? 2 : 1;
            }
            if (item == primary)
            {
                countPos = cPos;
                assoc = list;
                listEnd = q;
            }
        }
        if (countPos < 0) throw No("主画像のプロパティが見つかりません");

        int irotAt = assoc.FindIndex(a => TypeOf(a.index) == "irot");
        int imirAt = assoc.FindIndex(a => TypeOf(a.index) == "imir");
        if (irotAt >= 0 && assoc.Skip(irotAt + 1).Any(a => TypeOf(a.index) == "clap"))
            throw No("切り抜き(clap)が回転のあとにある HEIF は保存できません");

        var output = (byte[])d.Clone();
        var transforms = new List<(string type, int value)>(); // final order, for the EXIF orientation

        if (irotAt >= 0)
        {
            // change the angle in place (irot angles are counter-clockwise)
            int body = props[assoc[irotAt].index - 1].Body;
            int ccw = (d[body] & 3) * 90;
            // shown = mirror . rotate(ccw) when the mirror comes after the rotation:
            // rotate_cw(cw) . mirror . rotate(ccw) = mirror . rotate(ccw + cw)
            ccw = imirAt > irotAt ? ccw + cw : ccw - cw;
            ccw = ((ccw % 360) + 360) % 360;
            output[body] = (byte)((d[body] & 0xFC) | (ccw / 90));
            foreach (var a in assoc)
            {
                if (TypeOf(a.index) == "irot") transforms.Add(("irot", ccw));
                if (TypeOf(a.index) == "imir") transforms.Add(("imir", d[props[a.index - 1].Body] & 1));
            }
            UpdateExif(d, output, mk, iloc, transforms);
            return output;
        }

        // ---- add a new irot box
        int newIndex = props.Count + 1;
        if (newIndex > (wide ? 0x7FFF : 0x7F)) throw No("プロパティが多すぎて回転を追加できません");
        if (assoc.Count >= 255) throw No("プロパティが多すぎて回転を追加できません");
        // MIAF order: irot before imir. Before a mirror: rotate(ccw) with ccw = cw (see above);
        // otherwise it is the last transform: rotate counter-clockwise by -cw.
        int newCcw = imirAt >= 0 ? cw : (360 - cw) % 360;
        byte[] irot = { 0, 0, 0, 9, (byte)'i', (byte)'r', (byte)'o', (byte)'t', (byte)(newCcw / 90) };
        byte[] link = wide
            ? new[] { (byte)(0x80 | (newIndex >> 8)), (byte)newIndex } // essential
            : new[] { (byte)(0x80 | newIndex) };
        int linkAt = imirAt >= 0 ? assoc[imirAt].pos : listEnd;
        var inserts = new List<(int at, byte[] bytes)> { (ipco.End, irot), (linkAt, link) };
        inserts.Sort((a, b) => a.at.CompareTo(b.at));

        output[countPos] = (byte)(assoc.Count + 1);
        AddToSize(output, ipco, irot.Length);
        AddToSize(output, ipma, link.Length);
        AddToSize(output, iprp, irot.Length + link.Length);
        AddToSize(output, meta, irot.Length + link.Length);
        ShiftIloc(d, output, iloc, pos => inserts.Where(i => i.at <= pos).Sum(i => i.bytes.Length));

        foreach (var a in assoc)
        {
            if (linkAt == a.pos) transforms.Add(("irot", newCcw));
            if (TypeOf(a.index) == "imir") transforms.Add(("imir", d[props[a.index - 1].Body] & 1));
        }
        if (linkAt == listEnd) transforms.Add(("irot", newCcw));
        UpdateExif(d, output, mk, iloc, transforms);

        // splice the inserts into the patched copy
        var result = new byte[output.Length + inserts.Sum(i => i.bytes.Length)];
        int src = 0, dst = 0;
        foreach (var (at, bytes) in inserts)
        {
            Buffer.BlockCopy(output, src, result, dst, at - src);
            dst += at - src;
            src = at;
            Buffer.BlockCopy(bytes, 0, result, dst, bytes.Length);
            dst += bytes.Length;
        }
        Buffer.BlockCopy(output, src, result, dst, output.Length - src);
        return result;
    }

    // ---------------------------------------------------------------- iloc

    private sealed class Location
    {
        public uint Item;
        public int Method;                 // 0 = file offset, 1 = inside idat, 2 = item reference
        public int BasePos, BaseSize;
        public ulong BaseOffset;
        public readonly List<(int pos, ulong offset, ulong length)> Extents = new();
    }

    private static List<Location> ParseIloc(byte[] d, Box iloc, out int offSize)
    {
        int p = iloc.Body;
        int ver = d[p];
        p += 4;
        offSize = d[p] >> 4;
        int lenSize = d[p] & 15, baseSize = d[p + 1] >> 4, idxSize = ver is 1 or 2 ? d[p + 1] & 15 : 0;
        p += 2;
        uint count = ver < 2 ? U16(d, p) : U32(d, p);
        p += ver < 2 ? 2 : 4;
        var list = new List<Location>();
        for (uint i = 0; i < count; i++)
        {
            var loc = new Location { Item = ver < 2 ? U16(d, p) : U32(d, p) };
            p += ver < 2 ? 2 : 4;
            if (ver is 1 or 2)
            {
                loc.Method = (int)(U16(d, p) & 0xF);
                p += 2;
            }
            p += 2; // data_reference_index
            loc.BasePos = p;
            loc.BaseSize = baseSize;
            loc.BaseOffset = ReadN(d, p, baseSize);
            p += baseSize;
            int extents = (int)U16(d, p);
            p += 2;
            for (int e = 0; e < extents; e++)
            {
                p += idxSize;
                int offPos = p;
                ulong off = ReadN(d, p, offSize);
                p += offSize;
                ulong len = ReadN(d, p, lenSize);
                p += lenSize;
                loc.Extents.Add((offPos, off, len));
            }
            list.Add(loc);
        }
        return list;
    }

    /// <summary>Move every absolute data offset by the number of bytes inserted before it.</summary>
    private static void ShiftIloc(byte[] d, byte[] output, Box iloc, Func<long, int> shiftAt)
    {
        foreach (var loc in ParseIloc(d, iloc, out int offSize))
        {
            if (loc.Method != 0) continue; // idat / item references are relative
            var shifts = loc.Extents.Select(e => shiftAt((long)(loc.BaseOffset + e.offset))).ToList();
            if (shifts.All(s => s == 0)) continue;
            if (loc.BaseSize > 0)
            {
                if (shifts.Distinct().Count() != 1) throw No("画像データの位置を直せません");
                WriteN(output, loc.BasePos, loc.BaseSize, loc.BaseOffset + (ulong)shifts[0]);
            }
            else
            {
                if (offSize == 0) throw No("画像データの位置を直せません");
                for (int i = 0; i < loc.Extents.Count; i++)
                    WriteN(output, loc.Extents[i].pos, offSize, loc.Extents[i].offset + (ulong)shifts[i]);
            }
        }
    }

    // ---------------------------------------------------------------- EXIF

    /// <summary>
    /// Set the EXIF orientation of the 'Exif' item to the final irot/imir transform
    /// (written in place: positions refer to the original file, before any insertion).
    /// </summary>
    private static void UpdateExif(byte[] d, byte[] output, List<Box> metaChildren, Box iloc, List<(string type, int value)> transforms)
    {
        var iinf = metaChildren.FirstOrDefault(b => b.Type == "iinf");
        if (iinf is null) return;
        uint? exifItem = null;
        int p = iinf.Body;
        int ver = d[p];
        p += 4 + (ver == 0 ? 2 : 4);
        foreach (var infe in Children(d, p, iinf.End).Where(b => b.Type == "infe"))
        {
            int v = d[infe.Body];
            if (v < 2) continue;
            int ip = infe.Body + 4;
            uint id = v == 2 ? U16(d, ip) : U32(d, ip);
            ip += (v == 2 ? 2 : 4) + 2;
            if (Encoding.ASCII.GetString(d, ip, 4) == "Exif") exifItem = id;
        }
        if (exifItem is null) return;

        var loc = ParseIloc(d, iloc, out _).FirstOrDefault(l => l.Item == exifItem);
        if (loc is null || loc.Extents.Count != 1) return;
        long start;
        if (loc.Method == 0) start = (long)(loc.BaseOffset + loc.Extents[0].offset);
        else if (loc.Method == 1)
        {
            var idat = metaChildren.FirstOrDefault(b => b.Type == "idat");
            if (idat is null) return;
            start = idat.Body + (long)(loc.BaseOffset + loc.Extents[0].offset);
        }
        else return;

        int tiff = (int)(start + 4 + U32(d, (int)start)); // Exif item: 4-byte offset to the TIFF header
        bool le = d[tiff] == (byte)'I';
        int Read16(int at) => le ? d[at] | d[at + 1] << 8 : d[at] << 8 | d[at + 1];
        int Read32(int at) => le ? d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24 : d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3];
        int ifd = tiff + Read32(tiff + 4);
        int n = Read16(ifd);
        for (int i = 0; i < n; i++)
        {
            int entry = ifd + 2 + 12 * i;
            if (Read16(entry) != 0x0112 || Read16(entry + 2) != 3) continue;
            int value = ExifOrientation(transforms);
            output[entry + 8] = (byte)(le ? value : 0);
            output[entry + 9] = (byte)(le ? 0 : value);
            return;
        }
    }

    /// <summary>EXIF orientation (1..8) equal to applying the transforms in order.</summary>
    private static int ExifOrientation(List<(string type, int value)> transforms)
    {
        // state: flip horizontally (if f), then rotate clockwise by r
        int r = 0;
        bool f = false;
        foreach (var (type, value) in transforms)
        {
            // imir axis as libheif and Windows read it (checked against libheif's own files):
            // 1 = flip left-right, 0 = flip top-bottom
            if (type == "irot") r = (r - value + 360) % 360;              // value is counter-clockwise
            else if (value == 1) { r = (360 - r) % 360; f = !f; }        // flip left-right
            else { r = (540 - r) % 360; f = !f; }                         // flip top-bottom = left-right + 180
        }
        return (r, f) switch
        {
            (0, false) => 1, (0, true) => 2, (180, false) => 3, (180, true) => 4,
            (270, true) => 5, (90, false) => 6, (90, true) => 7, _ => 8,
        };
    }

    // ---------------------------------------------------------------- boxes and numbers

    private static List<Box> Children(byte[] d, int start, int end)
    {
        var list = new List<Box>();
        int p = start;
        while (p + 8 <= end)
        {
            long size = U32(d, p);
            int header = 8;
            if (size == 1)
            {
                size = (long)ReadN(d, p + 8, 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - p;
            }
            if (size < header || p + size > end) throw No("HEIF の構造を読めませんでした");
            list.Add(new Box { Pos = p, Header = header, Size = size, Type = Encoding.ASCII.GetString(d, p + 4, 4) });
            p += (int)size;
        }
        return list;
    }

    private static Box One(List<Box> boxes, string type)
    {
        var found = boxes.Where(b => b.Type == type).ToList();
        if (found.Count != 1) throw No($"HEIF の {type} が見つかりません");
        return found[0];
    }

    private static void AddToSize(byte[] output, Box box, int delta)
    {
        if (box.Header == 16)
        {
            WriteN(output, box.Pos + 8, 8, (ulong)(box.Size + delta));
            return;
        }
        long size = box.Size + delta;
        if (size > uint.MaxValue) throw No("ファイルが大きすぎます");
        WriteN(output, box.Pos, 4, (ulong)size);
    }

    private static uint U16(byte[] d, int p) => (uint)(d[p] << 8 | d[p + 1]);
    private static uint U24(byte[] d, int p) => (uint)(d[p] << 16 | d[p + 1] << 8 | d[p + 2]);
    private static uint U32(byte[] d, int p) => (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

    private static ulong ReadN(byte[] d, int p, int n)
    {
        ulong v = 0;
        for (int i = 0; i < n; i++) v = v << 8 | d[p + i];
        return v;
    }

    private static void WriteN(byte[] d, int p, int n, ulong v)
    {
        if (n < 8 && v >> (8 * n) != 0) throw No("ファイルが大きすぎます");
        for (int i = n - 1; i >= 0; i--, v >>= 8) d[p + i] = (byte)v;
    }

    private static NotSupportedException No(string message) => new(message);
}
