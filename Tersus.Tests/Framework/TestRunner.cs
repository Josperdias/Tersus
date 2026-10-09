using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security;
using System.Text;

namespace Tersus.Tests.Framework;

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
}

public sealed record TestResult(string ClassName, string Name, TestOutcome Outcome, TimeSpan Duration, string? Message, string? Detail, string[] Categories);

public sealed class RunOptions
{
    public string? Filter { get; init; }

    public string? Category { get; init; }

    public string? JUnitPath { get; init; }

    public string? MarkdownPath { get; init; }

    public bool ListOnly { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
}

public sealed class TestRunner
{
    private sealed record Case(Type Type, MethodInfo Method, string[] Categories, string? SkipReason, bool WindowsOnly);

    public static int Run(Assembly assembly, RunOptions options)
    {
        List<Case> cases = Discover(assembly, options);
        if (options.ListOnly)
        {
            foreach (Case c in cases)
            {
                Console.WriteLine($"{c.Type.Name}.{c.Method.Name}  [{string.Join(",", c.Categories)}]");
            }

            return 0;
        }

        Console.WriteLine($"Tersus.Tests: {cases.Count} test(s) on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        var results = new List<TestResult>();
        var total = Stopwatch.StartNew();
        foreach (Case c in cases)
        {
            TestResult r = RunOne(c, options.Timeout);
            results.Add(r);
            string tag = r.Outcome switch { TestOutcome.Passed => "PASS", TestOutcome.Failed => "FAIL", _ => "SKIP" };
            Console.WriteLine($"[{tag}] {r.ClassName}.{r.Name} ({r.Duration.TotalMilliseconds:0} ms){(r.Outcome == TestOutcome.Passed ? string.Empty : "\n       " + (r.Message ?? string.Empty).Replace("\n", "\n       ", StringComparison.Ordinal))}");
        }

        total.Stop();
        int passed = results.Count(r => r.Outcome == TestOutcome.Passed);
        int failed = results.Count(r => r.Outcome == TestOutcome.Failed);
        int skipped = results.Count(r => r.Outcome == TestOutcome.Skipped);
        Console.WriteLine();
        Console.WriteLine($"Summary: {passed} passed, {failed} failed, {skipped} skipped, {results.Count} total in {total.Elapsed.TotalSeconds:0.0} s");

        if (options.JUnitPath is not null)
        {
            WriteFile(options.JUnitPath, BuildJUnit(results, total.Elapsed));
        }

        if (options.MarkdownPath is not null)
        {
            WriteFile(options.MarkdownPath, BuildMarkdown(results, total.Elapsed));
        }

        if (cases.Count == 0)
        {
            Console.WriteLine("No tests matched the filter.");
            return 2;
        }

        return failed == 0 ? 0 : 1;
    }

    private static List<Case> Discover(Assembly assembly, RunOptions options)
    {
        var list = new List<Case>();
        foreach (Type type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            string[] classCats = type.GetCustomAttributes<CategoryAttribute>().Select(a => a.Name).ToArray();
            bool classWindows = type.GetCustomAttribute<WindowsOnlyAttribute>() is not null;
            string? classSkip = type.GetCustomAttribute<SkipAttribute>()?.Reason;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (m.GetCustomAttribute<TestAttribute>() is null)
                {
                    continue;
                }

                string[] cats = classCats.Concat(m.GetCustomAttributes<CategoryAttribute>().Select(a => a.Name)).ToArray();
                string full = $"{type.Name}.{m.Name}";
                if (options.Filter is not null && !full.Contains(options.Filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (options.Category is not null && !cats.Contains(options.Category, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new Case(type, m, cats, m.GetCustomAttribute<SkipAttribute>()?.Reason ?? classSkip, classWindows || m.GetCustomAttribute<WindowsOnlyAttribute>() is not null));
            }
        }

        return list;
    }

    private static TestResult RunOne(Case c, TimeSpan timeout)
    {
        string cls = c.Type.Name;
        string name = c.Method.Name;
        if (c.SkipReason is not null)
        {
            return new TestResult(cls, name, TestOutcome.Skipped, TimeSpan.Zero, c.SkipReason, null, c.Categories);
        }

        if (c.WindowsOnly && !OperatingSystem.IsWindows())
        {
            return new TestResult(cls, name, TestOutcome.Skipped, TimeSpan.Zero, "Requires Windows (runs in the Windows CI job).", null, c.Categories);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            Task task = Task.Run(async () =>
            {
                object? instance = Activator.CreateInstance(c.Type);
                try
                {
                    object? ret = c.Method.Invoke(instance, null);
                    if (ret is Task t)
                    {
                        await t.ConfigureAwait(false);
                    }
                }
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                }
                finally
                {
                    (instance as IDisposable)?.Dispose();
                }
            });

            if (!task.Wait(timeout))
            {
                return new TestResult(cls, name, TestOutcome.Failed, sw.Elapsed, $"Timed out after {timeout.TotalSeconds:0} s.", null, c.Categories);
            }

            return new TestResult(cls, name, TestOutcome.Passed, sw.Elapsed, null, null, c.Categories);
        }
        catch (AggregateException agg)
        {
            Exception ex = agg.Flatten().InnerExceptions[0];
            if (ex is SkipException skip)
            {
                return new TestResult(cls, name, TestOutcome.Skipped, sw.Elapsed, skip.Message, null, c.Categories);
            }

            string message = ex is AssertionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            return new TestResult(cls, name, TestOutcome.Failed, sw.Elapsed, message, ex.ToString(), c.Categories);
        }
    }

    private static void WriteFile(string path, string content)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string BuildJUnit(List<TestResult> results, TimeSpan total)
    {
        var sb = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<testsuites>\n");
        sb.Append(inv, $"  <testsuite name=\"Tersus.Tests\" tests=\"{results.Count}\" failures=\"{results.Count(r => r.Outcome == TestOutcome.Failed)}\" skipped=\"{results.Count(r => r.Outcome == TestOutcome.Skipped)}\" time=\"{total.TotalSeconds:0.###}\">\n");
        foreach (TestResult r in results)
        {
            sb.Append(inv, $"    <testcase classname=\"{Esc(r.ClassName)}\" name=\"{Esc(r.Name)}\" time=\"{r.Duration.TotalSeconds:0.###}\">");
            if (r.Outcome == TestOutcome.Failed)
            {
                sb.Append(inv, $"<failure message=\"{Esc(r.Message ?? string.Empty)}\">{Esc(r.Detail ?? string.Empty)}</failure>");
            }
            else if (r.Outcome == TestOutcome.Skipped)
            {
                sb.Append(inv, $"<skipped message=\"{Esc(r.Message ?? string.Empty)}\"/>");
            }

            sb.Append("</testcase>\n");
        }

        sb.Append("  </testsuite>\n</testsuites>\n");
        return sb.ToString();
    }

    private static string BuildMarkdown(List<TestResult> results, TimeSpan total)
    {
        var sb = new StringBuilder();
        int passed = results.Count(r => r.Outcome == TestOutcome.Passed);
        int failed = results.Count(r => r.Outcome == TestOutcome.Failed);
        int skipped = results.Count(r => r.Outcome == TestOutcome.Skipped);
        sb.AppendLine("## Resultado dos testes automatizados");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Sistema: `{System.Runtime.InteropServices.RuntimeInformation.OSDescription}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Total: **{results.Count}** | Aprovados: **{passed}** | Falhas: **{failed}** | Ignorados: **{skipped}** | Duração: {total.TotalSeconds:0.0} s");
        sb.AppendLine();
        sb.AppendLine("| Grupo | Aprovados | Falhas | Ignorados |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (IGrouping<string, TestResult> g in results.GroupBy(r => r.ClassName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count(r => r.Outcome == TestOutcome.Passed)} | {g.Count(r => r.Outcome == TestOutcome.Failed)} | {g.Count(r => r.Outcome == TestOutcome.Skipped)} |");
        }

        if (failed > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Falhas");
            foreach (TestResult r in results.Where(r => r.Outcome == TestOutcome.Failed))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- `{r.ClassName}.{r.Name}`: {(r.Message ?? string.Empty).Replace("\n", " ", StringComparison.Ordinal)}");
            }
        }

        if (skipped > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Ignorados (e por quê)");
            foreach (IGrouping<string, TestResult> g in results.Where(r => r.Outcome == TestOutcome.Skipped).GroupBy(r => r.Message ?? string.Empty))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {g.Count()} × {g.Key}");
            }
        }

        return sb.ToString();
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? string.Empty;
}
