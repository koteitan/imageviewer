using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>Image decoding (Windows Imaging Component) and folder listing.</summary>
internal static class ImageLoader
{
    /// <summary>Extensions WIC can decode on Windows 11 (webp/heic/avif need the Store codecs, which Win11 ships).</summary>
    public static readonly string[] Extensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff",
        ".ico", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".hdp", ".dds",
    ];

    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) => ExtensionSet.Contains(Path.GetExtension(path));

    public static string DialogFilter =>
        "画像ファイル|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|すべてのファイル|*.*";

    /// <summary>Decode the first frame (largest one for .ico), applying the EXIF orientation.</summary>
    public static BitmapSource Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder is IconBitmapDecoder
            ? decoder.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).First()
            : decoder.Frames[0];
        BitmapSource result = ApplyOrientation(PixelsWithAlpha(decoder, frame, fs), ReadOrientation(frame));
        if (result.CanFreeze) result.Freeze();
        return result;
    }

    private static readonly Guid WebPContainer = new("e094b0e2-67f2-45b3-b0ea-115337ca7cf3");

    /// <summary>
    /// The pixels of <paramref name="frame"/>, keeping transparency for WebP. Decoded the usual way
    /// (BitmapCreateOptions.None), the Windows WebP decoder hands out Bgr32 and the alpha channel is lost;
    /// its own RGBA format (kept with PreservePixelFormat) is converted to Bgra32 here instead.
    /// </summary>
    public static BitmapSource PixelsWithAlpha(BitmapDecoder decoder, BitmapFrame frame, Stream stream)
    {
        if (decoder.CodecInfo?.ContainerFormat != WebPContainer || !stream.CanSeek) return frame;
        stream.Position = 0;
        var native = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(native, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    public static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation"))
                return Convert.ToInt32(md.GetQuery("System.Photo.Orientation"));
        }
        catch (Exception)
        {
            // formats without EXIF support throw NotSupportedException; treat as "normal".
        }
        return 1;
    }

    /// <summary>EXIF orientation 1..8 as "flip horizontally (if <c>flip</c>), then rotate clockwise by <c>degrees</c>".</summary>
    public static (int degrees, bool flip) OrientationParts(int orientation) => orientation switch
    {
        2 => (0, true),
        3 => (180, false),
        4 => (180, true),   // flip vertical = flip horizontal + 180
        5 => (270, true),   // transpose
        6 => (90, false),
        7 => (90, true),    // transverse
        8 => (270, false),
        _ => (0, false),
    };

    /// <summary>EXIF orientation 1..8 -> upright bitmap.</summary>
    public static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        return t is null ? src : new TransformedBitmap(src, t);
    }

    /// <summary>Images in the folder ordered by <paramref name="timeOf"/>, oldest first (ties broken by name).</summary>
    public static List<FileInfo> ListFolder(string dir, Func<FileInfo, DateTime> timeOf)
    {
        var files = new DirectoryInfo(dir).EnumerateFiles().Where(f => IsImage(f.Name)).ToList();
        files.Sort((a, b) => CompareKey(timeOf(a), a.Name, timeOf(b), b.Name));
        return files;
    }

    /// <summary>Last write time of every image in the folder now.</summary>
    public static Dictionary<string, DateTime> SnapshotTimes(string dir)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                if (IsImage(f.Name)) map[f.FullName] = f.LastWriteTimeUtc;
        }
        catch (Exception)
        {
        }
        return map;
    }

    public static int CompareKey(DateTime ta, string na, DateTime tb, string nb)
    {
        int c = ta.CompareTo(tb);
        return c != 0 ? c : string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}
