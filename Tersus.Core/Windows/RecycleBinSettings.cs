using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Tersus.Core.Cleanup;

namespace Tersus.Core.Windows;

/// <summary>Feeds the portable <see cref="RecycleBinPolicy"/> with values read (never written) from the registry.</summary>
[SupportedOSPlatform("windows")]
internal static class RecycleBinSettings
{
    internal static RecycleBinPolicy.Result Read(string volumeRoot) => RecycleBinPolicy.Evaluate(volumeRoot, ReadDword, VolumeGuid);

    private static int? ReadDword(bool localMachine, string subKey, string name)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(localMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValue(name) is int v ? v : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"C:\" gives "{xxxxxxxx-xxxx-...}" (the key name Windows uses under BitBucket\Volume).</summary>
    private static string? VolumeGuid(string volumeRoot)
    {
        var sb = new StringBuilder(64);
        if (!NativeMethods.GetVolumeNameForVolumeMountPointW(volumeRoot, sb, (uint)sb.Capacity))
        {
            _ = Marshal.GetLastWin32Error();
            return null;
        }

        string name = sb.ToString(); // \\?\Volume{guid}\
        int open = name.IndexOf('{', StringComparison.Ordinal);
        int close = name.IndexOf('}', StringComparison.Ordinal);
        return open >= 0 && close > open ? name[open..(close + 1)] : null;
    }
}
