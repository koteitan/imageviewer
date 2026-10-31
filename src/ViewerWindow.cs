using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>
/// Borderless window whose client area is exactly the displayed image.
///
/// It is a plain Win32 layered window, not a WPF Window: every change (zoom, image switch)
/// renders the image with WPF into an offscreen bitmap and hands position, size and pixels to
/// UpdateLayeredWindow in one call. A WPF Window changes the window geometry and the pixels in
/// two steps, which shows one wrong frame (empty, or at the wrong position) on every wheel step.
///
/// All geometry is in physical pixels, so 1 image pixel = 1 screen pixel at 100% on any DPI.
/// </summary>
internal sealed class ViewerWindow
{
    private const double ZoomStep = 1.1;          // per wheel notch
    private const int MaxSidePx = 16384;          // largest window side
    private const double MaxAreaPx = 40_000_000;  // largest window area (bitmap memory is 4 byte/px)
    private const int MinLongSidePx = 16;         // smallest window long side
    private const string ClassName = "imageviewer.main";

    private static readonly Dictionary<IntPtr, ViewerWindow> Windows = new();
    private static readonly Native.WndProcDelegate WndProcRef = StaticWndProc; // kept alive for native code
    private static bool classRegistered;
    private static readonly Brush Checker = CreateCheckerBrush();

    private readonly IntPtr hwnd;
    private readonly InfoPopup popup;

    private BitmapSource bitmap = null!;
    private readonly List<BitmapSource> levels = new(); // levels[k] = bitmap shrunk by 2^k (built on demand)
    private string path;
    private double scale = 1.0;
    private int x, y, width, height;              // window rectangle in physical px
    private DateTime lastKnownWriteTime;

    public ViewerWindow(string path, BitmapSource bitmap)
    {
        this.path = path;
        SetBitmap(bitmap);

        RegisterClass();
        hwnd = Native.CreateWindowEx(
            Native.WS_EX_LAYERED | Native.WS_EX_APPWINDOW | Native.WS_EX_ACCEPTFILES,
            ClassName, "", Native.WS_POPUP | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed");
        Windows[hwnd] = this;

        int pref = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        popup = new InfoPopup(this);

        UpdateTitle();
        scale = InitialScale(bitmap.PixelWidth, bitmap.PixelHeight);
        Native.GetCursorPos(out var cursor);
        PlaceCentered(cursor.X, cursor.Y);
    }

    public IntPtr Handle => hwnd;
    public string FilePath => path;
    public double Scale => scale;

    public void Show()
    {
        Native.ShowWindow(hwnd, Native.SW_SHOW);
        Native.SetForegroundWindow(hwnd);
    }

    public void Activate() => Native.SetForegroundWindow(hwnd);

    // ---------------------------------------------------------------- Win32 window

    private static void RegisterClass()
    {
        if (classRegistered) return;
        string exe = typeof(ViewerWindow).Assembly.Location;
        Native.ExtractIconEx(exe, 0, out var bigIcon, out var smallIcon, 1);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = WndProcRef,
            hInstance = Native.GetModuleHandle(null),
            hCursor = Native.LoadCursor(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            hIcon = bigIcon,
            hIconSm = smallIcon,
            lpszClassName = ClassName,
        };
        if (Native.RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed");
        classRegistered = true;
    }

    private static IntPtr StaticWndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(h, out var window))
        {
            try
            {
                if (window.HandleMessage(msg, wParam, lParam)) return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                // Exceptions must not cross the native boundary.
                MessageBox.Show(ex.ToString(), "imageviewer", MessageBoxButton.OK, MessageBoxImage.Error);
                return IntPtr.Zero;
            }
        }
        return Native.DefWindowProc(h, msg, wParam, lParam);
    }

    /// <returns>true if the message was handled (return 0), false for DefWindowProc.</returns>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_LBUTTONDOWN:
                // Drag anywhere to move: let Windows treat the click as a click on a title bar.
                Native.ReleaseCapture();
                Native.SendMessage(hwnd, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
                return true;

            case Native.WM_RBUTTONUP:
                popup.Open();
                return true;

            case Native.WM_MOUSEWHEEL:
                OnMouseWheel(unchecked((short)((long)wParam >> 16)));
                return true;

            case Native.WM_KEYDOWN:
                switch ((int)wParam)
                {
                    case Native.VK_RIGHT: Navigate(+1); return true;
                    case Native.VK_LEFT: Navigate(-1); return true;
                    case Native.VK_ESCAPE: Native.DestroyWindow(hwnd); return true;
                }
                return false;

            case Native.WM_DROPFILES:
                OnDropFiles(wParam);
                return true;

            case Native.WM_DPICHANGED:
                // Keep the physical size: 1 image px = 1 screen px on every monitor.
                return true;

            case Native.WM_GETMINMAXINFO:
                var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
                mmi.ptMinTrackSize = new Native.POINT { X = 1, Y = 1 };
                mmi.ptMaxTrackSize = new Native.POINT { X = MaxSidePx, Y = MaxSidePx };
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
                return true;

            case Native.WM_DESTROY:
                Windows.Remove(hwnd);
                popup.Close();
                if (Windows.Count == 0) Application.Current?.Shutdown();
                return true;
        }
        return false;
    }

    private void OnDropFiles(IntPtr hDrop)
    {
        try
        {
            uint len = Native.DragQueryFile(hDrop, 0, null, 0);
            if (len == 0) return;
            var sb = new StringBuilder((int)len + 1);
            Native.DragQueryFile(hDrop, 0, sb, (uint)sb.Capacity);
            TryOpen(sb.ToString(), showError: true);
        }
        finally
        {
            Native.DragFinish(hDrop);
        }
    }

    private void UpdateTitle() => Native.SetWindowText(hwnd, $"{Path.GetFileName(path)} - imageviewer");

    // ---------------------------------------------------------------- sizing

    /// <summary>Initial zoom of a w x h image (requirements: "ウィンドウサイズの例外").</summary>
    public static double InitialScale(int w, int h)
    {
        if (w < 32 && h < 32) return 512.0 / Math.Max(w, h);
        if (w >= 1920 || h >= 1080) return Math.Min(1.0, Math.Min(1920.0 / w, 1080.0 / h));
        return 1.0;
    }

    private static int ScaledSide(int px, double s) => Math.Max(1, (int)Math.Round(px * s));

    private double ClampScale(double s)
    {
        int longSide = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        double max = Math.Min((double)MaxSidePx / longSide,
                              Math.Sqrt(MaxAreaPx / ((double)bitmap.PixelWidth * bitmap.PixelHeight)));
        double min = Math.Min(1.0, (double)MinLongSidePx / longSide);
        return Math.Max(min, Math.Min(max, s));
    }

    /// <summary>Show the image at the current scale in the rectangle (nx, ny, nw, nh), all in one screen update.</summary>
    private void SetBounds(int nx, int ny, int nw, int nh)
    {
        x = nx;
        y = ny;
        width = nw;
        height = nh;
        Present();
    }

    /// <summary>
    /// Size the window for the current scale, centered at (cx, cy), then keep it
    /// inside the work area of that monitor where it fits.
    /// </summary>
    private void PlaceCentered(int cx, int cy)
    {
        int w = ScaledSide(bitmap.PixelWidth, scale);
        int h = ScaledSide(bitmap.PixelHeight, scale);
        var work = Native.WorkAreaAt(cx, cy);
        SetBounds(FitAxis(cx - w / 2, w, work.Left, work.Right), FitAxis(cy - h / 2, h, work.Top, work.Bottom), w, h);
    }

    private static int FitAxis(int pos, int size, int lo, int hi)
    {
        if (size >= hi - lo) return lo;
        return Math.Max(lo, Math.Min(hi - size, pos));
    }

    private Native.RECT CurrentRect()
    {
        // The window can be moved by the system (drag), so ask Windows instead of using x/y.
        Native.GetWindowRect(hwnd, out var r);
        return r;
    }

    /// <summary>Zoom keeping the screen point (ax, ay) over the same image point.</summary>
    public void ZoomTo(double newScale, int ax, int ay)
    {
        newScale = ClampScale(newScale);
        var r = CurrentRect();
        double fx = (ax - r.Left) / (double)Math.Max(1, r.Width);
        double fy = (ay - r.Top) / (double)Math.Max(1, r.Height);
        int w = ScaledSide(bitmap.PixelWidth, newScale);
        int h = ScaledSide(bitmap.PixelHeight, newScale);
        scale = newScale;
        SetBounds((int)Math.Round(ax - fx * w), (int)Math.Round(ay - fy * h), w, h);
    }

    /// <summary>Zoom around the window center (used by the 拡大率 box).</summary>
    public void ZoomToCenter(double newScale)
    {
        var r = CurrentRect();
        ZoomTo(newScale, r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private void OnMouseWheel(int delta)
    {
        double next = scale * Math.Pow(ZoomStep, delta / 120.0);
        // Stop at 100% when passing through it.
        if ((scale < 1.0 && next > 1.0) || (scale > 1.0 && next < 1.0)) next = 1.0;
        Native.GetCursorPos(out var p);
        ZoomTo(next, p.X, p.Y);
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Render checkerboard + image at (width x height) and put it on the screen at (x, y).</summary>
    private void Present()
    {
        var visual = new DrawingVisual();
        // Pixel-exact at 100% and when enlarging; smooth when shrinking.
        RenderOptions.SetBitmapScalingMode(visual, scale < 1.0 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(Checker, null, rect);
            dc.DrawImage(SourceFor(scale), rect);
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        Native.UpdateLayered(hwnd, x, y, width, height,
            (bits, stride, size) => target.CopyPixels(Int32Rect.Empty, bits, size, stride));
    }

    /// <summary>
    /// The smallest pre-shrunk copy that is still at least as large as the output.
    /// Shrinking a huge photo from a copy at most 2x larger keeps every wheel step fast.
    /// </summary>
    private BitmapSource SourceFor(double s)
    {
        while (Math.Pow(0.5, levels.Count) >= s)
        {
            var last = levels[levels.Count - 1];
            if (last.PixelWidth < 2 || last.PixelHeight < 2) break;
            levels.Add(Half(last));
        }
        int k = 0;
        while (k + 1 < levels.Count && Math.Pow(0.5, k + 1) >= s) k++;
        return levels[k];
    }

    private static BitmapSource Half(BitmapSource src)
    {
        int w = Math.Max(1, (src.PixelWidth + 1) / 2);
        int h = Math.Max(1, (src.PixelHeight + 1) / 2);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    // ---------------------------------------------------------------- images

    /// <summary>Right = newer, Left = older (by last write time) in the same folder.</summary>
    private void Navigate(int direction)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return;

        var files = ImageLoader.ListFolder(dir);
        var cur = new FileInfo(path);
        DateTime curTime = cur.Exists ? cur.LastWriteTimeUtc : lastKnownWriteTime;
        string curName = cur.Name;

        // Start at the neighbor of the current position (works even if the current file was deleted).
        // Past the newest/oldest image, wrap around to the other end.
        int i = direction > 0
            ? files.FindIndex(f => ImageLoader.CompareKey(f.LastWriteTimeUtc, f.Name, curTime, curName) > 0)
            : files.FindLastIndex(f => ImageLoader.CompareKey(f.LastWriteTimeUtc, f.Name, curTime, curName) < 0);
        if (i < 0) i = direction > 0 ? 0 : files.Count - 1;

        // Visit every file at most once; skip the current one and files that fail to load.
        for (int n = 0; n < files.Count; n++, i = (i + direction + files.Count) % files.Count)
        {
            if (string.Equals(files[i].FullName, cur.FullName, StringComparison.OrdinalIgnoreCase)) return;
            if (TryOpen(files[i].FullName, showError: false)) return;
        }
    }

    private bool TryOpen(string file, bool showError)
    {
        BitmapSource bmp;
        try
        {
            bmp = ImageLoader.Load(file);
        }
        catch (Exception ex)
        {
            if (showError) Program.ShowLoadError(file, ex);
            return false;
        }

        var r = CurrentRect();
        path = Path.GetFullPath(file);
        SetBitmap(bmp);
        UpdateTitle();
        scale = InitialScale(bitmap.PixelWidth, bitmap.PixelHeight);
        PlaceCentered(r.Left + r.Width / 2, r.Top + r.Height / 2);
        return true;
    }

    private void SetBitmap(BitmapSource bmp)
    {
        bitmap = bmp;
        levels.Clear();
        levels.Add(bmp);
        try { lastKnownWriteTime = File.GetLastWriteTimeUtc(path); } catch (Exception) { }
    }

    /// <summary>Gray/white checkerboard behind transparent pixels.</summary>
    private static Brush CreateCheckerBrush()
    {
        var dark = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(dark, null, new GeometryGroup
        {
            Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) },
        }));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
