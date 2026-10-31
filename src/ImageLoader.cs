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
        BitmapSource result = ApplyOrientation(frame, ReadOrientation(frame));
        if (result.CanFreeze) result.Freeze();
        return result;
    }

    private static int ReadOrientation(BitmapFrame frame)
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

    /// <summary>EXIF orientation 1..8 -> upright bitmap.</summary>
    private static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
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

    /// <summary>Images in the folder, oldest first (ties broken by name).</summary>
    public static List<FileInfo> ListFolder(string dir)
    {
        var files = new DirectoryInfo(dir).EnumerateFiles().Where(f => IsImage(f.Name)).ToList();
        files.Sort(CompareByTime);
        return files;
    }

    public static int CompareByTime(FileInfo a, FileInfo b) => CompareKey(a.LastWriteTimeUtc, a.Name, b.LastWriteTimeUtc, b.Name);

    public static int CompareKey(DateTime ta, string na, DateTime tb, string nb)
    {
        int c = ta.CompareTo(tb);
        return c != 0 ? c : string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}
