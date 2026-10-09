namespace Tersus.Core.Knowledge;

/// <summary>
/// Locations where Tersus will never offer a cleanup action: the operating system, installed programs, browser profiles and user data.
/// Used by the explainer, the duplicate finder (to warn) and as one more argument for the static audit. Pure string logic.
/// </summary>
public static class ProtectedLocations
{
    private static readonly string[] SystemFragments =
    [
        @"\windows\", @"\program files\", @"\program files (x86)\", @"\programdata\", @"\$recycle.bin\", @"\system volume information\",
        @"\recovery\", @"\boot\",
    ];

    private static readonly string[] UserDataFragments =
    [
        @"\documents\", @"\desktop\", @"\downloads\", @"\pictures\", @"\music\", @"\videos\", @"\onedrive", @"\favorites\", @"\contacts\",
    ];

    private static readonly string[] BrowserProfileFragments =
    [
        @"\google\chrome\user data", @"\microsoft\edge\user data", @"\mozilla\firefox", @"\bravesoftware\", @"\opera software\", @"\vivaldi\",
        @"\chromium\user data",
    ];

    /// <summary>Lower-cases and uses backslashes, with a leading and trailing separator so fragments match whole folder names.</summary>
    public static string Canonical(string path)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        if (p.Length > 2 && p[1] == ':')
        {
            p = p[2..];
        }

        if (!p.StartsWith('\\'))
        {
            p = "\\" + p;
        }

        return p.EndsWith('\\') ? p : p + "\\";
    }

    public static bool IsOperatingSystemOrProgram(string path)
    {
        string p = Canonical(path);
        return SystemFragments.Any(f => p.StartsWith(f, StringComparison.Ordinal));
    }

    public static bool IsBrowserProfile(string path)
    {
        string p = Canonical(path);
        return BrowserProfileFragments.Any(f => p.Contains(f, StringComparison.Ordinal));
    }

    public static bool IsPersonalData(string path)
    {
        string p = Canonical(path);
        return UserDataFragments.Any(f => p.Contains(f, StringComparison.Ordinal));
    }

    public static bool IsAppData(string path) => Canonical(path).Contains(@"\appdata\", StringComparison.Ordinal);

    /// <summary>True for anything a person would regret losing or a program would break without; the UI shows a warning for these.</summary>
    public static bool IsSensitive(string path) =>
        IsOperatingSystemOrProgram(path) || IsBrowserProfile(path) || IsPersonalData(path) || IsAppData(path);
}
