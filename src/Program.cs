using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer;

internal static class Program
{
    /// <summary>
    /// Usage:
    ///   imageviewer.exe [image file]          show the image (without an argument a file dialog opens)
    ///   imageviewer.exe --register [--quiet]  register as a handler of the supported image types
    ///   imageviewer.exe --unregister [--quiet]
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // The viewer is a Win32 window, not a WPF Window: it shuts the app down itself (WM_DESTROY).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        if (args.Length > 0 && (args[0] == "--register" || args[0] == "--unregister"))
            return Associate(args[0] == "--register", quiet: Array.IndexOf(args, "--quiet") > 0);

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

    private static int Associate(bool register, bool quiet)
    {
        try
        {
            if (register) FileAssociation.Register(typeof(Program).Assembly.Location);
            else FileAssociation.Unregister();
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show($"失敗しました。\n\n{ex.Message}", "imageviewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        if (quiet) return 0;
        if (!register)
        {
            MessageBox.Show("関連付けを解除しました。", "imageviewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        var answer = MessageBox.Show(
            "imageviewer を画像の「プログラムから開く」に登録しました。\n\n" +
            "既定のアプリにするには、設定で拡張子ごとに imageviewer を選んでください。\n設定を開きますか?",
            "imageviewer", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FileAssociation.SettingsUri) { UseShellExecute = true });
        return 0;
    }

    private static string? AskFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "画像を開く", Filter = ImageLoader.DialogFilter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static void ShowLoadError(string path, Exception ex) =>
        MessageBox.Show($"画像を開けませんでした。\n{path}\n\n{ex.Message}", "imageviewer", MessageBoxButton.OK, MessageBoxImage.Error);
}
