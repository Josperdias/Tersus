using System.Globalization;

namespace Tersus.Core.Apps;

public sealed record InstalledApp(
    string Name,
    string? Publisher,
    string? Version,
    DateTime? InstallDate,
    long? EstimatedSizeBytes,
    string? InstallLocation,
    string Source,
    bool HasUninstaller);

public interface IAppCatalog
{
    IReadOnlyList<InstalledApp> List();
}

/// <summary>
/// Interprets one "Uninstall" registry entry (read-only). Hidden on purpose: system components, updates/hotfixes and entries with no name.
/// Sizes are what the installer REPORTED; they can be missing or wrong and are labelled that way in the UI. Last-use dates are not available
/// from the registry and are never invented.
/// </summary>
public static class UninstallEntryParser
{
    public static InstalledApp? Parse(IReadOnlyDictionary<string, object?> values, string source)
    {
        string? name = Text(values, "DisplayName")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Number(values, "SystemComponent") == 1)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(Text(values, "ParentKeyName")) || !string.IsNullOrWhiteSpace(Text(values, "ParentDisplayName")))
        {
            return null;
        }

        string? release = Text(values, "ReleaseType");
        if (release is not null && (release.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
            || release.Contains("Service Pack", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (name.StartsWith("KB", StringComparison.Ordinal) && name.Length < 12 && name[2..].All(char.IsDigit))
        {
            return null;
        }

        long? sizeKb = Number(values, "EstimatedSize");
        bool uninstaller = !string.IsNullOrWhiteSpace(Text(values, "UninstallString")) || !string.IsNullOrWhiteSpace(Text(values, "QuietUninstallString"));
        return new InstalledApp(
            name,
            Clean(Text(values, "Publisher")),
            Clean(Text(values, "DisplayVersion")),
            ParseDate(Text(values, "InstallDate")),
            sizeKb is > 0 and < long.MaxValue / 1024 ? sizeKb * 1024 : null,
            Clean(Text(values, "InstallLocation")),
            source,
            uninstaller);
    }

    /// <summary>Removes duplicates reported by the 32-bit and 64-bit registry views (same name, version and publisher).</summary>
    public static IReadOnlyList<InstalledApp> MergeAndSort(IEnumerable<InstalledApp> apps) =>
        [.. apps
            .GroupBy(a => (a.Name.ToUpperInvariant(), a.Version ?? string.Empty, (a.Publisher ?? string.Empty).ToUpperInvariant()))
            .Select(g => g.OrderByDescending(a => a.EstimatedSizeBytes ?? 0).ThenByDescending(a => a.InstallDate ?? DateTime.MinValue).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];

    private static string? Text(IReadOnlyDictionary<string, object?> v, string key) =>
        v.TryGetValue(key, out object? o) ? o as string : null;

    private static long? Number(IReadOnlyDictionary<string, object?> v, string key) =>
        v.TryGetValue(key, out object? o) ? o switch { int i => i, uint u => u, long l => l, _ => null } : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateTime? ParseDate(string? s) =>
        s is { Length: 8 } && DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) && d.Year >= 1990
            ? d
            : null;
}
