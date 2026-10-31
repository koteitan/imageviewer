using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ImageViewer;

/// <summary>Small dark modal dialog: a message and a row of buttons. Esc or closing = cancel.</summary>
internal sealed class ChoiceDialog : Window
{
    private int choice = -1;

    private ChoiceDialog(IntPtr owner, string message, string[] buttons, int cancelIndex)
    {
        var theme = Theme.Load();
        var text = new TextBlock
        {
            Text = message,
            Foreground = (Brush)theme["TextBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Margin = new Thickness(0, 0, 0, 14),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)theme["AccentButton"],
                MinWidth = 88,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == 0,
            };
            b.Click += (_, _) => { choice = index; Close(); };
            row.Children.Add(b);
        }
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(row);

        var card = new Border
        {
            Child = panel,
            Style = (Style)theme["Card"],
            Padding = new Thickness(18, 14, 14, 14),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.55, Color = Colors.Black },
            Resources = theme,
        };
        Theme.ApplyText(card);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "imageviewer";
        Content = new Border { Child = card, Padding = new Thickness(14), Background = Brushes.Transparent };
        if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            choice = cancelIndex;
            Close();
        };
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    /// <summary>Index of the pressed button; <paramref name="cancelIndex"/> for Esc / close.</summary>
    public static int Show(IntPtr owner, string message, string[] buttons, int cancelIndex)
    {
        var dialog = new ChoiceDialog(owner, message, buttons, cancelIndex);
        dialog.ShowDialog();
        return dialog.choice < 0 ? cancelIndex : dialog.choice;
    }
}
