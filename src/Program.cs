using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer;

internal static class Program
{
    /// <summary>Usage: imageviewer.exe [image file]. Without an argument a file dialog opens.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // The viewer is a Win32 window, not a WPF Window: it shuts the app down itself (WM_DESTROY).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        string? path = args.Length > 0 ? args[0] : AskFile();
        if (path is null) return 0;
        path = Path.GetFullPath(path);

        BitmapSource bitmap;
        try
        {
            bitmap = ImageLoader.Load(path);
        }
        catch (Exception ex)
        {
            ShowLoadError(path, ex);
            return 1;
        }

        new ViewerWindow(path, bitmap).Show();
        return app.Run();
    }

    private static string? AskFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "画像を開く", Filter = ImageLoader.DialogFilter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static void ShowLoadError(string path, Exception ex) =>
        MessageBox.Show($"画像を開けませんでした。\n{path}\n\n{ex.Message}", "imageviewer", MessageBoxButton.OK, MessageBoxImage.Error);
}
