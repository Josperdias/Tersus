using System.Text;

namespace Tersus.Core.Policy;

public enum PathProblem
{
    None = 0,
    Empty,
    NotAbsoluteDrivePath,
    DevicePrefix,
    AlternateStream,
    InvalidCharacter,
    TrailingDotOrSpace,
    ReservedName,
    TooLong,
}

/// <summary>
/// Pure-string path hygiene with Windows semantics (so it behaves identically on any host OS and can be
/// unit-tested everywhere). The policy only ever accepts plain absolute drive paths ("C:\folder\file.tmp");
/// everything exotic (UNC, \\?\ and \\.\ device paths, alternate data streams, reserved device names,
/// trailing dots/spaces that Win32 silently strips, over-long paths) is refused up front.
/// </summary>
public static class PathGuard
{
    /// <summary>MAX_PATH minus the terminating NUL. The Windows shell delete API cannot handle longer paths.</summary>
    public const int MaxPathChars = 259;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    private const string InvalidNameChars = "<>\"|?*";

    /// <summary>
    /// Validates and canonicalises a Windows drive path: upper-case drive letter, single backslashes,
    /// "." and ".." resolved, no trailing separator (except for a bare drive root "C:\").
    /// </summary>
    public static bool TryNormalize(string? path, out string normalized, out PathProblem problem)
    {
        normalized = string.Empty;
        problem = PathProblem.None;

        if (string.IsNullOrWhiteSpace(path))
        {
            problem = PathProblem.Empty;
            return false;
        }

        foreach (char c in path)
        {
            if (c < 0x20 || c == 0x7F)
            {
                problem = PathProblem.InvalidCharacter;
                return false;
            }
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            bool device = path.Length >= 4
                && (path[2] == '?' || path[2] == '.')
                && (path[3] == '\\' || path[3] == '/');
            problem = device ? PathProblem.DevicePrefix : PathProblem.NotAbsoluteDrivePath;
            return false;
        }

        if (path.Length < 3 || !IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
        {
            // Also rejects drive-relative ("C:foo"), bare ("C:") and rooted-without-drive ("\\foo") forms.
            problem = PathProblem.NotAbsoluteDrivePath;
            return false;
        }

        char drive = char.ToUpperInvariant(path[0]);
        var stack = new List<string>();

        foreach (string raw in path[3..].Split(['\\', '/']))
        {
            if (raw.Length == 0 || raw == ".")
            {
                continue;
            }

            if (raw == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            if (raw.Contains(':', StringComparison.Ordinal))
            {
                problem = PathProblem.AlternateStream;
                return false;
            }

            if (raw.AsSpan().IndexOfAny(InvalidNameChars) >= 0)
            {
                problem = PathProblem.InvalidCharacter;
                return false;
            }

            if (raw[^1] == '.' || raw[^1] == ' ')
            {
                problem = PathProblem.TrailingDotOrSpace;
                return false;
            }

            int dot = raw.IndexOf('.', StringComparison.Ordinal);
            string baseName = (dot < 0 ? raw : raw[..dot]).TrimEnd(' ');
            if (ReservedNames.Contains(baseName))
            {
                problem = PathProblem.ReservedName;
                return false;
            }

            stack.Add(raw);
        }

        var sb = new StringBuilder(path.Length);
        sb.Append(drive).Append(':').Append('\\');
        for (int i = 0; i < stack.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\\');
            }

            sb.Append(stack[i]);
        }

        if (sb.Length > MaxPathChars)
        {
            problem = PathProblem.TooLong;
            return false;
        }

        normalized = sb.ToString();
        return true;
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/> (never equal to it).
    /// Both must already be normalised. Component-wise, case-insensitive: "...\Temp2" is not inside "...\Temp".</summary>
    public static bool IsStrictlyUnder(string root, string path)
    {
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the two normalised paths are the same location (case-insensitive).</summary>
    public static bool AreSame(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="ancestor"/> equals or contains <paramref name="path"/>.</summary>
    public static bool IsSameOrAncestorOf(string ancestor, string path) =>
        AreSame(ancestor, path) || IsStrictlyUnder(ancestor, path);

    public static string FileNameOf(string normalized)
    {
        int i = normalized.LastIndexOf('\\');
        return i < 0 ? normalized : normalized[(i + 1)..];
    }

    /// <summary>Extension including the dot, from the LAST dot of the file name ("a.tmp.exe" gives ".exe"). Empty if none.</summary>
    public static string ExtensionOf(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot < 0 ? string.Empty : fileName[dot..];
    }

    /// <summary>Number of path components below the drive root ("C:\a\b" gives 2).</summary>
    public static int Depth(string normalized) =>
        normalized.Length <= 3 ? 0 : normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Strips the extended-length prefix returned by GetFinalPathNameByHandle ("\\?\C:\x" gives "C:\x").</summary>
    public static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    public static string Describe(PathProblem problem) => problem switch
    {
        PathProblem.None => string.Empty,
        PathProblem.Empty => "O caminho está vazio.",
        PathProblem.NotAbsoluteDrivePath => "O caminho não é um caminho absoluto de unidade local (ex.: C:\\pasta\\arquivo).",
        PathProblem.DevicePrefix => "Caminhos de dispositivo (\\\\?\\ ou \\\\.\\) não são aceitos.",
        PathProblem.AlternateStream => "Caminhos com fluxo alternativo de dados (:) não são aceitos.",
        PathProblem.InvalidCharacter => "O caminho contém caracteres inválidos.",
        PathProblem.TrailingDotOrSpace => "Nomes terminados em ponto ou espaço não são aceitos (o Windows os reinterpreta).",
        PathProblem.ReservedName => "O caminho contém um nome reservado do Windows (CON, NUL, COM1...).",
        PathProblem.TooLong => "O caminho excede 259 caracteres; a Lixeira do Windows não o trata com segurança.",
        _ => "Caminho inválido.",
    };

    private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
