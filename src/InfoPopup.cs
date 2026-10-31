using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ImageViewer;

/// <summary>
/// Right-click view (dark theme):
/// <code>
/// 拡大率:         [100%] [...]%
/// ファイル名:     [copy] [....]
/// フルパス  :     [copy] [....]
/// ペイントで開く: [開く]
/// 回転:           [保存] (only while the view is rotated)
/// </code>
/// </summary>
internal sealed class InfoPopup : Window
{
    private const double ShadowMargin = 14;     // transparent margin around the card for the drop shadow
    private readonly ViewerWindow owner;
    private readonly ResourceDictionary theme = Theme.Load();
    private readonly TextBox zoomBox;
    private readonly TextBox nameBox;
    private readonly TextBox pathBox;
    private readonly TextBlock rotateLabel;
    private readonly Button saveButton;
    private readonly TextBlock rotateInfo;

    public InfoPopup(ViewerWindow owner)
    {
        this.owner = owner;

        zoomBox = new TextBox { Width = 64, Height = RowHeight, TextAlignment = TextAlignment.Right };
        nameBox = ReadOnlyBox();
        pathBox = ReadOnlyBox();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // fixed width: every button has the same width, also while showing "✓ copied"
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ButtonWidth + Cell.Right) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 拡大率: [100%] [...]%
        Add(grid, Label("拡大率"), 0, 0);
        var reset = MakeButton("100%");
        reset.Click += (_, _) => { owner.ZoomToCenter(1.0); RefreshZoom(); };
        Add(grid, reset, 0, 1);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = Cell };
        zoomRow.Children.Add(zoomBox);
        zoomRow.Children.Add(new TextBlock { Text = "%", Style = ThemeStyle("Unit") });
        Add(grid, zoomRow, 0, 2);
        zoomBox.KeyDown += OnZoomKeyDown;
        zoomBox.GotKeyboardFocus += (_, _) => zoomBox.SelectAll();

        // ファイル名: [copy] [....]
        Add(grid, Label("ファイル名"), 1, 0);
        Add(grid, CopyButton(() => nameBox.Text), 1, 1);
        Add(grid, nameBox, 1, 2);

        // フルパス: [copy] [....]
        Add(grid, Label("フルパス"), 2, 0);
        Add(grid, CopyButton(() => pathBox.Text), 2, 1);
        Add(grid, pathBox, 2, 2);

        // ペイントで開く: [開く]
        Add(grid, Label("ペイントで開く"), 3, 0);
        var paint = MakeButton("開く");
        paint.Click += (_, _) => OpenInPaint();
        Add(grid, paint, 3, 1);

        // 回転: [保存] 右に 90°   (or why it cannot be saved)
        rotateLabel = Label("回転");
        Add(grid, rotateLabel, 4, 0);
        saveButton = MakeButton("保存");
        saveButton.Click += (_, _) => { owner.SaveRotation(); RefreshIfVisible(); };
        Add(grid, saveButton, 4, 1);
        rotateInfo = new TextBlock { Style = ThemeStyle("Unit"), TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(2, 0, 0, 0) };
        Add(grid, rotateInfo, 4, 2);

        var card = new Border
        {
            Child = grid,
            Style = ThemeStyle("Card"),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        card.Resources = theme;
        Theme.ApplyText(card);
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { HideAndReturn(); e.Handled = true; }
        };

        // A small borderless window owned by the viewer; it hides when it loses focus.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "imageviewer";
        Content = new Border { Child = card, Padding = new Thickness(ShadowMargin), Background = Brushes.Transparent };
        new WindowInteropHelper(this).Owner = owner.Handle;
        // Hide after the activation change has finished. Hiding the window while Windows is still
        // switching the active window makes Windows pick another app's window to activate
        // (the viewer that was clicked then falls behind it).
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsActive) Hide();
        }));
    }

    /// <summary>Show with the card's top-left corner at the mouse cursor, kept inside the work area.</summary>
    public void Open()
    {
        Refresh();

        Opacity = 0; // invisible until it is at the right place
        Show();
        Activate();
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(handle, out var r);
        Native.GetCursorPos(out var c);
        int m = (int)Math.Round(ShadowMargin * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var work = Native.WorkAreaAt(c.X, c.Y);
        int px = Math.Max(work.Left - m, Math.Min(work.Right + m - r.Width, c.X - m));
        int py = Math.Max(work.Top - m, Math.Min(work.Bottom + m - r.Height, c.Y - m));
        Native.SetWindowPos(handle, IntPtr.Zero, px, py, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Opacity = 1;
        zoomBox.Focus();
        zoomBox.SelectAll();
    }

    /// <summary>Update the contents if the view is open (the image was rotated or saved meanwhile).</summary>
    public void RefreshIfVisible()
    {
        if (IsVisible) Refresh();
    }

    private void Refresh()
    {
        RefreshZoom();
        nameBox.Text = Path.GetFileName(owner.FilePath);
        pathBox.Text = owner.FilePath;

        int r = owner.Rotation;
        var show = r == 0 ? Visibility.Collapsed : Visibility.Visible;
        rotateLabel.Visibility = saveButton.Visibility = rotateInfo.Visibility = show;
        if (r == 0) return;
        var mode = owner.RotationSavePlan(out string reason);
        saveButton.IsEnabled = mode != ImageSaver.Mode.Blocked;
        string angle = r switch { 90 => "右に 90°", 180 => "180°", _ => "左に 90°" };
        rotateInfo.Text = mode switch
        {
            ImageSaver.Mode.Overwrite => angle + "(上書き保存します)",
            ImageSaver.Mode.OverwriteLossy => angle + "(上書き保存します。" + reason + ")",
            _ => angle + " — " + reason,
        };
    }

    private void HideAndReturn()
    {
        Hide();
        owner.Activate();
    }

    private void RefreshZoom()
    {
        zoomBox.Text = Math.Round(owner.Scale * 100.0, 1).ToString("0.#", CultureInfo.InvariantCulture);
        zoomBox.SelectAll();
    }

    private void OnZoomKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string text = zoomBox.Text.Trim().TrimEnd('%').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            && percent > 0 && !double.IsInfinity(percent))
        {
            owner.ZoomToCenter(percent / 100.0);
        }
        RefreshZoom();
    }

    private void OpenInPaint()
    {
        Hide();
        try
        {
            Process.Start(new ProcessStartInfo("mspaint.exe", $"\"{owner.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ペイントを起動できませんでした。\n\n{ex.Message}", "imageviewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- building blocks

    private static readonly Thickness Cell = new(0, 4, 8, 4);
    private const double ButtonWidth = 88;
    private const double RowHeight = 32;     // buttons and text boxes

    private Style ThemeStyle(string key) => (Style)theme[key];

    private static void Add(Grid grid, FrameworkElement e, int row, int col)
    {
        if (e.Margin == default) e.Margin = Cell;
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        grid.Children.Add(e);
    }

    private TextBlock Label(string text) => new() { Text = text, Style = ThemeStyle("Label") };

    private TextBox ReadOnlyBox() => new() { IsReadOnly = true, MinWidth = 380, MaxWidth = 900, Height = RowHeight };

    private Button MakeButton(string text) =>
        new() { Content = text, Style = ThemeStyle("AccentButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Height = RowHeight };

    private Button CopyButton(Func<string> text)
    {
        var button = MakeButton("copy");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = "copy";
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.BorderBrushProperty);
        };
        button.Click += (_, _) =>
        {
            bool ok = TrySetClipboard(text());
            var brush = (Brush)theme[ok ? "SuccessBrush" : "ErrorBrush"];
            button.Content = ok ? "✓ copied" : "failed";
            button.Background = brush;
            button.BorderBrush = brush;
            timer.Stop();
            timer.Start();
        };
        return button;
    }

    /// <summary>The clipboard can be briefly locked by other apps; retry a few times.</summary>
    private static bool TrySetClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
        return false;
    }

}
