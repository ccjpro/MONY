using System.Runtime.InteropServices;

namespace MONY;

internal static class StartupRegistration
{
    private const string FileName = "MONY Startup.lnk";

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), FileName);

    internal static bool Enabled => File.Exists(ShortcutPath);

    internal static void SetEnabled(bool enabled)
    {
        var path = ShortcutPath;
        if (!enabled)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                shortcut.TargetPath = Environment.ProcessPath!;
                shortcut.Arguments = "--startup";
                shortcut.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
