using System;
using Microsoft.Win32;

namespace ImageViewer;

/// <summary>
/// Registers imageviewer as a handler for the supported image types (current user only, no admin rights).
///
/// Windows 10/11 do not let an app make itself the default program; after registering, imageviewer
/// appears in "Open with" and in Settings &gt; Apps &gt; Default apps, where the user picks it.
/// </summary>
internal static class FileAssociation
{
    public const string AppName = "imageviewer";
    private const string ProgId = "imageviewer.image";
    private const string Classes = @"Software\Classes";
    private const string CapabilitiesKey = @"Software\imageviewer\Capabilities";
    private const string RegisteredApplications = @"Software\RegisteredApplications";

    /// <summary>Settings page that lists the file types of this app.</summary>
    public const string SettingsUri = "ms-settings:defaultapps?registeredAppUser=" + AppName;

    public static void Register(string exePath)
    {
        string command = $"\"{exePath}\" \"%1\"";
        var hkcu = Registry.CurrentUser;

        using (var prog = hkcu.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            prog.SetValue("", "画像 (imageviewer)");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exePath}\",0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
        }

        using (var app = hkcu.CreateSubKey($@"{Classes}\Applications\imageviewer.exe"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (string ext in ImageLoader.Extensions) types.SetValue(ext, "");
        }

        foreach (string ext in ImageLoader.Extensions)
        {
            using var openWith = hkcu.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var caps = hkcu.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "縁なしの画像ビューア");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (string ext in ImageLoader.Extensions) assoc.SetValue(ext, ProgId);
        }
        using (var reg = hkcu.CreateSubKey(RegisteredApplications)) reg.SetValue(AppName, CapabilitiesKey);

        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Unregister()
    {
        var hkcu = Registry.CurrentUser;
        hkcu.DeleteSubKeyTree($@"{Classes}\{ProgId}", false);
        hkcu.DeleteSubKeyTree($@"{Classes}\Applications\imageviewer.exe", false);
        foreach (string ext in ImageLoader.Extensions)
        {
            using var openWith = hkcu.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", true);
            openWith?.DeleteValue(ProgId, false);
        }
        hkcu.DeleteSubKeyTree(@"Software\imageviewer", false);
        using (var reg = hkcu.OpenSubKey(RegisteredApplications, true)) reg?.DeleteValue(AppName, false);

        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
