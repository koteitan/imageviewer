using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewer;

/// <summary>
/// Borderless window whose client area is exactly the displayed image.
/// Window geometry is handled in physical pixels through Win32 so that
/// 1 image pixel = 1 screen pixel at 100% zoom on any monitor DPI.
/// </summary>
internal sealed class ViewerWindow : Window
{
    private const double ZoomStep = 1.1;          // per wheel notch
    private const int MaxSidePx = 16384;          // largest window side
    private const int MinLongSidePx = 16;         // smallest window long side

    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly InfoPopup popup;
    private IntPtr hwnd;

    private BitmapSource bitmap;
    private string path;
    private double scale = 1.0;
    private int width, height;                    // current window size in physical px

    public ViewerWindow(string path, BitmapSource bitmap)
    {
        this.path = path;
        this.bitmap = bitmap;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        AllowDrop = true;
        Background = CreateCheckerBrush();
        Content = image;

        popup = new InfoPopup(this);

        SourceInitialized += OnSourceInitialized;
        DpiChanged += OnDpiChanged;
        MouseLeftButtonDown += OnLeftButtonDown;
        MouseRightButtonUp += (_, e) => { popup.Open(); e.Handled = true; };
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;
        Drop += OnDrop;

        ShowBitmap();
    }

    public string FilePath => path;
    public double Scale => scale;

    // ---------------------------------------------------------------- setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // Windows 11 rounds window corners; that would cut the image corners.
        int pref = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        scale = InitialScale(bitmap.PixelWidth, bitmap.PixelHeight);
        Native.GetCursorPos(out var cursor);
        PlaceCentered(cursor.X, cursor.Y, cursor.X, cursor.Y);
    }

    /// <summary>
    /// Allow windows smaller than the system minimum and larger than the screen.
    /// The message is not marked handled: WPF's own handler must read these limits too,
    /// otherwise WPF lays out the content with the default limits
    /// (image stretched in tiny windows, black area in windows larger than the screen).
    /// </summary>
    private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
            mmi.ptMinTrackSize = new Native.POINT { X = 1, Y = 1 };
            mmi.ptMaxTrackSize = new Native.POINT { X = MaxSidePx, Y = MaxSidePx };
            Marshal.StructureToPtr(mmi, lParam, false);
        }
        return IntPtr.Zero;
    }

    /// <summary>WPF keeps the DIP size when the DPI changes; we want to keep the physical size.</summary>
    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Native.GetWindowRect(hwnd, out var r);
            if (r.Width != width || r.Height != height) SetBounds(r.Left, r.Top, width, height);
        }));
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
        double max = (double)MaxSidePx / longSide;
        double min = Math.Min(1.0, (double)MinLongSidePx / longSide);
        return Math.Max(min, Math.Min(max, s));
    }

    private void SetBounds(int x, int y, int w, int h)
    {
        width = w;
        height = h;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Size the window for the current scale, centered at (cx, cy), then keep it
    /// inside the work area of the monitor at (monX, monY) where it fits.
    /// </summary>
    private void PlaceCentered(int cx, int cy, int monX, int monY)
    {
        int w = ScaledSide(bitmap.PixelWidth, scale);
        int h = ScaledSide(bitmap.PixelHeight, scale);
        var work = Native.WorkAreaAt(monX, monY);
        int x = FitAxis(cx - w / 2, w, work.Left, work.Right);
        int y = FitAxis(cy - h / 2, h, work.Top, work.Bottom);
        UpdateScalingMode();
        SetBounds(x, y, w, h);
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
        Native.GetWindowRect(hwnd, out var r);
        double fx = (ax - r.Left) / (double)Math.Max(1, r.Width);
        double fy = (ay - r.Top) / (double)Math.Max(1, r.Height);
        int w = ScaledSide(bitmap.PixelWidth, newScale);
        int h = ScaledSide(bitmap.PixelHeight, newScale);
        int x = (int)Math.Round(ax - fx * w);
        int y = (int)Math.Round(ay - fy * h);
        scale = newScale;
        UpdateScalingMode();
        SetBounds(x, y, w, h);
    }

    /// <summary>Zoom around the window center (used by the 拡大率 box).</summary>
    public void ZoomToCenter(double newScale)
    {
        Native.GetWindowRect(hwnd, out var r);
        ZoomTo(newScale, r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private void UpdateScalingMode()
    {
        // Pixel-exact at 100% and when enlarging; smooth when shrinking.
        var mode = scale < 1.0 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor;
        RenderOptions.SetBitmapScalingMode(image, mode);
    }

    // ---------------------------------------------------------------- input

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double next = scale * Math.Pow(ZoomStep, e.Delta / 120.0);
        // Stop at 100% when passing through it.
        if ((scale < 1.0 && next > 1.0) || (scale > 1.0 && next < 1.0)) next = 1.0;
        Native.GetCursorPos(out var p);
        ZoomTo(next, p.X, p.Y);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Right: Navigate(+1); e.Handled = true; break;
            case Key.Left: Navigate(-1); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            TryOpen(files[0], showError: true);
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
        int i = direction > 0
            ? files.FindIndex(f => ImageLoader.CompareKey(f.LastWriteTimeUtc, f.Name, curTime, curName) > 0)
            : files.FindLastIndex(f => ImageLoader.CompareKey(f.LastWriteTimeUtc, f.Name, curTime, curName) < 0);
        if (i < 0) return;

        for (; i >= 0 && i < files.Count; i += direction)
            if (TryOpen(files[i].FullName, showError: false)) return;
    }

    private DateTime lastKnownWriteTime;

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

        Native.GetWindowRect(hwnd, out var r);
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;

        path = Path.GetFullPath(file);
        bitmap = bmp;
        ShowBitmap();
        scale = InitialScale(bitmap.PixelWidth, bitmap.PixelHeight);
        PlaceCentered(cx, cy, cx, cy);
        return true;
    }

    private void ShowBitmap()
    {
        image.Source = bitmap;
        Title = $"{Path.GetFileName(path)} - imageviewer";
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
