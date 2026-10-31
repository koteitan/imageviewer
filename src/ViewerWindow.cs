using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>
/// Borderless window that shows one image; the window is exactly the displayed image.
///
/// It is a plain Win32 layered window, not a WPF Window: every change renders the image with WPF
/// into an offscreen bitmap and hands position, size and pixels to UpdateLayeredWindow in one call,
/// so zooming and resizing never show a wrong frame.
///
/// The "image rectangle" (imgX, imgY, imgW, imgH) is where the whole image is on the screen. It may
/// extend past the monitors. The real Win32 window is only the part of it inside the virtual screen,
/// so a zoomed-in image costs no more than one screen of pixels to render.
///
/// All geometry is in physical pixels, so 1 image pixel = 1 screen pixel at 100% on any DPI.
/// </summary>
internal sealed class ViewerWindow
{
    private const double ZoomStep = 1.1;          // per wheel notch
    private const int MaxSidePx = 16384;          // largest image rectangle side
    private const double MaxAreaPx = 40_000_000;  // largest image rectangle area
    private const int MinLongSidePx = 16;         // smallest image rectangle long side
    private const int ResizeBorder = 16;          // width of the resize edges at 96 dpi
    private const double MinVisibleToRestore = 0.25; // a remembered place is used if at least this much is on a monitor
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
    private int imgX, imgY, imgW, imgH;           // image rectangle (see class comment)

    // what is on the screen now: window rectangle and its offset inside the image rectangle
    private bool shown;
    private int shownW, shownH, shownOffX, shownOffY;

    // mouse drag: move, or resize from an edge (hit test code)
    private int dragHit;                           // 0 = no drag
    private Native.POINT dragStart;
    private int startX, startY, startW, startH;

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
        PlaceForCurrentImage();
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
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
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
                if (window.HandleMessage(msg, wParam, lParam, out IntPtr result)) return result;
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

    /// <returns>true if handled (then <paramref name="result"/> is returned), false for DefWindowProc.</returns>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case Native.WM_LBUTTONDOWN:
                BeginDrag();
                return true;

            case Native.WM_MOUSEMOVE:
                if (dragHit != 0) ContinueDrag();
                return true;

            case Native.WM_LBUTTONUP:
                if (dragHit != 0) Native.ReleaseCapture(); // -> WM_CAPTURECHANGED
                return true;

            case Native.WM_CAPTURECHANGED:
                dragHit = 0;
                return true;

            case Native.WM_SETCURSOR:
                if ((int)((long)lParam & 0xFFFF) != Native.HTCLIENT) return false;
                Native.GetCursorPos(out var c);
                Native.SetCursor(CursorFor(dragHit != 0 ? dragHit : HitTest(c.X, c.Y)));
                result = (IntPtr)1;
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

            case Native.WM_DISPLAYCHANGE:
                Present(contentChanged: true); // the virtual screen changed: visible part changed
                return false;

            case Native.WM_GETMINMAXINFO:
                var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
                mmi.ptMinTrackSize = new Native.POINT { X = 1, Y = 1 };
                mmi.ptMaxTrackSize = new Native.POINT { X = MaxSidePx, Y = MaxSidePx };
                Marshal.StructureToPtr(mmi, lParam, false);
                return true;

            case Native.WM_ENDSESSION:
                if (wParam != IntPtr.Zero) RememberPlace();
                return false;

            case Native.WM_DESTROY:
                RememberPlace();
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

    // ---------------------------------------------------------------- mouse drag: move / resize

    /// <summary>Which edge of the image rectangle is under the screen point (HTCLIENT inside).</summary>
    private int HitTest(int px, int py)
    {
        double dpi = Native.GetDpiForWindow(hwnd);
        int band = (int)Math.Round(ResizeBorder * (dpi > 0 ? dpi : 96) / 96.0);
        band = Math.Max(2, Math.Min(band, Math.Min(imgW, imgH) / 4));
        bool left = px < imgX + band, right = px >= imgX + imgW - band;
        bool top = py < imgY + band, bottom = py >= imgY + imgH - band;
        if (top && left) return Native.HTTOPLEFT;
        if (top && right) return Native.HTTOPRIGHT;
        if (bottom && left) return Native.HTBOTTOMLEFT;
        if (bottom && right) return Native.HTBOTTOMRIGHT;
        if (left) return Native.HTLEFT;
        if (right) return Native.HTRIGHT;
        if (top) return Native.HTTOP;
        if (bottom) return Native.HTBOTTOM;
        return Native.HTCLIENT;
    }

    private static IntPtr CursorFor(int hit)
    {
        int id = hit switch
        {
            Native.HTLEFT or Native.HTRIGHT => Native.IDC_SIZEWE,
            Native.HTTOP or Native.HTBOTTOM => Native.IDC_SIZENS,
            Native.HTTOPLEFT or Native.HTBOTTOMRIGHT => Native.IDC_SIZENWSE,
            Native.HTTOPRIGHT or Native.HTBOTTOMLEFT => Native.IDC_SIZENESW,
            _ => Native.IDC_ARROW,
        };
        return Native.LoadCursor(IntPtr.Zero, (IntPtr)id);
    }

    private void BeginDrag()
    {
        Native.GetCursorPos(out dragStart);
        dragHit = HitTest(dragStart.X, dragStart.Y);
        (startX, startY, startW, startH) = (imgX, imgY, imgW, imgH);
        Native.SetCapture(hwnd);
    }

    private void ContinueDrag()
    {
        Native.GetCursorPos(out var c);
        int dx = c.X - dragStart.X, dy = c.Y - dragStart.Y;
        if (dragHit == Native.HTCLIENT)
        {
            // Move. Nothing pulls the window back onto the screen.
            imgX = startX + dx;
            imgY = startY + dy;
            Present(contentChanged: false);
            return;
        }

        // Resize keeping the aspect ratio; the opposite edge (or corner) stays where it is.
        bool l = dragHit is Native.HTLEFT or Native.HTTOPLEFT or Native.HTBOTTOMLEFT;
        bool r = dragHit is Native.HTRIGHT or Native.HTTOPRIGHT or Native.HTBOTTOMRIGHT;
        bool t = dragHit is Native.HTTOP or Native.HTTOPLEFT or Native.HTTOPRIGHT;
        bool b = dragHit is Native.HTBOTTOM or Native.HTBOTTOMLEFT or Native.HTBOTTOMRIGHT;
        double sw = (startW + (r ? dx : l ? -dx : 0)) / (double)bitmap.PixelWidth;
        double sh = (startH + (b ? dy : t ? -dy : 0)) / (double)bitmap.PixelHeight;
        double s = (l || r) && (t || b) ? Math.Max(sw, sh) : (l || r) ? sw : sh;
        scale = ClampScale(s);
        int w = ScaledSide(bitmap.PixelWidth, scale), h = ScaledSide(bitmap.PixelHeight, scale);
        int x = l ? startX + startW - w : r ? startX : startX + (startW - w) / 2;
        int y = t ? startY + startH - h : b ? startY : startY + (startH - h) / 2;
        SetImageRect(x, y, w, h);
    }

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

    private void SetImageRect(int x, int y, int w, int h)
    {
        (imgX, imgY, imgW, imgH) = (x, y, w, h);
        Present(contentChanged: true);
    }

    /// <summary>
    /// Remembered place of this image if enough of it is on a monitor;
    /// otherwise the initial zoom, centered at the mouse cursor and kept inside the work area.
    /// </summary>
    private void PlaceForCurrentImage()
    {
        if (PositionStore.TryGet(path, out int px, out int py, out double ps))
        {
            double s = ClampScale(ps);
            int w = ScaledSide(bitmap.PixelWidth, s), h = ScaledSide(bitmap.PixelHeight, s);
            if (VisibleFraction(px, py, w, h) >= MinVisibleToRestore)
            {
                scale = s;
                SetImageRect(px, py, w, h);
                return;
            }
        }

        scale = InitialScale(bitmap.PixelWidth, bitmap.PixelHeight);
        Native.GetCursorPos(out var c);
        int iw = ScaledSide(bitmap.PixelWidth, scale), ih = ScaledSide(bitmap.PixelHeight, scale);
        var work = Native.WorkAreaAt(c.X, c.Y);
        SetImageRect(FitAxis(c.X - iw / 2, iw, work.Left, work.Right), FitAxis(c.Y - ih / 2, ih, work.Top, work.Bottom), iw, ih);
    }

    private static double VisibleFraction(int x, int y, int w, int h)
    {
        var rect = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        long visible = 0;
        foreach (var m in Native.Monitors())
        {
            var i = Native.Intersect(rect, m);
            if (i.Width > 0 && i.Height > 0) visible += (long)i.Width * i.Height;
        }
        return visible / ((double)w * h);
    }

    private void RememberPlace()
    {
        PositionStore.Put(path, imgX, imgY, scale);
        PositionStore.Save();
    }

    private static int FitAxis(int pos, int size, int lo, int hi)
    {
        if (size >= hi - lo) return lo;
        return Math.Max(lo, Math.Min(hi - size, pos));
    }

    /// <summary>Zoom keeping the screen point (ax, ay) over the same image point.</summary>
    public void ZoomTo(double newScale, int ax, int ay)
    {
        newScale = ClampScale(newScale);
        double fx = (ax - imgX) / (double)Math.Max(1, imgW);
        double fy = (ay - imgY) / (double)Math.Max(1, imgH);
        int w = ScaledSide(bitmap.PixelWidth, newScale);
        int h = ScaledSide(bitmap.PixelHeight, newScale);
        scale = newScale;
        SetImageRect((int)Math.Round(ax - fx * w), (int)Math.Round(ay - fy * h), w, h);
    }

    /// <summary>Zoom around the center of the image (used by the 拡大率 box).</summary>
    public void ZoomToCenter(double newScale) => ZoomTo(newScale, imgX + imgW / 2, imgY + imgH / 2);

    private void OnMouseWheel(int delta)
    {
        double next = scale * Math.Pow(ZoomStep, delta / 120.0);
        // Stop at 100% when passing through it.
        if ((scale < 1.0 && next > 1.0) || (scale > 1.0 && next < 1.0)) next = 1.0;
        Native.GetCursorPos(out var p);
        ZoomTo(next, p.X, p.Y);
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// Put the visible part of the image rectangle on the screen.
    /// If only the position changed and the same pixels are still visible, the window is just moved.
    /// </summary>
    private void Present(bool contentChanged)
    {
        var image = new Native.RECT { Left = imgX, Top = imgY, Right = imgX + imgW, Bottom = imgY + imgH };
        var vis = Native.Intersect(image, Native.VirtualScreen());
        if (vis.Width <= 0 || vis.Height <= 0) vis = new Native.RECT { Left = imgX, Top = imgY, Right = imgX + 1, Bottom = imgY + 1 };
        int offX = vis.Left - imgX, offY = vis.Top - imgY;

        if (!contentChanged && shown && vis.Width == shownW && vis.Height == shownH && offX == shownOffX && offY == shownOffY)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, vis.Left, vis.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }

        var target = Render(offX, offY, vis.Width, vis.Height);
        Native.UpdateLayered(hwnd, vis.Left, vis.Top, vis.Width, vis.Height,
            (bits, stride, size) => target.CopyPixels(Int32Rect.Empty, bits, size, stride));
        (shown, shownW, shownH, shownOffX, shownOffY) = (true, vis.Width, vis.Height, offX, offY);
    }

    /// <summary>Render the part (offX, offY, w, h) of the image rectangle: checkerboard + image.</summary>
    private RenderTargetBitmap Render(int offX, int offY, int w, int h)
    {
        var src = SourceFor(scale);
        // Only the source pixels that land in the visible part are scaled (plus a small margin for filtering).
        double kx = (double)src.PixelWidth / imgW, ky = (double)src.PixelHeight / imgH; // source px per screen px
        int sx0 = Math.Max(0, (int)Math.Floor(offX * kx) - 2);
        int sy0 = Math.Max(0, (int)Math.Floor(offY * ky) - 2);
        int sx1 = Math.Min(src.PixelWidth, (int)Math.Ceiling((offX + w) * kx) + 2);
        int sy1 = Math.Min(src.PixelHeight, (int)Math.Ceiling((offY + h) * ky) + 2);

        var visual = new DrawingVisual();
        // Pixel-exact at 100% and when enlarging; smooth when shrinking.
        RenderOptions.SetBitmapScalingMode(visual, scale < 1.0 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-offX, -offY)); // image rectangle coordinates from here
            dc.DrawRectangle(Checker, null, new Rect(offX, offY, w, h));
            if (sx1 > sx0 && sy1 > sy0)
            {
                var part = new CroppedBitmap(src, new Int32Rect(sx0, sy0, sx1 - sx0, sy1 - sy0));
                dc.DrawImage(part, new Rect(sx0 / kx, sy0 / ky, (sx1 - sx0) / kx, (sy1 - sy0) / ky));
            }
            dc.Pop();
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    /// <summary>
    /// The smallest pre-shrunk copy that is still at least as large as the output.
    /// Shrinking a huge photo from a copy at most 2x larger keeps every step fast.
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
            if (TryOpen(files[i].FullName, showError: false, keepPlace: true)) return;
        }
    }

    /// <param name="keepPlace">
    /// true (left/right keys): the new image takes the place of the current one (same center, initial zoom).
    /// false (drag and drop): the remembered place of the new image, or the mouse cursor.
    /// </param>
    private bool TryOpen(string file, bool showError, bool keepPlace = false)
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

        RememberPlace();
        int cx = imgX + imgW / 2, cy = imgY + imgH / 2;
        path = Path.GetFullPath(file);
        SetBitmap(bmp);
        UpdateTitle();
        if (keepPlace)
        {
            scale = InitialScale(bitmap.PixelWidth, bitmap.PixelHeight);
            int w = ScaledSide(bitmap.PixelWidth, scale), h = ScaledSide(bitmap.PixelHeight, scale);
            SetImageRect(cx - w / 2, cy - h / 2, w, h);
        }
        else
        {
            PlaceForCurrentImage();
        }
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
