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
/// </code>
/// </summary>
internal sealed class InfoPopup : Window
{
    private const double ShadowMargin = 14;     // transparent margin around the card for the drop shadow
    private readonly ViewerWindow owner;
    private readonly ResourceDictionary theme = LoadTheme();
    private readonly TextBox zoomBox;
    private readonly TextBox nameBox;
    private readonly TextBox pathBox;

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
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

        var card = new Border
        {
            Child = grid,
            Style = ThemeStyle("Card"),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        card.Resources = theme;
        TextElementProps(card);
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
        RefreshZoom();
        nameBox.Text = Path.GetFileName(owner.FilePath);
        pathBox.Text = owner.FilePath;

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

    private static void TextElementProps(Border card)
    {
        System.Windows.Documents.TextElement.SetFontFamily(card, new FontFamily("Segoe UI Variable Text, Yu Gothic UI, Segoe UI"));
        System.Windows.Documents.TextElement.SetFontSize(card, 13);
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
    }

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

    // ---------------------------------------------------------------- theme

    private static ResourceDictionary LoadTheme() => (ResourceDictionary)XamlReader.Parse(ThemeXaml);

    /// <summary>Dark theme in the style of Windows 11 (Fluent) flyouts.</summary>
    private const string ThemeXaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="CardBrush" Color="#202020"/>
  <SolidColorBrush x:Key="CardBorderBrush" Color="#3A3A3A"/>
  <SolidColorBrush x:Key="TextBrush" Color="#F3F3F3"/>
  <SolidColorBrush x:Key="LabelBrush" Color="#A6A6A6"/>
  <SolidColorBrush x:Key="ControlBrush" Color="#2D2D2D"/>
  <SolidColorBrush x:Key="ControlHoverBrush" Color="#383838"/>
  <SolidColorBrush x:Key="ControlPressedBrush" Color="#262626"/>
  <SolidColorBrush x:Key="ControlBorderBrush" Color="#3F3F3F"/>
  <SolidColorBrush x:Key="InputBrush" Color="#1A1A1A"/>
  <SolidColorBrush x:Key="AccentBrush" Color="#4CC2FF"/>
  <SolidColorBrush x:Key="AccentHoverBrush" Color="#48B1E8"/>
  <SolidColorBrush x:Key="AccentPressedBrush" Color="#42A1D2"/>
  <SolidColorBrush x:Key="SuccessBrush" Color="#6CCB5F"/>
  <SolidColorBrush x:Key="ErrorBrush" Color="#FF99A4"/>

  <Style x:Key="Card" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource CardBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource CardBorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="8"/>
    <Setter Property="Padding" Value="16,10,8,10"/>
  </Style>

  <Style x:Key="Label" TargetType="TextBlock">
    <Setter Property="Foreground" Value="{StaticResource LabelBrush}"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
    <Setter Property="Margin" Value="0,4,16,4"/>
  </Style>

  <Style x:Key="Unit" TargetType="TextBlock">
    <Setter Property="Foreground" Value="{StaticResource LabelBrush}"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
    <Setter Property="Margin" Value="6,0,0,0"/>
  </Style>

  <Style x:Key="SubtleButton" TargetType="Button">
    <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
    <Setter Property="Background" Value="{StaticResource ControlBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource ControlBorderBrush}"/>
    <Setter Property="Padding" Value="12,4"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="{StaticResource ControlHoverBrush}"/>
      </Trigger>
      <Trigger Property="IsPressed" Value="True">
        <Setter Property="Background" Value="{StaticResource ControlPressedBrush}"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource SubtleButton}">
    <Setter Property="Foreground" Value="#000000"/>
    <Setter Property="Background" Value="{StaticResource AccentBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="{StaticResource AccentHoverBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource AccentHoverBrush}"/>
      </Trigger>
      <Trigger Property="IsPressed" Value="True">
        <Setter Property="Background" Value="{StaticResource AccentPressedBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource AccentPressedBrush}"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType="TextBox">
    <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
    <Setter Property="Background" Value="{StaticResource ControlBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource ControlBorderBrush}"/>
    <Setter Property="CaretBrush" Value="{StaticResource TextBrush}"/>
    <Setter Property="SelectionBrush" Value="{StaticResource AccentBrush}"/>
    <Setter Property="Padding" Value="8,0"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="Box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" CornerRadius="4" SnapsToDevicePixels="True">
            <Grid>
              <ScrollViewer x:Name="PART_ContentHost" Focusable="False" Margin="{TemplateBinding Padding}"
                            VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                            HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
              <Border x:Name="Underline" Height="2" VerticalAlignment="Bottom" Margin="1,0"
                      CornerRadius="0,0,3,3" Background="Transparent"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Box" Property="Background" Value="{StaticResource ControlHoverBrush}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Box" Property="Background" Value="{StaticResource InputBrush}"/>
              <Setter TargetName="Underline" Property="Background" Value="{StaticResource AccentBrush}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";
}
