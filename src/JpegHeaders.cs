using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ImageViewer;

/// <summary>
/// Puts the original JPEG's metadata on a rotated JPEG.
///
/// A JPEG is: SOI, header segments (APP0..APP15 = EXIF, ICC profile, XMP, ...; COM), then the
/// image (quantization / Huffman tables, frame header, compressed scan) and EOI.
/// The Windows JPEG encoder rotates the image without loss, but rewrites the header segments:
/// it replaces a wide-gamut ICC profile (e.g. Display P3) with sRGB and drops the EXIF ColorSpace.
/// So the result here = the original's header segments (EXIF patched) + the encoder's image part.
///
/// Patched in the copied EXIF: Orientation = 1, PixelXDimension / PixelYDimension = new size,
/// and the link to the EXIF thumbnail is removed (the thumbnail itself is not rotated).
/// Patched in XMP: tiff:Orientation = 1 and the pixel dimensions (when the length stays the same).
/// Dropped: MPF segments (they point at extra images after the end of the file, which are not kept).
/// When the image was re-compressed (not rotated losslessly), the Adobe APP14 segment (color transform
/// of the compressed data) is taken from the new image instead of the original.
/// </summary>
internal static class JpegHeaders
{
    private sealed class Segment
    {
        public byte Marker;
        public int LengthPos;      // position of the 2-byte length (payload follows it)
        public int Length;         // value of the length field (includes its own 2 bytes)
        public int PayloadPos => LengthPos + 2;
        public int PayloadLength => Length - 2;
    }

    public static byte[] Rebuild(byte[] original, byte[] rotated, bool reencoded)
    {
        var (headers, _) = Split(original);
        var (newHeaders, imageStart) = Split(rotated);
        var (width, height) = FrameSize(rotated, imageStart);

        using var ms = new MemoryStream();
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD8);
        foreach (var s in headers)
        {
            var seg = new byte[2 + s.Length];
            seg[0] = 0xFF;
            seg[1] = s.Marker;
            Buffer.BlockCopy(original, s.LengthPos, seg, 2, s.Length);
            const int payload = 4; // in seg: FF, marker, length (2)
            if (s.Marker == 0xE2 && StartsWith(seg, payload, "MPF\0")) continue;
            if (reencoded && IsAdobe(s.Marker, seg)) continue;
            if (s.Marker == 0xE1 && StartsWith(seg, payload, "Exif\0\0")) PatchExif(seg, payload + 6, width, height);
            else if (s.Marker == 0xE1 && StartsWith(seg, payload, "http://ns.adobe.com/xap/1.0/\0")) PatchXmp(seg, width, height);
            ms.Write(seg, 0, seg.Length);
        }
        if (reencoded)
        {
            foreach (var s in newHeaders)
            {
                var seg = new byte[2 + s.Length];
                seg[0] = 0xFF;
                seg[1] = s.Marker;
                Buffer.BlockCopy(rotated, s.LengthPos, seg, 2, s.Length);
                if (IsAdobe(s.Marker, seg)) ms.Write(seg, 0, seg.Length);
            }
        }
        ms.Write(rotated, imageStart, rotated.Length - imageStart);
        return ms.ToArray();
    }

    private static bool IsAdobe(byte marker, byte[] seg) => marker == 0xEE && StartsWith(seg, 4, "Adobe");

    /// <summary>The header segments (APPn, COM) and where the image part starts.</summary>
    private static (List<Segment> headers, int imageStart) Split(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new NotSupportedException("JPEG の構造を読めませんでした");
        var list = new List<Segment>();
        int p = 2;
        while (true)
        {
            if (p >= d.Length || d[p] != 0xFF) throw new NotSupportedException("JPEG の構造を読めませんでした");
            int q = p;
            while (q < d.Length && d[q] == 0xFF) q++; // fill bytes
            if (q + 2 >= d.Length) throw new NotSupportedException("JPEG の構造を読めませんでした");
            byte marker = d[q];
            bool header = marker is >= 0xE0 and <= 0xEF or 0xFE;
            if (!header) return (list, p);
            int length = d[q + 1] << 8 | d[q + 2];
            if (length < 2 || q + 1 + length > d.Length) throw new NotSupportedException("JPEG の構造を読めませんでした");
            list.Add(new Segment { Marker = marker, LengthPos = q + 1, Length = length });
            p = q + 1 + length;
        }
    }

    /// <summary>Width and height from the frame header (SOFn) of the image part.</summary>
    private static (int width, int height) FrameSize(byte[] d, int p)
    {
        while (p + 9 < d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            byte m = d[p + 1];
            if (m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (d[p + 7] << 8 | d[p + 8], d[p + 5] << 8 | d[p + 6]);
            if (m is 0xFF or 0x01 or >= 0xD0 and <= 0xD9) { p++; continue; }
            p += 2 + (d[p + 2] << 8 | d[p + 3]);
        }
        throw new NotSupportedException("JPEG の大きさを読めませんでした");
    }

    // ---------------------------------------------------------------- EXIF (TIFF structure)

    /// <summary>
    /// Patch the TIFF structure starting at <paramref name="tiff"/> in place. Throws if it cannot be
    /// read: keeping an old orientation value would make other apps rotate the image a second time.
    /// </summary>
    private static void PatchExif(byte[] s, int tiff, int width, int height)
    {
        try
        {
            bool le = s[tiff] == (byte)'I';
            int R16(int at) => le ? s[at] | s[at + 1] << 8 : s[at] << 8 | s[at + 1];
            int R32(int at) => le ? s[at] | s[at + 1] << 8 | s[at + 2] << 16 | s[at + 3] << 24 : s[at] << 24 | s[at + 1] << 16 | s[at + 2] << 8 | s[at + 3];
            void W16(int at, int v) { s[at] = (byte)(le ? v : v >> 8); s[at + 1] = (byte)(le ? v >> 8 : v); }
            void W32(int at, int v)
            {
                for (int i = 0; i < 4; i++) s[at + i] = (byte)(v >> (le ? 8 * i : 8 * (3 - i)));
            }
            void SetNumber(int entry, int value)
            {
                int type = R16(entry + 2);
                if (type == 3) W16(entry + 8, value);       // SHORT
                else if (type == 4) W32(entry + 8, value);  // LONG
            }

            int ifd0 = tiff + R32(tiff + 4);
            int n = R16(ifd0);
            int exifIfd = -1;
            for (int i = 0; i < n; i++)
            {
                int e = ifd0 + 2 + 12 * i;
                int tag = R16(e);
                if (tag == 0x0112) W16(e + 8, 1);                      // Orientation
                else if (tag == 0x8769) exifIfd = tiff + R32(e + 8);  // pointer to the Exif IFD
            }
            W32(ifd0 + 2 + 12 * n, 0); // no IFD1: the EXIF thumbnail is not rotated, so do not offer it

            if (exifIfd >= 0)
            {
                int m = R16(exifIfd);
                for (int i = 0; i < m; i++)
                {
                    int e = exifIfd + 2 + 12 * i;
                    int tag = R16(e);
                    if (tag == 0xA002) SetNumber(e, width);   // PixelXDimension
                    else if (tag == 0xA003) SetNumber(e, height); // PixelYDimension
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            throw new NotSupportedException("EXIF を読めませんでした");
        }
    }

    // ---------------------------------------------------------------- XMP

    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591); // 1 byte <-> 1 char

    /// <summary>Same-length text replacements in the XMP packet (the segment length stays valid).</summary>
    private static void PatchXmp(byte[] seg, int width, int height)
    {
        string text = Latin1.GetString(seg);
        string patched = text;
        patched = Replace(patched, "tiff:Orientation", "1");
        patched = Replace(patched, "exif:PixelXDimension", width.ToString());
        patched = Replace(patched, "exif:PixelYDimension", height.ToString());
        if (patched.Length == text.Length) Latin1.GetBytes(patched, 0, patched.Length, seg, 0);
    }

    /// <summary>Replace the value of name="v" or &lt;name&gt;v&lt;/name&gt; when the new value has the same length.</summary>
    private static string Replace(string xml, string name, string value)
    {
        string n = Regex.Escape(name);
        string Swap(Match m) => m.Groups[2].Value.Length == value.Length ? m.Groups[1].Value + value + m.Groups[3].Value : m.Value;
        xml = Regex.Replace(xml, "(" + n + "=\")([^\"]*)(\")", Swap);
        xml = Regex.Replace(xml, "(<" + n + ">)([^<]*)(</" + n + ">)", Swap);
        return xml;
    }

    private static bool StartsWith(byte[] d, int at, string ascii)
    {
        if (at + ascii.Length > d.Length) return false;
        for (int i = 0; i < ascii.Length; i++) if (d[at + i] != ascii[i]) return false;
        return true;
    }
}
