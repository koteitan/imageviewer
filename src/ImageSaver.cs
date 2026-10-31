using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>
/// Saves an image rotated by a multiple of 90 degrees. The format is taken from the file contents,
/// not from the extension (a ".webp" that really is a JPEG is handled as a JPEG, under its own name).
///
/// - JPEG: the compressed data is rotated without loss when width and height are multiples of 16;
///   otherwise the pixels are rotated and re-compressed at quality 95. Either way the original metadata
///   (EXIF, ICC profile, XMP) is kept with the orientation reset to 1 (see <see cref="JpegHeaders"/>).
/// - PNG / BMP / GIF / TIFF / JPEG XR: lossless formats, the pixels are rotated and re-encoded.
/// - HEIF (HEIC / AVIF): the 'irot' rotation property is changed (see <see cref="HeifRotator"/>).
/// - WebP: Windows cannot write WebP; it is saved as a new JPEG or PNG file instead.
/// </summary>
internal static class ImageSaver
{
    public enum Kind { Unknown, Jpeg, Png, Gif, Bmp, Tiff, JpegXr, Heif, WebP, Ico, Dds }

    /// <summary>Overwrite: no quality loss. OverwriteLossy: a JPEG that has to be re-compressed.</summary>
    public enum Mode { Overwrite, OverwriteLossy, ConvertWebP, Blocked }

    private const BitmapCreateOptions Exact = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;

    /// <summary>The real format, from the first bytes of the file.</summary>
    public static Kind Detect(string path)
    {
        var h = new byte[32];
        int n;
        using (var fs = OpenRead(path)) n = fs.Read(h, 0, h.Length);
        bool At(int offset, string ascii)
        {
            if (offset + ascii.Length > n) return false;
            for (int i = 0; i < ascii.Length; i++) if (h[offset + i] != ascii[i]) return false;
            return true;
        }
        if (n >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return Kind.Jpeg;
        if (n >= 8 && h[0] == 0x89 && At(1, "PNG")) return Kind.Png;
        if (At(0, "GIF8")) return Kind.Gif;
        if (At(0, "BM")) return Kind.Bmp;
        if (At(0, "II*\0") || At(0, "MM\0*")) return Kind.Tiff;
        if (n >= 3 && h[0] == 'I' && h[1] == 'I' && h[2] == 0xBC) return Kind.JpegXr;
        if (At(0, "RIFF") && At(8, "WEBP")) return Kind.WebP;
        if (At(4, "ftyp")) return Kind.Heif;
        if (n >= 4 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0) return Kind.Ico;
        if (At(0, "DDS ")) return Kind.Dds;
        return Kind.Unknown;
    }

    /// <summary>How a rotation of this file would be saved; <paramref name="reason"/> explains the modes other than Overwrite.</summary>
    public static Mode Plan(string path, out string reason)
    {
        reason = "";
        try
        {
            switch (Detect(path))
            {
                case Kind.WebP:
                    reason = "WebP は保存できないので、別の形式(JPG / PNG)で保存します";
                    return Mode.ConvertWebP;
                case Kind.Heif:
                    string? why = HeifRotator.CannotRotateReason(File.ReadAllBytes(path));
                    if (why is null) return Mode.Overwrite;
                    reason = why;
                    return Mode.Blocked;
                case Kind.Ico or Kind.Dds or Kind.Unknown:
                    reason = "この形式は保存できません";
                    return Mode.Blocked;
            }
            using var fs = OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, Exact, BitmapCacheOption.None);
            if (decoder.Frames.Count != 1)
            {
                reason = "アニメーションや複数ページの画像は保存できません";
                return Mode.Blocked;
            }
            var f = decoder.Frames[0];
            if (decoder is JpegBitmapDecoder && !IsLosslessJpegSize(f))
            {
                reason = "画質が少し落ちます";
                return Mode.OverwriteLossy;
            }
            return Mode.Overwrite;
        }
        catch (Exception ex)
        {
            reason = "読み込めません: " + ex.Message;
            return Mode.Blocked;
        }
    }

    /// <summary>Overwrite <paramref name="path"/> rotated clockwise by <paramref name="degrees"/> (90, 180 or 270).</summary>
    public static void SaveRotated(string path, int degrees)
    {
        var mode = Plan(path, out string reason);
        if (mode is not (Mode.Overwrite or Mode.OverwriteLossy)) throw new NotSupportedException(reason);
        var kind = Detect(path);
        if (kind == Kind.Heif)
        {
            byte[] rotated = HeifRotator.Rotate(File.ReadAllBytes(path), degrees);
            ReplaceWith(path, output => output.Write(rotated, 0, rotated.Length));
            return;
        }
        if (kind == Kind.Jpeg)
        {
            byte[] original = File.ReadAllBytes(path);
            byte[] rotated = RotateJpeg(original, degrees);
            ReplaceWith(path, output => output.Write(rotated, 0, rotated.Length));
            return;
        }
        ReplaceWith(path, output =>
        {
            using var fs = OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, Exact, BitmapCacheOption.None);
            EncodeLossless(decoder.Frames[0], degrees, kind, output);
        });
    }

    /// <summary>
    /// Save an image (a WebP) rotated as the file <paramref name="target"/> in the format
    /// <paramref name="kind"/> (Jpeg or Png). An existing target is replaced.
    /// </summary>
    public static void SaveConverted(string path, int degrees, Kind kind, string target)
    {
        BitmapSource pixels;
        using (var fs = OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            pixels = ImageLoader.ApplyOrientation(ImageLoader.PixelsWithAlpha(decoder, frame, fs), ImageLoader.ReadOrientation(frame));
        }
        if (degrees % 360 != 0) pixels = new TransformedBitmap(pixels, new RotateTransform(degrees));

        BitmapEncoder encoder;
        if (kind == Kind.Jpeg)
        {
            pixels = OnWhite(pixels); // JPEG has no transparency
            encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        }
        else
        {
            encoder = new PngBitmapEncoder();
        }
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        if (File.Exists(target))
        {
            ReplaceWith(target, encoder.Save);
        }
        else
        {
            using var output = File.Create(target);
            encoder.Save(output);
        }
    }

    /// <summary>Write a temporary file, then swap it in; the original stays untouched if anything fails.</summary>
    private static void ReplaceWith(string path, Action<Stream> write)
    {
        string tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".imageviewer.tmp");
        try
        {
            using (var output = File.Create(tmp)) write(output);
            File.Replace(tmp, path, null, true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ---------------------------------------------------------------- JPEG

    /// <summary>
    /// Lossless rotation of a whole JPEG file; the metadata is the original's (see <see cref="JpegHeaders"/>).
    /// The JPEG encoder transforms the compressed data without loss only for ONE operation (a rotation,
    /// or a horizontal flip, or a vertical flip); asked for a flip and a rotation together it decodes and
    /// re-compresses instead. So a flip + 90/270 rotation is done in two passes.
    /// </summary>
    private static byte[] RotateJpeg(byte[] original, int degrees)
    {
        using var input = new MemoryStream(original, writable: false);
        var frame = BitmapDecoder.Create(input, Exact, BitmapCacheOption.None).Frames[0];
        if (!IsLosslessJpegSize(frame)) return JpegHeaders.Rebuild(original, RecompressJpeg(frame, degrees), reencoded: true);

        // displayed = rotate(degrees) . orientation = rotate(degrees + o.degrees) . flipH(o.flip)
        var (oDegrees, flip) = ImageLoader.OrientationParts(ImageLoader.ReadOrientation(frame));
        int total = (degrees + oDegrees) % 360;

        byte[] image;
        if (flip && total is 90 or 270)
        {
            // pass 1: flip only; pass 2: rotate the flipped JPEG
            using var flipped = new MemoryStream(JpegTransform(frame, flipH: true, flipV: false, Rotation.Rotate0), writable: false);
            var second = BitmapDecoder.Create(flipped, Exact, BitmapCacheOption.None).Frames[0]; // keep it a JPEG frame
            image = JpegTransform(second, false, false, ToRotation(total));
        }
        else if (flip)
        {
            // flipH alone, or flipH + 180 = flipV
            image = JpegTransform(frame, flipH: total == 0, flipV: total == 180, Rotation.Rotate0);
        }
        else
        {
            image = JpegTransform(frame, false, false, ToRotation(total));
        }
        return JpegHeaders.Rebuild(original, image, reencoded: false);
    }

    /// <summary>
    /// Lossless rotation moves the compressed 16x16 units (8x8 when the color is not subsampled; WPF does
    /// not tell which, so 16 is required). A partial unit can only be at the right or bottom edge, and
    /// rotating would move it to the left or top edge.
    /// </summary>
    private static bool IsLosslessJpegSize(BitmapSource frame) => frame.PixelWidth % 16 == 0 && frame.PixelHeight % 16 == 0;

    /// <summary>Rotate the decoded pixels (as displayed) and compress them again at quality 95.</summary>
    private static byte[] RecompressJpeg(BitmapFrame frame, int degrees)
    {
        BitmapSource pixels = ImageLoader.ApplyOrientation(frame, ImageLoader.ReadOrientation(frame));
        if (degrees % 360 != 0) pixels = new TransformedBitmap(pixels, new RotateTransform(degrees));
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>The transformed JPEG. Its own metadata is thrown away later, so none is given here.</summary>
    private static byte[] JpegTransform(BitmapFrame frame, bool flipH, bool flipV, Rotation rotation)
    {
        var encoder = new JpegBitmapEncoder { FlipHorizontal = flipH, FlipVertical = flipV, Rotation = rotation };
        encoder.Frames.Add(BitmapFrame.Create(frame, null, null, null));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static Rotation ToRotation(int degrees) => degrees switch
    {
        90 => Rotation.Rotate90,
        180 => Rotation.Rotate180,
        270 => Rotation.Rotate270,
        _ => Rotation.Rotate0,
    };

    // ---------------------------------------------------------------- lossless formats

    private static void EncodeLossless(BitmapFrame frame, int degrees, Kind kind, Stream output)
    {
        BitmapSource pixels = ImageLoader.ApplyOrientation(frame, ImageLoader.ReadOrientation(frame));
        if (degrees % 360 != 0) pixels = new TransformedBitmap(pixels, new RotateTransform(degrees));

        // Keep the metadata when the encoder accepts it; some do not, then save without it.
        try
        {
            Encode(BitmapFrame.Create(pixels, null, MetadataWithNormalOrientation(frame), frame.ColorContexts), kind, output);
        }
        catch (Exception) when (output.CanSeek)
        {
            output.SetLength(0);
            output.Position = 0;
            Encode(BitmapFrame.Create(pixels), kind, output);
        }
    }

    private static void Encode(BitmapFrame frame, Kind kind, Stream output)
    {
        BitmapEncoder encoder = kind switch
        {
            Kind.Png => new PngBitmapEncoder(),
            Kind.Bmp => new BmpBitmapEncoder(),
            Kind.Gif => new GifBitmapEncoder(),
            Kind.Tiff => new TiffBitmapEncoder(),
            Kind.JpegXr => new WmpBitmapEncoder { Lossless = true },
            _ => throw new NotSupportedException("この形式は保存できません"),
        };
        encoder.Frames.Add(frame);
        encoder.Save(output);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A writable copy of the metadata with the orientation set to 1 (if it had one).</summary>
    private static BitmapMetadata? MetadataWithNormalOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata source) return null;
            var md = source.Clone();
            if (md.ContainsQuery("System.Photo.Orientation")) md.SetQuery("System.Photo.Orientation", (ushort)1);
            return md;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapSource OnWhite(BitmapSource src)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, src.PixelWidth, src.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(src, rect);
        }
        var target = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
