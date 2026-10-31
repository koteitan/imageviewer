using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer;

internal static class Program
{
    /// <summary>Usage: ImageViewer.exe [image file]. Without an argument a file dialog opens.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

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

        return app.Run(new ViewerWindow(path, bitmap));
    }

    private static string? AskFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "画像を開く", Filter = ImageLoader.DialogFilter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static void ShowLoadError(string path, Exception ex) =>
        MessageBox.Show($"画像を開けませんでした。\n{path}\n\n{ex.Message}", "ImageViewer", MessageBoxButton.OK, MessageBoxImage.Error);
}
