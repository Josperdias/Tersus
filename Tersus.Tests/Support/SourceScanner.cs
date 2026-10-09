using System.Text;

namespace Tersus.Tests.Support;

/// <summary>Helpers for the static audit: locate the repository and blank out comments and string literals so that rules look at CODE only.</summary>
public static class SourceScanner
{
    public static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Tersus.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Cannot find Tersus.sln above " + AppContext.BaseDirectory);
    }

    public static IEnumerable<string> ProductSourceFiles(string root, string pattern = "*.cs")
    {
        foreach (string project in new[] { "Tersus.Core", "Tersus.App" })
        {
            string dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>Replaces comments and string/char literals by spaces (newlines are kept, so line numbers stay valid).</summary>
    public static string StripCommentsAndStrings(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;
        int n = src.Length;
        while (i < n)
        {
            char c = src[i];
            char next = i + 1 < n ? src[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < n && src[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/'))
                {
                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < n)
                {
                    sb.Append("  ");
                    i += 2;
                }

                continue;
            }

            // raw string literal """ ... """
            if (c == '"' && next == '"' && i + 2 < n && src[i + 2] == '"')
            {
                int quotes = 0;
                while (i + quotes < n && src[i + quotes] == '"')
                {
                    quotes++;
                }

                sb.Append(' ', quotes);
                i += quotes;
                while (i < n)
                {
                    if (src[i] == '"')
                    {
                        int run = 0;
                        while (i + run < n && src[i + run] == '"')
                        {
                            run++;
                        }

                        if (run >= quotes)
                        {
                            sb.Append(' ', run);
                            i += run;
                            break;
                        }
                    }

                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            bool verbatim = (c == '@' && next == '"') || ((c == '$' || c == '@') && i + 2 < n && (next == '@' || next == '$') && src[i + 2] == '"');
            if (verbatim || c == '"' || (c == '$' && next == '"'))
            {
                bool isVerbatim = verbatim && (c == '@' || next == '@');
                while (i < n && src[i] != '"')
                {
                    sb.Append(' ');
                    i++;
                }

                sb.Append(' ');
                i++; // opening quote
                while (i < n)
                {
                    if (isVerbatim)
                    {
                        if (src[i] == '"' && i + 1 < n && src[i + 1] == '"')
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }

                        if (src[i] == '"')
                        {
                            sb.Append(' ');
                            i++;
                            break;
                        }
                    }
                    else
                    {
                        if (src[i] == '\\' && i + 1 < n)
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }

                        if (src[i] == '"')
                        {
                            sb.Append(' ');
                            i++;
                            break;
                        }
                    }

                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                // char literal (but not an apostrophe in code, which C# does not have outside literals)
                sb.Append(' ');
                i++;
                while (i < n && src[i] != '\'')
                {
                    if (src[i] == '\\' && i + 1 < n)
                    {
                        sb.Append("  ");
                        i += 2;
                        continue;
                    }

                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < n)
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    public static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
