using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ImageViewer;

/// <summary>
/// Right-click view:
/// <code>
/// 拡大率: [   ]%
/// ファイル名: [copy] [....]
/// フルパス  : [copy] [....]
/// ペイントで開く: [....]
/// </code>
/// </summary>
internal sealed class InfoPopup
{
    private readonly ViewerWindow owner;
    private readonly Popup popup;
    private readonly TextBox zoomBox = new() { Width = 70, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox nameBox = ReadOnlyBox();
    private readonly TextBox pathBox = ReadOnlyBox();

    public InfoPopup(ViewerWindow owner)
    {
        this.owner = owner;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 拡大率: [   ]%
        Add(grid, Label("拡大率:"), 0, 0);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 0, 3) };
        zoomRow.Children.Add(zoomBox);
        zoomRow.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) });
        Add(grid, zoomRow, 0, 1, span: 2);
        zoomBox.KeyDown += OnZoomKeyDown;
        zoomBox.GotKeyboardFocus += (_, _) => zoomBox.SelectAll();

        // ファイル名: [copy] [....]
        Add(grid, Label("ファイル名:"), 1, 0);
        Add(grid, CopyButton(() => nameBox.Text), 1, 1);
        Add(grid, nameBox, 1, 2);

        // フルパス  : [copy] [....]
        Add(grid, Label("フルパス:"), 2, 0);
        Add(grid, CopyButton(() => pathBox.Text), 2, 1);
        Add(grid, pathBox, 2, 2);

        // ペイントで開く: [....]
        Add(grid, Label("ペイントで開く:"), 3, 0);
        var paint = new Button { Content = "開く", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left };
        paint.Click += (_, _) => OpenInPaint();
        Add(grid, paint, 3, 1);

        var border = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 6, 10, 6),
            Background = SystemColors.ControlBrush,
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
        };
        TextElement.SetFontFamily(border, SystemFonts.MessageFontFamily);
        TextElement.SetFontSize(border, 13);
        border.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { popup!.IsOpen = false; e.Handled = true; }
        };

        popup = new Popup
        {
            Child = border,
            PlacementTarget = owner,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };
    }

    public bool IsOpen => popup.IsOpen;

    public void Open()
    {
        Refresh();
        popup.IsOpen = true;
        zoomBox.Focus();
        zoomBox.SelectAll();
    }

    private void Refresh()
    {
        zoomBox.Text = FormatPercent(owner.Scale);
        nameBox.Text = Path.GetFileName(owner.FilePath);
        pathBox.Text = owner.FilePath;
    }

    private static string FormatPercent(double scale) =>
        Math.Round(scale * 100.0, 1).ToString("0.#", CultureInfo.InvariantCulture);

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
        zoomBox.Text = FormatPercent(owner.Scale);
        zoomBox.SelectAll();
    }

    private void OpenInPaint()
    {
        popup.IsOpen = false;
        try
        {
            Process.Start(new ProcessStartInfo("mspaint.exe", $"\"{owner.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"ペイントを起動できませんでした。\n\n{ex.Message}", "ImageViewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Add(Grid grid, UIElement e, int row, int col, int span = 1)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        Grid.SetColumnSpan(e, span);
        grid.Children.Add(e);
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };

    private static TextBox ReadOnlyBox() => new()
    {
        IsReadOnly = true,
        MinWidth = 360,
        MaxWidth = 900,
        Margin = new Thickness(4, 3, 0, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static Button CopyButton(Func<string> text)
    {
        var button = new Button { Content = "copy", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 3, 0, 3) };
        button.Click += (_, _) =>
        {
            bool ok = TrySetClipboard(text());
            button.Content = ok ? "copied" : "failed";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => { timer.Stop(); button.Content = "copy"; };
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
