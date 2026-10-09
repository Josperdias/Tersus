using System.Reflection;
using Tersus.Tests.Framework;

namespace Tersus.Tests;

public static class Program
{
    /// <summary>
    /// Usage: dotnet run --project Tersus.Tests -c Release -- [--filter text] [--category name]
    ///        [--junit file.xml] [--markdown file.md] [--list]
    /// Exit code: 0 all passed (skips allowed), 1 at least one failure, 2 nothing ran.
    /// </summary>
    public static int Main(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var options = new RunOptions
        {
            Filter = Value("--filter"),
            Category = Value("--category"),
            JUnitPath = Value("--junit"),
            MarkdownPath = Value("--markdown"),
            ListOnly = args.Contains("--list"),
        };

        return TestRunner.Run(Assembly.GetExecutingAssembly(), options);
    }
}
