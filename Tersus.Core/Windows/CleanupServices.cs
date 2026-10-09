using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tersus.Core.Advice;
using Tersus.Core.Apps;
using Tersus.Core.Cleanup;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Core.Storage;

namespace Tersus.Core.Windows;

/// <summary>The real, production wiring of the cleanup stack (policy + file system + recycler) for the current user.</summary>
[SupportedOSPlatform("windows")]
public static class CleanupServices
{
    public sealed record Stack(FilePolicy? Policy, string? Problem, IFileSystem FileSystem, IRecycler Recycler, AppDataFiles Files, string TempFolder);

    public static Stack Create(AppPaths? paths = null)
    {
        var fs = new WindowsFileSystem();
        var files = new AppDataFiles(paths ?? new AppPaths());
        string temp = Path.GetTempPath();
        TempRootResult root = TempRootResolver.Resolve(fs, temp, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ProtectedFolders());
        FilePolicy? policy = root.CanonicalRoot is null ? null : new FilePolicy(root.CanonicalRoot);
        return new Stack(policy, root.Problem, fs, new ShellRecycler(files), files, temp);
    }

    /// <summary>Folders whose content a person would be hurt to lose; the TEMP folder may not contain, equal or sit inside any of them.</summary>
    public static IReadOnlyList<string> ProtectedFolders()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(profile, "Downloads"),
        };
        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value);
            }
        }

        return list;
    }
}

/// <summary>Read-only facts about this PC for the advisor.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsFacts
{
    public static AdvisorInput Collect(IReadOnlyList<InstalledApp> apps, long? cloudOnlyBytes = null, long? downloadsBytes = null)
    {
        string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        long? total = null;
        long? free = null;
        try
        {
            var drive = new DriveInfo(systemRoot);
            total = drive.TotalSize;
            free = drive.TotalFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Not fatal.
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var browsers = new List<string>();
        if (Directory.Exists(Path.Combine(local, "Google", "Chrome", "User Data")))
        {
            browsers.Add("Google Chrome");
        }

        if (Directory.Exists(Path.Combine(local, "Microsoft", "Edge", "User Data")))
        {
            browsers.Add("Microsoft Edge");
        }

        if (Directory.Exists(Path.Combine(roaming, "Mozilla", "Firefox")))
        {
            browsers.Add("Mozilla Firefox");
        }

        if (Directory.Exists(Path.Combine(local, "BraveSoftware")))
        {
            browsers.Add("Brave");
        }

        return new AdvisorInput
        {
            VolumeTotalBytes = total,
            VolumeFreeBytes = free,
            VolumeName = systemRoot.TrimEnd('\\'),
            HibernationFileBytes = SystemFileSize(systemRoot, "hiberfil.sys"),
            PageFileBytes = SystemFileSize(systemRoot, "pagefile.sys"),
            WindowsOldExists = Directory.Exists(Path.Combine(systemRoot, "Windows.old")),
            RecycleBinBytes = RecycleBinSize(),
            DownloadsBytes = downloadsBytes,
            CloudOnlyBytes = cloudOnlyBytes,
            Apps = apps,
            BrowsersDetected = browsers,
            BlenderDetected = Directory.Exists(Path.Combine(roaming, "Blender Foundation")) || apps.Any(a => a.Name.Contains("Blender", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static long? SystemFileSize(string root, string name)
    {
        try
        {
            var info = new FileInfo(Path.Combine(root, name));
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Total size of the Recycle Bin on all drives (read-only query).</summary>
    public static long? RecycleBinSize()
    {
        var info = new NativeMethods.ShQueryRbInfo { Size = (uint)Marshal.SizeOf<NativeMethods.ShQueryRbInfo>() };
        return NativeMethods.SHQueryRecycleBinW(null, ref info) == 0 ? info.SizeBytes : null;
    }
}
