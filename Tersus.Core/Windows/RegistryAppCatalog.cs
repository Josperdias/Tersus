using System.Runtime.Versioning;
using Microsoft.Win32;
using Tersus.Core.Apps;

namespace Tersus.Core.Windows;

/// <summary>Lists installed programs from the Windows "Uninstall" registry keys. Strictly read-only: it never runs an uninstaller and never edits the registry.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryAppCatalog : IAppCatalog
{
    private static readonly string[] ValueNames =
    [
        "DisplayName", "Publisher", "DisplayVersion", "InstallDate", "EstimatedSize", "InstallLocation", "SystemComponent",
        "ReleaseType", "ParentKeyName", "ParentDisplayName", "UninstallString", "QuietUninstallString",
    ];

    public IReadOnlyList<InstalledApp> List()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string path32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        var apps = new List<InstalledApp>();
        Read(apps, RegistryHive.LocalMachine, RegistryView.Registry64, path, "Todos os usuários (64 bits)");
        Read(apps, RegistryHive.LocalMachine, RegistryView.Registry64, path32, "Todos os usuários (32 bits)");
        Read(apps, RegistryHive.CurrentUser, RegistryView.Registry64, path, "Somente este usuário");
        return UninstallEntryParser.MergeAndSort(apps);
    }

    private static void Read(List<InstalledApp> into, RegistryHive hive, RegistryView view, string subKey, string source)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? parent = baseKey.OpenSubKey(subKey, writable: false);
            if (parent is null)
            {
                return;
            }

            foreach (string name in parent.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? key = parent.OpenSubKey(name, writable: false);
                    if (key is null)
                    {
                        continue;
                    }

                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (string valueName in ValueNames)
                    {
                        values[valueName] = key.GetValue(valueName);
                    }

                    InstalledApp? app = UninstallEntryParser.Parse(values, source);
                    if (app is not null)
                    {
                        into.Add(app);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // One unreadable entry must not hide the others.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // The whole hive/view may be unavailable; return what we have.
        }
    }
}
