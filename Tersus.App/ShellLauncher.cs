using System.Diagnostics;

namespace Tersus.App.Services;

/// <summary>
/// The ONLY place where Tersus starts another program, and only for four harmless, fixed purposes: show an item in Explorer,
/// open a folder, open the Windows "Installed apps" settings page, open the Recycle Bin. Paths are validated; nothing is ever executed.
/// </summary>
internal static class ShellLauncher
{
    public static void ShowInExplorer(string? path)
    {
        if (!IsSafePath(path))
        {
            return;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            Start("explorer.exe", $"/select,\"{path}\"");
            return;
        }

        string? parent = Path.GetDirectoryName(path);
        if (parent is not null && Directory.Exists(parent))
        {
            Start("explorer.exe", $"\"{parent}\"");
        }
    }

    public static void OpenFolder(string? path)
    {
        if (IsSafePath(path) && Directory.Exists(path))
        {
            Start("explorer.exe", $"\"{path}\"");
        }
    }

    public static void OpenAppsSettings() => StartShell("ms-settings:appsfeatures");

    public static void OpenRecycleBin() => Start("explorer.exe", "shell:RecycleBinFolder");

    private static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOf('"') < 0 && path.IndexOf('\0') < 0 && path.Length < 32_000;

    private static void Start(string file, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Opening Explorer is a convenience; failing to do so must never affect anything else.
        }
    }

    private static void StartShell(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
